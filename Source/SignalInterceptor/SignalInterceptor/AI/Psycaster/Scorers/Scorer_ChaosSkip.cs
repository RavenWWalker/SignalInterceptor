using Verse;

namespace SignalInterceptor.AI.Psycaster
{
    /// <summary>
    /// ChaosSkip — телепорт врага в СЛУЧАЙНУЮ клетку.
    /// Тактика: сбросить ближника, который догоняет, либо растащить плотную группу
    /// в Survive/Disengage. Дешевле Skip, не требует destination.
    /// </summary>
    public class Scorer_ChaosSkip : AbilityScorerBase
    {
        public override string AbilityDefName { get { return "ChaosSkip"; } }

        protected override float StanceMultiplier(PsycasterStance stance)
        {
            switch (stance)
            {
                case PsycasterStance.Opening: return PsycasterTuning.W_Opening_ChaosSkip;
                case PsycasterStance.Kite: return PsycasterTuning.W_Kite_ChaosSkip;
                case PsycasterStance.Disengage: return PsycasterTuning.W_Disengage_ChaosSkip;
                case PsycasterStance.CrowdControl: return PsycasterTuning.W_CrowdControl_ChaosSkip;
                case PsycasterStance.Hunt: return PsycasterTuning.W_Hunt_ChaosSkip;
                case PsycasterStance.Engulfed: return PsycasterTuning.W_Engulfed_ChaosSkip;
                case PsycasterStance.Survive: return PsycasterTuning.W_Survive_ChaosSkip;
                default: return 0f;
            }
        }

        protected override float MinPsyfocusFraction { get { return 0.20f; } }

        protected override ScoredAction ScoreInternal(PsycasterBrain brain, BattlefieldSnapshot snap)
        {
            if (snap.enemies == null || snap.enemies.Count == 0)
                return ScoredAction.None;

            EnemyAssessment best = null;
            float bestRaw = 0f;

            bool panicState =
                brain.CurrentStance == PsycasterStance.Engulfed ||
                brain.CurrentStance == PsycasterStance.Survive;

            for (int i = 0; i < snap.enemies.Count; i++)
            {
                EnemyAssessment e = snap.enemies[i];

                if (e == null || e.pawn == null)
                    continue;

                if (brain.WasPawnRecentlyMoved(e.pawn))
                    continue;

                if (e.distanceToCaster > PsycasterTuning.ChaosSkipMaxDistance)
                    continue;

                // КЛЮЧЕВАЯ ПРАВКА:
                // В Kite нельзя ChaosSkip'ать стрелков/снайперов.
                // Это телепортирует их в случайную клетку и ломает план "притянуть -> оглушить -> зарезать".
                if (e.IsRanged && !panicState)
                    continue;

                // В панике можно ChaosSkip'нуть дальника только если он уже почти вплотную.
                if (e.IsRanged && panicState && e.distanceToCaster > 2.5f)
                    continue;

                float raw = e.threatScore / 10f;

                if (e.IsMelee)
                    raw *= 1.8f;

                if (e.IsAnimal)
                    raw *= 1.4f;

                if (e.distanceToCaster <= 2.5f)
                    raw *= 1.5f;
                else if (e.distanceToCaster <= 4f)
                    raw *= 1.25f;

                if (panicState)
                    raw *= 1.4f;

                // В обычном Kite это только emergency-сброс ближника, не ротационная способность.
                if (brain.CurrentStance == PsycasterStance.Kite)
                    raw *= 0.65f;

                if (raw > bestRaw)
                {
                    bestRaw = raw;
                    best = e;
                }
            }

            if (best == null)
                return ScoredAction.None;

            ScoredAction action = new ScoredAction();
            action.abilityDefName = AbilityDefName;
            action.targetType = ScoredActionTargetType.Pawn;
            action.targetPawn = best.pawn;
            action.castWarmupTicks = PsycasterTuning.CastWarmupShort;
            action.score = bestRaw;
            action.debugReason = "ChaosSkip emergency " + best.pawn.LabelShort
                                 + " (role=" + best.role
                                 + ", d=" + best.distanceToCaster.ToString("F1") + ")";

            return action;
        }
    }
}
