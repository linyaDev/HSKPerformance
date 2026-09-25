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

        static Dictionary<string, List<Type>> simpleNames;

        /// <summary>Resolves "Full.Name", "A.Name|B.Name" or a bare simple name (must be unique in Assembly-CSharp).</summary>
        static Type T(string names)
        {
            foreach (var n in names.Split('|'))
            {
                Type t = null;
                try { t = CoreAsm.GetType(n); }
                catch (Exception e) { Notes.Add("type " + n + " could not be loaded: " + e.GetType().Name); continue; } // a broken type must never take the whole catalog down
                if (t != null) return t;
                if (n.IndexOf('.') < 0)
                {
                    t = BySimpleName(n);
                    if (t != null) return t;
                }
            }
            return null;
        }

        static Type BySimpleName(string name)
        {
            if (simpleNames == null)
            {
                simpleNames = new Dictionary<string, List<Type>>();
                Type[] all;
                try { all = CoreAsm.GetTypes(); }
                catch (ReflectionTypeLoadException e) { all = e.Types.Where(x => x != null).ToArray(); }
                foreach (var t in all)
                {
                    try
                    {
                        if (t.IsNested) continue;
                        List<Type> list;
                        if (!simpleNames.TryGetValue(t.Name, out list)) simpleNames[t.Name] = list = new List<Type>();
                        list.Add(t);
                    }
                    catch { } // one unloadable type must not hide the others
                }
            }
            List<Type> found;
            if (!simpleNames.TryGetValue(name, out found)) return null;
            if (found.Count == 1) return found[0];
            Notes.Add("type name '" + name + "' is ambiguous (" + found.Count + " matches), use the full name");
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

        // What TickManager.DoSingleTick, Map.MapPreTick/MapPostTick and World.WorldTick actually call (read from the 1.6 source).
        // Hooking every callee turns the "vanilla time not covered by a hook" of those four methods into named parts.
        static readonly string[] TickInternals =
        {
            // DoSingleTick
            "Tick|TickList:Tick", "Tick|DateNotifier:DateNotifierTick", "Tick|Scenario:TickScenario", "Tick|World:WorldPostTick",
            "Tick|StoryWatcher:StoryWatcherTick", "Tick|GameEnder:GameEndTick", "Tick|TaleManager:TaleManagerTick", "Tick|History:HistoryTick",
            "Tick|GameComponentUtility:GameComponentTick", "Tick|LetterStack:LetterStackTick", "Tick|Autosaver:AutosaverTick",
            "Tick|FilthMonitor:FilthMonitorTick", "Tick|TransportShipManager:ShipObjectsTick",
            // MapPreTick
            "Tick|ItemAvailability:Tick", "Tick|ListerHaulables:ListerHaulablesTick", "Tick|AutoBuildRoofAreaSetter:AutoBuildRoofAreaSetterTick_First",
            "Tick|RoofCollapseBufferResolver:CollapseRoofsMarkedToCollapse", "Tick|WindManager:WindManagerTick", "Tick|MapTemperature:MapTemperatureTick",
            "Tick|TemporaryThingDrawer:Tick", "Tick|Verse.PathFinder:PathFinderTick",
            // MapPostTick
            "Tick|WildAnimalSpawner:WildAnimalSpawnerTick", "Tick|WildPlantSpawner:WildPlantSpawnerTick", "Tick|PowerNetManager:PowerNetsTick",
            "Tick|SteadyEnvironmentEffects:SteadyEnvironmentEffectsTick", "Tick|TempTerrainManager:Tick", "Tick|GasGrid:Tick", "Tick|PollutionGrid:PollutionTick",
            "Tick|DeferredSpawner:DeferredSpawnerTick", "Tick|LordManager:LordManagerTick", "Tick|PassingShipManager:PassingShipManagerTick",
            "Tick|VoluntarilyJoinableLordsStarter:VoluntarilyJoinableLordsStarterTick", "Tick|GameConditionManager:GameConditionManagerTick",
            "Tick|WeatherManager:WeatherManagerTick", "Tick|ResourceCounter:ResourceCounterTick", "Tick|WeatherDecider:WeatherDeciderTick",
            "Tick|FireWatcher:FireWatcherTick", "Tick|WaterBodyTracker:Tick", "Tick|FleckManager:FleckManagerTick", "Tick|EffecterMaintainer:EffecterMaintainerTick",
            "Tick|MapComponentUtility:MapComponentTick",
            // WorldTick
            "Tick|FactionManager:FactionManagerTick", "Tick|WorldDebugDrawer:WorldDebugDrawerTick", "Tick|WorldPathGrid:WorldPathGridTick",
            "Tick|WorldComponentUtility:WorldComponentTick", "Tick|IdeoManager:IdeoManagerTick", "Tick|WorldObject:DoTick",
            // pathfinding and job execution, used by pawns but not inside a pawn tracker
            "Tick|Verse.PathFinder:FindPathNow", "Tick|JobDriver:DriverTick",
        };

        // What Pawn.Tick and Pawn.TickInterval call. Called for every pawn, so this group has the highest measurement overhead.
        static readonly string[] PawnTrackers =
        {
            "Tick|VerbTracker:VerbsTick", "Tick|Pawn_RopeTracker:RopingTick", "Tick|Pawn_FlightTracker:FlightTick", "Tick|Pawn_NativeVerbs:NativeVerbsTick",
            "Tick|Pawn_StanceTracker:StanceTrackerTick", "Tick|Pawn_EquipmentTracker:EquipmentTrackerTick", "Tick|Pawn_AbilityTracker:AbilitiesTick",
            "Tick|Pawn_InventoryTracker:InventoryTrackerTick", "Tick|Pawn_GeneTracker:GeneTrackerTick", "Tick|PawnRenderer:EffectersTick",
            "Tick|Pawn_ApparelTracker:ApparelTrackerTickRare", "Tick|Pawn_TrainingTracker:TrainingTrackerTickRare",
            "Tick|Pawn_CarryTracker:CarryHandsTickInterval", "Tick|Pawn_InfectionVectorTracker:InfectionTickInterval",
            "Tick|Pawn_ApparelTracker:ApparelTrackerTickInterval", "Tick|Pawn_InteractionsTracker:InteractionsTrackerTickInterval",
            "Tick|Pawn_CallTracker:CallTrackerTickInterval", "Tick|Pawn_SkillTracker:SkillsTickInterval", "Tick|Pawn_DraftController:DraftControllerTickInterval",
            "Tick|Pawn_RelationsTracker:RelationsTrackerTickInterval", "Tick|Pawn_PsychicEntropyTracker:PsychicEntropyTrackerTickInterval",
            "Tick|Pawn_GuestTracker:GuestTrackerTickInterval", "Tick|Pawn_IdeoTracker:IdeoTrackerTickInterval", "Tick|Pawn_GeneTracker:GeneTrackerTickInterval",
            "Tick|Pawn_RoyaltyTracker:RoyaltyTrackerTickInterval", "Tick|Pawn_StyleTracker:StyleTrackerTickInterval",
            "Tick|Pawn_StyleObserverTracker:StyleObserverTickInterval", "Tick|Pawn_SurroundingsTracker:SurroundingsTrackerTickInterval",
            "Tick|Pawn_LearningTracker:LearningTickInterval", "Tick|PollutionUtility:PawnPollutionTickInterval", "Tick|GasUtility:PawnGasEffectsTickInterval",
            "Tick|ToxicUtility:PawnToxicTickInterval", "Tick|VacuumUtility:PawnVacuumTickInterval", "Tick|Pawn_AgeTracker:AgeTickInterval",
            "Tick|Pawn_RecordsTracker:RecordsTickInterval", "Tick|Pawn_GuiltTracker:GuiltTrackerTickInterval", "Tick|PawnUtility:GainComfortFromThingIfPossible",
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
        internal static bool IsEmptyBody(MethodBase m)
        {
            int size = ILSize(m);
            if (size != 1) return false;
            try { return m.GetMethodBody().GetILAsByteArray()[0] == 0x2A; }
            catch { return false; }
        }

        /// <summary>Size of the method's IL in bytes, or -1 when it cannot be read.</summary>
        internal static int ILSize(MethodBase m)
        {
            try
            {
                var body = m.GetMethodBody();
                if (body == null) return -1;
                var il = body.GetILAsByteArray();
                return il == null ? -1 : il.Length;
            }
            catch { return -1; }
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
            AddSinkList(cfg, Sinks, "Vanilla hot spot");
            if (cfg.HookVanillaInternals) AddSinkList(cfg, TickInternals, "Vanilla tick internals");
            if (cfg.HookPawnTrackers) AddSinkList(cfg, PawnTrackers, "Pawn trackers");
        }

        static void AddSinkList(ProbeConfig cfg, string[] entries, string label)
        {
            foreach (var entry in entries)
            {
                var parts = entry.Split('|');
                string cat = parts[0];
                if (cat == "Tick" && !cfg.HookTick) continue;
                if (cat == "Update" && !cfg.HookUpdate) continue;
                if (cat == "GUI" && !cfg.HookGui) continue;
                bool root = parts.Length > 2 && parts[2] == "root";
                var tm = parts[1].Split(':');
                var type = T(tm[0]);
                if (type == null) { Notes.Add(label + ": type missing: " + parts[1]); continue; }
                // every non-generic overload (FindPathNow has two), not just the first one
                int hooked = 0;
                MethodInfo[] methods;
                try { methods = type.GetMethods(Declared); }
                catch (Exception e) { Notes.Add(label + ": " + parts[1] + " could not be inspected: " + e.GetType().Name); continue; }
                foreach (var m in methods)
                {
                    if (m.Name != tm[1] || m.IsAbstract || m.IsGenericMethod) continue;
                    var tg = AddTarget(m, cat, label, false, root, null);
                    hooked++;
                    if (tg != null && root)
                    {
                        if (cat == "Tick") TickRoot = tg.Slot;
                        else if (cat == "Update") UpdateRoot = tg.Slot;
                        else if (cat == "GUI") GuiRoot = tg.Slot;
                    }
                }
                if (hooked == 0) Notes.Add(label + ": method missing: " + parts[1]);
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
