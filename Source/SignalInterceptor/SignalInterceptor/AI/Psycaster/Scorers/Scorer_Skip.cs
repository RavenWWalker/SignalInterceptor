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

            for (int i = 0; i < snap.enemies.Count; i++)
            {
                EnemyAssessment e = snap.enemies[i];
                if (e == null || e.pawn == null) continue;
                if (e.role != EnemyRole.Ranged && e.role != EnemyRole.Sniper) continue;
                if (e.distanceToCaster < PsycasterTuning.SkipMinTargetDistance) continue;
                if (e.distanceToCaster > PsycasterTuning.SkipRangeMax) continue;

                // Чем выше threat и чем дальше — тем выгоднее тащить к себе.
                float raw = (e.threatScore / 10f) + (e.distanceToCaster * 0.10f);

                // Толстый damage-dealer карты — приоритет.
                // Считаем долю threat этой цели от суммарного threat всех врагов.
                if (e.threatScore >= AverageEnemyThreat(snap) * 1.8f)
                    raw *= 1.4f;

                if (raw > bestRaw)
                {
                    bestRaw = raw;
                    best = e;
                }
            }

            if (best == null)
                return ScoredAction.None;

            // Целевая клетка — соседняя с псикастером (мили-дистанция).
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
                                     + " -> " + dest;
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
