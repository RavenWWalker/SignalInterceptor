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

            // Ищем лучшую цель: с наивысшим threatScore, в LOS, в радиусе 18, не оглушённую.
            EnemyAssessment best = null;
            float bestThreat = 0f;

            for (int i = 0; i < snap.enemies.Count; i++)
            {
                EnemyAssessment e = snap.enemies[i];
                if (e == null || e.pawn == null) continue;
                if (!e.hasLineOfSight) continue;
                if (e.distanceToCaster > 18f) continue;
                if (e.isStunned) continue;
                if (e.IsMechanoid) continue; // не тратим Stun на роботов
                if (e.role == EnemyRole.Wimp) continue; // и на гражданских

                if (e.threatScore > bestThreat)
                {
                    bestThreat = e.threatScore;
                    best = e;
                }
            }

            if (best == null)
                return ScoredAction.None;

            ScoredAction action = new ScoredAction();
            action.abilityDefName = AbilityDefName;
            action.targetPawn = best.pawn;
            action.targetType = ScoredActionTargetType.Pawn;
            action.castWarmupTicks = PsycasterTuning.CastWarmupShort;

            // Базовый score = threatScore цели, нормализованный.
            // threatScore обычно в диапазоне 5-30, делим на 10 для приведения к 0.5-3.0.
            float baseScore = best.threatScore / 10f;

            // Бонус если цель собирается стрелять (Sniper/Heavy с LOS).
            if (best.role == EnemyRole.Sniper || best.role == EnemyRole.Heavy)
                baseScore *= 1.5f;

            // Бонус если у цели низкий HP — добивание стандартное.
            if (best.hpFraction < 0.4f)
                baseScore *= 1.2f;

            action.score = baseScore;
            action.debugReason = "Stun on " + best.pawn.LabelShort
                + " (role=" + best.role
                + ", threat=" + best.threatScore.ToString("F1") + ")";

            return action;
        }
    }
}
