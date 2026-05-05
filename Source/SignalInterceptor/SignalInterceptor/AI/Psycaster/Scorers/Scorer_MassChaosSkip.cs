using Verse;

namespace SignalInterceptor.AI.Psycaster
{
    /// <summary>
    /// MassChaosSkip — массовый разброс кластера врагов.
    /// Тактика: разнести группу, попавшую в плотный построение, или вырваться из окружения.
    /// </summary>
    public class Scorer_MassChaosSkip : AbilityScorerBase
    {
        public override string AbilityDefName { get { return "MassChaosSkip"; } }

        protected override float StanceMultiplier(PsycasterStance stance)
        {
            switch (stance)
            {
                case PsycasterStance.Opening: return PsycasterTuning.W_Opening_MassChaosSkip;
                case PsycasterStance.Kite: return PsycasterTuning.W_Kite_MassChaosSkip;
                case PsycasterStance.Disengage: return PsycasterTuning.W_Disengage_MassChaosSkip;
                case PsycasterStance.CrowdControl: return PsycasterTuning.W_CrowdControl_MassChaosSkip;
                case PsycasterStance.Hunt: return PsycasterTuning.W_Hunt_MassChaosSkip;
                case PsycasterStance.Engulfed: return PsycasterTuning.W_Engulfed_MassChaosSkip;
                case PsycasterStance.Survive: return PsycasterTuning.W_Survive_MassChaosSkip;
                default: return 0f;
            }
        }

        protected override float MinPsyfocusFraction { get { return 0.50f; } }

        protected override bool IsContextuallyAvailable(PsycasterBrain brain, BattlefieldSnapshot snap)
        {
            // Нужен значимый кластер.
            return snap.largestClusterSize >= 3 && snap.largestClusterCenter.IsValid;
        }

        protected override ScoredAction ScoreInternal(PsycasterBrain brain, BattlefieldSnapshot snap)
        {
            float dist = brain.Caster.Position.DistanceTo(snap.largestClusterCenter);
            if (dist > PsycasterTuning.SkipRangeMax)
                return ScoredAction.None;

            float raw = snap.largestClusterSize * 1.5f;

            // Если HP просел и кластер близко — точно пора сбрасывать.
            if (snap.casterHpFraction < 0.5f && dist < 12f)
                raw *= 1.5f;

            ScoredAction action = new ScoredAction();
            action.abilityDefName = AbilityDefName;
            action.targetType = ScoredActionTargetType.Cell;
            action.targetCell = snap.largestClusterCenter;
            action.castWarmupTicks = PsycasterTuning.CastWarmupMedium;
            action.score = raw;
            action.debugReason = "MassChaosSkip cluster=" + snap.largestClusterSize
                                     + " d=" + dist.ToString("F1");
            return action;
        }
    }
}
