using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using RimWorld;
using UnityEngine;
using Verse;

namespace HSKPerformance
{
    /// <summary>Writes report.md (for reading) and CSV files (for scripts).</summary>
    public static class ProbeReport
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        static string F(double v, int d = 2) { return v.ToString("F" + d, Inv); }
        static string Clean(string s) { return (s ?? "").Replace('|', '/').Replace('\n', ' ').Replace('\r', ' '); }
        static string Csv(string s)
        {
            s = s ?? "";
            if (s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0) return "\"" + s.Replace("\"", "\"\"") + "\"";
            return s;
        }
        static string Safe(Func<object> f) { try { var v = f(); return v == null ? "n/a" : v.ToString(); } catch { return "n/a"; } }

        sealed class ModAgg
        {
            public string Mod;
            public double Tick, Update, Gui, Harmony;
            public long Calls;
            public int HooksHit;
            public double MaxSingleMs;
            public double ContentMs;
            public string TopMethod; public double TopMethodMs;
            public double Total { get { return Tick + Update + Gui + Harmony; } }
        }

        public static List<string> Context()
        {
            var l = new List<string>();
            l.Add("Game version: " + Safe(() => VersionControl.CurrentVersionString));
            l.Add("Program state: " + Safe(() => Current.ProgramState));
            l.Add("Active mods: " + Safe(() => LoadedModManager.RunningModsListForReading.Count));
            l.Add("Game speed: " + Safe(() => Find.TickManager.CurTimeSpeed) + " (paused=" + Safe(() => Find.TickManager.Paused) + "), tick rate multiplier " + Safe(() => Find.TickManager.TickRateMultiplier));
            l.Add("Game ticks: " + Safe(() => Find.TickManager.TicksGame));
            l.Add("Maps: " + Safe(() => Find.Maps.Count));
            l.Add("Current map things: " + Safe(() => Find.CurrentMap.listerThings.AllThings.Count));
            l.Add("Current map spawned pawns: " + Safe(() => Find.CurrentMap.mapPawns.AllPawnsSpawned.Count));
            l.Add("Current map colonists: " + Safe(() => Find.CurrentMap.mapPawns.FreeColonistsSpawnedCount));
            l.Add("Camera: zoom " + Safe(() => Find.CameraDriver.CurrentZoom) + ", root size " + Safe(() => Find.CameraDriver.RootSize.ToString("F1", Inv))
                + " (about " + Safe(() => (Screen.height / (2f * Find.CameraDriver.RootSize)).ToString("F0", Inv)) + " px per map cell), viewing "
                + Safe(() => Find.CameraDriver.CurrentViewRect.Width + "x" + Find.CameraDriver.CurrentViewRect.Height) + " cells");
            l.Add("Flecks: " + Safe(() => FleckCensus.Describe()));
            l.Add("Drawing: " + Safe(() => DrawCensus.Describe()));
            l.Add("Far map: " + Safe(() => FarMap.ContextLine()));
            l.Add("In view: " + Safe(() => CountInView(false)) + " things, " + Safe(() => CountInView(true)) + " of them with a GUI overlay (labels)");
            l.Add("World pawns alive: " + Safe(() => Find.WorldPawns.AllPawnsAlive.Count()));
            l.Add("World objects: " + Safe(() => Find.WorldObjects.AllWorldObjects.Count));
            l.Add("Resolution: " + Safe(() => Screen.width) + "x" + Safe(() => Screen.height) + ", vSync=" + Safe(() => QualitySettings.vSyncCount) + ", targetFrameRate=" + Safe(() => Application.targetFrameRate));
            l.Add("CPU: " + Safe(() => SystemInfo.processorType) + " (" + Safe(() => SystemInfo.processorCount) + " threads), RAM " + Safe(() => SystemInfo.systemMemorySize) + " MB");
            l.Add("GPU: " + Safe(() => SystemInfo.graphicsDeviceName) + " (" + Safe(() => SystemInfo.graphicsMemorySize) + " MB)");
            return l;
        }

        static int CountInView(bool onlyWithOverlay)
        {
            var rect = Find.CameraDriver.CurrentViewRect;
            var map = Find.CurrentMap;
            var list = onlyWithOverlay ? map.listerThings.ThingsInGroup(ThingRequestGroup.HasGUIOverlay) : map.listerThings.AllThings;
            int n = 0;
            for (int i = 0; i < list.Count; i++)
                if (list[i].Spawned && rect.Contains(list[i].Position)) n++;
            return n;
        }

        static double Percentile(float[] sorted, double p)
        {
            if (sorted.Length == 0) return 0;
            double idx = p * (sorted.Length - 1);
            int lo = (int)Math.Floor(idx), hi = (int)Math.Ceiling(idx);
            if (lo == hi) return sorted[lo];
            return sorted[lo] + (sorted[hi] - sorted[lo]) * (idx - lo);
        }

        public static string Write()
        {
            var cfg = ProbeSession.Cfg;
            string dir = Path.Combine(ProbeConfig.Dir, "report-" + ProbeSession.StartedAt.ToString("yyyyMMdd-HHmmss", Inv) + (ProbeSession.Phase == 1 ? "-light" : ProbeSession.Phase == 2 ? "-full" : ""));
            Directory.CreateDirectory(dir);

            double toMs = 1000.0 / Stopwatch.Frequency;
            double secs = ProbeSession.CaptureSeconds;
            long frames = ProbeSession.Frames;
            var tickRoot = HookCatalog.TickRoot; var updRoot = HookCatalog.UpdateRoot; var guiRoot = HookCatalog.GuiRoot;
            long ticks = tickRoot != null ? tickRoot.Calls : 0;

            // ---- collect slots that were hit (calibration excluded) ----
            var slots = new List<Slot>();
            foreach (var t in HookCatalog.Targets)
                if (!t.IsCalibration && t.Slot.Calls > 0) slots.Add(t.Slot);
            if (ProbeCore.Unknown.Calls > 0) slots.Add(ProbeCore.Unknown);
            slots.Sort((a, b) => b.ExclTicks.CompareTo(a.ExclTicks));

            // ---- remove the probe's own overhead from every row ----
            // A probe call costs OverheadNs in total. WindowNs of it happens inside the measured window (charged to the
            // method itself); the rest happens outside it and ends up in the exclusive time of the calling method.
            double freq = Stopwatch.Frequency;
            double outsideNs = Math.Max(0, ProbeSession.OverheadNsStatic - ProbeSession.WindowNsStatic);
            long totRaw = 0, totNet = 0;
            foreach (var s in slots)
            {
                double win = s.TrackDef ? ProbeSession.WindowNsInst : ProbeSession.WindowNsStatic;
                double corrNs = s.Calls * win + s.ChildCalls * outsideNs;
                s.RawExclTicks = s.ExclTicks;
                s.ExclTicks = Math.Max(0, s.ExclTicks - (long)(corrNs * freq / 1e9));
                totRaw += s.RawExclTicks; totNet += s.ExclTicks;
            }
            foreach (var d in ProbeCore.DefStats.Values)
                d.ExclTicks = Math.Max(0, d.ExclTicks - (long)(d.Calls * ProbeSession.WindowNsInst * freq / 1e9));
            slots.Sort((a, b) => b.ExclTicks.CompareTo(a.ExclTicks));

            // ---- per mod ----
            var mods = new Dictionary<string, ModAgg>();
            Func<string, ModAgg> aggFor = name =>
            {
                ModAgg m;
                if (!mods.TryGetValue(name, out m)) { m = new ModAgg { Mod = name }; mods[name] = m; }
                return m;
            };
            foreach (var kv in ModMap.PackageIdByName) aggFor(kv.Key);
            aggFor(ModMap.Core);
            double totalMeasuredMs = 0;
            foreach (var s in slots)
            {
                var m = aggFor(s.Mod);
                double ms = s.ExclTicks * toMs / secs; // ms per real second
                totalMeasuredMs += ms;
                switch (s.Category)
                {
                    case "Tick": m.Tick += ms; break;
                    case "Update": m.Update += ms; break;
                    case "GUI": m.Gui += ms; break;
                    default: m.Harmony += ms; break;
                }
                m.Calls += s.Calls;
                m.HooksHit++;
                double single = s.MaxExclTicks * toMs;
                if (single > m.MaxSingleMs) m.MaxSingleMs = single;
                if (ms > m.TopMethodMs) { m.TopMethodMs = ms; m.TopMethod = s.Name; }
            }
            var defRows = ProbeCore.DefStats.Values.Where(d => d.Calls > 0).OrderByDescending(d => d.ExclTicks).ToList();
            foreach (var d in defRows) aggFor(ModMap.ForDef(d.Def)).ContentMs += d.ExclTicks * toMs / secs;
            var modList = mods.Values.OrderByDescending(m => m.Total).ThenByDescending(m => m.ContentMs).ToList();

            // ---- frame stats ----
            var sortedFrames = ProbeSession.FrameMs.ToArray();
            Array.Sort(sortedFrames);
            double avgFrame = frames > 0 ? ProbeSession.SumFrameMs / frames : 0;
            double p50 = Percentile(sortedFrames, 0.50), p90 = Percentile(sortedFrames, 0.90), p95 = Percentile(sortedFrames, 0.95), p99 = Percentile(sortedFrames, 0.99);
            double pmax = sortedFrames.Length > 0 ? sortedFrames[sortedFrames.Length - 1] : 0;
            int over33 = sortedFrames.Count(x => x > 33.4f), over50 = sortedFrames.Count(x => x > 50f), over100 = sortedFrames.Count(x => x > 100f);

            // ---- overhead that was removed ----
            double overheadPerSec = (totRaw - totNet) * toMs / secs;
            double rawPerSec = totRaw * toMs / secs;

            var sb = new StringBuilder(64 * 1024);
            sb.AppendLine("# HSK Performance report");
            sb.AppendLine();
            sb.AppendLine("Recorded " + ProbeSession.StartedAt.ToString("yyyy-MM-dd HH:mm:ss", Inv) + ", stopped by: " + ProbeSession.StopReason + ".");
            sb.AppendLine();
            sb.AppendLine("## How to read this");
            sb.AppendLine("- Times are **exclusive**: a method's own time minus time spent in other profiled methods called from it, so every millisecond is counted once and the mod totals can be added up.");
            sb.AppendLine("- `ms/s` = milliseconds of main-thread CPU per real second (1000 ms/s = the whole second). `%wall` = the same as a percentage.");
            sb.AppendLine("- Categories: **Tick** = simulation, **Update** = per-frame logic and drawing, **GUI** = OnGUI/interface, **Harmony** = other mods' Harmony prefix/postfix/finalizer methods (their cost is charged to the mod that wrote the patch, not to the patched vanilla method).");
            sb.AppendLine("- Vanilla \"Root\" rows (`DoSingleTick`, `Root_Play.Update`, `UIRoot_Play.UIRootOnGUI`) show time that no deeper hook covered, i.e. unprofiled vanilla code.");
            sb.AppendLine("- Only the main thread is measured. Anything running on worker threads or on the GPU is not visible here.");
            sb.AppendLine();

            // ---- summary ----
            sb.AppendLine("## Summary");
            sb.AppendLine();
            sb.AppendLine("- Capture: **" + F(secs, 1) + " s** real time after " + cfg.WarmupSeconds + " s warm-up, " + frames + " frames, " + ticks + " ticks");
            sb.AppendLine("- **FPS avg " + F(secs > 0 ? frames / secs : 0, 1) + "**, **TPS avg " + F(secs > 0 ? ticks / secs : 0, 1) + "** (at 1x speed the game aims for 60 TPS)");
            sb.AppendLine("- Main-thread work covered by the hooks, **probe overhead removed**: **" + F(totalMeasuredMs, 0) + " ms/s** (" + F(totalMeasuredMs / 10.0, 1) + " % of wall time)");
            sb.AppendLine("- Probe overhead: ~" + F(ProbeSession.OverheadNsStatic, 0) + " ns per call (" + F(ProbeSession.WindowNsStatic, 0) + " ns of it inside the measured window). The raw measurement was " + F(rawPerSec, 0)
                + " ms/s, so **" + F(overheadPerSec, 0) + " ms/s** (" + F(overheadPerSec / 10.0, 1) + " % of wall) was measurement overhead and has been subtracted from every row (raw values are in methods.csv). "
                + "The game still ran slower than normal while recording, and inclusive numbers (frame budget, timeline) still contain the overhead.");
            sb.AppendLine("- Empty methods skipped (nothing to measure): " + HookCatalog.EmptySkipped);
            sb.AppendLine("- Mode: **" + (cfg.LightMode ? "LIGHT" : "FULL") + "**" + (ProbeSession.Phase == 1 ? " (half 1 of 2 of an automatic light+full run)" : ProbeSession.Phase == 2 ? " (half 2 of 2, right after the light half)" : ""));
            if (cfg.LightMode) sb.AppendLine("- **Light mode**: only the vanilla roots and about 100 per-frame vanilla methods are hooked (a few thousand calls per second), so the probe overhead is tiny and the frame numbers are close to a normal run. Mod hooks are off on purpose, so per-mod tables are empty; the method table shows the vanilla frame parts. Inclusive time of GameComponentUpdate, MapComponentUpdate and MapComponentTick covers all components of all mods.");
            if (Safe(() => ModsConfig.IsActive("dubwise.dubsperformanceanalyzer.steam")) == "True")
                sb.AppendLine("- Note: Dubs Performance Analyzer is enabled. Its own patches were left out of the Harmony rows, but keep it closed while recording so its overhead does not add up with ours.");
            if (ticks == 0) sb.AppendLine("- **Warning: no ticks were recorded** (game paused, or not in a running game). Tick numbers are meaningless in this report.");
            if (ProbeCore.DepthOverflow > 0) sb.AppendLine("- Warning: " + ProbeCore.DepthOverflow + " calls were skipped because the profiled call stack got deeper than 512.");
            sb.AppendLine();
            sb.AppendLine("Start state:");
            foreach (var c in ProbeSession.StartContext ?? new List<string>()) sb.AppendLine("- " + c);
            sb.AppendLine();
            sb.AppendLine("End state:");
            foreach (var c in ProbeSession.EndContext ?? new List<string>())
                if (c.StartsWith("Game speed") || c.StartsWith("Game ticks") || c.StartsWith("Current map things") || c.StartsWith("Current map spawned") || c.StartsWith("Flecks") || c.StartsWith("Drawing") || c.StartsWith("Far map") || c.StartsWith("Camera") || c.StartsWith("In view")) sb.AppendLine("- " + c);
            sb.AppendLine();

            // ---- budget ----
            sb.AppendLine("## Frame and tick budget");
            sb.AppendLine();
            double updIncl = updRoot != null ? updRoot.InclTicks * toMs / Math.Max(1, frames) : 0;
            double tickInclPerFrame = tickRoot != null ? tickRoot.InclTicks * toMs / Math.Max(1, frames) : 0;
            double guiIncl = guiRoot != null ? guiRoot.InclTicks * toMs / Math.Max(1, frames) : 0;
            double other = Math.Max(0, avgFrame - updIncl - guiIncl);
            sb.AppendLine("| part of an average frame | ms | % of frame |");
            sb.AppendLine("|---|---:|---:|");
            sb.AppendLine("| whole frame (measured by Unity) | " + F(avgFrame) + " | 100.0 |");
            sb.AppendLine("| Root_Play.Update, everything in it | " + F(updIncl) + " | " + F(avgFrame > 0 ? updIncl / avgFrame * 100 : 0, 1) + " |");
            sb.AppendLine("| &nbsp;&nbsp;of which ticks (DoSingleTick) | " + F(tickInclPerFrame) + " | " + F(avgFrame > 0 ? tickInclPerFrame / avgFrame * 100 : 0, 1) + " |");
            sb.AppendLine("| UIRoot_Play.UIRootOnGUI, everything in it | " + F(guiIncl) + " | " + F(avgFrame > 0 ? guiIncl / avgFrame * 100 : 0, 1) + " |");
            sb.AppendLine("| rest: rendering, GPU/vsync wait, engine, other scripts | " + F(other) + " | " + F(avgFrame > 0 ? other / avgFrame * 100 : 0, 1) + " |");
            sb.AppendLine();
            if (tickRoot != null && ticks > 0)
                sb.AppendLine("Per tick: average **" + F(tickRoot.InclTicks * toMs / ticks, 3) + " ms**, worst single tick " + F(tickRoot.MaxInclTicks * toMs) + " ms (budget at 60 TPS is 16.67 ms).");
            sb.AppendLine();
            sb.AppendLine("A large \"rest\" share with small Update/GUI numbers means the frame is limited by rendering/GPU/vsync rather than by mod code.");
            sb.AppendLine();

            // ---- Unity frame phases (checkpoints, no method hooks) ----
            sb.AppendLine("## Frame phases (Unity checkpoints)");
            sb.AppendLine();
            long pf = FramePhases.Frames;
            if (!FramePhases.Installed) sb.AppendLine("Checkpoints were not installed (see the game log).");
            else if (pf == 0) sb.AppendLine("No complete frames were seen by the checkpoints.");
            else
            {
                double perFrame = 1.0 / pf * toMs;
                double scripts = FramePhases.ScriptsTicks * perFrame, wait = FramePhases.WaitTicks * perFrame;
                long fc = FramePhases.FramesWithCameras;
                double engine = fc > 0 ? FramePhases.EngineTicks * toMs / fc : 0, render = fc > 0 ? FramePhases.RenderTicks * toMs / fc : 0, gui = fc > 0 ? FramePhases.GuiTicks * toMs / fc : 0;
                // frames without camera events: everything between the last LateUpdate and the end of the frame
                long noCam = pf - fc;
                double noCamMs = noCam > 0 ? FramePhases.NoCameraEngineTicks * toMs / noCam : 0;
                double frameTotal = scripts + wait + (fc > 0 ? engine + render + gui : 0) * fc / (double)pf + noCamMs * noCam / pf;
                Func<double, string> pc = v => F(frameTotal > 0 ? v / frameTotal * 100 : 0, 1);
                double engineAvg = engine * fc / pf, renderAvg = render * fc / pf, guiAvg = gui * fc / pf, noCamAvg = noCamMs * noCam / pf;
                sb.AppendLine("Every frame is cut at fixed points. Frames measured: " + pf + " (" + fc + " with camera events). Numbers are averages per frame and include the probe's own overhead.");
                sb.AppendLine();
                sb.AppendLine("| phase | ms | % of frame | what is in it |");
                sb.AppendLine("|---|---:|---:|---|");
                sb.AppendLine("| scripts: first Update to last LateUpdate | " + F(scripts) + " | " + pc(scripts) + " | all Update/LateUpdate code, ticks, coroutines, animation. Hooked Root_Play.Update inside it: " + F(updIncl) + " ms |");
                sb.AppendLine("| engine work before drawing | " + F(engineAvg) + " | " + pc(engineAvg) + " | culling and scene preparation up to the first camera |");
                sb.AppendLine("| cameras: CPU side of drawing | " + F(renderAvg) + " | " + pc(renderAvg) + " | from the first camera culling to the last camera render |");
                sb.AppendLine("| after cameras: interface (OnGUI) | " + F(guiAvg) + " | " + pc(guiAvg) + " | UIRootOnGUI runs several times per frame. Hooked inside it: " + F(guiIncl) + " ms |");
                if (noCam > 0) sb.AppendLine("| no camera events (LateUpdate to end of frame) | " + F(noCamAvg) + " | " + pc(noCamAvg) + " | frames where Unity did not report a camera render |");
                sb.AppendLine("| end of frame to next Update: present, GPU and vsync wait | " + F(wait) + " | " + pc(wait) + " | worst " + F(FramePhases.MaxWaitTicks * toMs) + " ms |");
                sb.AppendLine("| sum | " + F(frameTotal) + " | 100.0 | Unity's own frame average was " + F(avgFrame) + " ms |");
                sb.AppendLine();
                sb.AppendLine("If the last-but-one row is large, the CPU is waiting for the graphics card or for vsync and mod code is not the limit. If \"scripts\" is much bigger than the hooked Root_Play.Update, other scripts (other mods' MonoBehaviours, coroutines) take the difference.");
                if (!FramePhases.CameraEventsSeen) sb.AppendLine("Note: no camera callbacks fired, so the split between engine, cameras and interface is not available.");
            }
            sb.AppendLine();

            sb.AppendLine("## Frame time distribution");
            sb.AppendLine();
            sb.AppendLine("| avg | p50 | p90 | p95 | p99 | max | frames >33 ms | >50 ms | >100 ms |");
            sb.AppendLine("|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
            sb.AppendLine("| " + F(avgFrame) + " | " + F(p50) + " | " + F(p90) + " | " + F(p95) + " | " + F(p99) + " | " + F(pmax) + " | " + over33 + " | " + over50 + " | " + over100 + " |");
            sb.AppendLine();

            // ---- top suspects ----
            var suspects = modList.Where(m => m.Mod != ModMap.Core && m.Total > 0).Take(5).ToList();
            sb.AppendLine("## Top suspects (excluding vanilla)");
            sb.AppendLine();
            if (suspects.Count == 0) sb.AppendLine("No non-vanilla code showed measurable cost.");
            foreach (var m in suspects)
                sb.AppendLine("- **" + Clean(m.Mod) + "**: " + F(m.Total) + " ms/s (" + F(m.Total / 10.0, 1) + " % wall); biggest single hook `" + Clean(m.TopMethod) + "` at " + F(m.TopMethodMs) + " ms/s");
            double modsTotal = modList.Where(m => m.Mod != ModMap.Core).Sum(m => m.Total);
            double coreTotal = modList.Where(m => m.Mod == ModMap.Core).Sum(m => m.Total);
            sb.AppendLine();
            sb.AppendLine("Hooked mod code in total: **" + F(modsTotal, 1) + " ms/s** (" + F(modsTotal / 10.0, 1) + " % wall). Vanilla plus everything not covered by a hook: **" + F(coreTotal, 1) + " ms/s** (" + F(coreTotal / 10.0, 1) + " % wall).");
            if (modsTotal < coreTotal * 0.15)
                sb.AppendLine("Mod code is a small part of the measured time, so no single mod is likely to be the main cause. Load comes from the amount of simulated content (things, pawns, world objects) or from vanilla systems; see the vanilla rows and the definitions table below. Mod code that runs INSIDE vanilla methods (transpilers, job toils, delegates) is counted as vanilla here.");
            sb.AppendLine();

            // ---- per mod table ----
            sb.AppendLine("## Cost per mod (code, exclusive ms per real second)");
            sb.AppendLine();
            sb.AppendLine("`content` = time spent in Tick/Update code on behalf of things and hediffs whose **definition** comes from that mod (works for XML-only mods too; it overlaps with the code columns, do not add them).");
            sb.AppendLine();
            sb.AppendLine("| # | Mod | total ms/s | %wall | Tick | Update | GUI | Harmony | calls/s | worst call ms | content ms/s | biggest hook |");
            sb.AppendLine("|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|");
            int rank = 0;
            foreach (var m in modList.Take(cfg.TopMods))
            {
                if (m.Total <= 0 && m.ContentMs <= 0) break;
                rank++;
                sb.AppendLine("| " + rank + " | " + Clean(m.Mod) + " | " + F(m.Total) + " | " + F(m.Total / 10.0, 1) + " | " + F(m.Tick) + " | " + F(m.Update) + " | " + F(m.Gui) + " | " + F(m.Harmony)
                    + " | " + F(m.Calls / secs, 0) + " | " + F(m.MaxSingleMs) + " | " + F(m.ContentMs) + " | " + Clean(m.TopMethod) + " |");
            }
            sb.AppendLine();

            // ---- top methods ----
            sb.AppendLine("## Top " + cfg.TopMethods + " hooks by exclusive cost");
            sb.AppendLine();
            sb.AppendLine("`per unit` is ms per tick for Tick rows and ms per frame for the others (Harmony rows: ms per frame).");
            sb.AppendLine();
            sb.AppendLine("`ms/s` already has the probe overhead subtracted. `~probe ms/s` is how much was subtracted from that row: if it is large compared with `ms/s`, the row is a cheap method that is called very often and its remaining value is mostly estimation noise (roughly +-0.05 us per call).");
            sb.AppendLine();
            sb.AppendLine("| # | cat | mod | method | ms/s | ~probe ms/s | %wall | per unit ms | calls/s | avg us/call | worst call ms |");
            sb.AppendLine("|---:|---|---|---|---:|---:|---:|---:|---:|---:|---:|");
            rank = 0;
            foreach (var s in slots.Take(cfg.TopMethods))
            {
                rank++;
                double ms = s.ExclTicks * toMs;
                double perUnit = s.Category == "Tick" ? ms / Math.Max(1, ticks) : ms / Math.Max(1, frames);
                double probeMs = (s.RawExclTicks - s.ExclTicks) * toMs / secs;
                string name = Clean(s.Name);
                if (s.IsRoot) name += " [root: vanilla time not covered by deeper hooks]";
                if (s.Category == "Harmony" && !string.IsNullOrEmpty(s.Extra)) name += " (" + Clean(s.Extra) + ")";
                sb.AppendLine("| " + rank + " | " + s.Category + " | " + Clean(s.Mod) + " | " + name + " | " + F(ms / secs) + " | " + F(probeMs) + " | " + F(ms / secs / 10.0, 1) + " | " + F(perUnit, 3)
                    + " | " + F(s.Calls / secs, 0) + " | " + F(ms * 1000.0 / s.Calls, 2) + " | " + F(s.MaxExclTicks * toMs) + " |");
            }
            sb.AppendLine();

            // ---- vanilla tick breakdown ----
            if (ticks > 0)
            {
                var containers = new HashSet<string>
                {
                    "TickManager.DoSingleTick", "Map.MapPreTick", "Map.MapPostTick", "World.WorldTick", "WorldObjectsHolder.WorldObjectsHolderTick",
                    "TickList.Tick", "Pawn.Tick", "Pawn.TickInterval", "Pawn_HealthTracker.HealthTick", "Pawn_HealthTracker.HealthTickInterval",
                    "Pawn_JobTracker.JobTrackerTick", "Pawn_JobTracker.JobTrackerTickInterval", "Pawn_NeedsTracker.NeedsTrackerTickInterval",
                    "Pawn_MindState.MindStateTickInterval", "ThingWithComps.Tick", "MapComponentUtility.MapComponentTick",
                    "GameComponentUtility.GameComponentTick", "WorldComponentUtility.WorldComponentTick", "LordManager.LordManagerTick",
                    "WorldObject.DoTick", "JobDriver.DriverTick", "Pawn_PathFollower.PatherTick", "PathFinder.FindPathNow",
                };
                var coreTick = slots.Where(s => s.Mod == ModMap.Core && s.Category == "Tick").OrderByDescending(s => s.ExclTicks).ToList();
                double tickWorkMs = slots.Where(s => s.Category == "Tick").Sum(s => s.ExclTicks * toMs) / ticks;
                sb.AppendLine("## Where the vanilla tick goes (ms per tick)");
                sb.AppendLine();
                sb.AppendLine("Exclusive time of vanilla methods in the Tick category. `% of tick work` is relative to " + F(tickWorkMs, 2) + " ms per tick, the sum of all exclusive Tick rows (mods included). Rows marked **remainder** are container methods (they call other hooked methods): what is shown is only the part not covered by a deeper hook, i.e. what is still unexplained.");
                sb.AppendLine();
                sb.AppendLine("| # | method | group | ms/tick | % of tick work | calls/tick | avg us/call | note |");
                sb.AppendLine("|---:|---|---|---:|---:|---:|---:|---|");
                rank = 0;
                foreach (var s in coreTick.Take(45))
                {
                    rank++;
                    double ms = s.ExclTicks * toMs;
                    sb.AppendLine("| " + rank + " | " + Clean(s.Name) + " | " + Clean(s.Family) + " | " + F(ms / ticks, 3) + " | " + F(tickWorkMs > 0 ? ms / ticks / tickWorkMs * 100 : 0, 1)
                        + " | " + F((double)s.Calls / ticks, 2) + " | " + F(s.Calls > 0 ? ms * 1000.0 / s.Calls : 0, 2) + " | " + (containers.Contains(s.Name) ? "**remainder**" : "") + " |");
                }
                double remainderMs = coreTick.Where(s => containers.Contains(s.Name)).Sum(s => s.ExclTicks * toMs) / ticks;
                sb.AppendLine();
                sb.AppendLine("Unexplained (sum of the remainder rows above): **" + F(remainderMs, 3) + " ms per tick** = " + F(tickWorkMs > 0 ? remainderMs / tickWorkMs * 100 : 0, 1) + " % of tick work.");
                sb.AppendLine();
            }

            // ---- defs ----
            sb.AppendLine("## Top " + cfg.TopDefs + " definitions by Tick/Update cost");
            sb.AppendLine();
            sb.AppendLine("Time spent in code that ran for things, comps and hediffs of this def (all mods' code included). Only hooks on Thing / ThingComp / Hediff / HediffComp are attributed here.");
            sb.AppendLine();
            sb.AppendLine("| # | kind | defName | from mod | ms/s | calls/s | avg us/call | worst call ms |");
            sb.AppendLine("|---:|---|---|---|---:|---:|---:|---:|");
            rank = 0;
            foreach (var d in defRows.Take(cfg.TopDefs))
            {
                rank++;
                double ms = d.ExclTicks * toMs;
                sb.AppendLine("| " + rank + " | " + d.Def.GetType().Name + " | " + Clean(d.Def.defName) + " | " + Clean(ModMap.ForDef(d.Def)) + " | " + F(ms / secs) + " | " + F(d.Calls / secs, 0) + " | " + F(ms * 1000.0 / d.Calls, 2) + " | " + F(d.MaxExclTicks * toMs) + " |");
            }
            sb.AppendLine();

            // ---- timeline ----
            sb.AppendLine("## Timeline (one row per second)");
            sb.AppendLine();
            sb.AppendLine("Tick/Update/GUI ms are the inclusive time of the three vanilla roots within that second (Update includes ticks). Look for seconds where max frame or GC counts jump.");
            sb.AppendLine();
            sb.AppendLine("| t s | TPS | FPS | avg frame ms | max frame ms | tick ms | update ms | gui ms | GC0/1/2 | heap MB | speed |");
            sb.AppendLine("|---:|---:|---:|---:|---:|---:|---:|---:|---|---:|---|");
            foreach (var r in ProbeSession.Rows.Take(300))
                sb.AppendLine("| " + F(r.T, 0) + " | " + r.Ticks + " | " + r.Frames + " | " + F(r.AvgFrameMs) + " | " + F(r.MaxFrameMs) + " | " + F(r.TickMs, 0) + " | " + F(r.UpdateMs, 0) + " | " + F(r.GuiMs, 0)
                    + " | " + r.Gc0 + "/" + r.Gc1 + "/" + r.Gc2 + " | " + F(r.HeapMb, 0) + " | " + r.Speed + " |");
            sb.AppendLine();

            // ---- world pawns + fixes ----
            string worldPawns = WorldPawnDump.TryWrite(dir);
            sb.AppendLine("## World pawns and fixes");
            sb.AppendLine();
            sb.AppendLine("- " + (worldPawns != null ? worldPawns + ". Details: world_pawns.md / world_pawns.csv in this folder." : "world pawn list not available"));
            string worldObjects = WorldObjectDump.TryWrite(dir);
            sb.AppendLine("- " + (worldObjects != null ? worldObjects + ". Details: world_objects.md / world_objects.csv in this folder." : "world object list not available"));
            string tickingThings = TickingThingsDump.TryWrite(dir);
            sb.AppendLine("- " + (tickingThings != null ? tickingThings + ". Details: ticking_things.md / ticking_things.csv in this folder." : "ticking things list not available"));
            if (PerfFixes.Status.Count == 0) sb.AppendLine("- fixes: none active");
            foreach (var st in PerfFixes.Status) sb.AppendLine("- " + Clean(st));
            string liveFix = PerfFixes.LiveStatus();
            if (liveFix.Length > 0) sb.AppendLine("- " + Clean(liveFix));
            sb.AppendLine();

            // ---- diagnostics ----
            sb.AppendLine("## Diagnostics");
            sb.AppendLine();
            sb.AppendLine("- Hooks installed: " + HookCatalog.PatchedOk + " ok, " + HookCatalog.PatchedFail + " failed; hooks that were actually called: " + slots.Count);
            sb.AppendLine("- Catalog build " + F(HookCatalog.BuildMs, 0) + " ms, patching " + F(HookCatalog.InstallMs, 0) + " ms");
            sb.AppendLine("- Harmony patch methods skipped (Harmony itself, this mod, Dubs Analyzer): " + HookCatalog.HarmonyPatchesSkipped);
            sb.AppendLine();
            sb.AppendLine("Hooks by family:");
            foreach (var kv in HookCatalog.FamilyCounts) if (kv.Key != "Calibration") sb.AppendLine("- " + kv.Key + ": " + kv.Value);
            if (HookCatalog.Failures.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Patch failures (first " + HookCatalog.Failures.Count + "):");
                foreach (var f in HookCatalog.Failures) sb.AppendLine("- " + Clean(f));
            }
            if (HookCatalog.Notes.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Notes:");
                foreach (var n in HookCatalog.Notes) sb.AppendLine("- " + Clean(n));
            }

            File.WriteAllText(Path.Combine(dir, "report.md"), sb.ToString(), new UTF8Encoding(false));

            // ---- CSV files (complete data) ----
            var csv = new StringBuilder();
            csv.AppendLine("mod,package_id,total_ms_s,pct_wall,tick_ms_s,update_ms_s,gui_ms_s,harmony_ms_s,calls_s,hooks_hit,worst_call_ms,content_ms_s,biggest_hook");
            foreach (var m in modList)
            {
                string pid; ModMap.PackageIdByName.TryGetValue(m.Mod, out pid);
                csv.AppendLine(string.Join(",", new[] { Csv(m.Mod), Csv(pid), F(m.Total, 3), F(m.Total / 10.0, 2), F(m.Tick, 3), F(m.Update, 3), F(m.Gui, 3), F(m.Harmony, 3), F(m.Calls / secs, 1), m.HooksHit.ToString(Inv), F(m.MaxSingleMs, 3), F(m.ContentMs, 3), Csv(m.TopMethod) }));
            }
            File.WriteAllText(Path.Combine(dir, "mods.csv"), csv.ToString(), new UTF8Encoding(false));

            csv.Clear();
            csv.AppendLine("category,family,mod,type,method,extra,calls,calls_s,excl_ms_s,excl_raw_ms_s,excl_ms_total,incl_ms_total,avg_excl_us_per_call,worst_excl_ms,worst_incl_ms");
            foreach (var s in slots)
            {
                double ms = s.ExclTicks * toMs;
                double rawMs = s.RawExclTicks * toMs;
                csv.AppendLine(string.Join(",", new[] { s.Category, Csv(s.Family), Csv(s.Mod), Csv(s.Type), Csv(s.Name), Csv(s.Extra), s.Calls.ToString(Inv), F(s.Calls / secs, 1), F(ms / secs, 4), F(rawMs / secs, 4), F(ms, 3), F(s.InclTicks * toMs, 3), F(ms * 1000.0 / s.Calls, 3), F(s.MaxExclTicks * toMs, 3), F(s.MaxInclTicks * toMs, 3) }));
            }
            File.WriteAllText(Path.Combine(dir, "methods.csv"), csv.ToString(), new UTF8Encoding(false));

            csv.Clear();
            csv.AppendLine("kind,def_name,from_mod,calls,calls_s,ms_s,ms_total,avg_us_per_call,worst_ms");
            foreach (var d in defRows)
            {
                double ms = d.ExclTicks * toMs;
                csv.AppendLine(string.Join(",", new[] { d.Def.GetType().Name, Csv(d.Def.defName), Csv(ModMap.ForDef(d.Def)), d.Calls.ToString(Inv), F(d.Calls / secs, 1), F(ms / secs, 4), F(ms, 3), F(ms * 1000.0 / d.Calls, 3), F(d.MaxExclTicks * toMs, 3) }));
            }
            File.WriteAllText(Path.Combine(dir, "defs.csv"), csv.ToString(), new UTF8Encoding(false));

            csv.Clear();
            csv.AppendLine("t_s,ticks,frames,avg_frame_ms,max_frame_ms,tick_ms,update_ms,gui_ms,gc0,gc1,gc2,heap_mb,speed");
            foreach (var r in ProbeSession.Rows)
                csv.AppendLine(string.Join(",", new[] { F(r.T, 1), r.Ticks.ToString(Inv), r.Frames.ToString(Inv), F(r.AvgFrameMs, 3), F(r.MaxFrameMs, 3), F(r.TickMs, 3), F(r.UpdateMs, 3), F(r.GuiMs, 3), r.Gc0.ToString(Inv), r.Gc1.ToString(Inv), r.Gc2.ToString(Inv), F(r.HeapMb, 1), r.Speed }));
            File.WriteAllText(Path.Combine(dir, "timeline.csv"), csv.ToString(), new UTF8Encoding(false));

            return dir;
        }
    }
}
