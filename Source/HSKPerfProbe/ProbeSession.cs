using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace HSKPerfProbe
{
    public sealed class TimelineRow
    {
        public double T;
        public int Ticks, Frames;
        public double AvgFrameMs, MaxFrameMs;
        public double TickMs, UpdateMs, GuiMs;   // inclusive root time spent in that second
        public double HeapMb;
        public int Gc0, Gc1, Gc2;
        public string Speed;
    }

    public enum SessionState { Idle, Installing, Warmup, Recording, Removing }

    /// <summary>
    /// One recording: build catalog, install hooks over several frames, warm up, sample every frame,
    /// stop, write the report, remove hooks over several frames.
    /// </summary>
    public static class ProbeSession
    {
        public static SessionState State { get; private set; }
        public static bool Busy { get { return State != SessionState.Idle; } }

        public static string LastMessage = "";
        public static double LastMessageUntil;

        public static ProbeConfig Cfg;
        static Harmony harmony;

        // capture data (read by ProbeReport)
        public static double CaptureStartReal, CaptureSeconds;
        public static long Frames;
        public static double SumFrameMs;
        public static readonly List<float> FrameMs = new List<float>(8192);
        public static readonly List<TimelineRow> Rows = new List<TimelineRow>();
        public static List<string> StartContext, EndContext;
        public static string StopReason;
        public static DateTime StartedAt;
        public static double OverheadNsStatic, OverheadNsInst, BaselineNs;
        // part of the per-call overhead that falls INSIDE the measured window (charged to the method itself);
        // the rest (OverheadNs - WindowNs) lands in the exclusive time of the caller
        public static double WindowNsStatic, WindowNsInst;

        const double FrameBudgetMs = 50.0;   // patching work per frame while installing / removing
        static int cursor;                   // progress of install / uninstall
        static double baseStatic, baseInst;
        static double warmEndReal, lastRowReal;
        static int rowFrames; static double rowSumMs, rowMaxMs;
        static long lastTickCalls, lastTickIncl, lastUpdateIncl, lastGuiIncl;
        static int lastGc0, lastGc1, lastGc2;
        static bool hadCaptureData;

        public static double ElapsedCapture(double now) { return Math.Max(0, now - CaptureStartReal); }

        /// <summary>Shows a message in the corner for a while (only while no recording is running).</summary>
        public static void Notify(string message, double seconds = 40)
        {
            if (State != SessionState.Idle) return;
            LastMessage = message;
            LastMessageUntil = Time.realtimeSinceStartup + seconds;
        }

        // ------------------------------------------------------------------ start

        public static void Start()
        {
            if (State != SessionState.Idle) return;
            try
            {
                Cfg = ProbeConfig.Load();
                ModMap.Build();
                HookCatalog.Build(Cfg);
                harmony = new Harmony(ProbeCore.HarmonyId);

                ProbeCore.Active = false;
                ProbeCore.ResetAll();
                MeasureCalls(out baseStatic, out baseInst);

                HookCatalog.PrepareInstall();
                cursor = 0;
                StartedAt = DateTime.Now;
                StopReason = null;
                LastMessage = "";
                hadCaptureData = false;
                State = SessionState.Installing;
                Log.Message("[HSKPerfProbe] catalog built: " + HookCatalog.Targets.Count + " hooks in "
                    + HookCatalog.BuildMs.ToString("F0", CultureInfo.InvariantCulture) + " ms. Installing over several frames...");
            }
            catch (Exception e)
            {
                Log.Error("[HSKPerfProbe] failed to start: " + e);
                Abort("start failed: " + e.Message);
            }
        }

        static void MeasureCalls(out double staticNs, out double instNs)
        {
            const int N = 100000;
            var obj = new CalibrationTarget();
            for (int i = 0; i < 2000; i++) { Calibration.Empty(); obj.EmptyInst(); }
            ProbeCore.ResetAll(); // drop the warm-up (JIT) calls so they do not pollute the calibration slots
            long t0 = Stopwatch.GetTimestamp();
            for (int i = 0; i < N; i++) Calibration.Empty();
            long t1 = Stopwatch.GetTimestamp();
            for (int i = 0; i < N; i++) obj.EmptyInst();
            long t2 = Stopwatch.GetTimestamp();
            double f = 1e9 / Stopwatch.Frequency / N;
            staticNs = (t1 - t0) * f;
            instNs = (t2 - t1) * f;
        }

        // ------------------------------------------------------------------ per frame

        /// <summary>Called once per Unity frame from the driver.</summary>
        public static void OnFrame(float unscaledDt, double now)
        {
            switch (State)
            {
                case SessionState.Installing: StepInstall(now); break;
                case SessionState.Removing: StepRemove(now); break;
                case SessionState.Warmup:
                    if (now >= warmEndReal) BeginCapture(now);
                    break;
                case SessionState.Recording:
                    Record(unscaledDt, now);
                    break;
            }
        }

        static void StepInstall(double now)
        {
            var targets = HookCatalog.Targets;
            long start = Stopwatch.GetTimestamp();
            while (cursor < targets.Count)
            {
                HookCatalog.InstallOne(harmony, targets[cursor]);
                cursor++;
                if ((Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency > FrameBudgetMs) break;
            }
            if (cursor < targets.Count) return;

            // all hooks in place: measure the real per-call cost, then let them warm up (JIT)
            ProbeCore.ResetAll();
            ProbeCore.Active = true;
            double patchedStatic, patchedInst;
            MeasureCalls(out patchedStatic, out patchedInst);
            BaselineNs = baseStatic;
            OverheadNsStatic = Math.Max(0, patchedStatic - baseStatic);
            OverheadNsInst = Math.Max(0, patchedInst - baseInst);
            WindowNsStatic = WindowNsInst = 0;
            foreach (var t in HookCatalog.Targets)
            {
                if (!t.IsCalibration || t.Slot.Calls <= 0) continue;
                double w = t.Slot.ExclTicks * 1e9 / Stopwatch.Frequency / t.Slot.Calls; // body is empty, so all of it is overhead
                if (t.Inst) WindowNsInst = Math.Min(w, OverheadNsInst); else WindowNsStatic = Math.Min(w, OverheadNsStatic);
            }
            ProbeCore.ResetAll();

            warmEndReal = now + Cfg.WarmupSeconds;
            CaptureStartReal = now;
            State = SessionState.Warmup;
            Log.Message("[HSKPerfProbe] hooks installed: " + HookCatalog.PatchedOk + " ok, " + HookCatalog.PatchedFail + " failed ("
                + HookCatalog.InstallMs.ToString("F0", CultureInfo.InvariantCulture) + " ms of patching). Warming up "
                + Cfg.WarmupSeconds + " s, then recording " + Cfg.DurationSeconds + " s (Ctrl+F9 stops early).");
            if (Cfg.WarmupSeconds <= 0) BeginCapture(now);
        }

        static void BeginCapture(double now)
        {
            ProbeCore.ResetAll();
            Frames = 0; SumFrameMs = 0;
            FrameMs.Clear(); Rows.Clear();
            CaptureStartReal = now;
            lastRowReal = now;
            rowFrames = 0; rowSumMs = 0; rowMaxMs = 0;
            lastTickCalls = lastTickIncl = lastUpdateIncl = lastGuiIncl = 0;
            lastGc0 = GC.CollectionCount(0); lastGc1 = GC.CollectionCount(1); lastGc2 = GC.CollectionCount(2);
            StartContext = ProbeReport.Context();
            hadCaptureData = true;
            State = SessionState.Recording;
        }

        static void Record(float unscaledDt, double now)
        {
            double ms = unscaledDt * 1000.0;
            Frames++;
            SumFrameMs += ms;
            FrameMs.Add((float)ms);
            rowFrames++; rowSumMs += ms; if (ms > rowMaxMs) rowMaxMs = ms;

            if (now - lastRowReal >= 1.0) PushRow(now);
            if (now - CaptureStartReal >= Cfg.DurationSeconds) Stop("timeout (" + Cfg.DurationSeconds + " s)");
        }

        static void PushRow(double now)
        {
            double toMs = 1000.0 / Stopwatch.Frequency;
            var tr = HookCatalog.TickRoot; var ur = HookCatalog.UpdateRoot; var gr = HookCatalog.GuiRoot;
            long tc = tr != null ? tr.Calls : 0, ti = tr != null ? tr.InclTicks : 0;
            long ui = ur != null ? ur.InclTicks : 0, gi = gr != null ? gr.InclTicks : 0;
            int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
            string speed = "n/a";
            try { if (Find.TickManager != null) speed = Find.TickManager.CurTimeSpeed.ToString(); } catch { }

            Rows.Add(new TimelineRow
            {
                T = now - CaptureStartReal,
                Ticks = (int)(tc - lastTickCalls),
                Frames = rowFrames,
                AvgFrameMs = rowFrames > 0 ? rowSumMs / rowFrames : 0,
                MaxFrameMs = rowMaxMs,
                TickMs = (ti - lastTickIncl) * toMs,
                UpdateMs = (ui - lastUpdateIncl) * toMs,
                GuiMs = (gi - lastGuiIncl) * toMs,
                HeapMb = GC.GetTotalMemory(false) / 1048576.0,
                Gc0 = g0 - lastGc0, Gc1 = g1 - lastGc1, Gc2 = g2 - lastGc2,
                Speed = speed,
            });
            lastTickCalls = tc; lastTickIncl = ti; lastUpdateIncl = ui; lastGuiIncl = gi;
            lastGc0 = g0; lastGc1 = g1; lastGc2 = g2;
            rowFrames = 0; rowSumMs = 0; rowMaxMs = 0;
            lastRowReal = now;
        }

        // ------------------------------------------------------------------ stop

        public static void Stop(string reason)
        {
            if (State == SessionState.Idle || State == SessionState.Removing) return;
            var was = State;
            ProbeCore.Active = false;
            StopReason = reason;
            double now = Time.realtimeSinceStartup;

            if (was == SessionState.Recording && hadCaptureData && Frames > 0)
            {
                CaptureSeconds = Math.Max(0.001, now - CaptureStartReal);
                try
                {
                    EndContext = ProbeReport.Context();
                    string dir = ProbeReport.Write();
                    LastMessage = "Report saved: " + dir;
                    Log.Message("[HSKPerfProbe] " + LastMessage);
                }
                catch (Exception e)
                {
                    LastMessage = "Report failed: " + e.Message;
                    Log.Error("[HSKPerfProbe] report failed: " + e);
                }
            }
            else
            {
                LastMessage = "Stopped before any data was captured.";
            }
            LastMessageUntil = now + 45;
            BeginRemove();
        }

        public static void Abort(string why)
        {
            ProbeCore.Active = false;
            LastMessage = "PerfProbe aborted: " + why;
            LastMessageUntil = Time.realtimeSinceStartup + 30;
            BeginRemove();
        }

        static void BeginRemove()
        {
            // only hooks that were actually installed need to go: cursor is how far installation got
            if (State == SessionState.Installing) { }
            else cursor = HookCatalog.Targets.Count;
            if (harmony == null || cursor <= 0) { State = SessionState.Idle; return; }
            removeUpTo = cursor;
            cursor = 0;
            State = SessionState.Removing;
        }

        static int removeUpTo;

        static void StepRemove(double now)
        {
            var targets = HookCatalog.Targets;
            long start = Stopwatch.GetTimestamp();
            while (cursor < removeUpTo && cursor < targets.Count)
            {
                HookCatalog.UninstallOne(harmony, targets[cursor]);
                cursor++;
                if ((Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency > FrameBudgetMs) break;
            }
            if (cursor < removeUpTo && cursor < targets.Count) return;
            State = SessionState.Idle;
            // safety net: anything of ours that is still attached (e.g. after an error) goes now
            try { harmony.UnpatchAll(ProbeCore.HarmonyId); } catch { }
            Log.Message("[HSKPerfProbe] hooks removed.");
        }

        // ------------------------------------------------------------------ overlay

        public static string Overlay(double now)
        {
            switch (State)
            {
                case SessionState.Installing:
                    return "PerfProbe: installing hooks " + cursor + "/" + HookCatalog.Targets.Count + "  (Ctrl+F9 cancels)";
                case SessionState.Warmup:
                    return "PerfProbe: warming up... (Ctrl+F9 cancels)";
                case SessionState.Recording:
                    return "PerfProbe REC " + ElapsedCapture(now).ToString("F0", CultureInfo.InvariantCulture) + "/" + Cfg.DurationSeconds
                        + " s  -  Ctrl+F9 = stop and save";
                case SessionState.Removing:
                    return (string.IsNullOrEmpty(LastMessage) ? "" : LastMessage + "   |   ") + "removing hooks " + cursor + "/" + removeUpTo;
            }
            if (!string.IsNullOrEmpty(LastMessage) && now < LastMessageUntil) return LastMessage;
            return null;
        }
    }
}
