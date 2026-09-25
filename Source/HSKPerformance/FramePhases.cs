using System;
using System.Diagnostics;
using System.Collections;
using UnityEngine;
using Verse;

namespace HSKPerformance
{
    /// <summary>
    /// Splits every Unity frame at fixed checkpoints, without hooking any method: the first script of the frame,
    /// the last LateUpdate, the first camera culling, the last camera render, and the end of the frame.
    /// The gap between the end of one frame and the first script of the next is where Unity presents the frame
    /// and waits for the GPU / vsync.
    /// </summary>
    public static class FramePhases
    {
        // checkpoints of the frame in progress (Stopwatch ticks, 0 = not reached yet)
        static long t0, t1, t2, t3, t4;

        // sums over recorded frames
        public static long Frames, FramesWithCameras;
        public static long ScriptsTicks, EngineTicks, RenderTicks, GuiTicks, WaitTicks;
        public static long NoCameraEngineTicks; // frames where no camera event fired: everything from LateUpdate to the end of the frame
        public static long MaxWaitTicks;
        public static bool Installed, CameraEventsSeen;

        public static void Reset()
        {
            Frames = FramesWithCameras = 0;
            ScriptsTicks = EngineTicks = RenderTicks = GuiTicks = WaitTicks = NoCameraEngineTicks = MaxWaitTicks = 0;
            t0 = t1 = t2 = t3 = t4 = 0;
        }

        public static void Install(GameObject go)
        {
            try
            {
                go.AddComponent<FrameStartProbe>();
                go.AddComponent<FrameEndProbe>();
                Camera.onPreCull += OnPreCull;
                Camera.onPostRender += OnPostRender;
                Installed = true;
            }
            catch (Exception e)
            {
                Log.Warning("[HSK Performance] frame checkpoints not installed: " + e.Message);
            }
        }

        static bool Recording { get { return ProbeSession.State == SessionState.Recording; } }

        internal static void FrameStart()
        {
            long now = Stopwatch.GetTimestamp();
            if (Recording && t0 != 0 && t1 != 0 && t4 != 0) Commit(now);
            t0 = now; t1 = t2 = t3 = t4 = 0;
        }

        internal static void ScriptsEnd() { t1 = Stopwatch.GetTimestamp(); }
        internal static void FrameEnd() { t4 = Stopwatch.GetTimestamp(); }

        static void OnPreCull(Camera cam)
        {
            if (t2 == 0) t2 = Stopwatch.GetTimestamp();
            CameraEventsSeen = true;
        }

        static void OnPostRender(Camera cam) { t3 = Stopwatch.GetTimestamp(); }

        static void Commit(long nextStart)
        {
            Frames++;
            ScriptsTicks += t1 - t0;
            long wait = Math.Max(0, nextStart - t4);
            WaitTicks += wait;
            if (wait > MaxWaitTicks) MaxWaitTicks = wait;
            if (t2 != 0 && t3 != 0 && t2 >= t1 && t3 >= t2 && t4 >= t3)
            {
                FramesWithCameras++;
                EngineTicks += t2 - t1;
                RenderTicks += t3 - t2;
                GuiTicks += t4 - t3;
            }
            else NoCameraEngineTicks += Math.Max(0, t4 - t1);
        }
    }

    [DefaultExecutionOrder(-32000)]
    public sealed class FrameStartProbe : MonoBehaviour
    {
        void Start() { StartCoroutine(EndOfFrameLoop()); }

        void Update() { FramePhases.FrameStart(); }

        IEnumerator EndOfFrameLoop()
        {
            var wait = new WaitForEndOfFrame();
            while (true)
            {
                yield return wait;
                FramePhases.FrameEnd();
            }
        }
    }

    [DefaultExecutionOrder(32000)]
    public sealed class FrameEndProbe : MonoBehaviour
    {
        void LateUpdate() { FramePhases.ScriptsEnd(); }
    }
}
