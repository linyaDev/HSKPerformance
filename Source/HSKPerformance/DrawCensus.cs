using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Verse;

namespace HSKPerformance
{
    /// <summary>
    /// What the game has to draw every frame, counted from its own data structures (no hooks, only used for reports and Ctrl+F10):
    /// - MapDrawer.DrawMapMesh calls Graphics.DrawMesh once per finished sub mesh of every layer of every 17x17 section that overlaps the view;
    ///   the count per layer type shows which layers are the cost.
    /// - DynamicDrawManager.DrawDynamicThings draws the things that are not baked into a section mesh; the count per class shows what they are.
    /// </summary>
    public static class DrawCensus
    {
        static FieldInfo sectionsField, layersField;

        public static string Describe(int top = 12)
        {
            var map = Find.CurrentMap;
            if (map == null) return "no current map";
            var view = Find.CameraDriver.CurrentViewRect.ExpandedBy(1).ClipInsideMap(map);
            var text = new System.Text.StringBuilder();

            // ---- map mesh
            try
            {
                if (sectionsField == null) sectionsField = typeof(MapDrawer).GetField("sections", BindingFlags.Instance | BindingFlags.NonPublic);
                if (layersField == null) layersField = typeof(Section).GetField("layers", BindingFlags.Instance | BindingFlags.NonPublic);
                var sections = (Section[,])sectionsField.GetValue(map.mapDrawer);
                int total = sections.GetLength(0) * sections.GetLength(1), inView = 0, calls = 0, layerCount = 0;
                var perLayer = new Dictionary<string, int[]>();   // layer type -> {draw mesh calls, layers, vertices}
                foreach (var section in sections)
                {
                    if (!view.Overlaps(section.Bounds)) continue;
                    inView++;
                    var layers = (System.Collections.IList)layersField.GetValue(section);
                    foreach (var o in layers)
                    {
                        var layer = (MapDrawLayer)o;
                        layerCount++;
                        int[] a;
                        string name = layer.GetType().Name;
                        if (!perLayer.TryGetValue(name, out a)) { a = new int[3]; perLayer[name] = a; }
                        a[1]++;
                        if (!layer.Visible) continue;
                        foreach (var sub in layer.subMeshes)
                        {
                            if (sub.finalized && !sub.disabled) { a[0]++; calls++; a[2] += sub.verts.Count; }
                        }
                    }
                }
                text.Append("map mesh: ").Append(inView).Append(" of ").Append(total).Append(" sections in view, ").Append(layerCount).Append(" layers, about ")
                    .Append(calls).Append(" Graphics.DrawMesh calls per frame; by layer: ")
                    .Append(string.Join(", ", perLayer.Where(kv => kv.Value[0] > 0).OrderByDescending(kv => kv.Value[0]).Take(top)
                        .Select(kv => kv.Key + " " + kv.Value[0] + " (" + (kv.Value[2] / 1000).ToString(CultureInfo.InvariantCulture) + "k verts)").ToArray()));
            }
            catch (Exception e) { text.Append("map mesh: census failed (" + e.GetType().Name + ": " + e.Message + ")"); }

            // ---- dynamic things
            try
            {
                var things = map.dynamicDrawManager.DrawThings;
                var byClass = new Dictionary<string, int>();
                int inViewCount = 0;
                for (int i = 0; i < things.Count; i++)
                {
                    var t = things[i];
                    if (!view.Contains(t.Position)) continue;
                    inViewCount++;
                    string name = t.GetType().Name;
                    int c; byClass.TryGetValue(name, out c); byClass[name] = c + 1;
                }
                text.Append(" | dynamic things: ").Append(things.Count).Append(" registered, ").Append(inViewCount).Append(" in view; by class: ")
                    .Append(string.Join(", ", byClass.OrderByDescending(kv => kv.Value).Take(top).Select(kv => kv.Key + " " + kv.Value).ToArray()));
            }
            catch (Exception e) { text.Append(" | dynamic things: census failed (" + e.GetType().Name + ": " + e.Message + ")"); }
            return text.ToString();
        }
    }
}
