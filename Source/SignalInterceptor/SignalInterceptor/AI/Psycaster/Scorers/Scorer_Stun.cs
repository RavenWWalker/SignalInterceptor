using Verse;

namespace SignalInterceptor.AI.Psycaster
{
    /// <summary>
    /// Stun — точечное оглушение одной цели на ~3 секунды.
    /// Дешёвый, быстрый каст. Главное применение: прервать готовящегося стрелка
    /// или дать пси-кастеру время отбежать от ближника.
    ///
    /// Лучшая цель — самый опасный враг с LOS, ещё не оглушённый и не под mind control.
    /// Не работает на mechanoid (формально работает, но у них своя логика стана —
    /// и все равно бесполезно сжигать на них фокус).
    /// </summary>
    public class Scorer_Stun : AbilityScorerBase
    {
        public override string AbilityDefName { get { return "Stun"; } }

        protected override float StanceMultiplier(PsycasterStance stance)
        {
            switch (stance)
            {
                case PsycasterStance.Opening: return PsycasterTuning.W_Opening_Stun;
                case PsycasterStance.Kite: return PsycasterTuning.W_Kite_Stun;
                case PsycasterStance.Disengage: return PsycasterTuning.W_Disengage_Stun;
                case PsycasterStance.CrowdControl: return PsycasterTuning.W_CrowdControl_Stun;
                case PsycasterStance.Hunt: return PsycasterTuning.W_Hunt_Stun;
                case PsycasterStance.Engulfed: return PsycasterTuning.W_Engulfed_Stun;
                case PsycasterStance.Survive: return PsycasterTuning.W_Survive_Stun;
                default: return 0f;
            }
        }

        protected override ScoredAction ScoreInternal(PsycasterBrain brain, BattlefieldSnapshot snap)
        {
            if (snap.enemies == null || snap.enemies.Count == 0)
                return ScoredAction.None;

            EnemyAssessment best = null;
            float bestScore = 0f;

            bool singleEnemy = snap.enemies.Count == 1;

            for (int i = 0; i < snap.enemies.Count; i++)
            {
                EnemyAssessment e = snap.enemies[i];

                if (e == null || e.pawn == null)
                    continue;

                if (!e.hasLineOfSight)
                    continue;

                if (e.distanceToCaster > 18f)
                    continue;

                if (e.isStunned)
                    continue;

                if (e.IsMechanoid)
                    continue;

                if (e.role == EnemyRole.Wimp)
                    continue;

                // Если цель только что была Beckon/Skip'нута и ещё далеко —
                // сначала пусть кастер добежит. Иначе будет цикл:
                // Beckon -> Stun на дистанции -> снова добегает, но стан уже спал.
                if (singleEnemy && brain.WasPawnRecentlyMoved(e.pawn) && e.distanceToCaster > 6f)
                    continue;

                float score = e.threatScore / 10f;

                if (e.role == EnemyRole.Sniper || e.role == EnemyRole.Heavy)
                    score *= 1.5f;

                if (e.hpFraction < 0.4f)
                    score *= 1.2f;

                // 1v1: стан в ближнем бою — полезен, но не должен превращаться
                // в бесконечный stun-spam вместо ударов.
                if (singleEnemy && e.distanceToCaster <= 1.6f)
                {
                    score = 18f;

                    if (e.role == EnemyRole.Sniper || e.role == EnemyRole.Heavy)
                        score += 5f;

                    if (e.hpFraction < 0.5f)
                        score += 3f;

                    // Если цель недавно уже контролилась, чуть режем повторный стан.
                    if (brain.WasPawnRecentlyMoved(e.pawn))
                        score *= 0.65f;
                }
                else if (singleEnemy && e.distanceToCaster <= 3.5f)
                {
                    score = 14f;

                    if (e.role == EnemyRole.Sniper || e.role == EnemyRole.Heavy)
                        score += 4f;

                    if (e.hpFraction < 0.5f)
                        score += 2f;

                    if (brain.WasPawnRecentlyMoved(e.pawn))
                        score *= 0.65f;
                }
                else if (singleEnemy && e.IsRanged && e.distanceToCaster <= 7f)
                {
                    // Стрелок отступает. Можно станить, но score ниже melee,
                    // чтобы melee pursuit не перебивался постоянно.
                    score = 7f + (e.threatScore / 25f);

                    if (brain.WasPawnRecentlyMoved(e.pawn))
                        score *= 0.5f;
                }

                if (brain.CurrentStance == PsycasterStance.Survive)
                    score *= 0.35f;

                if (score > bestScore)
                {
                    bestScore = score;
                    best = e;
                }
            }

            if (best == null || bestScore <= 0f)
                return ScoredAction.None;

            ScoredAction action = new ScoredAction();
            action.abilityDefName = AbilityDefName;
            action.targetPawn = best.pawn;
            action.targetType = ScoredActionTargetType.Pawn;
            action.castWarmupTicks = PsycasterTuning.CastWarmupShort;
            action.score = bestScore;
            action.debugReason = "Stun on " + best.pawn.LabelShort
                                 + " (role=" + best.role
                                 + ", threat=" + best.threatScore.ToString("F1")
                                 + ", d=" + best.distanceToCaster.ToString("F1") + ")";

            return action;
        }
    }
}
