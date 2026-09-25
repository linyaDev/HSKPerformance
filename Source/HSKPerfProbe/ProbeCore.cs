using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using RimWorld;
using Verse;

namespace HSKPerfProbe
{
    /// <summary>One profiled method. All times are Stopwatch ticks.</summary>
    public sealed class Slot
    {
        public string Category;   // Tick / Update / GUI / Harmony
        public string Family;     // which hook family found it
        public string Mod;        // mod that owns the declaring assembly
        public string Type;       // full type name
        public string Name;       // Type.Method (short)
        public string Extra;      // harmony: owner + target
        public bool IsRoot;
        public bool TrackDef;
        public long Calls;
        public long ExclTicks;    // time in the method minus time in nested profiled methods
        public long InclTicks;
        public long MaxExclTicks;
        public long MaxInclTicks;
        public long ChildCalls;     // profiled methods called directly from this one (their probe overhead lands in our exclusive time)
        public long RawExclTicks;   // exclusive time before the probe overhead was subtracted (set by the report)

        public void Reset()
        {
            Calls = ExclTicks = InclTicks = MaxExclTicks = MaxInclTicks = ChildCalls = RawExclTicks = 0;
        }
    }

    public sealed class DefStat
    {
        public Def Def;
        public long Calls;
        public long ExclTicks;
        public long MaxExclTicks;
    }

    sealed class RefComparer : IEqualityComparer<Def>
    {
        public static readonly RefComparer Instance = new RefComparer();
        public bool Equals(Def a, Def b) { return ReferenceEquals(a, b); }
        public int GetHashCode(Def d) { return RuntimeHelpers.GetHashCode(d); }
    }

    /// <summary>
    /// Hot path. Enter/Exit are called from Harmony prefix/finalizer on every profiled method,
    /// so everything here is static, allocation free and main-thread only.
    /// </summary>
    public static class ProbeCore
    {
        public const string HarmonyId = "linya.hskperfprobe";

        public static volatile bool Active;
        public static int MainThreadId;

        const int MaxDepth = 512;
        static readonly long[] childAcc = new long[MaxDepth + 2];
        static readonly int[] childCnt = new int[MaxDepth + 2];
        static int depth;

        public static readonly Dictionary<IntPtr, Slot> SlotByHandle = new Dictionary<IntPtr, Slot>();
        public static readonly Dictionary<Def, DefStat> DefStats = new Dictionary<Def, DefStat>(RefComparer.Instance);
        public static readonly Slot Unknown = new Slot { Category = "?", Family = "unknown", Mod = "?", Name = "(unresolved handle)", Type = "" };
        public static long DepthOverflow;

        public static void ResetAll()
        {
            depth = 0;
            DepthOverflow = 0;
            DefStats.Clear();
            foreach (var s in SlotByHandle.Values) s.Reset();
            Unknown.Reset();
        }

        public static long Enter()
        {
            if (!Active || Environment.CurrentManagedThreadId != MainThreadId) return 0;
            if (depth >= MaxDepth) { DepthOverflow++; return 0; }
            depth++;
            childAcc[depth] = 0;
            childCnt[depth] = 0;
            return Stopwatch.GetTimestamp();
        }

        public static void Exit(long start, MethodBase original, object instance)
        {
            if (start == 0) return;
            long elapsed = Stopwatch.GetTimestamp() - start;
            if (depth <= 0) return; // counters were reset while this call was in flight
            long excl = elapsed - childAcc[depth];
            if (excl < 0) excl = 0;
            int kids = childCnt[depth];
            depth--;
            childAcc[depth] += elapsed;
            childCnt[depth]++;

            Slot s;
            if (!SlotByHandle.TryGetValue(original.MethodHandle.Value, out s)) s = Unknown;
            s.Calls++;
            s.ChildCalls += kids;
            s.ExclTicks += excl;
            s.InclTicks += elapsed;
            if (excl > s.MaxExclTicks) s.MaxExclTicks = excl;
            if (elapsed > s.MaxInclTicks) s.MaxInclTicks = elapsed;

            if (instance != null) TrackDef(instance, excl);
        }

        static void TrackDef(object inst, long excl)
        {
            Def def = null;
            var thing = inst as Thing;
            if (thing != null) def = thing.def;
            else
            {
                var comp = inst as ThingComp;
                if (comp != null) { var p = comp.parent; if (p != null) def = p.def; }
                else
                {
                    var hediff = inst as Hediff;
                    if (hediff != null) def = hediff.def;
                    else
                    {
                        var hc = inst as HediffComp;
                        if (hc != null && hc.parent != null) def = hc.parent.def;
                    }
                }
            }
            if (def == null) return;
            DefStat ds;
            if (!DefStats.TryGetValue(def, out ds)) { ds = new DefStat { Def = def }; DefStats[def] = ds; }
            ds.Calls++;
            ds.ExclTicks += excl;
            if (excl > ds.MaxExclTicks) ds.MaxExclTicks = excl;
        }
    }

    // Harmony patch classes. Prefix and finalizer of one patch MUST live in the same class,
    // because Harmony matches __state by declaring type.
    public static class ProbePatch
    {
        public static void Pre(out long __state) { __state = ProbeCore.Enter(); }
        public static void Fin(long __state, MethodBase __originalMethod) { ProbeCore.Exit(__state, __originalMethod, null); }
    }

    public static class ProbePatchInst
    {
        public static void Pre(out long __state) { __state = ProbeCore.Enter(); }
        public static void Fin(long __state, MethodBase __originalMethod, object __instance) { ProbeCore.Exit(__state, __originalMethod, __instance); }
    }

    /// <summary>Empty methods used to measure the real per-call cost of the probes.</summary>
    public static class Calibration
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Empty() { }
    }

    public sealed class CalibrationTarget
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void EmptyInst() { }
    }
}
