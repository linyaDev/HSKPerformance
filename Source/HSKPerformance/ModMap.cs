using System.Collections.Generic;
using System.Reflection;
using Verse;

namespace HSKPerformance
{
    /// <summary>Maps an assembly (or a Def) to the mod it belongs to.</summary>
    public static class ModMap
    {
        public const string Core = "RimWorld Core (Assembly-CSharp)";

        static readonly Dictionary<Assembly, string> byAssembly = new Dictionary<Assembly, string>();
        public static readonly Dictionary<string, string> PackageIdByName = new Dictionary<string, string>();

        public static void Build()
        {
            byAssembly.Clear();
            PackageIdByName.Clear();
            foreach (var mod in LoadedModManager.RunningModsListForReading)
            {
                string name = mod.Name ?? mod.PackageId ?? "?";
                PackageIdByName[name] = mod.PackageId;
                if (mod.assemblies == null) continue;
                foreach (var a in mod.assemblies.loadedAssemblies) byAssembly[a] = name;
            }
            byAssembly[typeof(Thing).Assembly] = Core;
        }

        public static string For(Assembly a)
        {
            if (a == null) return "?";
            string s;
            if (byAssembly.TryGetValue(a, out s)) return s;
            return "[asm] " + a.GetName().Name;
        }

        public static string ForDef(Def d)
        {
            if (d == null) return "?";
            var pack = d.modContentPack;
            if (pack == null) return "(no mod / generated def)";
            return pack.Name ?? pack.PackageId ?? "?";
        }
    }
}
