using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Verse;

namespace HSKPerfProbe
{
    /// <summary>Tiny key=value config, created with defaults on first run.</summary>
    public sealed class ProbeConfig
    {
        public int DurationSeconds = 90;
        public int WarmupSeconds = 3;
        public bool HookTick = true;
        public bool HookUpdate = true;
        public bool HookGui = true;
        public bool HookHarmony = true;
        public int TopMods = 40;
        public int TopMethods = 60;
        public int TopDefs = 30;

        // ---- fixes (applied at game start, independent of recording) ----
        public bool FixRotStorage = true;      // O(1) storage lookup in Core_SK's CompBetterRottable_Patch
        public int FixRotVerifyCalls = 1000;   // compare fast and original result for the first N calls (0 = trust blindly)
        public int WorldPawnRows = 300;        // rows in world_pawns.md (the CSV always has all of them)
        public bool FixMothballHediffs = true; // let world pawns with these chronic hediffs be mothballed (see PerfFixes)
        // defNames, a trailing * means "starts with". Mothballing only freezes a pawn (age and records keep counting, hediffs stop progressing).
        public string[] MothballHediffs = { "DimonSever000_*", "Malnutrition", "GeneticDrugNeed", "HemogenCraving" };

        public static string DirOverride; // tests only

        public static string Dir
        {
            get { return DirOverride ?? Path.Combine(GenFilePaths.SaveDataFolderPath, "HSKPerfProbe"); }
        }

        static string FilePath { get { return Path.Combine(Dir, "config.txt"); } }

        public static ProbeConfig Load()
        {
            var c = new ProbeConfig();
            try
            {
                Directory.CreateDirectory(Dir);
                if (!File.Exists(FilePath))
                {
                    File.WriteAllText(FilePath,
                        "# HSKPerfProbe config. Delete a line to get the default.\n" +
                        "duration_seconds=90\n" +
                        "warmup_seconds=3\n" +
                        "hook_tick=true\n" +
                        "hook_update=true\n" +
                        "hook_gui=true\n" +
                        "hook_harmony_patches=true\n" +
                        "top_mods=40\n" +
                        "top_methods=60\n" +
                        "top_defs=30\n" +
                        "fix_rot_storage=true\n" +
                        "fix_rot_verify_calls=1000\n" +
                        "world_pawn_rows=300\n" +
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
                        case "top_mods": if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) && n > 0) c.TopMods = n; break;
                        case "top_methods": if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) && n > 0) c.TopMethods = n; break;
                        case "top_defs": if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) && n > 0) c.TopDefs = n; break;
                        case "fix_rot_storage": if (bool.TryParse(v, out b)) c.FixRotStorage = b; break;
                        case "fix_rot_verify_calls": if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) && n >= 0) c.FixRotVerifyCalls = n; break;
                        case "world_pawn_rows": if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) && n > 0) c.WorldPawnRows = n; break;
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
                Log.Warning("[HSKPerfProbe] config problem, using defaults: " + e.Message);
            }
            return c;
        }
    }
}
