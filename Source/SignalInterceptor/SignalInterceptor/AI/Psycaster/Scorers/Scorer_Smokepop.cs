using Verse;

namespace SignalInterceptor.AI.Psycaster
{
    /// <summary>
    /// Smokepop:
    /// defensive tool против реального ranged pressure.
    ///
    /// Важно:
    /// больше не кастуется автоматически при полном HP только потому,
    /// что есть один стрелок с LOS.
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

            bool fullHp = snap.casterHpFraction >= 0.95f;
            bool healthy = snap.casterHpFraction >= 0.80f;
            bool lowHp = snap.casterHpFraction <= 0.65f;
            bool damaged = snap.casterHpFraction <= 0.80f;
            bool survive = brain.CurrentStance == PsycasterStance.Survive;

            bool moderateRangedPressure =
                rangedLos >= 2 ||
                snap.totalIncomingDps >= 24f;

            bool severeRangedPressure =
                rangedLos >= 3 ||
                snap.totalIncomingDps >= 55f;

            /*
             * Главный фикс:
             * На полном HP не жмём Smokepop против одного стрелка.
             * Пусть сначала работают Invisibility / BlindingPulse / BerserkPulse / Wallraise.
             */
            if (fullHp && !severeRangedPressure)
                return ScoredAction.None;

            /*
             * Если уже невидим, smoke обычно лишний.
             * Оставляем smoke только при действительно тяжёлом огне.
             */
            if (snap.casterIsInvisible && !severeRangedPressure && !lowHp)
                return ScoredAction.None;

            bool hpTrigger =
                lowHp && rangedLos >= 1;

            bool pressureTrigger =
                damaged && moderateRangedPressure;

            bool panicTrigger =
                severeRangedPressure;

            bool surviveTrigger =
                survive && rangedLos >= 1 && snap.casterHpFraction <= 0.85f;

            if (!hpTrigger && !pressureTrigger && !panicTrigger && !surviveTrigger)
                return ScoredAction.None;

            float raw = 3f;

            raw += rangedLos * 1.5f;
            raw += snap.totalIncomingDps / 9f;

            if (hpTrigger)
                raw += (0.70f - snap.casterHpFraction) * 16f;

            if (pressureTrigger)
                raw += 4f;

            if (panicTrigger)
                raw += 8f;

            if (surviveTrigger)
                raw += 6f;

            /*
             * На высоком HP smoke должен проигрывать активному контролю,
             * если давление не экстремальное.
             */
            if (healthy && !severeRangedPressure)
                raw *= 0.55f;

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
                " invisible=" + snap.casterIsInvisible +
                " skipshield=" + snap.casterHasSkipshield;

            return action;
        }
    }
}
