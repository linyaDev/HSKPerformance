using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace HSKPerfProbe
{
    /// <summary>
    /// Every world object is ticked on every tick by WorldObjectsHolder.WorldObjectsHolderTick: WorldObject.DoTick (wrapper),
    /// then Tick(), which just calls CompTick() on each of its comps. Most settlements, sites and camps have nothing to do
    /// in there. This lists what each object actually does, so it is clear which ones only pay the wrapper cost.
    ///
    /// Classification of an override, by the size of its IL: none (not overridden), trivial (up to 8 bytes: it just calls
    /// base) or real (more than that, so it does work of its own).
    /// </summary>
    public static class WorldObjectDump
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        const int TrivialIl = 8;
        // measured in the 25.09 14:47 report: DoTick wrapper 0.23 us + Tick() loop 0.18 us per object per tick
        const double PerObjectUs = 0.41;

        public const int None = 0, Trivial = 1, Real = 2;

        sealed class Row
        {
            public string Class, ClassFull, Def, Faction, Label, Tile, TickingComps, IntervalComps, AllComps;
            public int TickKind, IntervalKind, CompCount, TickingCompCount, IntervalCompCount;
            public bool Holder;
            /// <summary>Nothing runs every tick: no real Tick and no comp with a real CompTick. Interval-only work does not count.</summary>
            public bool Inert { get { return TickKind < Real && TickingCompCount == 0; } }
            public bool IntervalOnly { get { return Inert && (IntervalKind == Real || IntervalCompCount > 0); } }
        }

        static string Clean(string s) { return (s ?? "").Replace('|', '/').Replace('\n', ' ').Replace('\r', ' ').Replace(',', ';'); }
        static string Csv(string s)
        {
            s = s ?? "";
            return s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        }
        static string KindName(int k) { return k == Real ? "real" : (k == Trivial ? "trivial" : "no"); }

        /// <summary>How much work the most derived override of the named method does compared with the base declaration.</summary>
        internal static int OverrideKind(Type t, string name, Type baseType, Type[] args)
        {
            try
            {
                var m = t.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, args, null);
                if (m == null || m.DeclaringType == baseType) return None;
                int il = HookCatalog.ILSize(m);
                return il >= 0 && il <= TrivialIl ? Trivial : Real;
            }
            catch { return None; }
        }

        public static string TryWrite(string dir)
        {
            try
            {
                if (Current.ProgramState != ProgramState.Playing || Find.WorldObjects == null) return null;
                Directory.CreateDirectory(dir);

                var noArgs = Type.EmptyTypes;
                var intArg = new[] { typeof(int) };
                var rows = new List<Row>();
                foreach (var obj in Find.WorldObjects.AllWorldObjects.ToList())
                {
                    try
                    {
                        var t = obj.GetType();
                        var r = new Row();
                        r.Class = t.Name;
                        r.ClassFull = t.FullName;
                        r.Def = obj.def != null ? obj.def.defName : "?";
                        r.Faction = obj.Faction != null ? obj.Faction.Name : "(none)";
                        try { r.Label = obj.LabelCap; } catch { r.Label = "?"; }
                        try { r.Tile = obj.Tile.ToString(); } catch { r.Tile = "?"; }
                        r.TickKind = OverrideKind(t, "Tick", typeof(WorldObject), noArgs);
                        r.IntervalKind = OverrideKind(t, "TickInterval", typeof(WorldObject), intArg);
                        r.Holder = obj is IThingHolder;

                        var all = new List<string>();
                        var ticking = new List<string>();
                        var interval = new List<string>();
                        foreach (var comp in obj.AllComps)
                        {
                            var ct = comp.GetType();
                            all.Add(ct.Name);
                            if (OverrideKind(ct, "CompTick", typeof(WorldObjectComp), noArgs) == Real) ticking.Add(ct.Name);
                            if (OverrideKind(ct, "CompTickInterval", typeof(WorldObjectComp), intArg) == Real) interval.Add(ct.Name);
                        }
                        r.CompCount = all.Count;
                        r.TickingCompCount = ticking.Count;
                        r.IntervalCompCount = interval.Count;
                        r.AllComps = string.Join("; ", all.ToArray());
                        r.TickingComps = string.Join("; ", ticking.ToArray());
                        r.IntervalComps = string.Join("; ", interval.ToArray());
                        rows.Add(r);
                    }
                    catch (Exception e)
                    {
                        rows.Add(new Row { Class = "?", ClassFull = "?", Def = "?", Faction = "?", Label = "could not be described: " + e.GetType().Name, Tile = "?", TickingComps = "", AllComps = "" });
                    }
                }

                int inert = rows.Count(r => r.Inert);
                int intervalOnly = rows.Count(r => r.IntervalOnly);
                string summary = "world objects: " + rows.Count + " go through DoTick every tick, " + inert + " of them do nothing per tick (wrapper only, about "
                    + (inert * PerObjectUs / 1000.0).ToString("F2", Inv) + " ms per tick; " + intervalOnly + " of those still work once per interval), "
                    + (rows.Count - inert) + " do real work every tick";

                WriteMarkdown(dir, rows, summary, inert);
                WriteCsv(dir, rows);
                return summary;
            }
            catch (Exception e)
            {
                try { Log.Error("[HSKPerfProbe] world object dump failed: " + e); } catch { }
                return null;
            }
        }

        static void Group(StringBuilder sb, string title, string col, IEnumerable<KeyValuePair<string, int[]>> rows, string[] heads)
        {
            sb.AppendLine("**" + title + "**");
            sb.AppendLine();
            sb.AppendLine("| " + col + " | " + string.Join(" | ", heads) + " |");
            sb.AppendLine("|---|" + string.Join("|", heads.Select(h => "---:")) + "|");
            foreach (var kv in rows) sb.AppendLine("| " + Clean(kv.Key) + " | " + string.Join(" | ", kv.Value.Select(v => v.ToString(Inv)).ToArray()) + " |");
            sb.AppendLine();
        }

        static void WriteMarkdown(string dir, List<Row> rows, string summary, int inert)
        {
            int limit = ProbeConfig.Load().WorldPawnRows;
            var sb = new StringBuilder(24 * 1024);
            sb.AppendLine("# World objects that are ticked every tick");
            sb.AppendLine();
            sb.AppendLine("Written " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", Inv) + ".");
            sb.AppendLine();
            sb.AppendLine("- " + summary);
            sb.AppendLine("- `WorldObjectsHolder.WorldObjectsHolderTick` copies the list of all world objects and calls `WorldObject.DoTick()` on each, every tick. `DoTick` calls `Tick()` (which only calls `CompTick()` on the object's comps), calls `TickInterval()` every few ticks, and for objects that hold things (`IThingHolder`) walks their child holders. Settlement trade stock is not ticked (`dontTickContents`).");
            sb.AppendLine("- An override counts as **real** when its IL is longer than " + TrivialIl + " bytes, **trivial** when it is not (it only calls base). This is an estimate from the size of the code, not a proof of what it does.");
            sb.AppendLine("- `TickInterval` (and `CompTickInterval`) is NOT called every tick: the game calls it once per 15 ticks while the world map is not open (every tick while it is). Only a real `Tick` or a real `CompTick` costs something every tick. An object is **inert** when it has neither.");
            sb.AppendLine("- Measured cost of the wrapper: about " + PerObjectUs.ToString("F2", Inv) + " us per object per tick (25.09 report), so inert objects cost only that.");
            sb.AppendLine();

            sb.AppendLine("## Summary");
            sb.AppendLine();
            sb.AppendLine("| | objects |");
            sb.AppendLine("|---|---:|");
            sb.AppendLine("| all world objects | " + rows.Count + " |");
            sb.AppendLine("| **inert every tick** (only the DoTick wrapper) | " + inert + " |");
            sb.AppendLine("| &nbsp;&nbsp;of which work once per interval (real `TickInterval` or `CompTickInterval`) | " + rows.Count(r => r.IntervalOnly) + " |");
            sb.AppendLine("| override `Tick` with real code (works every tick) | " + rows.Count(r => r.TickKind == Real) + " |");
            sb.AppendLine("| have a comp with a real `CompTick` (works every tick) | " + rows.Count(r => r.TickingCompCount > 0) + " |");
            sb.AppendLine("| override `TickInterval` with real code | " + rows.Count(r => r.IntervalKind == Real) + " |");
            sb.AppendLine("| have a comp with a real `CompTickInterval` | " + rows.Count(r => r.IntervalCompCount > 0) + " |");
            sb.AppendLine("| hold things (`IThingHolder`, walked every tick) | " + rows.Count(r => r.Holder) + " |");
            sb.AppendLine();

            Group(sb, "By class", "class", rows.GroupBy(r => r.Class).OrderByDescending(g => g.Count()).Take(25)
                .Select(g => new KeyValuePair<string, int[]>(g.Key, new[] { g.Count(), g.Count(r => r.Inert), g.Count(r => r.TickKind == Real), g.Count(r => r.TickingCompCount > 0) })),
                new[] { "objects", "inert", "real Tick", "ticking comps" });
            Group(sb, "By faction", "faction", rows.GroupBy(r => r.Faction).OrderByDescending(g => g.Count()).Take(20)
                .Select(g => new KeyValuePair<string, int[]>(g.Key, new[] { g.Count(), g.Count(r => r.Inert) })),
                new[] { "objects", "inert" });
            Group(sb, "By definition", "def", rows.GroupBy(r => r.Def).OrderByDescending(g => g.Count()).Take(20)
                .Select(g => new KeyValuePair<string, int[]>(g.Key, new[] { g.Count(), g.Count(r => r.Inert) })),
                new[] { "objects", "inert" });

            Func<Func<Row, string>, Dictionary<string, int>> countComps = pick =>
            {
                var d = new Dictionary<string, int>();
                foreach (var r in rows)
                    foreach (var c in (pick(r) ?? "").Split(new[] { "; " }, StringSplitOptions.RemoveEmptyEntries))
                    { int n; d.TryGetValue(c, out n); d[c] = n + 1; }
                return d;
            };
            Group(sb, "Comps with a real CompTick (work every tick)", "comp class", countComps(r => r.TickingComps).OrderByDescending(kv => kv.Value).Take(25)
                .Select(kv => new KeyValuePair<string, int[]>(kv.Key, new[] { kv.Value })), new[] { "objects" });
            Group(sb, "Comps with a real CompTickInterval (work once per interval)", "comp class", countComps(r => r.IntervalComps).OrderByDescending(kv => kv.Value).Take(25)
                .Select(kv => new KeyValuePair<string, int[]>(kv.Key, new[] { kv.Value })), new[] { "objects" });

            var active = rows.Where(r => !r.Inert).ToList();
            sb.AppendLine("## Objects that work every tick (" + Math.Min(limit, active.Count) + " of " + active.Count + "; the CSV has all " + rows.Count + ")");
            sb.AppendLine();
            sb.AppendLine("| # | class | def | faction | label | tile | Tick | TickInterval | comps every tick | comps per interval |");
            sb.AppendLine("|---:|---|---|---|---|---|---|---|---|---|");
            int i = 0;
            foreach (var r in active.Take(limit))
            {
                i++;
                sb.AppendLine("| " + i + " | " + Clean(r.Class) + " | " + Clean(r.Def) + " | " + Clean(r.Faction) + " | " + Clean(r.Label) + " | " + Clean(r.Tile) + " | " + KindName(r.TickKind)
                    + " | " + KindName(r.IntervalKind) + " | " + Clean(r.TickingComps) + " | " + Clean(r.IntervalComps) + " |");
            }
            File.WriteAllText(Path.Combine(dir, "world_objects.md"), sb.ToString(), new UTF8Encoding(false));
        }

        static void WriteCsv(string dir, List<Row> rows)
        {
            var sb = new StringBuilder();
            sb.AppendLine("class,class_full,def,faction,label,tile,inert_every_tick,interval_only,tick,tick_interval,holder,comp_count,per_tick_comp_count,interval_comp_count,per_tick_comps,interval_comps,all_comps");
            foreach (var r in rows)
                sb.AppendLine(string.Join(",", new[] { Csv(r.Class), Csv(r.ClassFull), Csv(r.Def), Csv(r.Faction), Csv(r.Label), Csv(r.Tile), r.Inert ? "1" : "0", r.IntervalOnly ? "1" : "0", KindName(r.TickKind), KindName(r.IntervalKind),
                    r.Holder ? "1" : "0", r.CompCount.ToString(Inv), r.TickingCompCount.ToString(Inv), r.IntervalCompCount.ToString(Inv), Csv(r.TickingComps), Csv(r.IntervalComps), Csv(r.AllComps) }));
            File.WriteAllText(Path.Combine(dir, "world_objects.csv"), sb.ToString(), new UTF8Encoding(false));
        }
    }
}
