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

                if (hits < 2) continue;

                // Предпочитаем точку, где больше ближников (VertigoPulse сильнее против них).
                if (hits > bestHits || (hits == bestHits && meleeHits > bestMeleeHits))
                {
                    bestHits = hits;
                    bestMeleeHits = meleeHits;
                    bestCell = cell;
                }
            }

            if (!bestCell.IsValid)
                return ScoredAction.None;

            ScoredAction action = new ScoredAction();
            action.abilityDefName = AbilityDefName;
            action.targetCell = bestCell;
            action.targetType = ScoredActionTargetType.Cell;
            action.castWarmupTicks = PsycasterTuning.CastWarmupMedium;

            // Базовый score = число целей + бонус за каждого ближника в зоне.
            float baseScore = bestHits + bestMeleeHits * 0.5f;

            action.score = baseScore;
            action.debugReason = "VertigoPulse hitting " + bestHits + " enemies (" + bestMeleeHits + " melee)";

            return action;
        }
    }
}
