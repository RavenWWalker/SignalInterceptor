using Verse;

namespace SignalInterceptor.AI.Psycaster
{
    /// <summary>
    /// BlindingPulse — AoE-слепота вокруг точки. Цели не могут стрелять.
    /// Главное применение: подавить группу дальников.
    ///
    /// Лучшая точка — центр кластера 2+ дальников вне SelfDamageSafeRadius.
    /// На животных тоже работает (формально), но низкий приоритет.
    /// </summary>
    public class Scorer_BlindingPulse : AbilityScorerBase
    {
        public override string AbilityDefName { get { return "BlindingPulse"; } }

        protected override float StanceMultiplier(PsycasterStance stance)
        {
            switch (stance)
            {
                case PsycasterStance.Opening: return PsycasterTuning.W_Opening_BlindingPulse;
                case PsycasterStance.Kite: return PsycasterTuning.W_Kite_BlindingPulse;
                case PsycasterStance.Disengage: return PsycasterTuning.W_Disengage_BlindingPulse;
                case PsycasterStance.CrowdControl: return PsycasterTuning.W_CrowdControl_BlindingPulse;
                case PsycasterStance.Hunt: return PsycasterTuning.W_Hunt_BlindingPulse;
                case PsycasterStance.Engulfed: return PsycasterTuning.W_Engulfed_BlindingPulse;
                case PsycasterStance.Survive: return PsycasterTuning.W_Survive_BlindingPulse;
                default: return 0f;
            }
        }

        protected override ScoredAction ScoreInternal(PsycasterBrain brain, BattlefieldSnapshot snap)
        {
            if (snap.enemies == null || snap.enemies.Count == 0)
                return ScoredAction.None;

            // Ищем точку с максимальным числом восприимчивых врагов в радиусе ClusterRadius.
            // Подходящие центры — позиции самих врагов (по аналогии со старым FindBestPsycasterPulseCell).
            IntVec3 bestCell = IntVec3.Invalid;
            int bestHits = 0;
            float bestRangedDpsInRadius = 0f;

            for (int i = 0; i < snap.enemies.Count; i++)
            {
                EnemyAssessment center = snap.enemies[i];
                if (center == null || center.pawn == null) continue;
                if (!center.IsSusceptibleToMindControl && !center.IsAnimal) continue;

                IntVec3 cell = center.pawn.Position;
                float distFromCaster = cell.DistanceTo(snap.caster.Position);

                if (distFromCaster > PsycasterTuning.StandardPulseRange) continue;
                if (distFromCaster <= PsycasterTuning.SelfDamageSafeRadius) continue;
                if (!GenSight.LineOfSight(snap.caster.Position, cell, snap.map)) continue;

                int hits = 0;
                float rangedDps = 0f;

                for (int j = 0; j < snap.enemies.Count; j++)
                {
                    EnemyAssessment e = snap.enemies[j];
                    if (e == null || e.pawn == null) continue;
                    if (!e.IsSusceptibleToMindControl && !e.IsAnimal) continue;
                    if (e.IsMechanoid) continue; // не работает на мехах

                    if (e.pawn.Position.DistanceTo(cell) <= 4.0f) // радиус эффекта BlindingPulse
                    {
                        hits++;
                        if (e.IsRanged && e.canShootNow)
                            rangedDps += e.estimatedDps;
                    }
                }

                if (hits < 2) continue; // не имеет смысла на одиночку — есть Stun

                if (hits > bestHits || (hits == bestHits && rangedDps > bestRangedDpsInRadius))
                {
                    bestHits = hits;
                    bestRangedDpsInRadius = rangedDps;
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

            // Базовый score = число целей * (1 + норма от подавленного DPS).
            float baseScore = bestHits * (1.0f + bestRangedDpsInRadius / 30f);

            action.score = baseScore;
            action.debugReason = "BlindingPulse hitting " + bestHits + " enemies, suppressing "
                + bestRangedDpsInRadius.ToString("F1") + " ranged DPS";

            return action;
        }
    }
}
