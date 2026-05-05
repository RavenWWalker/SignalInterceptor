using Verse;

namespace SignalInterceptor.AI.Psycaster
{
    /// <summary>
    /// BerserkPulse — AoE-ярость. Цели атакуют ВСЁ вокруг, включая друг друга.
    /// Самая разрушительная пси-способность в арсенале.
    ///
    /// Стратегически: лучшая цель — плотный кластер 3+ человекоподобных врагов,
    /// особенно если среди них есть ближники (они быстрее всех передерутся).
    /// Не имеет смысла на одного — есть Berserk одиночный.
    /// На животных и мехах не работает.
    /// </summary>
    public class Scorer_BerserkPulse : AbilityScorerBase
    {
        public override string AbilityDefName { get { return "BerserkPulse"; } }

        protected override float StanceMultiplier(PsycasterStance stance)
        {
            switch (stance)
            {
                case PsycasterStance.Opening: return PsycasterTuning.W_Opening_BerserkPulse;
                case PsycasterStance.Kite: return PsycasterTuning.W_Kite_BerserkPulse;
                case PsycasterStance.Disengage: return PsycasterTuning.W_Disengage_BerserkPulse;
                case PsycasterStance.CrowdControl: return PsycasterTuning.W_CrowdControl_BerserkPulse;
                case PsycasterStance.Hunt: return PsycasterTuning.W_Hunt_BerserkPulse;
                case PsycasterStance.Engulfed: return PsycasterTuning.W_Engulfed_BerserkPulse;
                case PsycasterStance.Survive: return PsycasterTuning.W_Survive_BerserkPulse;
                default: return 0f;
            }
        }

        protected override bool IsContextuallyAvailable(PsycasterBrain brain, BattlefieldSnapshot snap)
        {
            // BerserkPulse имеет смысл только если на карте 3+ восприимчивых врагов.
            // Иначе вообще не пытаемся.
            int susceptibleCount = 0;
            if (snap.enemies != null)
            {
                for (int i = 0; i < snap.enemies.Count; i++)
                {
                    EnemyAssessment e = snap.enemies[i];
                    if (e != null && e.IsSusceptibleToMindControl && !e.IsMechanoid)
                        susceptibleCount++;
                }
            }
            return susceptibleCount >= 3;
        }

        protected override ScoredAction ScoreInternal(PsycasterBrain brain, BattlefieldSnapshot snap)
        {
            IntVec3 bestCell = IntVec3.Invalid;
            int bestHits = 0;

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
                for (int j = 0; j < snap.enemies.Count; j++)
                {
                    EnemyAssessment e = snap.enemies[j];
                    if (e == null || e.pawn == null) continue;
                    if (!e.IsSusceptibleToMindControl) continue;
                    if (e.IsMechanoid) continue;

                    if (e.pawn.Position.DistanceTo(cell) <= 4.0f)
                        hits++;
                }

                if (hits < 3) continue; // меньше 3 — нет смысла

                if (hits > bestHits)
                {
                    bestHits = hits;
                    bestCell = cell;
                }
            }

            if (!bestCell.IsValid)
                return ScoredAction.None;

            ScoredAction action = new ScoredAction();
            action.abilityDefName = AbilityDefName;
            action.targetCell = bestCell;
            action.targetType = ScoredActionTargetType.Cell;
            action.castWarmupTicks = PsycasterTuning.CastWarmupLong;

            // BerserkPulse — большая способность с большим cooldown'ом.
            // Базовый score высокий и квадратично растёт от числа целей,
            // потому что они начинают резать друг друга — синергия.
            float baseScore = bestHits * bestHits * 0.5f;

            action.score = baseScore;
            action.debugReason = "BerserkPulse on " + bestHits + " enemies (cluster cell " + bestCell + ")";

            return action;
        }
    }
}
