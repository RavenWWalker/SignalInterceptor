using Verse;

namespace SignalInterceptor
{
    public class HediffCompProperties_PsycasterEntropyStabilizer : HediffCompProperties
    {
        public int noDamageDelayTicks = 900;
        public int intervalTicks = 60;
        public float entropyReductionPerInterval = 0.045f;

        public HediffCompProperties_PsycasterEntropyStabilizer()
        {
            compClass = typeof(HediffComp_PsycasterEntropyStabilizer);
        }
    }
}
