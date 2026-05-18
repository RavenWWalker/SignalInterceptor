using HarmonyLib;
using Verse;
using System.Collections.Generic;
using System.Reflection;

namespace SignalInterceptor
{
    [StaticConstructorOnStartup]
    public static class HarmonyPatches
    {
        static HarmonyPatches()
        {
            var harmony = new Harmony("ravenwwalker.signalinterceptor");
            harmony.PatchAll();
            Log.Message("[Signal Interceptor] Harmony patches applied.");
        }
    }

    [HarmonyPatch(typeof(MapGenerator), nameof(MapGenerator.GenerateMap))]
    public static class Patch_MapGenerator_GenerateMap
    {
        public static void Postfix(Map __result)
        {
            if (__result == null) return;

            var comp = Current.Game.GetComponent<SignalInterceptorGameComponent>();
            if (comp == null) return;

            comp.TrySpawnVIPOnMapGenerated(__result);
        }
    }

    [HarmonyPatch]
    public static class Patch_Hediff_BleedRate
    {
        public static void Postfix(Hediff __instance, ref float __result)
        {
            if (__result <= 0f) return;
            Pawn pawn = __instance.pawn;
            if (pawn?.health?.hediffSet == null) return;

            HediffDef bleedDef = DefDatabase<HediffDef>.GetNamedSilentFail("SI_CloneBleedRate");
            if (bleedDef != null && pawn.health.hediffSet.HasHediff(bleedDef))
            {
                __result *= 3f;
            }
        }
    }

}
