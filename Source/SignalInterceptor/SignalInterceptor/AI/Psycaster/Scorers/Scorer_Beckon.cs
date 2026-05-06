using Verse;

namespace SignalInterceptor.AI.Psycaster
{
    /// <summary>
    /// Beckon — тянет ОДНОГО врага по прямой к псикастеру.
    /// Тактика: главное лекарство от deadlock «дальники стоят и стреляют, не могу подойти».
    /// Идеально на самого толстого damage-dealer'а.
    /// </summary>
    public class Scorer_Beckon : AbilityScorerBase
    {
        public override string AbilityDefName { get { return "Beckon"; } }

        protected override float StanceMultiplier(PsycasterStance stance)
        {
            switch (stance)
            {
                case PsycasterStance.Opening: return PsycasterTuning.W_Opening_Beckon;
                case PsycasterStance.Kite: return PsycasterTuning.W_Kite_Beckon;
                case PsycasterStance.Disengage: return PsycasterTuning.W_Disengage_Beckon;
                case PsycasterStance.CrowdControl: return PsycasterTuning.W_CrowdControl_Beckon;
                case PsycasterStance.Hunt: return PsycasterTuning.W_Hunt_Beckon;
                case PsycasterStance.Engulfed: return PsycasterTuning.W_Engulfed_Beckon;
                case PsycasterStance.Survive: return PsycasterTuning.W_Survive_Beckon;
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
                if (e.role != EnemyRole.Ranged && e.role != EnemyRole.Sniper) continue;
                if (e.distanceToCaster < PsycasterTuning.BeckonMinDistance) continue;
                if (e.distanceToCaster > PsycasterTuning.BeckonMaxDistance) continue;

                // База: threat + бонус за дистанцию (чем дальше дальник — тем больше выгоды от Beckon).
                float raw = (e.threatScore / 10f) * 0.8f + (e.distanceToCaster * 0.06f);

                // Толстый damage-dealer карты — приоритет.
                // Считаем долю threat этой цели от суммарного threat всех врагов.
                if (e.threatScore >= AverageEnemyThreat(snap) * 1.8f)
                    raw *= 1.4f;

                // Снайпер — почти всегда главная цель Beckon.
                if (e.role == EnemyRole.Sniper)
                    raw *= 1.3f;

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
            action.debugReason = "Beckon " + best.pawn.LabelShort
                                     + " (role=" + best.role
                                     + ", d=" + best.distanceToCaster.ToString("F1") + ")";
            return action;
        }

        private float AverageEnemyThreat(BattlefieldSnapshot snap)
        {
            if (snap.enemies == null || snap.enemies.Count == 0) return 1f;
            float sum = 0f;
            int n = 0;
            for (int i = 0; i < snap.enemies.Count; i++)
            {
                EnemyAssessment e = snap.enemies[i];
                if (e == null || e.pawn == null) continue;
                sum += e.threatScore;
                n++;
            }
            return n > 0 ? sum / n : 1f;
        }
    }
}
