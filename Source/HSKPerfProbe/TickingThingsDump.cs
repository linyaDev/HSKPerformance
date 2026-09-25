using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using RimWorld;
using Verse;

namespace HSKPerfProbe
{
    /// <summary>
    /// Inventory of the things that the tick manager processes: everything on the maps whose def has tickerType Normal
    /// gets Tick() called on EVERY tick (Thing.DoTick), Rare things every 250 ticks, Long things every 2000.
    /// The camera only changes how often the additional TickInterval() runs (1-15 ticks).
    /// The interesting group is Normal tickers that have no real per-tick code: they cost dispatch and an empty comp loop
    /// on every single tick and do nothing with it.
    /// </summary>
    public static class TickingThingsDump
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        const int Real = WorldObjectDump.Real;

        sealed class Row
        {
            public string Def, Class, Mod, Ticker, PerTickComps, IntervalComps, RareComps, LongComps;
            public int OnMap, HeldByPawns, ClassTick, ClassInterval;
            public int PerTickCompCount, IntervalCompCount;
            public bool Normal;
            public bool DoesPerTickWork { get { return ClassTick == Real || PerTickCompCount > 0; } }
            public bool Idle { get { return Normal && !DoesPerTickWork; } }
            public int Total { get { return OnMap + HeldByPawns; } }
        }

        static string Clean(string s) { return (s ?? "").Replace('|', '/').Replace('\n', ' ').Replace('\r', ' ').Replace(',', ';'); }
        static string Csv(string s)
        {
            s = s ?? "";
            return s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        }
        static string KindName(int k) { return k == Real ? "real" : (k == WorldObjectDump.Trivial ? "trivial" : "no"); }

        /// <summary>
        /// Tick/TickInterval are declared in Entity (empty), and ThingWithComps has the shared base implementation.
        /// Anything declared in a class below those counts as the class's own code.
        /// </summary>
        internal static int ThingKind(Type t, string name, Type[] args)
        {
            try
            {
                var m = t.GetMethod(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic, null, args, null);
                if (m == null) return WorldObjectDump.None;
                var d = m.DeclaringType;
                if (d == typeof(Entity) || d == typeof(Thing) || d == typeof(ThingWithComps)) return WorldObjectDump.None;
                int il = HookCatalog.ILSize(m);
                return il >= 0 && il <= 8 ? WorldObjectDump.Trivial : Real;
            }
            catch { return WorldObjectDump.None; }
        }

        public static string TryWrite(string dir)
        {
            try
            {
                if (Current.ProgramState != ProgramState.Playing || Find.Maps == null) return null;
                Directory.CreateDirectory(dir);

                var noArgs = Type.EmptyTypes;
                var intArg = new[] { typeof(int) };
                var rows = new Dictionary<string, Row>();
                int mapThings = 0, neverThings = 0;

                Action<Thing, bool> add = (thing, held) =>
                {
                    var def = thing.def;
                    if (def == null || def.tickerType == TickerType.Never) return;
                    Row r;
                    if (!rows.TryGetValue(def.defName, out r))
                    {
                        r = new Row();
                        r.Def = def.defName;
                        var t = thing.GetType();
                        r.Class = t.Name;
                        r.Mod = ModMap.ForDef(def);
                        r.Ticker = def.tickerType.ToString();
                        r.Normal = def.tickerType == TickerType.Normal;
                        r.ClassTick = ThingKind(t, "Tick", noArgs);
                        r.ClassInterval = ThingKind(t, "TickInterval", intArg);
                        var perTick = new List<string>(); var interval = new List<string>(); var rare = new List<string>(); var lng = new List<string>();
                        var twc = thing as ThingWithComps;
                        if (twc != null && twc.AllComps != null)
                        {
                            foreach (var c in twc.AllComps)
                            {
                                var ct = c.GetType();
                                if (WorldObjectDump.OverrideKind(ct, "CompTick", typeof(ThingComp), noArgs) == Real) perTick.Add(ct.Name);
                                if (WorldObjectDump.OverrideKind(ct, "CompTickInterval", typeof(ThingComp), intArg) == Real) interval.Add(ct.Name);
                                if (WorldObjectDump.OverrideKind(ct, "CompTickRare", typeof(ThingComp), noArgs) == Real) rare.Add(ct.Name);
                                if (WorldObjectDump.OverrideKind(ct, "CompTickLong", typeof(ThingComp), noArgs) == Real) lng.Add(ct.Name);
                            }
                        }
                        r.PerTickComps = string.Join("; ", perTick.ToArray()); r.PerTickCompCount = perTick.Count;
                        r.IntervalComps = string.Join("; ", interval.ToArray()); r.IntervalCompCount = interval.Count;
                        r.RareComps = string.Join("; ", rare.ToArray());
                        r.LongComps = string.Join("; ", lng.ToArray());
                        rows[def.defName] = r;
                    }
                    if (held) r.HeldByPawns++; else r.OnMap++;
                };

                foreach (var map in Find.Maps)
                {
                    var all = map.listerThings.AllThings;
                    for (int i = 0; i < all.Count; i++)
                    {
                        mapThings++;
                        var th = all[i];
                        if (th.def != null && th.def.tickerType == TickerType.Never) neverThings++;
                        try { add(th, false); } catch { }
                    }
                    // things carried, worn or wielded by pawns are ticked through the pawn (holder walk), not through the map list
                    foreach (var pawn in map.mapPawns.AllPawns.ToList())
                    {
                        try
                        {
                            if (pawn.inventory != null) foreach (var th in pawn.inventory.innerContainer.ToList()) add(th, true);
                            if (pawn.equipment != null) foreach (var th in pawn.equipment.AllEquipmentListForReading.ToList()) add(th, true);
                            if (pawn.apparel != null) foreach (var th in pawn.apparel.WornApparel.ToList()) add(th, true);
                        }
                        catch { }
                    }
                }

                var list = rows.Values.OrderByDescending(r => r.Total).ToList();
                int normalTotal = list.Where(r => r.Normal).Sum(r => r.Total);
                int idle = list.Where(r => r.Idle).Sum(r => r.Total);
                string summary = "ticking things: " + normalTotal + " Normal (Tick every tick), " + idle + " of them have no per-tick code of their own; " + mapThings + " things on the maps in total";

                WriteMarkdown(dir, list, summary, mapThings, neverThings, normalTotal, idle);
                WriteCsv(dir, list);
                return summary;
            }
            catch (Exception e)
            {
                try { Log.Error("[HSKPerfProbe] ticking things dump failed: " + e); } catch { }
                return null;
            }
        }

        static void Table(StringBuilder sb, string title, string col, IEnumerable<KeyValuePair<string, int[]>> rows, string[] heads)
        {
            sb.AppendLine("**" + title + "**");
            sb.AppendLine();
            sb.AppendLine("| " + col + " | " + string.Join(" | ", heads) + " |");
            sb.AppendLine("|---|" + string.Join("|", heads.Select(h => "---:")) + "|");
            foreach (var kv in rows) sb.AppendLine("| " + Clean(kv.Key) + " | " + string.Join(" | ", kv.Value.Select(v => v.ToString(Inv)).ToArray()) + " |");
            sb.AppendLine();
        }

        static void WriteMarkdown(string dir, List<Row> list, string summary, int mapThings, int neverThings, int normalTotal, int idle)
        {
            int limit = ProbeConfig.Load().WorldPawnRows;
            var sb = new StringBuilder(32 * 1024);
            sb.AppendLine("# Things that are ticked");
            sb.AppendLine();
            sb.AppendLine("Written " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", Inv) + ".");
            sb.AppendLine();
            sb.AppendLine("- " + summary);
            sb.AppendLine("- `Thing.DoTick` (1.6): a def with `tickerType` **Normal** gets `Tick()` called on every tick, **Rare** every 250 ticks, **Long** every 2000. The camera only changes how often the additional `TickInterval()` runs (every 1-4 ticks for things in view, every 15 otherwise). For `ThingWithComps`, `Tick()` calls `CompTick()` on each comp.");
            sb.AppendLine("- **Idle** means: a Normal ticker whose class has no real `Tick` override and none of whose comps has a real `CompTick`. It still costs the tick-list dispatch (about 0.8 us) and the base `ThingWithComps.Tick` (about 0.5 us) on every tick and does nothing with it. \"Real\" is judged by the size of the IL (more than 8 bytes), so it is an estimate.");
            sb.AppendLine("- Cross-check: the profiler reports about 640 `ThingWithComps.Tick` calls per tick. Compare with the Normal total below (things carried or worn by pawns are included, they are ticked through the pawn).");
            sb.AppendLine("- Things inside containers, caskets or other holders are not listed unless a pawn carries them.");
            sb.AppendLine();

            sb.AppendLine("## Summary");
            sb.AppendLine();
            sb.AppendLine("| | things |");
            sb.AppendLine("|---|---:|");
            sb.AppendLine("| things on the maps (spawned) | " + mapThings + " |");
            sb.AppendLine("| of which never ticked (`tickerType` Never) | " + neverThings + " |");
            sb.AppendLine("| Normal tickers, `Tick()` every tick | " + normalTotal + " |");
            sb.AppendLine("| &nbsp;&nbsp;with real per-tick code | " + (normalTotal - idle) + " |");
            sb.AppendLine("| &nbsp;&nbsp;**idle** (nothing to do per tick) | " + idle + " |");
            sb.AppendLine("| Rare tickers (every 250 ticks) | " + list.Where(r => r.Ticker == "Rare").Sum(r => r.Total) + " |");
            sb.AppendLine("| Long tickers (every 2000 ticks) | " + list.Where(r => r.Ticker == "Long").Sum(r => r.Total) + " |");
            sb.AppendLine();

            var normals = list.Where(r => r.Normal).ToList();
            Table(sb, "Normal tickers by mod (where the def comes from)", "mod", normals.GroupBy(r => r.Mod).OrderByDescending(g => g.Sum(r => r.Total)).Take(15)
                .Select(g => new KeyValuePair<string, int[]>(g.Key, new[] { g.Sum(r => r.Total), g.Where(r => r.Idle).Sum(r => r.Total), g.Count() })), new[] { "things", "idle", "defs" });
            Table(sb, "Normal tickers by class", "class", normals.GroupBy(r => r.Class).OrderByDescending(g => g.Sum(r => r.Total)).Take(20)
                .Select(g => new KeyValuePair<string, int[]>(g.Key, new[] { g.Sum(r => r.Total), g.Where(r => r.Idle).Sum(r => r.Total), g.Count() })), new[] { "things", "idle", "defs" });

            sb.AppendLine("## Normal tickers by definition (top " + Math.Min(limit, normals.Count) + " of " + normals.Count + "; the CSV has everything)");
            sb.AppendLine();
            sb.AppendLine("| # | def | class | mod | on map | carried | per-tick code | idle | comps with real CompTick | comps with real CompTickInterval |");
            sb.AppendLine("|---:|---|---|---|---:|---:|---|---|---|---|");
            int i = 0;
            foreach (var r in normals.Take(limit))
            {
                i++;
                sb.AppendLine("| " + i + " | " + Clean(r.Def) + " | " + Clean(r.Class) + " | " + Clean(r.Mod) + " | " + r.OnMap + " | " + r.HeldByPawns + " | " + KindName(r.ClassTick)
                    + " | " + (r.Idle ? "**idle**" : "") + " | " + Clean(r.PerTickComps) + " | " + Clean(r.IntervalComps) + " |");
            }
            File.WriteAllText(Path.Combine(dir, "ticking_things.md"), sb.ToString(), new UTF8Encoding(false));
        }

        static void WriteCsv(string dir, List<Row> list)
        {
            var sb = new StringBuilder();
            sb.AppendLine("def,class,mod,ticker,on_map,carried,class_tick,class_tick_interval,idle,per_tick_comps,interval_comps,rare_comps,long_comps");
            foreach (var r in list)
                sb.AppendLine(string.Join(",", new[] { Csv(r.Def), Csv(r.Class), Csv(r.Mod), r.Ticker, r.OnMap.ToString(Inv), r.HeldByPawns.ToString(Inv), KindName(r.ClassTick), KindName(r.ClassInterval),
                    r.Idle ? "1" : "0", Csv(r.PerTickComps), Csv(r.IntervalComps), Csv(r.RareComps), Csv(r.LongComps) }));
            File.WriteAllText(Path.Combine(dir, "ticking_things.csv"), sb.ToString(), new UTF8Encoding(false));
        }
    }
}
