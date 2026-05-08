using Verse;

namespace SignalInterceptor.AI.Psycaster
{
    /// <summary>
    /// ChaosSkip — аварийный телепорт врага в случайную клетку.
    ///
    /// ВАЖНО:
    /// - НЕ использовать как обычный opener;
    /// - НЕ использовать для начала melee-погони;
    /// - НЕ использовать по Wimp/ближнику на дистанции;
    /// - использовать только как panic-button, когда пси-кастер реально зажат.
    ///
    /// Для группового разброса есть MassChaosSkip.
    /// Для притягивания цели есть Beckon/Skip/Stun.
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

            bool panicState =
                brain.CurrentStance == PsycasterStance.Engulfed ||
                brain.CurrentStance == PsycasterStance.Survive;

            /*
             * ГЛАВНЫЙ ФИКС:
             * Обычный ChaosSkip больше не используется вне panic-state.
             *
             * Именно это ломало бой:
             * - цель была на 6-10 клетках;
             * - AI кастовал ChaosSkip;
             * - цель улетала на 18 клеток;
             * - AI сам себе создавал длинную погоню.
             */
            if (!panicState)
                return ScoredAction.None;

            /*
             * Даже в panic-state не надо использовать ChaosSkip, если пси-кастер
             * не зажат рядом.
             */
            if (snap.enemiesAdjacent == null || snap.enemiesAdjacent.Count == 0)
                return ScoredAction.None;

            EnemyAssessment best = null;
            float bestRaw = 0f;

            for (int i = 0; i < snap.enemiesAdjacent.Count; i++)
            {
                EnemyAssessment e = snap.enemiesAdjacent[i];

                if (e == null || e.pawn == null)
                    continue;

                if (brain.WasPawnRecentlyMoved(e.pawn))
                    continue;

                if (brain.IsKillContractTarget(e.pawn))
                    continue;

                if (e.distanceToCaster > 2.5f)
                    continue;

                /*
                 * Не тратим ChaosSkip на дальника, если он не стоит прямо в упор.
                 */
                if (e.IsRanged && e.distanceToCaster > 1.6f)
                    continue;

                /*
                 * Не отбрасываем слабого Wimp, если можно просто зарезать/оглушить.
                 */
                if (e.role == EnemyRole.Wimp && snap.casterHpFraction > 0.35f)
                    continue;

                float raw = 8f + (e.threatScore / 8f);

                if (e.IsAnimal)
                    raw *= 1.35f;

                if (e.IsMelee)
                    raw *= 1.25f;

                if (e.distanceToCaster <= 1.2f)
                    raw *= 1.25f;

                if (snap.casterHpFraction < 0.45f)
                    raw *= 1.5f;

                if (snap.enemiesAdjacent.Count >= 3)
                    raw *= 1.35f;

                if (raw > bestRaw)
                {
                    bestRaw = raw;
                    best = e;
                }
            }

            if (best == null || bestRaw <= 0f)
                return ScoredAction.None;

            ScoredAction action = new ScoredAction();
            action.abilityDefName = AbilityDefName;
            action.targetType = ScoredActionTargetType.Pawn;
            action.targetPawn = best.pawn;
            action.castWarmupTicks = PsycasterTuning.CastWarmupShort;
            action.score = bestRaw;
            action.debugReason = "ChaosSkip panic " + best.pawn.LabelShort
                                 + " (role=" + best.role
                                 + ", d=" + best.distanceToCaster.ToString("F1")
                                 + ", adjacent=" + snap.enemiesAdjacent.Count
                                 + ", hp=" + snap.casterHpFraction.ToString("F2") + ")";

            return action;
        }
    }
}
