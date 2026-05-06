using Verse;

namespace SignalInterceptor.AI.Psycaster
{
    /// <summary>
    /// VertigoPulse — AoE-дезориентация. Цели рвут, не могут нормально действовать.
    /// Похож на BlindingPulse, но эффект другой и cooldown отдельный.
    ///
    /// Стратегически: бэкап для BlindingPulse. Когда BlindingPulse на soft-cooldown,
    /// VertigoPulse даёт второй шанс подавить группу.
    /// Эффективнее BlindingPulse против ближников (т.к. слепота им не очень мешает,
    /// а тошнота снижает вообще всё).
    /// </summary>
    public class Scorer_VertigoPulse : AbilityScorerBase
    {
        public override string AbilityDefName { get { return "VertigoPulse"; } }

        protected override float StanceMultiplier(PsycasterStance stance)
        {
            switch (stance)
            {
                case PsycasterStance.Opening: return PsycasterTuning.W_Opening_VertigoPulse;
                case PsycasterStance.Kite: return PsycasterTuning.W_Kite_VertigoPulse;
                case PsycasterStance.Disengage: return PsycasterTuning.W_Disengage_VertigoPulse;
                case PsycasterStance.CrowdControl: return PsycasterTuning.W_CrowdControl_VertigoPulse;
                case PsycasterStance.Hunt: return PsycasterTuning.W_Hunt_VertigoPulse;
                case PsycasterStance.Engulfed: return PsycasterTuning.W_Engulfed_VertigoPulse;
                case PsycasterStance.Survive: return PsycasterTuning.W_Survive_VertigoPulse;
                default: return 0f;
            }
        }

        protected override ScoredAction ScoreInternal(PsycasterBrain brain, BattlefieldSnapshot snap)
        {
            if (snap.enemies == null || snap.enemies.Count == 0)
                return ScoredAction.None;

            IntVec3 bestCell = IntVec3.Invalid;
            int bestHits = 0;
            int bestMeleeHits = 0;
            float bestScore = 0f;
            string reason = null;

            bool singleEnemy = snap.enemies.Count == 1;

            for (int i = 0; i < snap.enemies.Count; i++)
            {
                EnemyAssessment center = snap.enemies[i];
                if (center == null || center.pawn == null) continue;
                if (!center.IsSusceptibleToMindControl) continue;

                IntVec3 cell = center.pawn.Position;
                float distFromCaster = cell.DistanceTo(snap.caster.Position);

                if (distFromCaster > PsycasterTuning.StandardPulseRange) continue;
                if (distFromCaster <= PsycasterTuning.SelfDamageSafeRadius) continue;
                if (!GenSight.LineOfSight(snap.caster.Position, cell, snap.map)) continue;

                int hits = 0;
                int meleeHits = 0;

                for (int j = 0; j < snap.enemies.Count; j++)
                {
                    EnemyAssessment e = snap.enemies[j];
                    if (e == null || e.pawn == null) continue;
                    if (!e.IsSusceptibleToMindControl) continue;
                    if (e.IsMechanoid) continue;

                    if (e.pawn.Position.DistanceTo(cell) <= 4.0f)
                    {
                        hits++;
                        if (e.IsMelee) meleeHits++;
                    }
                }

                float score = 0f;
                string localReason = null;

                if (hits >= 2)
                {
                    score = hits + meleeHits * 0.5f;

                    localReason = "VertigoPulse hitting " + hits
                                  + " enemies (" + meleeHits + " melee)";
                }
                else if (singleEnemy)
                {
                    EnemyAssessment e = center;

                    if (e.IsMechanoid)
                        continue;

                    if (e.distanceToCaster < 6f)
                        continue;

                    bool dangerous =
                        e.role == EnemyRole.Sniper ||
                        e.role == EnemyRole.Heavy ||
                        e.threatScore >= 18f ||
                        brain.IsAntiKiteEscapeTarget(e.pawn);

                    if (!dangerous)
                        continue;

                    // Vertigo как single-target suppression:
                    // чуть слабее, чем Blinding против чистого стрелка,
                    // но хорош как резервный контроль, если Blinding/Skip/Beckon недоступны.
                    score = 8f + (e.threatScore / 12f) + (e.distanceToCaster * 0.06f);

                    if (e.role == EnemyRole.Sniper || e.role == EnemyRole.Heavy)
                        score *= 1.20f;

                    if (brain.IsAntiKiteEscapeTarget(e.pawn))
                        score *= 1.30f;

                    if (brain.IsKillContractTarget(e.pawn))
                        score *= 1.10f;

                    localReason = "Single-target VertigoPulse "
                                  + e.pawn.LabelShort
                                  + " role=" + e.role
                                  + " threat=" + e.threatScore.ToString("F1")
                                  + " d=" + e.distanceToCaster.ToString("F1")
                                  + " antiKite=" + brain.IsAntiKiteEscapeTarget(e.pawn);
                }
                else
                {
                    continue;
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    bestHits = hits;
                    bestMeleeHits = meleeHits;
                    bestCell = cell;
                    reason = localReason;
                }
            }

            if (!bestCell.IsValid || bestScore <= 0f)
                return ScoredAction.None;

            ScoredAction action = new ScoredAction();
            action.abilityDefName = AbilityDefName;
            action.targetCell = bestCell;
            action.targetType = ScoredActionTargetType.Cell;
            action.castWarmupTicks = PsycasterTuning.CastWarmupMedium;
            action.score = bestScore;
            action.debugReason = reason ?? ("VertigoPulse score=" + bestScore.ToString("F1"));

            return action;
        }
    }
}
