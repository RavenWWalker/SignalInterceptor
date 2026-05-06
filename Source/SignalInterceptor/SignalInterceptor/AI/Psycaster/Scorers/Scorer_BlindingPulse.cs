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

            IntVec3 bestCell = IntVec3.Invalid;
            int bestHits = 0;
            float bestRangedDpsInRadius = 0f;
            float bestScore = 0f;
            string reason = null;

            bool singleEnemy = snap.enemies.Count == 1;

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
                    if (e.IsMechanoid) continue;

                    if (e.pawn.Position.DistanceTo(cell) <= 4.0f)
                    {
                        hits++;

                        if (e.IsRanged && e.canShootNow)
                            rangedDps += e.estimatedDps;
                    }
                }

                float score = 0f;
                string localReason = null;

                if (hits >= 2)
                {
                    score = hits * (1.0f + rangedDps / 30f);
                    localReason = "BlindingPulse hitting " + hits + " enemies, suppressing "
                                  + rangedDps.ToString("F1") + " ranged DPS";
                }
                else if (singleEnemy)
                {
                    EnemyAssessment e = center;

                    // Новый режим: одиночная dangerous ranged цель.
                    // Это нужно против снайпера/джамп-пака, когда Stun на дистанции запрещён,
                    // а Skip/Beckon могут быть на cooldown.
                    if (!e.IsRanged)
                        continue;

                    if (e.IsMechanoid)
                        continue;

                    if (!e.hasLineOfSight && !brain.IsAntiKiteEscapeTarget(e.pawn))
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

                    score = 9f + (e.threatScore / 10f) + (e.distanceToCaster * 0.08f);

                    if (e.role == EnemyRole.Sniper || e.role == EnemyRole.Heavy)
                        score *= 1.25f;

                    if (brain.IsAntiKiteEscapeTarget(e.pawn))
                        score *= 1.35f;

                    if (brain.IsKillContractTarget(e.pawn))
                        score *= 1.10f;

                    localReason = "Single-target BlindingPulse "
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
                    bestRangedDpsInRadius = rangedDps;
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
            action.debugReason = reason ?? ("BlindingPulse score=" + bestScore.ToString("F1"));

            return action;
        }
    }
}
