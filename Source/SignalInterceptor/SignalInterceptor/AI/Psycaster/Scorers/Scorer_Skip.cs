using Verse;

namespace SignalInterceptor.AI.Psycaster
{
    /// <summary>
    /// Skip — телепорт ОДНОЙ цели в указанную клетку.
    /// Тактика: вытащить изолированного дальника ВПЛОТНУЮ к псикастеру и взять в melee.
    /// Лечит deadlock «дальник стоит и стреляет, я не могу подойти».
    /// </summary>
    public class Scorer_Skip : AbilityScorerBase
    {
        public override string AbilityDefName { get { return "Skip"; } }

        protected override float StanceMultiplier(PsycasterStance stance)
        {
            switch (stance)
            {
                case PsycasterStance.Opening: return PsycasterTuning.W_Opening_Skip;
                case PsycasterStance.Kite: return PsycasterTuning.W_Kite_Skip;
                case PsycasterStance.Disengage: return PsycasterTuning.W_Disengage_Skip;
                case PsycasterStance.CrowdControl: return PsycasterTuning.W_CrowdControl_Skip;
                case PsycasterStance.Hunt: return PsycasterTuning.W_Hunt_Skip;
                case PsycasterStance.Engulfed: return PsycasterTuning.W_Engulfed_Skip;
                case PsycasterStance.Survive: return PsycasterTuning.W_Survive_Skip;
                default: return 0f;
            }
        }

        protected override float MinPsyfocusFraction { get { return 0.25f; } }

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
                if (e == null || e.pawn == null) continue;

                bool antiKiteEscape = brain.IsAntiKiteEscapeTarget(e.pawn);

                // Обычно не скипаем недавно перемещённую цель,
                // НО если она jump-pack'ом/рывком сбежала из melee-contract,
                // наоборот надо вернуть её обратно.
                if (brain.WasPawnRecentlyMoved(e.pawn) && !antiKiteEscape)
                    continue;

                if (e.role != EnemyRole.Ranged && e.role != EnemyRole.Sniper && e.role != EnemyRole.Heavy)
                    continue;

                float minDistance = antiKiteEscape ? 6f : PsycasterTuning.SkipMinTargetDistance;

                if (singleEnemy && e.IsRanged)
                    minDistance = antiKiteEscape ? 5f : 8f;

                if (e.distanceToCaster < minDistance)
                    continue;

                if (e.distanceToCaster > PsycasterTuning.SkipRangeMax)
                    continue;

                float raw;

                if (antiKiteEscape)
                {
                    // Антикайт должен уверенно перебивать Wallraise/Stun/обычный chase.
                    raw = 42f + (e.threatScore / 8f) + (e.distanceToCaster * 0.35f);

                    if (e.role == EnemyRole.Sniper || e.role == EnemyRole.Heavy)
                        raw *= 1.25f;
                }
                else
                {
                    raw = 8f + (e.threatScore / 18f) + (e.distanceToCaster * 0.22f);

                    if (e.distanceToCaster >= 6f && e.distanceToCaster <= 14f)
                        raw *= 1.35f;
                    else if (e.distanceToCaster > 14f)
                        raw *= 1.2f;

                    if (e.threatScore >= AverageEnemyThreat(snap) * 1.8f)
                        raw *= 1.25f;

                    if (e.role == EnemyRole.Sniper || e.role == EnemyRole.Heavy)
                        raw *= 1.2f;
                }

                if (raw > bestRaw)
                {
                    bestRaw = raw;
                    best = e;
                }
            }

            if (best == null)
                return ScoredAction.None;

            IntVec3 dest = FindAdjacentDropCell(brain.Caster);
            if (!dest.IsValid)
                return ScoredAction.None;

            ScoredAction action = new ScoredAction();
            action.abilityDefName = AbilityDefName;
            action.targetType = ScoredActionTargetType.PawnToDestination;
            action.targetPawn = best.pawn;
            action.destinationCell = dest;
            action.castWarmupTicks = PsycasterTuning.CastWarmupShort;
            action.score = bestRaw;
            action.debugReason = "Skip ranged " + best.pawn.LabelShort
                                 + " d=" + best.distanceToCaster.ToString("F1")
                                 + " -> " + dest
                                 + " antiKite=" + brain.IsAntiKiteEscapeTarget(best.pawn);

            return action;
        }

        private IntVec3 FindAdjacentDropCell(Pawn caster)
        {
            if (caster == null || caster.Map == null) return IntVec3.Invalid;
            IntVec3 origin = caster.Position;
            for (int i = 0; i < GenAdj.AdjacentCells.Length; i++)
            {
                IntVec3 c = origin + GenAdj.AdjacentCells[i];
                if (!c.InBounds(caster.Map)) continue;
                if (!c.Standable(caster.Map)) continue;
                if (c.GetFirstPawn(caster.Map) != null) continue;
                return c;
            }
            return IntVec3.Invalid;
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
