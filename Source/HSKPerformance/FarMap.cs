using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace HSKPerformance
{
    /// <summary>
    /// "Far map": when the camera is zoomed out so far that a map cell is smaller than a threshold, the game stops drawing the map and
    /// everything on it except pawns, and we draw a simple map instead: one colour for the background, one for constructed walls, one for natural rock,
    /// all from a single texture (one pixel per cell) drawn as one quad. Pawns, animals and enemies are still drawn by the game (and reduced to dots by Camera+).
    ///
    /// Why: at the farthest zoom the map costs about 5800 Graphics.DrawMesh calls (280 sections, ~20 layers each) plus ~660 dynamic things
    /// and the camera has to submit all of it. Measured: about 15 of the 22 ms per frame that are not ticks.
    ///
    /// Skipped while active: MapDrawer.DrawMapMesh (terrain, buildings, plants, items, lighting, fog, roofs, zones, shadows), every dynamic thing that
    /// is not a pawn, overlays, designations, temporary things, fleck drawing and game condition overlays.
    /// Any exception switches the mode off for the rest of the session and the game draws as usual.
    /// </summary>
    public static class FarMap
    {
        public static bool Installed;
        public static bool Enabled = true;
        public static float ThresholdPx = 17f;

        /// <summary>Size of a map cell on screen in pixels at the last check (0 before the first one), for the settings window.</summary>
        public static float CurrentPx { get { return lastPx; } }

        // ---- looks: three colours, set from the config / settings window as RRGGBB (dark on purpose so that the pawn dots stand out)
        public const string DefaultWall = "6E7680", DefaultRock = "3A302A", DefaultBackground = "121614";
        static Color32 WallColor = new Color32(0x6E, 0x76, 0x80, 255);
        static Color32 RockColor = new Color32(0x3A, 0x30, 0x2A, 255);
        static Color32 WaterColor = new Color32(0x26, 0x39, 0x4A, 255);   // very pale: only slightly lighter than the background
        static Color32 MarshColor = new Color32(0x2B, 0x3A, 0x2A, 255);
        static Color32 DoorColor = new Color32(0x43, 0x4B, 0x53, 255);    // between the background and the walls: faint
        static Color32 BuildingColor = new Color32(0x2D, 0x34, 0x3A, 255);  // buildings that cannot be walked through (big machines, blocks): very pale
        static Color FireSpotColor = new Color(0xB5 / 255f, 0x40 / 255f, 0x0F / 255f, 1f);   // dark orange spot under the flame so that a fire is visible at nine pixels per cell
        static Material fireSpotMaterial;
        static Color32 StorageColor = new Color32(0x3A, 0x33, 0x48, 255);   // impassable storage buildings: another pale colour
        static readonly Color32 Clear = new Color32(0, 0, 0, 0);
        static Color BackgroundColor = new Color(0x12 / 255f, 0x16 / 255f, 0x14 / 255f, 1f);
        static bool colorsChanged;

        static bool TryParseHex(string hex, out Color32 c)
        {
            c = default(Color32);
            if (string.IsNullOrEmpty(hex)) return false;
            hex = hex.Trim().TrimStart('#');
            if (hex.Length != 6) return false;
            int rgb;
            if (!int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out rgb)) return false;
            c = new Color32((byte)((rgb >> 16) & 255), (byte)((rgb >> 8) & 255), (byte)(rgb & 255), 255);
            return true;
        }

        /// <summary>Sets the colours from three RRGGBB strings; a string that is not valid keeps the current colour. The texture is redrawn on the next far map frame.</summary>
        public static void SetColors(ProbeConfig cfg)
        {
            SetColors(cfg.FarMapWall, cfg.FarMapRock, cfg.FarMapBackground, cfg.FarMapWater, cfg.FarMapMarsh, cfg.FarMapDoor, cfg.FarMapBuilding, cfg.FarMapStorage, cfg.FarMapFire);
        }

        static void SetColors(string wall, string rock, string background, string water, string marsh, string door, string building, string storage, string fire)
        {
            Color32 c;
            bool changed = false;
            if (TryParseHex(wall, out c) && !c.Equals(WallColor)) { WallColor = c; changed = true; }
            if (TryParseHex(rock, out c) && !c.Equals(RockColor)) { RockColor = c; changed = true; }
            if (TryParseHex(water, out c) && !c.Equals(WaterColor)) { WaterColor = c; changed = true; }
            if (TryParseHex(marsh, out c) && !c.Equals(MarshColor)) { MarshColor = c; changed = true; }
            if (TryParseHex(door, out c) && !c.Equals(DoorColor)) { DoorColor = c; changed = true; }
            if (TryParseHex(building, out c) && !c.Equals(BuildingColor)) { BuildingColor = c; changed = true; }
            if (TryParseHex(storage, out c) && !c.Equals(StorageColor)) { StorageColor = c; changed = true; }
            if (TryParseHex(fire, out c))
            {
                var f = new Color(c.r / 255f, c.g / 255f, c.b / 255f, 1f);
                if (f != FireSpotColor) { FireSpotColor = f; fireSpotMaterial = null; }
            }
            if (TryParseHex(background, out c))
            {
                var bg = new Color(c.r / 255f, c.g / 255f, c.b / 255f, 1f);
                if (bg != BackgroundColor) { BackgroundColor = bg; backgroundMaterial = null; changed = true; }
            }
            if (changed) colorsChanged = true;
        }

        // ---- state
        static bool failed;
        static int decidedFrame = -1;
        static bool activeNow;
        static float lastPx;
        static long activeFrames, inactiveFrames, rebuilds, dirtyApplied;

        static Map texMap;
        static Texture2D tex;
        static Color32[] pixels;
        static Material wallMaterial, backgroundMaterial;
        static readonly HashSet<int> dirty = new HashSet<int>();
        static bool needApply;
        static float lastApply;

        static FieldInfo drawThingsField;
        static readonly List<Thing> pawnsOnly = new List<Thing>();

        // ------------------------------------------------------------------ install

        public static void Apply(Harmony harmony, ProbeConfig cfg)
        {
            Enabled = cfg.FixFarMap;
            ThresholdPx = cfg.FarMapPx;
            SetColors(cfg);
            var missing = new List<string>();
            int patched = 0;

            Func<Type, string, Type[], MethodInfo> find = (t, name, args) => t == null ? null : AccessTools.Method(t, name, args);
            var skip = new HarmonyMethod(typeof(FarMap).GetMethod("SkipWhenFar", BindingFlags.Static | BindingFlags.NonPublic)) { priority = Priority.First };

            // 1) the map mesh: replaced by our own drawing
            var drawMesh = find(typeof(MapDrawer), "DrawMapMesh", Type.EmptyTypes);
            if (drawMesh != null)
            {
                harmony.Patch(drawMesh, prefix: new HarmonyMethod(typeof(FarMap).GetMethod("DrawMapMeshPrefix", BindingFlags.Static | BindingFlags.NonPublic)) { priority = Priority.First });
                patched++;
            }
            else missing.Add("MapDrawer.DrawMapMesh");

            // 2) dynamic things: only pawns stay in the list while the mode is on
            drawThingsField = AccessTools.Field(typeof(DynamicDrawManager), "drawThings");
            var dyn = find(typeof(DynamicDrawManager), "DrawDynamicThings", Type.EmptyTypes);
            if (dyn != null && drawThingsField != null && typeof(List<Thing>).IsAssignableFrom(drawThingsField.FieldType))
            {
                harmony.Patch(dyn,
                    prefix: new HarmonyMethod(typeof(FarMap).GetMethod("DynamicPrefix", BindingFlags.Static | BindingFlags.NonPublic)) { priority = Priority.First },
                    finalizer: new HarmonyMethod(typeof(FarMap).GetMethod("DynamicFinalizer", BindingFlags.Static | BindingFlags.NonPublic)));
                patched++;
            }
            else missing.Add("DynamicDrawManager.DrawDynamicThings / drawThings");

            // 3) other things drawn every frame that the far map does not show
            foreach (var m in new[]
            {
                find(typeof(OverlayDrawer), "DrawAllOverlays", Type.EmptyTypes),
                find(typeof(DesignationManager), "DrawDesignations", Type.EmptyTypes),
                find(typeof(TemporaryThingDrawer), "Draw", Type.EmptyTypes),
                find(typeof(FleckManager), "FleckManagerDraw", Type.EmptyTypes),
                find(typeof(GameConditionManager), "GameConditionManagerDraw", new[] { typeof(Map) }),
            })
            {
                if (m != null && m.ReturnType == typeof(void)) { harmony.Patch(m, prefix: skip); patched++; }
                else missing.Add("one of OverlayDrawer/DesignationManager/TemporaryThingDrawer/FleckManager/GameConditionManager draw methods");
            }

            // 4) keep the wall texture up to date: a cell changed
            var dirtyM = find(typeof(MapDrawer), "MapMeshDirty", new[] { typeof(IntVec3), typeof(ulong), typeof(bool), typeof(bool) });
            if (dirtyM != null)
            {
                harmony.Patch(dirtyM, postfix: new HarmonyMethod(typeof(FarMap).GetMethod("DirtyPostfix", BindingFlags.Static | BindingFlags.NonPublic)));
                patched++;
            }
            else missing.Add("MapDrawer.MapMeshDirty");

            // 5) the Dubs Mint Minimap window shows the same map, so it is not drawn while the far map is active (soft dependency)
            var mini = AccessTools.TypeByName("DubsMintMinimap.MainTabWindow_MiniMap");
            var miniOnGui = mini == null ? null : AccessTools.Method(mini, "WindowOnGUI", Type.EmptyTypes);
            if (miniOnGui != null && miniOnGui.ReturnType == typeof(void))
            {
                harmony.Patch(miniOnGui, prefix: skip);
                PerfFixes.Status.Add("far map: Dubs Mint Minimap window is hidden while the far map is active");
            }

            Installed = patched > 0 && missing.Count == 0;
            PerfFixes.Status.Add("far map: " + (Installed ? (Enabled ? "ON" : "installed but OFF") : "NOT installed") + " (" + patched + " patches; the map is replaced by walls/rock/background when a cell is smaller than "
                + ThresholdPx.ToString("F1", CultureInfo.InvariantCulture) + " px on screen)" + (missing.Count > 0 ? "; NOT found: " + string.Join(", ", missing.ToArray()) : ""));
            if (!Installed) Enabled = false;
        }

        public static string Live()
        {
            return "far map live: " + (failed ? "SWITCHED OFF after an error" : (activeNow ? "ACTIVE" : "off")) + ", cell " + lastPx.ToString("F1", CultureInfo.InvariantCulture)
                + " px (threshold " + ThresholdPx.ToString("F1", CultureInfo.InvariantCulture) + "), " + activeFrames + " frames drawn, " + rebuilds + " texture rebuilds, " + dirtyApplied + " cells updated, " + selectedDrawn + " selected things drawn, " + firesDrawn + " fires drawn";
        }

        public static string ContextLine()
        {
            return (failed ? "off (error)" : (Enabled ? (activeNow ? "ACTIVE" : "enabled, not active") : "disabled")) + ", cell " + lastPx.ToString("F1", CultureInfo.InvariantCulture)
                + " px, threshold " + ThresholdPx.ToString("F1", CultureInfo.InvariantCulture);
        }

        // ------------------------------------------------------------------ decision

        /// <summary>True while the far map replaces the normal drawing. Cached per frame, with a little hysteresis at the threshold.</summary>
        public static bool Active()
        {
            int frame = Time.frameCount;
            if (frame == decidedFrame) return activeNow;
            decidedFrame = frame;
            bool on = false;
            try
            {
                if (Enabled && !failed && Current.ProgramState == ProgramState.Playing && Find.CurrentMap != null && WorldRendererUtility.DrawingMap && Find.CameraDriver != null)
                {
                    lastPx = Screen.height / (2f * Find.CameraDriver.RootSize);
                    on = activeNow ? lastPx < ThresholdPx * 1.05f : lastPx < ThresholdPx;
                }
            }
            catch (Exception e) { Fail(e); on = false; }
            if (on) activeFrames++; else inactiveFrames++;
            if (on != activeNow) { activeNow = on; if (!on) dirty.Clear(); }
            return on;
        }

        static void Fail(Exception e)
        {
            if (failed) return;
            failed = true; activeNow = false;
            try { Verse.Log.Error("[HSK Performance] far map switched off after an error, normal drawing restored: " + e); } catch { }
        }

        // ------------------------------------------------------------------ patches

        static bool SkipWhenFar() { return !Active(); }

        static bool DrawMapMeshPrefix()
        {
            if (!Active()) return true;
            try
            {
                DrawFar(Find.CurrentMap);
                return false;
            }
            catch (Exception e) { Fail(e); return true; }
        }

        static bool DynamicPrefix(DynamicDrawManager __instance, out List<Thing> __state)
        {
            __state = null;
            if (!Active()) return true;
            try
            {
                var original = (List<Thing>)drawThingsField.GetValue(__instance);
                pawnsOnly.Clear();
                for (int i = 0; i < original.Count; i++)
                    if (original[i] is Pawn || original[i] is PawnFlyer) pawnsOnly.Add(original[i]);   // Camera+ also draws markers for flying pawns
                drawThingsField.SetValue(__instance, pawnsOnly);
                __state = original;
            }
            catch (Exception e) { Fail(e); }
            return true;
        }

        static void DynamicFinalizer(DynamicDrawManager __instance, List<Thing> __state)
        {
            if (__state == null) return;
            try { drawThingsField.SetValue(__instance, __state); }
            catch (Exception e) { Fail(e); }
        }

        static void DirtyPostfix(IntVec3 loc)
        {
            if (!activeNow || texMap == null) return;
            if (loc.InBounds(texMap)) dirty.Add(texMap.cellIndices.CellToIndex(loc));
        }

        // ------------------------------------------------------------------ drawing

        static void DrawFar(Map map)
        {
            EnsureTexture(map);

            // apply the cells that changed, but not more often than 4 times a second
            if (dirty.Count > 0)
            {
                foreach (int idx in dirty) pixels[idx] = PixelFor(map, idx);
                dirtyApplied += dirty.Count;
                dirty.Clear();
                needApply = true;
            }
            if (needApply && Time.realtimeSinceStartup - lastApply >= 0.25f)
            {
                tex.SetPixels32(pixels);
                tex.Apply(false);
                needApply = false;
                lastApply = Time.realtimeSinceStartup;
            }

            int w = map.Size.x, h = map.Size.z;
            var centre = new Vector3(w * 0.5f, 0f, h * 0.5f);
            var scale = new Vector3(w, 1f, h);
            var bg = centre; bg.y = AltitudeLayer.Terrain.AltitudeFor();
            var wall = centre; wall.y = AltitudeLayer.Building.AltitudeFor();
            Graphics.DrawMesh(MeshPool.plane10, Matrix4x4.TRS(bg, Quaternion.identity, scale), backgroundMaterial, 0);
            Graphics.DrawMesh(MeshPool.plane10, Matrix4x4.TRS(wall, Quaternion.identity, scale), wallMaterial, 0);
            DrawFires(map);
            DrawSelectedThings(map);
        }

        static long firesDrawn;
        static bool fireErrorLogged;

        /// <summary>
        /// Everything that burns is shown: the game's own animated flame texture (Fire.DrawNowAt, the same sprite the game draws) on top of a small dark orange spot
        /// that keeps it visible when a map cell is only a few pixels. Burning buildings and pawns have a Fire thing too, so they show up as well.
        /// </summary>
        static void DrawFires(Map map)
        {
            var fires = map.listerThings.ThingsOfDef(ThingDefOf.Fire);
            if (fires == null || fires.Count == 0) return;
            if (fireSpotMaterial == null) fireSpotMaterial = SolidColorMaterials.SimpleSolidColorMaterial(FireSpotColor);
            int count = Math.Min(fires.Count, 400);
            for (int i = 0; i < count; i++)
            {
                var fire = fires[i] as Fire;
                if (fire == null || !fire.Spawned) continue;
                try
                {
                    var pos = fire.DrawPos;
                    float size = Mathf.Clamp(0.9f + fire.fireSize * 0.8f, 1f, 2.2f);   // in cells: a bigger fire, a bigger spot
                    var spot = new Vector3(pos.x, AltitudeLayer.BuildingOnTop.AltitudeFor(), pos.z);
                    Graphics.DrawMesh(MeshPool.plane10, Matrix4x4.TRS(spot, Quaternion.identity, new Vector3(size, 1f, size)), fireSpotMaterial, 0);
                    fire.DrawNowAt(pos);
                    firesDrawn++;
                }
                catch (Exception e)
                {
                    if (!fireErrorLogged)
                    {
                        fireErrorLogged = true;
                        try { Verse.Log.Warning("[HSK Performance] far map: could not draw a fire: " + e.GetType().Name + " " + e.Message); } catch { }
                    }
                }
            }
        }

        static long selectedDrawn;
        static bool selectedErrorLogged;

        /// <summary>
        /// A building or item the player has selected is drawn as it really looks. Most things are printed into the map mesh (which is not drawn now),
        /// so the graphic is drawn directly: MapMeshOnly things with Graphic.Draw, real-time things with their own DrawAt (doors, animated benches),
        /// and things that are both get the two. Pawns are not handled here (the game draws them).
        /// </summary>
        static void DrawSelectedThings(Map map)
        {
            var selector = Find.Selector;
            if (selector == null) return;
            var list = selector.SelectedObjectsListForReading;
            for (int i = 0; i < list.Count; i++)
            {
                var t = list[i] as Thing;
                if (t == null || t is Pawn || !t.Spawned || t.Map != map || t.def == null) continue;
                try
                {
                    var drawer = t.def.drawerType;
                    if (drawer == DrawerType.None) continue;
                    var pos = t.DrawPos;
                    if (drawer != DrawerType.RealtimeOnly) t.Graphic.Draw(pos, t.Rotation, t);   // the printed body
                    if (drawer != DrawerType.MapMeshOnly) t.DrawNowAt(pos);                        // real-time part: doors, animation, comps
                    selectedDrawn++;
                }
                catch (Exception e)
                {
                    if (!selectedErrorLogged)
                    {
                        selectedErrorLogged = true;
                        try { Verse.Log.Warning("[HSK Performance] far map: could not draw the selected " + t + ": " + e.GetType().Name + " " + e.Message); } catch { }
                    }
                }
            }
        }

        /// <summary>One cell: door, rock, wall, an impassable storage building or other impassable building, then marsh and water (very pale), otherwise transparent so the background shows.</summary>
        static Color32 PixelFor(Map map, int index)
        {
            var b = map.edificeGrid[index];
            if (b != null)
            {
                if (b is Building_Door) return DoorColor;
                var def = b.def;
                if (def.building != null && def.building.isNaturalRock) return RockColor;
                var g = def.graphicData;
                if (g != null && (g.linkFlags & LinkFlags.Wall) != 0) return WallColor;
                if (def.passability == Traversability.Impassable)
                {
                    // only what cannot be walked through: storage buildings (Adaptive Storage derives from Building_Storage) and other buildings in their own pale colours
                    return b is Building_Storage ? StorageColor : BuildingColor;
                }
            }
            var t = map.terrainGrid.TerrainAt(index);
            if (t != null)
            {
                // marsh is a shallow water terrain too, so it has to be checked first; marshy soil (MarshyTerrain) is not drawn
                if (t == TerrainDefOf.Marsh || string.Equals(t.defName, "Marsh", StringComparison.Ordinal)) return MarshColor;
                if (t.IsWater) return WaterColor;
            }
            return Clear;
        }

        static void EnsureTexture(Map map)
        {
            int w = map.Size.x, h = map.Size.z;
            bool fresh = tex == null || texMap != map || tex.width != w || tex.height != h;
            bool recolor = colorsChanged;
            colorsChanged = false;
            if (fresh)
            {
                if (tex != null) UnityEngine.Object.Destroy(tex);
                tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
                pixels = new Color32[w * h];
                texMap = map;
                wallMaterial = MaterialPool.MatFrom(tex, ShaderDatabase.Transparent, Color.white);
            }
            else if (activeFramesSinceLastRebuildNeeded() || recolor) fresh = true;
            if (backgroundMaterial == null) backgroundMaterial = SolidColorMaterials.SimpleSolidColorMaterial(BackgroundColor);

            if (fresh)
            {
                int n = w * h;
                for (int i = 0; i < n; i++) pixels[i] = PixelFor(map, i);
                dirty.Clear();
                tex.SetPixels32(pixels);
                tex.Apply(false);
                lastApply = Time.realtimeSinceStartup;
                needApply = false;
                rebuilds++;
                lastRebuiltFrame = Time.frameCount;
            }
        }

        // The dirty set is only filled while the mode is active, so after a pause in it (normal zoom for a while) the texture may be stale: rebuild once on re-entry.
        static int lastRebuiltFrame = -1, lastActiveFrame = -1;
        static bool activeFramesSinceLastRebuildNeeded()
        {
            int frame = Time.frameCount;
            bool gap = lastActiveFrame >= 0 && frame - lastActiveFrame > 1;
            lastActiveFrame = frame;
            return gap;
        }
    }
}
