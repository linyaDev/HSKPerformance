using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace HSKPerfProbe
{
    public sealed class HookTarget
    {
        public MethodBase Method;
        public Slot Slot;
        public bool Inst;         // use the __instance variant (needed for per-def attribution)
        public bool IsCalibration;
        public bool Failed;
    }

    sealed class Family
    {
        public string Cat;
        public string Label;
        public Type Base;
        public string[] Names;
        public bool TrackDef;
    }

    /// <summary>
    /// Decides WHICH methods get profiled: every override of the interesting virtual methods
    /// (found by reflection, so it adapts to whatever mods are loaded), a curated list of vanilla
    /// hot spots, and the prefix/postfix/finalizer methods of other mods' Harmony patches.
    /// </summary>
    public static class HookCatalog
    {
        public static readonly List<HookTarget> Targets = new List<HookTarget>();
        public static readonly List<string> Notes = new List<string>();
        public static readonly List<string> Failures = new List<string>();
        public static readonly List<KeyValuePair<string, int>> FamilyCounts = new List<KeyValuePair<string, int>>();

        public static Slot TickRoot, UpdateRoot, GuiRoot;
        public static double BuildMs, InstallMs;
        public static int PatchedOk, PatchedFail;
        public static int HarmonyPatchesSkipped;

        static HashSet<IntPtr> seen = new HashSet<IntPtr>();
        static Dictionary<string, int> counts = new Dictionary<string, int>();
        static readonly Assembly CoreAsm = typeof(Thing).Assembly;

        const BindingFlags Declared = BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        static Type T(string names)
        {
            foreach (var n in names.Split('|'))
            {
                var t = CoreAsm.GetType(n);
                if (t != null) return t;
            }
            return null;
        }

        // Category | Type:Method | optional "root"
        static readonly string[] Sinks =
        {
            "Tick|Verse.TickManager:DoSingleTick|root",
            "Tick|Verse.Map:MapPreTick", "Tick|Verse.Map:MapPostTick",
            "Tick|RimWorld.Planet.World:WorldTick", "Tick|RimWorld.Planet.WorldObjectsHolder:WorldObjectsHolderTick",
            "Tick|RimWorld.Planet.WorldPawns:WorldPawnsTick",
            "Tick|Verse.AI.Pawn_JobTracker:JobTrackerTick", "Tick|Verse.AI.Pawn_JobTracker:JobTrackerTickInterval",
            "Tick|Verse.AI.Pawn_JobTracker:TryFindAndStartJob",
            "Tick|Verse.AI.Pawn_PathFollower:PatherTick",
            "Tick|Verse.Pawn_HealthTracker:HealthTick", "Tick|Verse.Pawn_HealthTracker:HealthTickInterval",
            "Tick|RimWorld.Pawn_NeedsTracker:NeedsTrackerTickInterval",
            "Tick|Verse.AI.Pawn_MindState:MindStateTickInterval",
            "Tick|Verse.RegionAndRoomUpdater:TryRebuildDirtyRegionsAndRooms",
            "Tick|RimWorld.Storyteller:StorytellerTick", "Tick|RimWorld.QuestManager:QuestManagerTick",

            "Update|Verse.Root_Play:Update|root", "Update|Verse.Root:Update",
            "Update|Verse.Map:MapUpdate", "Update|RimWorld.Planet.World:WorldUpdate",
            "Update|Verse.MapDrawer:MapMeshDrawerUpdate_First", "Update|Verse.MapDrawer:DrawMapMesh",
            "Update|Verse.DynamicDrawManager:DrawDynamicThings",
            "Update|Verse.PawnRenderer:DynamicDrawPhaseAt", "Update|Verse.PawnRenderer:RenderPawnAt",
            "Update|Verse.PawnRenderTree:ParallelPreDraw", "Update|Verse.PawnRenderTree:SetDirty",
            "Update|Verse.Section:RegenerateAllLayers",
            "Update|RimWorld.UIRoot_Play:UIRootUpdate", "Update|RimWorld.MapInterface:MapInterfaceUpdate",
            "Update|RimWorld.AlertsReadout:AlertsReadoutUpdate",
            "Update|RimWorld.Planet.WorldRenderer:CheckActivateWorldCamera",

            "GUI|RimWorld.UIRoot_Play:UIRootOnGUI|root", "GUI|Verse.UIRoot:UIRootOnGUI",
            "GUI|RimWorld.ResourceReadout:ResourceReadoutOnGUI", "GUI|RimWorld.ColonistBar:ColonistBarOnGUI",
            "GUI|RimWorld.AlertsReadout:AlertsReadoutOnGUI", "GUI|Verse.ThingOverlays:ThingOverlaysOnGUI",
            "GUI|Verse.WindowStack:WindowStackOnGUI", "GUI|RimWorld.MainButtonsRoot:MainButtonsOnGUI",
            "GUI|RimWorld.MapInterface:MapInterfaceOnGUI_BeforeMainTabs", "GUI|RimWorld.MapInterface:MapInterfaceOnGUI_AfterMainTabs",
            "GUI|RimWorld.InspectPaneUtility:InspectPaneOnGUI", "GUI|RimWorld.GlobalControls:GlobalControlsOnGUI",
            "GUI|Verse.Messages:MessagesDoGUI", "GUI|RimWorld.Planet.WorldSelector:WorldSelectorOnGUI",
        };

        static List<Family> BuildFamilies(ProbeConfig cfg)
        {
            var l = new List<Family>();
            void Add(string cat, string label, bool track, string typeName, params string[] names)
            {
                if (cat == "Tick" && !cfg.HookTick) return;
                if (cat == "Update" && !cfg.HookUpdate) return;
                if (cat == "GUI" && !cfg.HookGui) return;
                var t = T(typeName);
                if (t == null) { Notes.Add("family type not found in this game version: " + typeName); return; }
                l.Add(new Family { Cat = cat, Label = label, Base = t, Names = names, TrackDef = track });
            }

            // ---- Tick ----
            Add("Tick", "Thing tick overrides", true, "Verse.Thing", "Tick", "TickInterval", "TickRare", "TickLong");
            Add("Tick", "ThingComp tick overrides", true, "Verse.ThingComp", "CompTick", "CompTickInterval", "CompTickRare", "CompTickLong");
            Add("Tick", "Hediff tick overrides", true, "Verse.Hediff", "Tick", "TickInterval", "PostTick", "PostTickInterval");
            Add("Tick", "HediffComp tick overrides", true, "Verse.HediffComp", "CompPostTick", "CompPostTickInterval");
            Add("Tick", "Gene tick overrides", false, "Verse.Gene", "Tick", "TickInterval");
            Add("Tick", "GameComponent tick", false, "Verse.GameComponent", "GameComponentTick");
            Add("Tick", "MapComponent tick", false, "Verse.MapComponent", "MapComponentTick");
            Add("Tick", "WorldComponent tick", false, "RimWorld.Planet.WorldComponent", "WorldComponentTick");
            Add("Tick", "WorldObject tick", false, "RimWorld.Planet.WorldObject", "Tick");
            Add("Tick", "WorldObjectComp tick", false, "RimWorld.Planet.WorldObjectComp", "CompTick");
            Add("Tick", "Need interval", false, "RimWorld.Need", "NeedInterval");
            Add("Tick", "ThinkNode (AI tree)", false, "Verse.AI.ThinkNode", "TryIssueJobPackage", "GetPriority");
            Add("Tick", "JobGiver", false, "Verse.AI.ThinkNode_JobGiver", "TryGiveJob");
            Add("Tick", "WorkGiver", false, "RimWorld.WorkGiver", "ShouldSkip", "HasJobOnThing", "JobOnThing", "HasJobOnCell", "JobOnCell");
            Add("Tick", "ThoughtWorker", false, "RimWorld.ThoughtWorker", "CurrentStateInternal", "CurrentSocialStateInternal");
            Add("Tick", "IncidentWorker", false, "RimWorld.IncidentWorker", "CanFireNowSub", "TryExecuteWorker");
            Add("Tick", "StatWorker", false, "RimWorld.StatWorker", "GetValueUnfinalized", "FinalizeValue");
            Add("Tick", "StatPart", false, "RimWorld.StatPart", "TransformValue");
            Add("Tick", "HediffGiver", false, "Verse.HediffGiver", "OnIntervalPassed");
            Add("Tick", "Room stat workers", false, "Verse.RoomStatWorker|RimWorld.RoomStatWorker", "GetScore");
            Add("Tick", "Room role workers", false, "Verse.RoomRoleWorker|RimWorld.RoomRoleWorker", "GetScore", "GetScoreDeltaIfBuildingPlaced");
            Add("Tick", "LordToil", false, "Verse.AI.Group.LordToil", "LordToilTick");

            // ---- Update (frame work: drawing) ----
            Add("Update", "Thing draw overrides", true, "Verse.Thing", "DynamicDrawPhaseAt", "DrawAt", "Print");
            Add("Update", "ThingComp draw overrides", true, "Verse.ThingComp", "PostDraw", "DrawAt", "PostPrintOnto");
            Add("Update", "GameComponent update", false, "Verse.GameComponent", "GameComponentUpdate");
            Add("Update", "MapComponent update", false, "Verse.MapComponent", "MapComponentUpdate");
            Add("Update", "WorldComponent update", false, "RimWorld.Planet.WorldComponent", "WorldComponentUpdate");
            Add("Update", "Alert calc", false, "RimWorld.Alert", "GetReport", "AlertActiveUpdate");
            Add("Update", "Graphic draw", false, "Verse.Graphic", "DrawWorker", "Print");
            Add("Update", "Designator update", false, "Verse.Designator|RimWorld.Designator", "SelectedUpdate");
            Add("Update", "Window update", false, "Verse.Window", "WindowUpdate");

            // ---- GUI ----
            Add("GUI", "Thing overlay GUI", true, "Verse.Thing", "DrawGUIOverlay");
            Add("GUI", "ThingComp overlay GUI", true, "Verse.ThingComp", "DrawGUIOverlay");
            Add("GUI", "Window contents", false, "Verse.Window", "DoWindowContents", "ExtraOnGUI");
            Add("GUI", "Inspect tab", false, "Verse.InspectTabBase", "FillTab");
            Add("GUI", "Gizmo", false, "Verse.Gizmo", "GizmoOnGUI");
            Add("GUI", "Alert draw", false, "RimWorld.Alert", "DrawAt");
            Add("GUI", "Need draw", false, "RimWorld.Need", "DrawOnGUI");
            Add("GUI", "GameComponent OnGUI", false, "Verse.GameComponent", "GameComponentOnGUI");
            Add("GUI", "MapComponent OnGUI", false, "Verse.MapComponent", "MapComponentOnGUI");
            Add("GUI", "WorldComponent OnGUI", false, "RimWorld.Planet.WorldComponent", "WorldComponentOnGUI");
            Add("GUI", "Mod settings window", false, "Verse.Mod", "DoSettingsWindowContents");
            Add("GUI", "Designator GUI", false, "Verse.Designator|RimWorld.Designator", "DoExtraGuiControls");
            return l;
        }

        public static int EmptySkipped;

        /// <summary>True when the IL is a single 'ret': hooking it would only measure the probe itself.</summary>
        static bool IsEmptyBody(MethodBase m)
        {
            try
            {
                var body = m.GetMethodBody();
                if (body == null) return false;
                var il = body.GetILAsByteArray();
                return il != null && il.Length == 1 && il[0] == 0x2A;
            }
            catch { return false; }
        }

        static bool Acceptable(MethodInfo m)
        {
            if ((m.MethodImplementationFlags & MethodImplAttributes.InternalCall) != 0) return false;
            var rt = m.ReturnType;
            // iterator methods only build the enumerator when called; timing them is meaningless
            if (rt != typeof(string) && typeof(IEnumerable).IsAssignableFrom(rt)) return false;
            if (typeof(IEnumerator).IsAssignableFrom(rt)) return false;
            return true;
        }

        static HookTarget AddTarget(MethodBase m, string cat, string label, bool track, bool root, string extra)
        {
            IntPtr h;
            try { h = m.MethodHandle.Value; } catch { return null; }
            if (!seen.Add(h)) return null;
            var slot = new Slot
            {
                Category = cat,
                Family = label,
                Mod = ModMap.For(m.DeclaringType.Assembly),
                Type = m.DeclaringType.FullName,
                Name = m.DeclaringType.Name + "." + m.Name,
                Extra = extra,
                IsRoot = root,
                TrackDef = track,
            };
            var tg = new HookTarget { Method = m, Slot = slot, Inst = track };
            Targets.Add(tg);
            ProbeCore.SlotByHandle[h] = slot;
            int c; counts.TryGetValue(label, out c); counts[label] = c + 1;
            return tg;
        }

        public static void Build(ProbeConfig cfg)
        {
            var sw = Stopwatch.StartNew();
            Targets.Clear(); Notes.Clear(); Failures.Clear(); FamilyCounts.Clear();
            seen = new HashSet<IntPtr>();
            counts = new Dictionary<string, int>();
            ProbeCore.SlotByHandle.Clear();
            TickRoot = UpdateRoot = GuiRoot = null;
            PatchedOk = PatchedFail = HarmonyPatchesSkipped = EmptySkipped = 0;

            ScanFamilies(BuildFamilies(cfg));
            AddSinks(cfg);
            if (cfg.HookHarmony) AddHarmonyPatches();
            AddCalibration();

            foreach (var kv in counts) FamilyCounts.Add(kv);
            FamilyCounts.Sort((a, b) => b.Value.CompareTo(a.Value));
            sw.Stop();
            BuildMs = sw.Elapsed.TotalMilliseconds;
        }

        static void ScanFamilies(List<Family> families)
        {
            var ourAsm = typeof(HookCatalog).Assembly;
            var allTypes = new HashSet<Type>();
            try { foreach (var t in GenTypes.AllTypes) allTypes.Add(t); } catch (Exception e) { Notes.Add("GenTypes.AllTypes failed: " + e.Message); }
            try { foreach (var t in CoreAsm.GetTypes()) allTypes.Add(t); } catch (ReflectionTypeLoadException e) { foreach (var t in e.Types) if (t != null) allTypes.Add(t); } catch { }

            foreach (var t in allTypes)
            {
                if (t == null || t.IsInterface || t.ContainsGenericParameters || t.Assembly == ourAsm) continue;
                List<Family> matching = null;
                foreach (var f in families)
                {
                    if (f.Base.IsAssignableFrom(t))
                    {
                        if (matching == null) matching = new List<Family>();
                        matching.Add(f);
                    }
                }
                if (matching == null) continue;

                MethodInfo[] methods;
                try { methods = t.GetMethods(Declared); } catch { continue; }
                foreach (var m in methods)
                {
                    if (m.IsStatic || !m.IsVirtual || m.IsAbstract || m.IsGenericMethod) continue;
                    foreach (var f in matching)
                    {
                        if (Array.IndexOf(f.Names, m.Name) < 0) continue;
                        if (!Acceptable(m)) break;
                        if (IsEmptyBody(m)) { EmptySkipped++; break; }
                        AddTarget(m, f.Cat, f.Label, f.TrackDef, false, null);
                        break;
                    }
                }
            }
        }

        static void AddSinks(ProbeConfig cfg)
        {
            foreach (var entry in Sinks)
            {
                var parts = entry.Split('|');
                string cat = parts[0];
                if (cat == "Tick" && !cfg.HookTick) continue;
                if (cat == "Update" && !cfg.HookUpdate) continue;
                if (cat == "GUI" && !cfg.HookGui) continue;
                bool root = parts.Length > 2 && parts[2] == "root";
                var tm = parts[1].Split(':');
                var type = T(tm[0]);
                if (type == null) { Notes.Add("vanilla hot spot type missing: " + parts[1]); continue; }
                MethodInfo method = null;
                foreach (var m in type.GetMethods(Declared))
                {
                    if (m.Name == tm[1] && !m.IsAbstract && !m.IsGenericMethod) { method = m; break; }
                }
                if (method == null) { Notes.Add("vanilla hot spot method missing: " + parts[1]); continue; }
                var tg = AddTarget(method, cat, "Vanilla hot spot", false, root, null);
                if (tg != null && root)
                {
                    if (cat == "Tick") TickRoot = tg.Slot;
                    else if (cat == "Update") UpdateRoot = tg.Slot;
                    else if (cat == "GUI") GuiRoot = tg.Slot;
                }
            }
        }

        static void AddHarmonyPatches()
        {
            var perMethod = new Dictionary<IntPtr, HookTarget>();
            var extraTargets = new Dictionary<IntPtr, int>();
            List<MethodBase> patched;
            try { patched = Harmony.GetAllPatchedMethods().ToList(); }
            catch (Exception e) { Notes.Add("Harmony.GetAllPatchedMethods failed: " + e.Message); return; }

            var harmonyAsm = typeof(Harmony).Assembly;
            var ourAsm = typeof(HookCatalog).Assembly;

            void Consider(string kind, IEnumerable<Patch> patches, MethodBase target)
            {
                if (patches == null) return;
                foreach (var p in patches)
                {
                    if (p.owner == ProbeCore.HarmonyId) continue;
                    var pm = p.PatchMethod;
                    if (pm == null || pm.DeclaringType == null || pm.IsGenericMethod || pm.DeclaringType.ContainsGenericParameters) continue;
                    var asm = pm.DeclaringType.Assembly;
                    if (asm == harmonyAsm || asm == ourAsm || asm.GetName().Name == "PerformanceAnalyzer") { HarmonyPatchesSkipped++; continue; }
                    if (IsEmptyBody(pm)) { EmptySkipped++; continue; }
                    IntPtr h;
                    try { h = pm.MethodHandle.Value; } catch { continue; }

                    HookTarget existing;
                    if (perMethod.TryGetValue(h, out existing))
                    {
                        int n; extraTargets.TryGetValue(h, out n); extraTargets[h] = n + 1;
                        continue;
                    }
                    string tName = (target.DeclaringType != null ? target.DeclaringType.Name : "?") + "." + target.Name;
                    var tg = AddTarget(pm, "Harmony", "Other mods' Harmony patches", false, false, p.owner + " | " + kind + " on " + tName);
                    if (tg != null) perMethod[h] = tg;
                }
            }

            foreach (var target in patched)
            {
                Patches info;
                try { info = Harmony.GetPatchInfo(target); } catch { continue; }
                if (info == null) continue;
                Consider("Prefix", info.Prefixes, target);
                Consider("Postfix", info.Postfixes, target);
                Consider("Finalizer", info.Finalizers, target);
            }
            foreach (var kv in extraTargets)
            {
                HookTarget tg;
                if (perMethod.TryGetValue(kv.Key, out tg)) tg.Slot.Extra += " (+" + kv.Value + " more targets)";
            }
        }

        static void AddCalibration()
        {
            var a = AddTarget(typeof(Calibration).GetMethod("Empty"), "Calibration", "Calibration", false, false, null);
            var b = AddTarget(typeof(CalibrationTarget).GetMethod("EmptyInst"), "Calibration", "Calibration", true, false, null);
            if (a != null) a.IsCalibration = true;
            if (b != null) b.IsCalibration = true;
        }

        // Patching costs ~3 ms per method, so the session installs/removes hooks in small batches
        // spread over frames (InstallOne / UninstallOne) instead of freezing the game for many seconds.
        static HarmonyMethod preM, finM, preIM, finIM;

        public static void PrepareInstall()
        {
            preM = new HarmonyMethod(typeof(ProbePatch).GetMethod("Pre")) { priority = Priority.First };
            finM = new HarmonyMethod(typeof(ProbePatch).GetMethod("Fin")) { priority = Priority.Last };
            preIM = new HarmonyMethod(typeof(ProbePatchInst).GetMethod("Pre")) { priority = Priority.First };
            finIM = new HarmonyMethod(typeof(ProbePatchInst).GetMethod("Fin")) { priority = Priority.Last };
            PatchedOk = PatchedFail = 0;
            InstallMs = 0;
            foreach (var t in Targets) t.Failed = false;
        }

        public static void InstallOne(Harmony h, HookTarget t)
        {
            long t0 = Stopwatch.GetTimestamp();
            try
            {
                h.Patch(t.Method, prefix: t.Inst ? preIM : preM, finalizer: t.Inst ? finIM : finM);
                PatchedOk++;
            }
            catch (Exception e)
            {
                PatchedFail++;
                t.Failed = true;
                if (Failures.Count < 40)
                {
                    string msg = e.Message ?? e.GetType().Name;
                    if (msg.Length > 160) msg = msg.Substring(0, 160);
                    Failures.Add(t.Slot.Category + " " + t.Slot.Name + ": " + msg.Replace('\n', ' '));
                }
            }
            InstallMs += (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
        }

        public static void UninstallOne(Harmony h, HookTarget t)
        {
            if (t.Failed) return;
            try { h.Unpatch(t.Method, HarmonyPatchType.All, ProbeCore.HarmonyId); }
            catch { }
        }

        /// <summary>Synchronous variant, used by the offline test only.</summary>
        public static void Install(Harmony h)
        {
            PrepareInstall();
            foreach (var t in Targets) InstallOne(h, t);
        }
    }
}
