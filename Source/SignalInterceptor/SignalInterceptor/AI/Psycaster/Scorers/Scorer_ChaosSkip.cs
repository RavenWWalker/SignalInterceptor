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

            for (int i = 0; i < snap.enemies.Count; i++)
            {
                EnemyAssessment e = snap.enemies[i];
                if (e == null || e.pawn == null) continue;
                if (brain.WasPawnRecentlyMoved(e.pawn)) continue;
                if (e.distanceToCaster > PsycasterTuning.ChaosSkipMaxDistance) continue;

                float raw = e.threatScore / 10f;

                // Ближник, который добежал — приоритетный «отпинуть».
                if (e.role == EnemyRole.Melee) raw *= 1.6f;

                // Очень близко — тем выгоднее сбросить.
                if (e.distanceToCaster < 4f) raw *= 1.3f;

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
            action.debugReason = "ChaosSkip close " + best.pawn.LabelShort
                                     + " (role=" + best.role
                                     + ", d=" + best.distanceToCaster.ToString("F1") + ")";
            return action;
        }
    }
}
