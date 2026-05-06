using Verse;

namespace SignalInterceptor.AI.Psycaster
{
    /// <summary>
    /// Smokepop:
    /// запасной defensive tool, если нет Skipshield/Invisibility или они на cooldown.
    /// Кастуется на себя против ranged pressure.
    /// </summary>
    public class Scorer_Smokepop : AbilityScorerBase
    {
        public override string AbilityDefName { get { return "Smokepop"; } }

        protected override float StanceMultiplier(PsycasterStance stance)
        {
            switch (stance)
            {
                case PsycasterStance.Opening: return PsycasterTuning.W_Opening_Smokepop;
                case PsycasterStance.Kite: return PsycasterTuning.W_Kite_Smokepop;
                case PsycasterStance.Disengage: return PsycasterTuning.W_Disengage_Smokepop;
                case PsycasterStance.CrowdControl: return PsycasterTuning.W_CrowdControl_Smokepop;
                case PsycasterStance.Hunt: return PsycasterTuning.W_Hunt_Smokepop;
                case PsycasterStance.Engulfed: return PsycasterTuning.W_Engulfed_Smokepop;
                case PsycasterStance.Survive: return PsycasterTuning.W_Survive_Smokepop;
                default: return 0f;
            }
        }

        protected override float MinPsyfocusFraction { get { return 0.10f; } }

        protected override ScoredAction ScoreInternal(PsycasterBrain brain, BattlefieldSnapshot snap)
        {
            if (snap == null || snap.caster == null)
                return ScoredAction.None;

            int rangedLos = 0;

            if (snap.enemies != null)
            {
                for (int i = 0; i < snap.enemies.Count; i++)
                {
                    EnemyAssessment e = snap.enemies[i];

                    if (e == null || e.pawn == null)
                        continue;

                    if (e.IsRanged && e.hasLineOfSight && e.canShootNow)
                        rangedLos++;
                }
            }

            bool hpTrigger = snap.casterHpFraction <= 0.60f && rangedLos >= 1;
            bool fireTrigger = rangedLos >= 2;
            bool severeFire = rangedLos >= 3 || snap.totalIncomingDps >= 24f;
            bool survive = brain.CurrentStance == PsycasterStance.Survive;

            if (!hpTrigger && !fireTrigger && !severeFire && !survive)
                return ScoredAction.None;

            float raw = 4f;

            raw += rangedLos * 1.8f;
            raw += snap.totalIncomingDps / 7f;

            if (hpTrigger)
                raw += (0.65f - snap.casterHpFraction) * 12f;

            if (severeFire)
                raw += 6f;

            if (survive)
                raw += 8f;

            // Если skipshield уже есть, smoke менее нужен.
            if (snap.casterHasSkipshield)
                raw *= 0.45f;

            ScoredAction action = new ScoredAction();
            action.abilityDefName = AbilityDefName;
            action.targetType = ScoredActionTargetType.Self;
            action.castWarmupTicks = PsycasterTuning.CastWarmupShort;
            action.score = raw;
            action.debugReason =
                "Smokepop hp=" + snap.casterHpFraction.ToString("F2") +
                " rangedLOS=" + rangedLos +
                " incomingDps=" + snap.totalIncomingDps.ToString("F1") +
                " skipshield=" + snap.casterHasSkipshield;

            return action;
        }
    }
}
