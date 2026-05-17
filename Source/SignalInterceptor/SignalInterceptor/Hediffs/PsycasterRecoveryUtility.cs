using Verse;

namespace SignalInterceptor
{
    public static class PsycasterRecoveryUtility
    {
        public const string RestoringMechanismsDefName = "SI_RestoringMechanisms";

        public static HediffComp_PsycasterRestoringMechanisms GetRestoringComp(Pawn pawn)
        {
            if (pawn == null || pawn.health == null || pawn.health.hediffSet == null)
                return null;

            HediffDef def = DefDatabase<HediffDef>.GetNamedSilentFail(RestoringMechanismsDefName);

            if (def == null)
                return null;

            Hediff hediff = pawn.health.hediffSet.GetFirstHediffOfDef(def);

            if (hediff == null)
                return null;

            return hediff.TryGetComp<HediffComp_PsycasterRestoringMechanisms>();
        }

        public static void NotifyDamage(Pawn pawn)
        {
            HediffComp_PsycasterRestoringMechanisms comp = GetRestoringComp(pawn);

            if (comp != null)
                comp.Notify_Damaged();
        }
    }
}
