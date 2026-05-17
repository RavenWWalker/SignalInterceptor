using HarmonyLib;
using RimWorld;
using System.Reflection;
using Verse;
using Verse.AI;

namespace SignalInterceptor
{
    [HarmonyPatch(typeof(MentalState_SocialFighting), nameof(MentalState_SocialFighting.MentalStateTick))]
    public static class Patch_SocialFightingNullGuard_MentalStateTick
    {
        public static bool Prefix(MentalState_SocialFighting __instance)
        {
            if (__instance == null)
                return true;

            Pawn pawn = __instance.pawn;

            if (pawn == null || pawn.Destroyed || pawn.Dead)
                return false;

            Pawn otherPawn = GetOtherPawn(__instance);

            if (otherPawn != null && !otherPawn.Destroyed && !otherPawn.Dead)
                return true;

            if (pawn.mindState != null && pawn.mindState.mentalStateHandler != null)
            {
                pawn.mindState.mentalStateHandler.Reset();
            }

            Log.Warning("[Signal Interceptor] Broken SocialFighting mental state removed. Pawn=" +
                        pawn.LabelShort);

            return false;
        }

        private static Pawn GetOtherPawn(MentalState_SocialFighting state)
        {
            if (state == null)
                return null;

            FieldInfo field = AccessTools.Field(typeof(MentalState_SocialFighting), "otherPawn");

            if (field == null)
                return null;

            return field.GetValue(state) as Pawn;
        }
    }

    [HarmonyPatch(typeof(JobGiver_SocialFighting), "TryGiveJob")]
    public static class Patch_SocialFightingNullGuard_JobGiver
    {
        public static bool Prefix(Pawn pawn, ref Job __result)
        {
            if (pawn == null || pawn.Destroyed || pawn.Dead)
            {
                __result = null;
                return false;
            }

            MentalState_SocialFighting state = pawn.MentalState as MentalState_SocialFighting;

            if (state == null)
                return true;

            Pawn otherPawn = GetOtherPawn(state);

            if (otherPawn != null && !otherPawn.Destroyed && !otherPawn.Dead)
                return true;

            if (pawn.mindState != null && pawn.mindState.mentalStateHandler != null)
            {
                pawn.mindState.mentalStateHandler.Reset();
            }

            Log.Warning("[Signal Interceptor] Broken SocialFighting job prevented. Pawn=" +
                        pawn.LabelShort);

            __result = null;
            return false;
        }

        private static Pawn GetOtherPawn(MentalState_SocialFighting state)
        {
            if (state == null)
                return null;

            FieldInfo field = AccessTools.Field(typeof(MentalState_SocialFighting), "otherPawn");

            if (field == null)
                return null;

            return field.GetValue(state) as Pawn;
        }
    }
}
