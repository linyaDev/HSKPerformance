using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;

namespace HSKPerfProbe
{
    /// <summary>
    /// Permanent performance fixes. Unlike the profiler hooks these stay installed all the time,
    /// so every one of them must be safe: it only patches when the expected target exists with the
    /// expected signature, verifies itself against the original behaviour, and switches itself off
    /// on the first sign of trouble.
    /// </summary>
    public static class PerfFixes
    {
        public const string HarmonyId = "linya.hskperfprobe.fixes";

        /// <summary>Human readable status lines, copied into every report.</summary>
        public static readonly List<string> Status = new List<string>();

        /// <summary>Counters that change while the game runs (empty when the fix is not installed).</summary>
        public static string LiveStatus()
        {
            return RotStorageFix.Installed ? RotStorageFix.Live() : "";
        }

        public static void Init()
        {
            try
            {
                var cfg = ProbeConfig.Load();
                var harmony = new Harmony(HarmonyId);
                if (cfg.FixRotStorage) RotStorageFix.Apply(harmony, cfg);
                else Status.Add("rot storage lookup fix: disabled in config.txt");
                if (cfg.FixMothballHediffs) MothballHediffFix.Apply(cfg);
                else Status.Add("mothball hediff fix: disabled in config.txt");
            }
            catch (Exception e)
            {
                Status.Add("fix init failed: " + e.Message);
                Verse.Log.Error("[HSKPerfProbe] fix init failed: " + e);
            }
            foreach (var s in Status) Verse.Log.Message("[HSKPerfProbe] " + s);
        }
    }

    /// <summary>
    /// WorldPawns.WorldPawnsTick ticks every world pawn that is not "mothballed" on EVERY tick. A pawn is mothballed
    /// (WorldPawns.DoMothballProcessing, every 15000 ticks) only if none of its hediffs blocks it, and a hediff blocks
    /// it unless it is permanent or HediffDef.AlwaysAllowMothball is true. Chronic diseases, malnutrition (world pawns
    /// never eat, so it never heals) and drug needs therefore keep pawns in the per-tick list for hundreds of days.
    ///
    /// Mothballing does not advance hediffs: Pawn.TickMothballed only ages the pawn and updates its records. So marking these
    /// defs as "always allow mothball" just freezes the disease in place while the pawn is a world pawn. The only side effect is
    /// that such a disease stops killing world pawns. The same trick is used by the MothballFix.xml patch of Missile Girl.
    /// </summary>
    static class MothballHediffFix
    {
        public static void Apply(ProbeConfig cfg)
        {
            var fAllow = AccessTools.Field(typeof(HediffDef), "alwaysAllowMothball");
            var fCached = AccessTools.Field(typeof(HediffDef), "alwaysAllowMothballCached");
            if (fAllow == null || fCached == null || fAllow.FieldType != typeof(bool) || fCached.FieldType != typeof(bool))
            {
                PerfFixes.Status.Add("mothball hediff fix: HediffDef.alwaysAllowMothball fields not found (game changed?), NOT applied");
                return;
            }
            var patterns = cfg.MothballHediffs ?? new string[0];
            var matchedPattern = new bool[patterns.Length];
            var changed = new List<string>();
            int already = 0;

            foreach (var def in DefDatabase<HediffDef>.AllDefsListForReading)
            {
                int hit = -1;
                for (int i = 0; i < patterns.Length; i++)
                {
                    if (Matches(def.defName, patterns[i])) { hit = i; break; }
                }
                if (hit < 0) continue;
                matchedPattern[hit] = true;

                bool allowedNow;
                try { allowedNow = def.AlwaysAllowMothball; } catch { continue; } // also fills the cache
                if (allowedNow) { already++; continue; }
                fAllow.SetValue(def, true);
                fCached.SetValue(def, true);
                changed.Add(def.defName);
            }

            var unmatched = new List<string>();
            for (int i = 0; i < patterns.Length; i++) if (!matchedPattern[i]) unmatched.Add(patterns[i]);

            string sample = string.Join(", ", changed.Take(10).ToArray()) + (changed.Count > 10 ? ", +" + (changed.Count - 10) + " more" : "");
            PerfFixes.Status.Add("mothball hediff fix: " + changed.Count + " hediff defs now let world pawns be mothballed" + (changed.Count > 0 ? " (" + sample + ")" : "")
                + "; " + already + " matching defs already allowed it"
                + (unmatched.Count > 0 ? "; patterns with no match: " + string.Join(", ", unmatched.ToArray()) : ""));
        }

        internal static bool Matches(string defName, string pattern)
        {
            if (string.IsNullOrEmpty(defName) || string.IsNullOrEmpty(pattern)) return false;
            if (pattern[pattern.Length - 1] == '*')
                return defName.StartsWith(pattern.Substring(0, pattern.Length - 1), StringComparison.Ordinal);
            return string.Equals(defName, pattern, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// HSK Core (SK.CompBetterRottable_Patch) finds the storage building that holds a rotting item by walking
    /// EVERY colonist storage building and EVERY cell of it, for every rotting item, every 250 ticks (and again
    /// in a postfix). That was measured at ~84 us per call. The game already has a cell -> slot group grid, so the
    /// same answer is one array lookup.
    ///
    /// Original:  storage = SpawnedOrAnyParentSpawned ? MapHeld.listerBuildings.AllBuildingsColonistOfClass&lt;Building_Storage&gt;()
    ///                        .FirstOrDefault(x => x.AllSlotCellsList().Any(y => y == PositionHeld)) : null;
    /// </summary>
    static class RotStorageFix
    {
        static int verifyLeft, verified, mismatches;
        static bool disabled;
        public static bool Installed;
        static long fastTicks, slowTicks;

        public static void Apply(Harmony harmony, ProbeConfig cfg)
        {
            var type = AccessTools.TypeByName("SK.CompBetterRottable_Patch");
            if (type == null)
            {
                PerfFixes.Status.Add("rot storage fix: SK.CompBetterRottable_Patch not found (HSK Core not loaded), nothing to fix");
                return;
            }
            var target = AccessTools.Method(type, "TryGetAnyStorage");
            var ps = target == null ? null : target.GetParameters();
            if (target == null || !target.IsStatic || target.ReturnType != typeof(bool) || ps.Length != 2
                || ps[0].ParameterType != typeof(CompRottable) || !ps[1].IsOut)
            {
                PerfFixes.Status.Add("rot storage fix: TryGetAnyStorage is missing or has a different signature (Core_SK changed?), NOT patched");
                return;
            }

            verifyLeft = cfg.FixRotVerifyCalls;
            harmony.Patch(target, prefix: new HarmonyMethod(typeof(RotStorageFix).GetMethod("Prefix")) { priority = Priority.First });
            Installed = true;
            PerfFixes.Status.Add("rot storage fix: active" + (verifyLeft > 0 ? " (self-check on the first " + verifyLeft + " calls)" : " (no self-check)"));
        }

        static Building_Storage Fast(CompRottable comp)
        {
            var parent = comp.parent;
            if (!parent.SpawnedOrAnyParentSpawned) return null;
            var map = parent.MapHeld;
            if (map == null) return null;
            var cell = parent.PositionHeld;
            if (!cell.InBounds(map)) return null;
            var storage = map.haulDestinationManager.SlotGroupAt(cell)?.parent as Building_Storage;
            // AllBuildingsColonistOfClass only contains buildings of the player faction
            return storage != null && storage.Faction == Faction.OfPlayer ? storage : null;
        }

        static Building_Storage Slow(CompRottable comp)
        {
            var parent = comp.parent;
            return parent.SpawnedOrAnyParentSpawned
                ? parent.MapHeld.listerBuildings.AllBuildingsColonistOfClass<Building_Storage>()
                    .FirstOrDefault(x => x.AllSlotCellsList().Any(y => y == parent.PositionHeld))
                : null;
        }

        // Positional arguments (__0, __1) so we do not depend on the parameter names of the private original.
        static long calls;

        /// <summary>Live state for the corner message and the report, so it is visible whether the fix is being hit at all.</summary>
        public static string Live()
        {
            return "rot storage fix live: prefix hit " + calls + " times, self-check " + verified + " done / " + verifyLeft + " left, " + mismatches + " mismatches" + (disabled ? ", SWITCHED OFF" : "");
        }

        public static bool Prefix(CompRottable __0, out Building_Storage __1, ref bool __result)
        {
            calls++;
            if (disabled) { __1 = null; return true; } // run the original
            try
            {
                if (verifyLeft > 0)
                {
                    long t0 = Stopwatch.GetTimestamp();
                    var fast = Fast(__0);
                    long t1 = Stopwatch.GetTimestamp();
                    var slow = Slow(__0);
                    long t2 = Stopwatch.GetTimestamp();
                    fastTicks += t1 - t0;
                    slowTicks += t2 - t1;
                    verified++;
                    if (!ReferenceEquals(fast, slow))
                    {
                        mismatches++;
                        if (mismatches <= 5)
                            Verse.Log.Warning("[HSKPerfProbe] rot storage fix MISMATCH for " + __0.parent + " at " + __0.parent.PositionHeld + ": original="
                                + (slow == null ? "none" : slow.ToString()) + ", fast=" + (fast == null ? "none" : fast.ToString()));
                        if (mismatches >= 3) Disable("3 mismatches between the fast lookup and the original");
                    }
                    __1 = slow; // during self-check the original answer always wins
                    __result = slow != null;
                    if (--verifyLeft == 0) FinishVerification();
                    return false;
                }
                __1 = Fast(__0);
                __result = __1 != null;
                return false;
            }
            catch (Exception e)
            {
                Disable("exception: " + e.GetType().Name + ": " + e.Message);
                __1 = null;
                return true; // let the original handle this call
            }
        }

        static void Disable(string why)
        {
            disabled = true;
            PerfFixes.Status.Add("rot storage fix: SWITCHED OFF after " + verified + " checked calls: " + why);
            Verse.Log.Error("[HSKPerfProbe] rot storage fix switched off: " + why);
        }

        static void FinishVerification()
        {
            double f = 1e6 / Stopwatch.Frequency / Math.Max(1, verified);
            string s = "rot storage fix: self-check finished, " + verified + " calls, " + mismatches + " mismatches; average original "
                + (slowTicks * f).ToString("F1", CultureInfo.InvariantCulture) + " us per call vs fast "
                + (fastTicks * f).ToString("F2", CultureInfo.InvariantCulture) + " us";
            if (!disabled) PerfFixes.Status.Add(s);
            Verse.Log.Message("[HSKPerfProbe] " + s);
        }
    }
}
