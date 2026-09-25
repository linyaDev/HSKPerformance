using System;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace HSKPerformance
{
    /// <summary>
    /// Camera+ draws pawns as markers when zoomed out. Its colours come from DotTools.GetMarkerColors: a matching rule of the player (DotConfig), else
    /// for animals and foreign pawns the outline is PawnNameColorUtility.PawnNameColorOf (light blue for factionless pawns, i.e. wild animals), and
    /// colonists get white with a black outline. We post-process that result, only when the player has no rule for the pawn (dotConfig == null):
    ///   - colonists get a bright green marker with a darker green outline (not while selected, downed or in a mental state, those keep Camera+'s own colours),
    ///   - drones (race defName starts with "Drone") get a pale grey-blue fill instead of white,
    ///   - guests (host faction is the player, or quest lodgers) light blue, prisoners of the colony orange, slaves of the colony yellow,
    ///   - predators that are not ours (RaceProps.predator, wild or hostile faction) get a red marker with a darker red outline, even when a Camera+ rule matches them.
    ///
    /// A selected pawn that Camera+ shows as a marker also gets its name drawn under the marker (Camera+ hides the vanilla name of pawns in marker mode).
    /// The name is drawn with the vanilla GenMapUI.DrawPawnLabel at the vanilla label position; a truncateToWidth different from 9999 makes
    /// Camera+'s own DrawPawnLabel patch let the call through.
    /// Soft dependency: nothing is patched when Camera+ is not loaded or its method has a different signature.
    /// </summary>
    static class MarkerColors
    {
        public static bool Installed;
        public static bool Enabled = true;

        static Color colonist = new Color(0x33 / 255f, 0xE0 / 255f, 0x55 / 255f, 1f);
        static Color predator = new Color(0xE6 / 255f, 0x30 / 255f, 0x30 / 255f, 1f);
        static Color guest = new Color(0x8F / 255f, 0xD4 / 255f, 0xF5 / 255f, 1f);     // light blue
        static Color prisoner = new Color(0xFF / 255f, 0x95 / 255f, 0x00 / 255f, 1f);  // orange
        static Color slave = new Color(0xF0 / 255f, 0xD0 / 255f, 0x20 / 255f, 1f);     // yellow
        static Color drone = new Color(0x74 / 255f, 0x7E / 255f, 0x88 / 255f, 1f);     // pale grey-blue: drones are white otherwise and dominate the map
        static long recolored, labelsDrawn, calls, predatorsSeen, predatorsByRule, colonistsSeen, colonistsByRule;
        static MethodInfo shouldShowMarker;

        public static void Apply(Harmony harmony, ProbeConfig cfg)
        {
            Enabled = cfg.FixMarkerColors;
            SetColors(cfg.MarkerColonist, cfg.MarkerPredator, cfg.MarkerGuest, cfg.MarkerPrisoner, cfg.MarkerSlave, cfg.MarkerDrone);
            var type = AccessTools.TypeByName("CameraPlus.DotTools");
            MethodInfo target = null;
            if (type != null)
            {
                foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (m.Name != "GetMarkerColors") continue;
                    var ps = m.GetParameters();
                    if (ps.Length == 4 && ps[0].ParameterType == typeof(Pawn) && ps[2].ParameterType == typeof(Color).MakeByRefType() && ps[3].ParameterType == typeof(Color).MakeByRefType()
                        && ps[3].Name == "outerColor" && ps[1].Name == "dotConfig" && m.ReturnType == typeof(bool)) { target = m; break; }
                }
            }
            if (target == null)
            {
                PerfFixes.Status.Add("marker colors: Camera+ (CameraPlus.DotTools.GetMarkerColors) not found, nothing to recolor");
                return;
            }
            try
            {
                harmony.Patch(target, postfix: new HarmonyMethod(typeof(MarkerColors).GetMethod("Postfix", BindingFlags.Static | BindingFlags.NonPublic)));
                Installed = true;
                InstallNameLabel(harmony, type);
                PerfFixes.Status.Add("marker colors: " + (Enabled ? "ON" : "installed but OFF") + " (Camera+ markers: colonists fully green unless you have a Camera+ rule for them, predators that are not ours fully red)");
            }
            catch (Exception e)
            {
                PerfFixes.Status.Add("marker colors: could not patch Camera+ (" + e.GetType().Name + "), NOT applied");
            }
        }

        static bool TryHex(string hex, out Color c)
        {
            c = default(Color);
            if (string.IsNullOrEmpty(hex)) return false;
            hex = hex.Trim().TrimStart('#');
            int rgb;
            if (hex.Length != 6 || !int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out rgb)) return false;
            c = new Color(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, 1f);
            return true;
        }

        /// <summary>RRGGBB strings; one that is not valid keeps the current colour.</summary>
        public static void SetColors(string colonistHex, string predatorHex, string guestHex, string prisonerHex, string slaveHex, string droneHex)
        {
            Color c;
            if (TryHex(colonistHex, out c)) colonist = c;
            if (TryHex(predatorHex, out c)) predator = c;
            if (TryHex(guestHex, out c)) guest = c;
            if (TryHex(prisonerHex, out c)) prisoner = c;
            if (TryHex(slaveHex, out c)) slave = c;
            if (TryHex(droneHex, out c)) drone = c;
        }

        static void Paint(ref Color inner, ref Color outer, Color c)
        {
            inner = c;
            outer = new Color(c.r * 0.35f, c.g * 0.35f, c.b * 0.35f, 1f);   // a darker outline of the same colour keeps the shape readable
        }

        static void InstallNameLabel(Harmony harmony, Type dotTools)
        {
            try
            {
                foreach (var m in dotTools.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    var ps = m.GetParameters();
                    if (m.Name == "ShouldShowMarker" && ps.Length == 2 && ps[0].ParameterType == typeof(Pawn) && m.ReturnType == typeof(bool)) { shouldShowMarker = m; break; }
                }
                var hook = AccessTools.Method(typeof(RimWorld.MapInterface), "MapInterfaceOnGUI_AfterMainTabs", Type.EmptyTypes);
                if (shouldShowMarker == null || hook == null) { PerfFixes.Status.Add("marker colors: selected pawn name under the marker NOT installed (Camera+ or the game changed)"); return; }
                harmony.Patch(hook, postfix: new HarmonyMethod(typeof(MarkerColors).GetMethod("NamePostfix", BindingFlags.Static | BindingFlags.NonPublic)));
                PerfFixes.Status.Add("marker colors: the name of a selected pawn is drawn under its Camera+ marker");
            }
            catch (Exception e)
            {
                PerfFixes.Status.Add("marker colors: name under the selected marker not installed (" + e.GetType().Name + ")");
            }
        }

        // Runs in the map GUI; draws the vanilla name label for every selected pawn that is currently a Camera+ marker.
        static void NamePostfix()
        {
            if (!Enabled || shouldShowMarker == null) return;
            var e = Event.current;
            if (e == null || e.type != EventType.Repaint) return;
            var selector = Find.Selector;
            var map = Find.CurrentMap;
            if (selector == null || map == null) return;
            var list = selector.SelectedObjectsListForReading;
            for (int i = 0; i < list.Count; i++)
            {
                var pawn = list[i] as Pawn;
                if (pawn == null || !pawn.Spawned || pawn.Map != map || pawn.Dead) continue;
                try
                {
                    if (!(bool)shouldShowMarker.Invoke(null, new object[] { pawn, null })) continue;   // real body drawn: the vanilla name is there already
                    GenMapUI.DrawPawnLabel(pawn, GenMapUI.LabelDrawPosFor(pawn, -0.6f), 1f, 9998f, null, GameFont.Tiny, true, true);
                    labelsDrawn++;
                }
                catch { }
            }
        }

        public static string Live()
        {
            return "marker colors live: " + (Enabled ? "ON" : "off") + ", GetMarkerColors called " + calls + " times, colonists seen " + colonistsSeen + " (+" + colonistsByRule + " with a Camera+ rule), predators seen " + predatorsSeen
                + " (" + predatorsByRule + " of them with a Camera+ rule), " + recolored + " markers recolored, " + labelsDrawn + " selected names drawn";
        }

        // Camera+ signature: bool GetMarkerColors(Pawn pawn, DotConfig dotConfig, out Color innerColor, out Color outerColor)
        static void Postfix(Pawn pawn, object dotConfig, ref Color innerColor, ref Color outerColor, ref bool __result)
        {
            if (!Enabled || !__result || pawn == null) return;
            calls++;
            try
            {
                var selector = Find.Selector;
                if (selector != null && selector.IsSelected(pawn)) return;            // the selected pawn keeps Camera+'s highlight
                // drones (Odyssey: Drone_Hunter, Drone_Wasp, Drone_Sentry, ...): only the fill is toned down, the outline (faction colour) stays
                if (pawn.def != null && pawn.def.defName != null && pawn.def.defName.StartsWith("Drone", StringComparison.Ordinal))
                {
                    innerColor = drone; recolored++; return;
                }
                // guests, prisoners and slaves of the colony each get their own colour (also for pawns that count as colonists, e.g. quest lodgers)
                if (pawn.RaceProps != null && pawn.RaceProps.Humanlike)
                {
                    if (pawn.IsSlaveOfColony) { Paint(ref innerColor, ref outerColor, slave); recolored++; return; }
                    if (pawn.IsPrisonerOfColony) { Paint(ref innerColor, ref outerColor, prisoner); recolored++; return; }
                    if (pawn.HostFaction == Faction.OfPlayer || pawn.IsQuestLodger()) { Paint(ref innerColor, ref outerColor, guest); recolored++; return; }
                }
                bool predatorNotOurs = !pawn.IsColonist && pawn.RaceProps != null && pawn.RaceProps.predator && pawn.Faction != Faction.OfPlayer;
                if (predatorNotOurs)
                {
                    // red for every predator that is not ours (wild, or belonging to a hostile faction), also when the player has a Camera+ rule for it
                    predatorsSeen++;
                    if (dotConfig != null) predatorsByRule++;
                    Paint(ref innerColor, ref outerColor, predator);
                    recolored++;
                    return;
                }
                if (dotConfig != null) { if (pawn.IsColonist) colonistsByRule++; return; }   // colonists: a Camera+ rule of the player wins
                if (pawn.IsColonist)
                {
                    colonistsSeen++;
                    if (pawn.Downed || pawn.MentalStateDef != null) return;            // keep Camera+'s downed and mental colours
                    Paint(ref innerColor, ref outerColor, colonist);
                    recolored++;
                }
            }
            catch { }
        }
    }
}
