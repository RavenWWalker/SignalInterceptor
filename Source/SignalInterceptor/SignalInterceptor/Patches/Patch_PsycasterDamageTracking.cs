using HarmonyLib;
using Verse;

namespace SignalInterceptor
{
    [HarmonyPatch(typeof(Pawn_HealthTracker), "PostApplyDamage")]
    public static class Patch_PsycasterDamageTracking
    {
        public static void Postfix(Pawn_HealthTracker __instance, DamageInfo dinfo, float totalDamageDealt)
        {
            if (totalDamageDealt <= 0f)
                return;

            Pawn pawn = __instance != null && __instance.hediffSet != null
                ? __instance.hediffSet.pawn
                : null;

            if (pawn == null || pawn.Destroyed || pawn.Dead)
                return;

            HediffComp_PsycasterRestoringMechanisms comp =
                PsycasterRecoveryUtility.GetRestoringComp(pawn);

            if (comp == null)
                return;

            comp.Notify_Damaged();

            if (Prefs.DevMode)
            {
                Log.Message(
                    "[Signal Interceptor] Psycaster regeneration delayed by damage: " +
                    pawn.LabelShort +
                    " | damage=" + totalDamageDealt.ToString("F2") +
                    " | def=" + (dinfo.Def != null ? dinfo.Def.defName : "null"));
            }
        }
    }
}
