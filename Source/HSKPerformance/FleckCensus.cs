using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Verse;

namespace HSKPerformance
{
    /// <summary>
    /// Counts the visual effects (flecks) that are alive on the current map, by FleckDef. FleckManager.FleckManagerTick walks every one of
    /// them on every tick, so this shows what the cost of that method is made of. Reads private lists by reflection, so it is only used
    /// for reports and the Ctrl+F10 dump, never per tick.
    /// </summary>
    public static class FleckCensus
    {
        static readonly Dictionary<Type, FieldInfo[]> listFields = new Dictionary<Type, FieldInfo[]>();
        static readonly Dictionary<Type, Func<object, string>> defNamers = new Dictionary<Type, Func<object, string>>();

        public static string Describe(int top = 10)
        {
            var map = Find.CurrentMap;
            if (map == null || map.flecks == null) return "no current map";
            var byDef = new Dictionary<string, int>();
            var bySystem = new Dictionary<string, int>();
            int total = 0;
            foreach (var system in map.flecks.Systems)
            {
                int inSystem = 0;
                foreach (var f in ListFields(system.GetType()))
                {
                    var list = f.GetValue(system) as IList;
                    if (list == null) continue;
                    inSystem += list.Count;
                    for (int i = 0; i < list.Count; i++)
                    {
                        object item = list[i];
                        string name = NameOf(item);
                        int c; byDef.TryGetValue(name, out c); byDef[name] = c + 1;
                    }
                }
                bySystem[system.GetType().Name] = inSystem;
                total += inSystem;
            }
            var sb = new System.Text.StringBuilder();
            sb.Append(total.ToString(CultureInfo.InvariantCulture)).Append(" alive; systems: ");
            sb.Append(string.Join(", ", bySystem.Where(kv => kv.Value > 0).OrderByDescending(kv => kv.Value).Select(kv => kv.Key + " " + kv.Value).ToArray()));
            sb.Append("; top defs: ");
            sb.Append(string.Join(", ", byDef.OrderByDescending(kv => kv.Value).Take(top).Select(kv => kv.Key + " " + kv.Value).ToArray()));
            return sb.ToString();
        }

        static FieldInfo[] ListFields(Type systemType)
        {
            FieldInfo[] cached;
            if (listFields.TryGetValue(systemType, out cached)) return cached;
            var found = new List<FieldInfo>();
            for (var t = systemType; t != null; t = t.BaseType)
            {
                foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly))
                {
                    if (f.Name == "dataRealtime" || f.Name == "dataGametime") found.Add(f);
                }
            }
            cached = found.ToArray();
            listFields[systemType] = cached;
            return cached;
        }

        // The element is a struct that holds the def either directly (FleckStatic.def) or inside baseData (FleckThrown, FleckSplash, ...)
        static string NameOf(object item)
        {
            if (item == null) return "?";
            var t = item.GetType();
            Func<object, string> namer;
            if (!defNamers.TryGetValue(t, out namer))
            {
                var direct = t.GetField("def", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var baseData = t.GetField("baseData", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var inner = baseData != null ? baseData.FieldType.GetField("def", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) : null;
                if (direct != null) namer = o => { var d = direct.GetValue(o) as Def; return d != null ? d.defName : "?"; };
                else if (baseData != null && inner != null) namer = o => { var d = inner.GetValue(baseData.GetValue(o)) as Def; return d != null ? d.defName : "?"; };
                else namer = o => t.Name;
                defNamers[t] = namer;
            }
            try { return namer(item); } catch { return "?"; }
        }
    }
}
