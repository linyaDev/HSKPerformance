using System;
using UnityEngine;
using Verse;

namespace HSKPerfProbe
{
    /// <summary>
    /// Plain Unity behaviour: runs outside RimWorld's own Update/OnGUI, so starting and stopping
    /// (patch / unpatch) never happens while a profiled method is on the call stack.
    /// </summary>
    public sealed class ProbeDriver : MonoBehaviour
    {
        GUIStyle style;
        Texture2D bg;

        void Update()
        {
            try
            {
                bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
                if (ctrl && Input.GetKeyDown(KeyCode.F9))
                {
                    switch (ProbeSession.State)
                    {
                        case SessionState.Idle: ProbeSession.Start(); break;
                        case SessionState.Removing: break; // still cleaning up, ignore
                        default: ProbeSession.Stop("hotkey"); break;
                    }
                }
                if (ctrl && Input.GetKeyDown(KeyCode.F10))
                {
                    string msg = WorldPawnDump.WriteStandalone();
                    if (msg == null) msg = "World pawn dump is only available inside a running game.";
                    else Log.Message("[HSKPerfProbe] " + msg);
                    string live = PerfFixes.LiveStatus();
                    if (live.Length > 0) Log.Message("[HSKPerfProbe] " + live);
                    ProbeSession.Notify(msg + (live.Length > 0 ? "   |   " + live : ""));
                }
                ProbeSession.OnFrame(Time.unscaledDeltaTime, Time.realtimeSinceStartup);
            }
            catch (Exception e)
            {
                Log.Error("[HSKPerfProbe] driver error: " + e);
                ProbeSession.Abort(e.Message);
            }
        }

        void OnGUI()
        {
            if (Event.current == null || Event.current.type != EventType.Repaint) return;
            string text = ProbeSession.Overlay(Time.realtimeSinceStartup);
            if (text == null) return;

            if (style == null)
            {
                style = new GUIStyle(GUI.skin.label) { fontSize = 14, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft };
                style.normal.textColor = Color.white;
                bg = new Texture2D(1, 1);
                bg.SetPixel(0, 0, new Color(0f, 0f, 0f, 0.65f));
                bg.Apply();
            }
            var size = style.CalcSize(new GUIContent(text));
            var r = new Rect(8f, 8f, size.x + 16f, 26f);
            GUI.DrawTexture(r, bg);
            GUI.Label(new Rect(r.x + 8f, r.y, r.width, r.height), text, style);
        }
    }

    [StaticConstructorOnStartup]
    public static class ProbeStartup
    {
        static ProbeStartup()
        {
            ProbeCore.MainThreadId = Environment.CurrentManagedThreadId;
            var go = new GameObject("HSKPerfProbe");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<ProbeDriver>();
            Log.Message("[HSKPerfProbe] ready. Ctrl+F9 = start/stop a capture, Ctrl+F10 = list world pawns that are ticked every tick. Reports go to " + ProbeConfig.Dir);
            PerfFixes.Init();
        }
    }
}
