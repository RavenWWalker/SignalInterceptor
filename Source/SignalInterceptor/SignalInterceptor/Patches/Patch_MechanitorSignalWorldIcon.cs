using HarmonyLib;
using RimWorld.Planet;
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using Verse;

namespace SignalInterceptor
{
    /*
     * Mechanitor signal world icon fix.
     *
     * Goal:
     * - keep SI_Mechanitor as the expanded icon;
     * - let vanilla draw the HasMap layer green;
     * - make that green layer slightly bigger;
     * - then draw the normal centipede icon on top.
     */
    [HarmonyPatch(typeof(ExpandableWorldObjectsUtility), nameof(ExpandableWorldObjectsUtility.ExpandableWorldObjectsOnGUI))]
    public static class Patch_MechanitorSignal_ExpandableWorldObjectsOnGUI
    {
        public static void Prefix()
        {
            MechanitorSignalWorldIconUtility.ClearRects();
        }

        public static void Postfix()
        {
            MechanitorSignalWorldIconUtility.DrawNormalIconsOverGreenLayer();
            MechanitorSignalWorldIconUtility.ClearRects();
        }
    }

    /*
     * Vanilla calls this before drawing an expanding icon.
     *
     * We store the original rect for the normal top icon,
     * then enlarge __result so vanilla draws the green HasMap layer bigger.
     */
    [HarmonyPatch]
    public static class Patch_MechanitorSignal_ExpandedIconScreenRect
    {
        private const float ExtraPixelsHorizontal = 2f;
        private const float ExtraPixelsVertical = 4f;

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

            Rect normalRect = __result;
            MechanitorSignalWorldIconUtility.StoreNormalRect(o, normalRect);

            Rect greenRect = normalRect;
            greenRect.x -= ExtraPixelsHorizontal;
            greenRect.y -= ExtraPixelsVertical;
            greenRect.width += ExtraPixelsHorizontal * 2f;
            greenRect.height += ExtraPixelsVertical * 2f;

            __result = greenRect;
        }
    }

    /*
     * Force vanilla HasMap expanded layer for this site to be green.
     * Then our OnGUI postfix draws the normal centipede icon over it.
     */
    [HarmonyPatch(typeof(WorldObject), "get_ExpandingIconColor")]
    public static class Patch_MechanitorSignal_ExpandingIconColor
    {
        private static readonly Color MechanitorHasMapGreen = new Color(0.1f, 0.9f, 0.1f, 1f);

        public static void Postfix(WorldObject __instance, ref Color __result)
        {
            if (!MechanitorSignalWorldIconUtility.ShouldPatchMechanitorSite(__instance))
                return;

            __result = MechanitorHasMapGreen;
        }
    }

    internal static class MechanitorSignalWorldIconUtility
    {
        private static readonly Dictionary<WorldObject, Rect> normalRects = new Dictionary<WorldObject, Rect>();

        public static void ClearRects()
        {
            normalRects.Clear();
        }

        public static void StoreNormalRect(WorldObject worldObject, Rect rect)
        {
            if (worldObject == null)
                return;

            normalRects[worldObject] = rect;
        }

        public static void DrawNormalIconsOverGreenLayer()
        {
            if (normalRects.Count == 0)
                return;

            Color oldColor = GUI.color;

            try
            {
                foreach (KeyValuePair<WorldObject, Rect> pair in normalRects)
                {
                    WorldObject worldObject = pair.Key;

                    if (!ShouldPatchMechanitorSite(worldObject))
                        continue;

                    if (worldObject.HiddenBehindTerrainNow())
                        continue;

                    Texture2D icon = worldObject.ExpandingIcon;

                    if (icon == null || icon == BaseContent.BadTex)
                        continue;

                    Rect rect = pair.Value;

                    if (worldObject.ExpandingIconFlipHorizontal)
                    {
                        rect.x = rect.xMax;
                        rect.width *= -1f;
                    }

                    GUI.color = Color.white;

                    Widgets.DrawTextureRotated(
                        rect,
                        icon,
                        worldObject.ExpandingIconRotation,
                        null
                    );
                }
            }
            catch (Exception ex)
            {
                Log.Warning("[Signal Interceptor] Failed to draw mechanitor signal icon overlay: " + ex);
            }
            finally
            {
                GUI.color = oldColor;
            }
        }

        public static bool ShouldPatchMechanitorSite(WorldObject worldObject)
        {
            if (worldObject == null)
                return false;

            MapParent mapParent = worldObject as MapParent;

            if (mapParent == null || !mapParent.HasMap)
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
