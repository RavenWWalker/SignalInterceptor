using Verse;

namespace SignalInterceptor
{
    public class HediffCompProperties_PsycasterRestoringMechanisms : HediffCompProperties
    {
        public int noDamageDelayTicks = 900;
        public int intervalTicks = 60;

        public float healAmount = 0.55f;
        public float bleedingHealMultiplier = 1.35f;
        public float bloodLossReduction = 0.012f;

        public bool onlyWhenNotRecentlyDamaged = true;

        public HediffCompProperties_PsycasterRestoringMechanisms()
        {
            compClass = typeof(HediffComp_PsycasterRestoringMechanisms);
        }
    }
}
