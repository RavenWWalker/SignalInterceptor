using System.Reflection;
using Verse;

namespace SignalInterceptor
{
    using System.Reflection;
    using Verse;

    namespace SignalInterceptor
    {
        public class HediffComp_PsycasterEntropyStabilizer : HediffComp
        {
            private int nextTick;

            public HediffCompProperties_PsycasterEntropyStabilizer Props
            {
                get { return (HediffCompProperties_PsycasterEntropyStabilizer)props; }
            }

            public override void CompPostTick(ref float severityAdjustment)
            {
                base.CompPostTick(ref severityAdjustment);

                Pawn pawn = parent != null ? parent.pawn : null;

                if (pawn == null || pawn.Destroyed || pawn.Dead)
                    return;

                int tick = Find.TickManager.TicksGame;

                if (tick < nextTick)
                    return;

                nextTick = tick + Props.intervalTicks;

                HediffComp_PsycasterRestoringMechanisms restore =
                    PsycasterRecoveryUtility.GetRestoringComp(pawn);

                if (restore != null && restore.RecentlyDamaged)
                    return;

                TryReduceEntropy(pawn, Props.entropyReductionPerInterval);
            }

            private static void TryReduceEntropy(Pawn pawn, float amount)
            {
                if (pawn == null || pawn.psychicEntropy == null)
                    return;

                try
                {
                    object tracker = pawn.psychicEntropy;
                    System.Type type = tracker.GetType();

                    MethodInfo tryAddEntropy = type.GetMethod(
                        "TryAddEntropy",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                        null,
                        new[] { typeof(float) },
                        null);

                    if (tryAddEntropy != null)
                    {
                        tryAddEntropy.Invoke(tracker, new object[] { -amount });
                        return;
                    }

                    FieldInfo field = type.GetField(
                        "currentEntropy",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                    if (field != null && field.FieldType == typeof(float))
                    {
                        float current = (float)field.GetValue(tracker);
                        field.SetValue(tracker, UnityEngine.Mathf.Max(0f, current - amount));
                    }
                }
                catch
                {
                    // Не ломаем AI, если RimWorld internals отличаются.
                }
            }

            public override void CompExposeData()
            {
                base.CompExposeData();

                Scribe_Values.Look(ref nextTick, "nextTick", 0);
            }
        }
    }
}
