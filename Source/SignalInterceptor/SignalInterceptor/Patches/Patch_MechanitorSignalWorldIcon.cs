using HarmonyLib;
using RimWorld.Planet;
using System;
using System.Reflection;
using UnityEngine;

namespace SignalInterceptor
{
    /*
     * Safe mechanitor signal world icon patch.
     *
     * Patches RimWorld.Planet.ExpandableWorldObjectsUtility.ExpandedIconScreenRect(WorldObject o, float factor)
     * and slightly enlarges the vanilla expanding icon rect only for SI_MechanitorSignalSite.
     *
     * No manual OnGUI drawing.
     * No custom GUI.color.
     * No reflection Invoke.
     */
    [HarmonyPatch]
    public static class Patch_MechanitorSignal_ExpandedIconScreenRect
    {
        private const float WidthFactor = 1.04f;
        private const float HeightFactor = 1.10f;

        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(
                typeof(ExpandableWorldObjectsUtility),
                "ExpandedIconScreenRect",
                new Type[]
                {
                    typeof(WorldObject),
                    typeof(float)
                }
            );
        }

        public static void Postfix(WorldObject o, float factor, ref Rect __result)
        {
            if (!MechanitorSignalWorldIconUtility.ShouldPatchMechanitorSite(o))
                return;

            float oldWidth = __result.width;
            float oldHeight = __result.height;

            float newWidth = oldWidth * WidthFactor;
            float newHeight = oldHeight * HeightFactor;

            __result.x -= (newWidth - oldWidth) * 1f;
            __result.y -= (newHeight - oldHeight) * 1f;
            __result.width = newWidth;
            __result.height = newHeight;
        }
    }

    internal static class MechanitorSignalWorldIconUtility
    {
        public static bool ShouldPatchMechanitorSite(WorldObject worldObject)
        {
            if (worldObject == null)
                return false;

            Site site = worldObject as Site;
            if (site == null || site.parts == null)
                return false;

            for (int i = 0; i < site.parts.Count; i++)
            {
                SitePart part = site.parts[i];

                if (part != null &&
                    part.def != null &&
                    part.def.defName == "SI_MechanitorSignalSite")
                {
                    return true;
                }
            }

            return false;
        }
    }
}
