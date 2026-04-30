using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace SignalInterceptor
{
    /*
     * Фикс проблемы прилёта на сайты на шаттле:
     * RimWorld иногда выбирает точку посадки слишком близко к краю карты.
     *
     * Клетка near может быть InBounds, но сам PassengerShuttleIncoming имеет размер,
     * например 3x5, и его footprint вылезает за границу карты.
     *
     * Без фикса:
     * - GenSpawn.Spawn пишет Error;
     * - TransportShip ломается;
     * - шаттл и пассажиры могут исчезнуть.
     */
    [HarmonyPatch(typeof(TransportersArrivalActionUtility), nameof(TransportersArrivalActionUtility.DropShuttle))]
    public static class Patch_DropShuttle_ClampCell
    {
        private const int SafeEdgeMargin = 6;

        private static void Prefix(
            ActiveTransporterInfo transporter,
            Map map,
            ref IntVec3 near,
            ref Rot4? rotation,
            Faction faction)
        {
            if (map == null)
                return;

            if (!near.IsValid)
                return;

            IntVec3 originalNear = near;

            near = ClampCellAwayFromMapEdge(near, map, SafeEdgeMargin);

            if (near != originalNear)
            {
                Log.Warning("[Signal Interceptor] Shuttle landing cell was too close to map edge. " +
                            "Adjusted landing cell from " + originalNear +
                            " to " + near +
                            " | mapSize=" + map.Size +
                            " | rotation=" + (rotation.HasValue ? rotation.Value.ToString() : "null") +
                            " | faction=" + (faction?.Name ?? "null"));
            }
        }

        private static IntVec3 ClampCellAwayFromMapEdge(IntVec3 cell, Map map, int margin)
        {
            int minX = margin;
            int minZ = margin;
            int maxX = map.Size.x - 1 - margin;
            int maxZ = map.Size.z - 1 - margin;

            /*
             * Защита от очень маленьких/нестандартных карт.
             */
            if (maxX < minX)
            {
                minX = 0;
                maxX = map.Size.x - 1;
            }

            if (maxZ < minZ)
            {
                minZ = 0;
                maxZ = map.Size.z - 1;
            }

            cell.x = Mathf.Clamp(cell.x, minX, maxX);
            cell.z = Mathf.Clamp(cell.z, minZ, maxZ);

            return cell;
        }
    }
}
