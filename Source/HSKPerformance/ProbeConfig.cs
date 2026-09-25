using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Verse;

namespace HSKPerformance
{
    /// <summary>Tiny key=value config, created with defaults on first run.</summary>
    public sealed class ProbeConfig
    {
        public int DurationSeconds = 45;
        public int WarmupSeconds = 3;
        public bool HookTick = true;
        public bool HookUpdate = true;
        public bool HookGui = true;
        public bool HookHarmony = true;
        public bool HookVanillaInternals = true; // name the parts of DoSingleTick, MapPreTick/PostTick and WorldTick
        public bool HookPawnTrackers = true;     // name the trackers inside Pawn.Tick/TickInterval (many calls, more overhead)
        public bool TwoPhase = true;             // one Ctrl+F9 = light recording, then automatically a full one (LightMode is then forced by the session)
        public bool LightMode = false;           // only the three vanilla roots are hooked: almost no probe overhead, no per-mod numbers
        public int TopMods = 40;
        public int TopMethods = 60;
        public int TopDefs = 30;

        // ---- fixes (applied at game start, independent of recording) ----
        public bool FixRotStorage = true;      // O(1) storage lookup in Core_SK's CompBetterRottable_Patch
        public int FixRotVerifyCalls = 1000;   // compare fast and original result for the first N calls (0 = trust blindly)
        public int WorldPawnRows = 300;        // rows in world_pawns.md (the CSV always has all of them)
        // Footprints, breath vapour, water ripples and the smoke of wrecks (HSK Core) are cosmetic flecks that the game ticks every tick. Skipped at game speed >= this (3 = Fast, the second speed).
        // Far map: when a cell is smaller than far_map_px on screen, draw walls, rock and a background colour instead of the map, and only pawns on top.
        public bool FixFarMap = true;
        public float FarMapPx = 17f;
        public string FarMapWall = "6E7680", FarMapRock = "3A302A", FarMapBackground = "121614";   // RRGGBB
        public string FarMapWater = "26394A", FarMapMarsh = "2B3A2A", FarMapDoor = "434B53", FarMapBuilding = "2D343A", FarMapStorage = "3A3348";
        // Camera+ marker outlines: colonists green, wild predators red (only where the player has no Camera+ rule for the pawn).
        public bool FixMarkerColors = true;
        public string MarkerColonist = "33E055", MarkerPredator = "E63030";   // RRGGBB
        public string MarkerGuest = "8FD4F5", MarkerPrisoner = "FF9500", MarkerSlave = "F0D020", MarkerDrone = "747E88";
        public string MarkerDronePrefixes = "Drone,AIRobot_";   // race defName prefixes (comma separated) that get the pale drone fill
        public bool FixPawnEffects = true;
        public float PawnEffectsMinSpeed = 3f;
        public bool FixMothballHediffs = true; // let world pawns with these chronic hediffs be mothballed (see PerfFixes)
        // defNames, a trailing * means "starts with". Mothballing only freezes a pawn (age and records keep counting, hediffs stop progressing).
        public string[] MothballHediffs = { "DimonSever000_*", "Malnutrition", "GeneticDrugNeed", "HemogenCraving" };

        public static string DirOverride; // tests only

        public static string Dir
        {
            get { return DirOverride ?? Path.Combine(GenFilePaths.SaveDataFolderPath, "HSKPerformance"); }
        }

        static string FilePath { get { return Path.Combine(Dir, "config.txt"); } }

        static string B(bool v) { return v ? "true" : "false"; }

        /// <summary>Writes every key back (used by the mod settings window and the Ctrl+F8 shortcut).</summary>
        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllText(FilePath,
                    "# HSKPerformance config. Delete a line to get the default.\n" +
                    "duration_seconds=" + DurationSeconds.ToString(CultureInfo.InvariantCulture) + "\n" +
                    "warmup_seconds=" + WarmupSeconds.ToString(CultureInfo.InvariantCulture) + "\n" +
                    "hook_tick=" + B(HookTick) + "\n" +
                    "hook_update=" + B(HookUpdate) + "\n" +
                    "hook_gui=" + B(HookGui) + "\n" +
                    "hook_harmony_patches=" + B(HookHarmony) + "\n" +
                    "hook_vanilla_internals=" + B(HookVanillaInternals) + "\n" +
                    "hook_pawn_trackers=" + B(HookPawnTrackers) + "\n" +
                    "two_phase=" + B(TwoPhase) + "\n" +
                    "light_mode=" + B(LightMode) + "\n" +
                    "top_mods=" + TopMods.ToString(CultureInfo.InvariantCulture) + "\n" +
                    "top_methods=" + TopMethods.ToString(CultureInfo.InvariantCulture) + "\n" +
                    "top_defs=" + TopDefs.ToString(CultureInfo.InvariantCulture) + "\n" +
                    "fix_rot_storage=" + B(FixRotStorage) + "\n" +
                    "fix_rot_verify_calls=" + FixRotVerifyCalls.ToString(CultureInfo.InvariantCulture) + "\n" +
                    "world_pawn_rows=" + WorldPawnRows.ToString(CultureInfo.InvariantCulture) + "\n" +
                    "fix_far_map=" + B(FixFarMap) + "\n" +
                    "far_map_px=" + FarMapPx.ToString(CultureInfo.InvariantCulture) + "\n" +
                    "far_map_wall=" + FarMapWall + "\n" +
                    "far_map_rock=" + FarMapRock + "\n" +
                    "far_map_background=" + FarMapBackground + "\n" +
                    "far_map_water=" + FarMapWater + "\n" +
                    "far_map_marsh=" + FarMapMarsh + "\n" +
                    "far_map_door=" + FarMapDoor + "\n" +
                    "far_map_building=" + FarMapBuilding + "\n" +
                    "far_map_storage=" + FarMapStorage + "\n" +
                    "fix_marker_colors=" + B(FixMarkerColors) + "\n" +
                    "marker_colonist=" + MarkerColonist + "\n" +
                    "marker_predator=" + MarkerPredator + "\n" +
                    "marker_guest=" + MarkerGuest + "\n" +
                    "marker_prisoner=" + MarkerPrisoner + "\n" +
                    "marker_slave=" + MarkerSlave + "\n" +
                    "marker_drone=" + MarkerDrone + "\n" +
                    "marker_drone_prefixes=" + MarkerDronePrefixes + "\n" +
                    "fix_pawn_effects=" + B(FixPawnEffects) + "\n" +
                    "pawn_effects_min_speed=" + PawnEffectsMinSpeed.ToString(CultureInfo.InvariantCulture) + "\n" +
                    "fix_mothball_hediffs=" + B(FixMothballHediffs) + "\n" +
                    "mothball_hediffs=" + string.Join(",", MothballHediffs) + "\n");
            }
            catch (Exception e)
            {
                Log.Warning("[HSK Performance] could not save config: " + e.Message);
            }
        }

        public static ProbeConfig Load()
        {
            var c = new ProbeConfig();
            try
            {
                Directory.CreateDirectory(Dir);
                if (!File.Exists(FilePath))
                {
                    File.WriteAllText(FilePath,
                        "# HSKPerformance config. Delete a line to get the default.\n" +
                        "duration_seconds=45\n" +
                        "warmup_seconds=3\n" +
                        "hook_tick=true\n" +
                        "hook_update=true\n" +
                        "hook_gui=true\n" +
                        "hook_harmony_patches=true\n" +
                        "hook_vanilla_internals=true\n" +
                        "hook_pawn_trackers=true\n" +
                        "two_phase=true\n" +
                        "light_mode=false\n" +
                        "top_mods=40\n" +
                        "top_methods=60\n" +
                        "top_defs=30\n" +
                        "fix_rot_storage=true\n" +
                        "fix_rot_verify_calls=1000\n" +
                        "world_pawn_rows=300\n" +
                        "fix_far_map=true\n" +
                        "far_map_px=17\n" +
                        "far_map_wall=6E7680\n" +
                        "far_map_rock=3A302A\n" +
                        "far_map_background=121614\n" +
                        "far_map_water=26394A\n" +
                        "far_map_marsh=2B3A2A\n" +
                        "far_map_door=434B53\n" +
                        "far_map_building=2D343A\n" +
                        "far_map_storage=3A3348\n" +
                        "fix_marker_colors=true\n" +
                        "marker_colonist=33E055\n" +
                        "marker_predator=E63030\n" +
                        "marker_guest=8FD4F5\n" +
                        "marker_prisoner=FF9500\n" +
                        "marker_slave=F0D020\n" +
                        "marker_drone=747E88\n" +
                        "marker_drone_prefixes=Drone,AIRobot_\n" +
                        "fix_pawn_effects=true\n" +
                        "pawn_effects_min_speed=3\n" +
                        "fix_mothball_hediffs=true\n" +
                        "mothball_hediffs=DimonSever000_*,Malnutrition,GeneticDrugNeed,HemogenCraving\n");
                    return c;
                }
                foreach (var raw in File.ReadAllLines(FilePath))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#') continue;
                    int eq = line.IndexOf('=');
                    if (eq < 0) continue;
                    string k = line.Substring(0, eq).Trim().ToLowerInvariant();
                    string v = line.Substring(eq + 1).Trim();
                    int n; bool b;
                    switch (k)
                    {
                        case "duration_seconds": if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) && n > 0) c.DurationSeconds = n; break;
                        case "warmup_seconds": if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) && n >= 0) c.WarmupSeconds = n; break;
                        case "hook_tick": if (bool.TryParse(v, out b)) c.HookTick = b; break;
                        case "hook_update": if (bool.TryParse(v, out b)) c.HookUpdate = b; break;
                        case "hook_gui": if (bool.TryParse(v, out b)) c.HookGui = b; break;
                        case "hook_harmony_patches": if (bool.TryParse(v, out b)) c.HookHarmony = b; break;
                        case "hook_vanilla_internals": if (bool.TryParse(v, out b)) c.HookVanillaInternals = b; break;
                        case "hook_pawn_trackers": if (bool.TryParse(v, out b)) c.HookPawnTrackers = b; break;
                        case "two_phase": if (bool.TryParse(v, out b)) c.TwoPhase = b; break;
                        case "light_mode": if (bool.TryParse(v, out b)) c.LightMode = b; break;
                        case "top_mods": if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) && n > 0) c.TopMods = n; break;
                        case "top_methods": if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) && n > 0) c.TopMethods = n; break;
                        case "top_defs": if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) && n > 0) c.TopDefs = n; break;
                        case "fix_rot_storage": if (bool.TryParse(v, out b)) c.FixRotStorage = b; break;
                        case "fix_rot_verify_calls": if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) && n >= 0) c.FixRotVerifyCalls = n; break;
                        case "world_pawn_rows": if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) && n > 0) c.WorldPawnRows = n; break;
                        case "fix_far_map": if (bool.TryParse(v, out b)) c.FixFarMap = b; break;
                        case "far_map_px": { float fpx; if (float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out fpx) && fpx > 0f) c.FarMapPx = fpx; break; }
                        case "far_map_wall": c.FarMapWall = v; break;
                        case "far_map_rock": c.FarMapRock = v; break;
                        case "far_map_background": c.FarMapBackground = v; break;
                        case "far_map_water": c.FarMapWater = v; break;
                        case "far_map_marsh": c.FarMapMarsh = v; break;
                        case "far_map_door": c.FarMapDoor = v; break;
                        case "far_map_building": c.FarMapBuilding = v; break;
                        case "far_map_storage": c.FarMapStorage = v; break;
                        case "fix_marker_colors": if (bool.TryParse(v, out b)) c.FixMarkerColors = b; break;
                        case "marker_colonist": c.MarkerColonist = v; break;
                        case "marker_predator": c.MarkerPredator = v; break;
                        case "marker_guest": c.MarkerGuest = v; break;
                        case "marker_prisoner": c.MarkerPrisoner = v; break;
                        case "marker_slave": c.MarkerSlave = v; break;
                        case "marker_drone": c.MarkerDrone = v; break;
                        case "marker_drone_prefixes": c.MarkerDronePrefixes = v; break;
                        case "fix_pawn_effects": if (bool.TryParse(v, out b)) c.FixPawnEffects = b; break;
                        case "pawn_effects_min_speed": { float f; if (float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out f) && f >= 0f) c.PawnEffectsMinSpeed = f; break; }
                        case "fix_mothball_hediffs": if (bool.TryParse(v, out b)) c.FixMothballHediffs = b; break;
                        case "mothball_hediffs":
                            var names = v.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
                            var cleaned = new System.Collections.Generic.List<string>();
                            foreach (var nm in names) { var t = nm.Trim(); if (t.Length > 0) cleaned.Add(t); }
                            c.MothballHediffs = cleaned.ToArray();
                            break;
                    }
                }
            }
            catch (Exception e)
            {
                Log.Warning("[HSK Performance] config problem, using defaults: " + e.Message);
            }
            return c;
        }
    }
}
