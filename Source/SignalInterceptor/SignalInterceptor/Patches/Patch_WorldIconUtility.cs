using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using Verse;

namespace SignalInterceptor
{
    /*
     * Signal Interceptor world expanding icon fix.
     *
     * Goal:
     * - keep custom expandingIconTexture as the visible icon;
     * - let vanilla draw the HasMap layer;
     * - force that HasMap layer to green for selected custom SitePartDefs;
     * - make that green layer slightly bigger;
     * - then draw the normal expanding icon on top.
     *
     * Important:
     * This patch works with worldObject.ExpandingIcon.
     * That means it uses <expandingIconTexture>, not necessarily <siteTexture>.
     *
     * siteTexture and expandingIconTexture do NOT have to be the same.
     */
    [HarmonyPatch(typeof(ExpandableWorldObjectsUtility), nameof(ExpandableWorldObjectsUtility.ExpandableWorldObjectsOnGUI))]
    public static class Patch_SignalInterceptor_ExpandableWorldObjectsOnGUI
    {
        public static void Prefix()
        {
            Patch_WorldIconUtility.ClearRects();
        }

        public static void Postfix()
        {
            Patch_WorldIconUtility.DrawNormalIconsOverGreenLayer();
            Patch_WorldIconUtility.ClearRects();
        }
    }

    /*
     * Vanilla calls this before drawing an expanding world object icon.
     *
     * We:
     * 1) store the original rect for the normal top icon;
     * 2) enlarge __result so vanilla draws the green HasMap layer slightly bigger.
     */
    [HarmonyPatch]
    public static class Patch_SignalInterceptor_ExpandedIconScreenRect
    {
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
            Patch_WorldIconUtility.IconOverlaySettings settings;

            if (!Patch_WorldIconUtility.TryGetOverlaySettings(o, out settings))
                return;

            Rect normalRect = __result;
            Patch_WorldIconUtility.StoreNormalRect(o, normalRect);

            Rect greenRect = normalRect;
            greenRect.x -= settings.extraPixelsHorizontal;
            greenRect.y -= settings.extraPixelsVertical;
            greenRect.width += settings.extraPixelsHorizontal * 2f;
            greenRect.height += settings.extraPixelsVertical * 2f;

            __result = greenRect;
        }
    }

    /*
     * Force vanilla HasMap expanded layer to green for selected custom sites.
     *
     * Vanilla then draws:
     * - enlarged green layer first;
     * - our OnGUI postfix draws the normal icon over it.
     */
    [HarmonyPatch(typeof(WorldObject), "get_ExpandingIconColor")]
    public static class Patch_SignalInterceptor_ExpandingIconColor
    {
        private static readonly Color HasMapGreen = new Color(0.1f, 0.9f, 0.1f, 1f);

        public static void Postfix(WorldObject __instance, ref Color __result)
        {
            Patch_WorldIconUtility.IconOverlaySettings settings;

            if (!Patch_WorldIconUtility.TryGetOverlaySettings(__instance, out settings))
                return;

            __result = HasMapGreen;
        }
    }

    internal static class Patch_WorldIconUtility
    {
        public enum TopIconColorMode
        {
            White,
            Faction
        }

        /*
         * Add new custom SitePartDef.defName entries here.
         *
         * Format:
         * "SitePartDefName", new IconOverlaySettings(horizontalExtraPixels, verticalExtraPixels, topIconColorMode)
         *
         * White:
         *   top icon is drawn as white/original white mask.
         *
         * Faction:
         *   top icon is drawn using site faction color.
         */
        private static readonly Dictionary<string, IconOverlaySettings> PatchedSiteParts =
            new Dictionary<string, IconOverlaySettings>
            {
                {
                    "SI_MechanitorVIPSite",
                    new IconOverlaySettings(2f, 4f, TopIconColorMode.White)
                },

                {
                    "SI_DoppelgangerVIPSite",
                    new IconOverlaySettings(2f, 3f, TopIconColorMode.White)
                },

                {
                    "SI_PsycasterVIPSite",
                    new IconOverlaySettings(2f, 3f, TopIconColorMode.White)
                },

                {
                    "SI_PilgrimVIPSite",
                    new IconOverlaySettings(2f, 3f, TopIconColorMode.Faction)
                }

                /*
                 * Examples for future sites:
                 *
                 * {
                 *     "SI_ShuttleVIPSite",
                 *     new IconOverlaySettings(2f, 4f, TopIconColorMode.Faction)
                 * },
                 *
                 * {
                 *     "SI_AncientDroneSite",
                 *     new IconOverlaySettings(2f, 4f, TopIconColorMode.White)
                 * }
                 */
            };

        private static readonly Dictionary<WorldObject, Rect> normalRects =
            new Dictionary<WorldObject, Rect>();

        public struct IconOverlaySettings
        {
            public readonly float extraPixelsHorizontal;
            public readonly float extraPixelsVertical;
            public readonly TopIconColorMode topIconColorMode;

            public IconOverlaySettings(
                float extraPixelsHorizontal,
                float extraPixelsVertical,
                TopIconColorMode topIconColorMode)
            {
                this.extraPixelsHorizontal = extraPixelsHorizontal;
                this.extraPixelsVertical = extraPixelsVertical;
                this.topIconColorMode = topIconColorMode;
            }
        }

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

                    IconOverlaySettings settings;

                    if (!TryGetOverlaySettings(worldObject, out settings))
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

                    GUI.color = GetTopIconColor(worldObject, settings);

                    /*
                     * Important:
                     * material must be null here.
                     *
                     * Using ShaderDatabase.Transparent / MaterialPool.MatFrom can make
                     * the white icon look gray due to shader/filtering behavior.
                     */
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
                Log.Warning("[Signal Interceptor] Failed to draw custom world icon overlay: " + ex);
            }
            finally
            {
                GUI.color = oldColor;
            }
        }

        private static Color GetTopIconColor(WorldObject worldObject, IconOverlaySettings settings)
        {
            if (settings.topIconColorMode == TopIconColorMode.White)
                return Color.white;

            if (settings.topIconColorMode == TopIconColorMode.Faction)
            {
                Color factionColor;

                if (TryGetFactionColor(worldObject, out factionColor))
                    return factionColor;

                return Color.white;
            }

            return Color.white;
        }

        private static bool TryGetFactionColor(WorldObject worldObject, out Color color)
        {
            color = Color.white;

            if (worldObject == null)
                return false;

            Faction faction = worldObject.Faction;

            if (faction == null)
                return false;

            /*
             * Normal RimWorld faction color.
             */
            color = faction.Color;
            color.a = 1f;

            return true;
        }

        public static bool TryGetOverlaySettings(WorldObject worldObject, out IconOverlaySettings settings)
        {
            settings = default(IconOverlaySettings);

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

                if (part == null || part.def == null)
                    continue;

                if (PatchedSiteParts.TryGetValue(part.def.defName, out settings))
                    return true;
            }

            return false;
        }
    }
}
