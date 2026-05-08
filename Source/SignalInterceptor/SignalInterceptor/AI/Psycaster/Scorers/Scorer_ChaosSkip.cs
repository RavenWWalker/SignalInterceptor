using Verse;

namespace SignalInterceptor.AI.Psycaster
{
    /// <summary>
    /// ChaosSkip — телепорт врага в СЛУЧАЙНУЮ клетку.
    ///
    /// ВАЖНО:
    /// - не использовать против единственного melee-врага в дуэли;
    /// - не использовать по цели kill-contract, если это не настоящая паника;
    /// - не ломать melee-commit случайным отбрасыванием цели.
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

            bool singleEnemy = snap.enemies.Count == 1;

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

                /*
                 * ГЛАВНЫЙ ФИКС:
                 * В дуэли 1 на 1 против ближника ChaosSkip почти всегда вреден.
                 * Он случайно отбрасывает цель и ломает собственный melee-commit.
                 *
                 * Разрешаем только в настоящей панике и только если цель уже вплотную.
                 */
                if (singleEnemy && e.IsMelee)
                {
                    if (!panicState)
                        continue;

                    if (e.distanceToCaster > 1.6f)
                        continue;
                }

                /*
                 * Если цель уже является kill-contract целью, не надо её случайно
                 * отбрасывать. Пси-кастер уже решил её зарезать.
                 */
                if (brain.IsKillContractTarget(e.pawn) && !panicState)
                    continue;

                /*
                 * В обычном состоянии ChaosSkip по melee допустим только как
                 * короткий emergency-сброс, когда враг реально рядом.
                 */
                if (e.IsMelee && !panicState && e.distanceToCaster > 2.5f)
                    continue;

                /*
                 * В Kite нельзя ChaosSkip'ать стрелков/снайперов.
                 * Это телепортирует их в случайную клетку и ломает план
                 * "притянуть -> оглушить -> зарезать".
                 */
                if (e.IsRanged && !panicState)
                    continue;

                /*
                 * В панике можно ChaosSkip'нуть дальника только если он уже почти вплотную.
                 */
                if (e.IsRanged && panicState && e.distanceToCaster > 2.5f)
                    continue;

                float raw = e.threatScore / 10f;

                if (e.IsMelee)
                    raw *= 1.8f;

                if (e.IsAnimal)
                    raw *= 1.4f;

                if (e.distanceToCaster <= 1.6f)
                    raw *= 1.6f;
                else if (e.distanceToCaster <= 2.5f)
                    raw *= 1.3f;

                if (panicState)
                    raw *= 1.4f;

                if (brain.CurrentStance == PsycasterStance.Kite)
                    raw *= 0.65f;

                /*
                 * Даже в panic state одиночную melee-цель не надо делать
                 * приоритетнее нормального Stun/melee, если ситуация не критическая.
                 */
                if (singleEnemy && e.IsMelee)
                    raw *= 0.45f;

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
                                 + ", d=" + best.distanceToCaster.ToString("F1")
                                 + ", single=" + singleEnemy
                                 + ", panic=" + panicState
                                 + ")";

            return action;
        }
    }
}
