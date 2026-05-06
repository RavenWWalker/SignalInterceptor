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

            bool singleEnemy = snap.enemies.Count == 1;

            for (int i = 0; i < snap.enemies.Count; i++)
            {
                EnemyAssessment e = snap.enemies[i];

                if (e == null || e.pawn == null)
                    continue;

                if (!e.IsRanged)
                    continue;

                if (e.isStunned || e.isMindControlled)
                    continue;

                bool antiKiteEscape = brain.IsAntiKiteEscapeTarget(e.pawn);

                if (brain.WasPawnRecentlyMoved(e.pawn) && !antiKiteEscape)
                    continue;

                // Старое ограничение 18f в Kite было слишком жёстким.
                // Из-за него Beckon почти не участвовал в дуэли.
                float minUsefulDistance = 18f;

                if (singleEnemy)
                    minUsefulDistance = 10f;

                if (brain.CurrentStance == PsycasterStance.Hunt)
                    minUsefulDistance = 8f;

                if (antiKiteEscape)
                    minUsefulDistance = 6f;

                if (e.distanceToCaster < minUsefulDistance)
                    continue;

                if (e.distanceToCaster > PsycasterTuning.BeckonMaxDistance)
                    continue;

                float raw;

                if (antiKiteEscape)
                {
                    raw = 30f + (e.threatScore / 9f) + (e.distanceToCaster * 0.25f);

                    if (e.role == EnemyRole.Sniper || e.role == EnemyRole.Heavy)
                        raw *= 1.25f;

                    if (e.hasLineOfSight)
                        raw *= 1.15f;
                }
                else
                {
                    raw = 6f + (e.threatScore / 12f) + (e.distanceToCaster * 0.10f);

                    if (e.hasLineOfSight)
                        raw *= 1.20f;

                    if (e.role == EnemyRole.Sniper || e.role == EnemyRole.Heavy)
                        raw *= 1.30f;

                    if (singleEnemy)
                        raw *= 1.35f;

                    if (brain.IsKillContractTarget(e.pawn))
                        raw *= 1.15f;
                }

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
                                 + ", d=" + best.distanceToCaster.ToString("F1")
                                 + ", antiKite=" + brain.IsAntiKiteEscapeTarget(best.pawn)
                                 + ", contract=" + brain.IsKillContractTarget(best.pawn)
                                 + ")";

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
