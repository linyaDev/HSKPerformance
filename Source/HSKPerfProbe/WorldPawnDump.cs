using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace HSKPerfProbe
{
    /// <summary>
    /// World pawns that are NOT mothballed are ticked by WorldPawns.WorldPawnsTick every single tick, like colonists.
    /// This lists them with everything the game knows about who they are and why they are still active.
    /// A pawn moves to the mothballed set in a pass that runs every 15000 ticks, unless something blocks it
    /// (a non-permanent hediff, caravan membership, a transport pod trip). New world pawns simply wait for that pass.
    /// </summary>
    public static class WorldPawnDump
    {
        const int MothballPassTicks = 15000;

        static readonly FieldInfo FAlive = AccessTools.Field(typeof(WorldPawns), "pawnsAlive");
        static readonly FieldInfo FMothballed = AccessTools.Field(typeof(WorldPawns), "pawnsMothballed");
        static readonly MethodInfo MCritical = AccessTools.Method(typeof(WorldPawnGC), "GetCriticalPawnReason");
        static readonly MethodInfo MPrevent = AccessTools.Method(typeof(WorldPawns), "DefPreventingMothball");

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        sealed class Row
        {
            public string Name, Kind, Race, Faction, FactionDef, Situation, KeepReason, Blocker, Status, Holder, Hediffs, Gender;
            public bool Hostile, EverColonist;
            public double DaysInWorld;
            public int Age;
        }

        static string Clean(string s) { return (s ?? "").Replace('|', '/').Replace('\n', ' ').Replace('\r', ' ').Replace(',', ';'); }
        static string Csv(string s)
        {
            s = s ?? "";
            return s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        }

        /// <summary>Writes into its own timestamped folder. Returns a one-line summary, or null if there is no game.</summary>
        public static string WriteStandalone()
        {
            string dir = Path.Combine(ProbeConfig.Dir, "world-pawns-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", Inv));
            string summary = TryWrite(dir);
            return summary == null ? null : summary + "  ->  " + dir;
        }

        public static string TryWrite(string dir)
        {
            try
            {
                if (Current.ProgramState != ProgramState.Playing || Find.WorldPawns == null || Find.TickManager == null) return null;
                if (FAlive == null || FMothballed == null)
                {
                    Log.Warning("[HSKPerfProbe] WorldPawns fields not found (game changed?), world pawn dump skipped");
                    return null;
                }
                Directory.CreateDirectory(dir);

                var wp = Find.WorldPawns;
                var alive = ((HashSet<Pawn>)FAlive.GetValue(wp)).ToList();
                int mothballed = ((HashSet<Pawn>)FMothballed.GetValue(wp)).Count;
                int ticksGame = Find.TickManager.TicksGame;
                int untilPass = MothballPassTicks - ticksGame % MothballPassTicks;
                int ticksAbs = GenTicks.TicksAbs;

                var rows = new List<Row>(alive.Count);
                foreach (var pawn in alive)
                {
                    try { rows.Add(Describe(wp, pawn, ticksAbs)); }
                    catch (Exception e) { rows.Add(new Row { Name = SafeName(pawn), Status = "could not be described: " + e.GetType().Name }); }
                }
                rows = rows.OrderBy(r => r.Blocker == null || r.Blocker == "" ? 1 : 0).ThenBy(r => r.Faction).ThenBy(r => r.Kind).ToList();

                int blocked = rows.Count(r => !string.IsNullOrEmpty(r.Blocker));
                string summary = "world pawns: " + (alive.Count + mothballed) + " alive, " + alive.Count + " ticked every tick (" + blocked + " blocked from mothballing, "
                    + (alive.Count - blocked) + " waiting for the next pass in " + untilPass + " ticks), " + mothballed + " mothballed";

                WriteMarkdown(dir, rows, summary, alive.Count + mothballed, mothballed, blocked, untilPass, ticksGame);
                WriteCsv(dir, rows);
                return summary;
            }
            catch (Exception e)
            {
                try { Log.Error("[HSKPerfProbe] world pawn dump failed: " + e); } catch { }
                return null;
            }
        }

        static string SafeName(Pawn p) { try { return p.LabelShortCap; } catch { return "?"; } }

        static Row Describe(WorldPawns wp, Pawn pawn, int ticksAbs)
        {
            var r = new Row();
            r.Name = pawn.Name != null ? pawn.Name.ToStringFull : pawn.LabelShortCap;
            r.Kind = pawn.kindDef != null ? pawn.kindDef.defName : "?";
            r.Race = pawn.def != null ? pawn.def.defName : "?";
            var faction = pawn.Faction;
            r.Faction = faction != null ? faction.Name : "(none)";
            r.FactionDef = faction != null && faction.def != null ? faction.def.defName : "";
            r.Hostile = faction != null && faction.HostileTo(Faction.OfPlayer);
            r.Situation = wp.GetSituation(pawn).ToString();
            r.Gender = pawn.gender.ToString();
            r.Age = pawn.ageTracker != null ? pawn.ageTracker.AgeBiologicalYears : -1;
            r.EverColonist = PawnUtility.EverBeenColonistOrTameAnimal(pawn);

            // why does the game keep this pawn at all (null = it would be garbage collected)
            string keep = null;
            try { if (MCritical != null) keep = MCritical.Invoke(wp.gc, new object[] { pawn }) as string; } catch { }
            // null only means "no CRITICAL reason". The game's clean-up pass also keeps pawns that are referenced by
            // relationships, memories, logs, etc., so this does not mean the pawn is about to be deleted.
            r.KeepReason = keep ?? "(no critical reason; kept by relations/memories or removed at next GC)";

            // why is it not mothballed yet
            string blocker = null;
            try
            {
                if (MPrevent != null)
                {
                    var hd = MPrevent.Invoke(wp, new object[] { pawn }) as HediffDef;
                    if (hd != null) blocker = "hediff " + hd.defName;
                }
            }
            catch { }
            if (blocker == null && pawn.IsCaravanMember()) blocker = "caravan member";
            if (blocker == null && PawnUtility.IsTravelingInTransportPodWorldObject(pawn)) blocker = "in transport pod";
            r.Blocker = blocker ?? "";
            r.Status = blocker != null ? "blocked: " + blocker : "no blocker, will be mothballed at the next pass";

            r.DaysInWorld = pawn.becameWorldPawnTickAbs >= 0 ? (ticksAbs - pawn.becameWorldPawnTickAbs) / 60000.0 : -1;

            string holder = "";
            try
            {
                var ph = pawn.ParentHolder;
                if (ph != null)
                {
                    holder = ph.GetType().Name;
                    var wo = ph as WorldObject;
                    if (wo != null) holder += " " + wo.LabelCap;
                }
                var caravan = pawn.GetCaravan();
                if (caravan != null) holder = "Caravan " + caravan.LabelCap;
            }
            catch { }
            r.Holder = holder;

            var hediffs = pawn.health != null && pawn.health.hediffSet != null ? pawn.health.hediffSet.hediffs : null;
            if (hediffs != null && hediffs.Count > 0)
                r.Hediffs = string.Join("; ", hediffs.Take(8).Select(h => h.def.defName + (h.IsPermanent() ? "(perm)" : "")).ToArray()) + (hediffs.Count > 8 ? "; +" + (hediffs.Count - 8) + " more" : "");
            else r.Hediffs = "";
            return r;
        }

        static void Group(StringBuilder sb, string title, IEnumerable<Row> rows, Func<Row, string> key, int top = 15)
        {
            sb.AppendLine("**" + title + "**");
            sb.AppendLine();
            sb.AppendLine("| value | pawns |");
            sb.AppendLine("|---|---:|");
            foreach (var g in rows.GroupBy(key).OrderByDescending(g => g.Count()).Take(top))
                sb.AppendLine("| " + Clean(string.IsNullOrEmpty(g.Key) ? "(empty)" : g.Key) + " | " + g.Count() + " |");
            sb.AppendLine();
        }

        static void WriteMarkdown(string dir, List<Row> rows, string summary, int total, int mothballed, int blocked, int untilPass, int ticksGame)
        {
            int limit = ProbeConfig.Load().WorldPawnRows;
            var sb = new StringBuilder(32 * 1024);
            sb.AppendLine("# World pawns that are ticked every tick");
            sb.AppendLine();
            sb.AppendLine("Written " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", Inv) + " at game tick " + ticksGame + ".");
            sb.AppendLine();
            sb.AppendLine("- " + summary);
            sb.AppendLine("- `WorldPawns.WorldPawnsTick` calls `DoTick()` on every one of the ticked pawns on every tick. Mothballed pawns are skipped and only get a batch update every " + MothballPassTicks + " ticks.");
            sb.AppendLine("- A pawn is mothballed in the pass that runs when `ticks % " + MothballPassTicks + " == 0` (next one in " + untilPass + " ticks) unless it has a non-permanent hediff, is a caravan member or is in a transport pod. New world pawns therefore stay ticked until the next pass.");
            foreach (var st in PerfFixes.Status)
                if (st.StartsWith("mothball")) sb.AppendLine("- fix: " + Clean(st));
            sb.AppendLine("- `keep reason` is the game's own answer to \"why is this pawn still in the world instead of being deleted\" (`WorldPawnGC`).");
            sb.AppendLine();

            sb.AppendLine("## Summary");
            sb.AppendLine();
            Group(sb, "Why they are still ticked", rows, r => r.Status.StartsWith("blocked") ? r.Blocker : "no blocker (will be mothballed at the next pass)");
            Group(sb, "By faction", rows, r => r.Faction + (r.Hostile ? " [hostile]" : ""));
            Group(sb, "By situation", rows, r => r.Situation);
            Group(sb, "Why the game keeps them (keep reason)", rows, r => r.KeepReason);
            Group(sb, "By kind", rows, r => r.Kind);
            Group(sb, "By race", rows, r => r.Race);
            sb.AppendLine("**Time spent as a world pawn**");
            sb.AppendLine();
            sb.AppendLine("| days in world | pawns |");
            sb.AppendLine("|---|---:|");
            sb.AppendLine("| unknown | " + rows.Count(r => r.DaysInWorld < 0) + " |");
            sb.AppendLine("| less than 1 | " + rows.Count(r => r.DaysInWorld >= 0 && r.DaysInWorld < 1) + " |");
            sb.AppendLine("| 1 to 6 | " + rows.Count(r => r.DaysInWorld >= 1 && r.DaysInWorld < 6) + " |");
            sb.AppendLine("| 6 to 15 | " + rows.Count(r => r.DaysInWorld >= 6 && r.DaysInWorld < 15) + " |");
            sb.AppendLine("| 15 and more | " + rows.Count(r => r.DaysInWorld >= 15) + " |");
            sb.AppendLine();

            sb.AppendLine("## Pawns (blocked ones first, " + Math.Min(limit, rows.Count) + " of " + rows.Count + "; the CSV has all of them)");
            sb.AppendLine();
            sb.AppendLine("| # | name | kind | race | faction | situation | keep reason | status | days in world | age | ex-colonist | holder | hediffs |");
            sb.AppendLine("|---:|---|---|---|---|---|---|---|---:|---:|---|---|---|");
            int n = 0;
            foreach (var r in rows.Take(limit))
            {
                n++;
                sb.AppendLine("| " + n + " | " + Clean(r.Name) + " | " + Clean(r.Kind) + " | " + Clean(r.Race) + " | " + Clean(r.Faction) + (r.Hostile ? " [hostile]" : "") + " | " + r.Situation + " | " + Clean(r.KeepReason)
                    + " | " + Clean(r.Status) + " | " + (r.DaysInWorld < 0 ? "?" : r.DaysInWorld.ToString("F1", Inv)) + " | " + r.Age + " | " + (r.EverColonist ? "yes" : "") + " | " + Clean(r.Holder) + " | " + Clean(r.Hediffs) + " |");
            }
            File.WriteAllText(Path.Combine(dir, "world_pawns.md"), sb.ToString(), new UTF8Encoding(false));
        }

        static void WriteCsv(string dir, List<Row> rows)
        {
            var sb = new StringBuilder();
            sb.AppendLine("name,kind,race,faction,faction_def,hostile,situation,keep_reason,blocker,status,days_in_world,age,gender,ever_colonist,holder,hediffs");
            foreach (var r in rows)
                sb.AppendLine(string.Join(",", new[] { Csv(r.Name), Csv(r.Kind), Csv(r.Race), Csv(r.Faction), Csv(r.FactionDef), r.Hostile ? "1" : "0", r.Situation, Csv(r.KeepReason), Csv(r.Blocker), Csv(r.Status),
                    r.DaysInWorld.ToString("F2", Inv), r.Age.ToString(Inv), r.Gender, r.EverColonist ? "1" : "0", Csv(r.Holder), Csv(r.Hediffs) }));
            File.WriteAllText(Path.Combine(dir, "world_pawns.csv"), sb.ToString(), new UTF8Encoding(false));
        }
    }
}
