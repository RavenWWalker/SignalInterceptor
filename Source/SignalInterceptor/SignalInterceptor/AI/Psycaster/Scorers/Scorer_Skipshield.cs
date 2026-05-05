using Verse;

namespace SignalInterceptor.AI.Psycaster
{
    /// <summary>
    /// Skipshield — самозащитный купол, блокирующий снаряды снаружи.
    /// Тактика: триггер по HP или плотному огню. Окно для безопасной перезарядки.
    /// </summary>
    public class Scorer_Skipshield : AbilityScorerBase
    {
        public override string AbilityDefName { get { return "Skipshield"; } }

        protected override float StanceMultiplier(PsycasterStance stance)
        {
            switch (stance)
            {
                case PsycasterStance.Opening: return PsycasterTuning.W_Opening_Skipshield;
                case PsycasterStance.Kite: return PsycasterTuning.W_Kite_Skipshield;
                case PsycasterStance.Disengage: return PsycasterTuning.W_Disengage_Skipshield;
                case PsycasterStance.CrowdControl: return PsycasterTuning.W_CrowdControl_Skipshield;
                case PsycasterStance.Hunt: return PsycasterTuning.W_Hunt_Skipshield;
                case PsycasterStance.Engulfed: return PsycasterTuning.W_Engulfed_Skipshield;
                case PsycasterStance.Survive: return PsycasterTuning.W_Survive_Skipshield;
                default: return 0f;
            }
        }

        protected override float MinPsyfocusFraction { get { return 0.30f; } }

        protected override ScoredAction ScoreInternal(PsycasterBrain brain, BattlefieldSnapshot snap)
        {
            int rangedInLOS = 0;
            for (int i = 0; i < snap.enemies.Count; i++)
            {
                EnemyAssessment e = snap.enemies[i];
                if (e == null || e.pawn == null) continue;
                if (e.role != EnemyRole.Ranged && e.role != EnemyRole.Sniper) continue;
                if (e.distanceToCaster > PsycasterTuning.WallraiseMaxRangedDist) continue;
                if (!e.hasLineOfSight) continue;
                rangedInLOS++;
            }

            bool hpTrigger = snap.casterHpFraction < PsycasterTuning.SkipshieldHpTriggerHp;
            bool fireTrigger = rangedInLOS >= PsycasterTuning.SkipshieldRangedTrigger;

            if (!hpTrigger && !fireTrigger)
                return ScoredAction.None;

            float raw = 1.0f;
            if (hpTrigger)
                raw += (PsycasterTuning.SkipshieldHpTriggerHp - snap.casterHpFraction) * 6f;
            if (fireTrigger)
                raw += rangedInLOS * 0.7f;

            ScoredAction action = new ScoredAction();
            action.abilityDefName = AbilityDefName;
            action.targetType = ScoredActionTargetType.Cell;
            action.targetCell = brain.Caster.Position;
            action.castWarmupTicks = PsycasterTuning.CastWarmupMedium;
            action.score = raw;
            action.debugReason = "Skipshield hp=" + snap.casterHpFraction.ToString("F2")
                                     + " rangedLOS=" + rangedInLOS;
            return action;
        }
    }
}
