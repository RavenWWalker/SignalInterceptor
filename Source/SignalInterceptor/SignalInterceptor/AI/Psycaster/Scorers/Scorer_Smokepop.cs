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
            if (brain == null || snap == null || snap.caster == null)
                return ScoredAction.None;

            int rangedPressure = 0;
            float meaningfulIncomingDps = 0f;
            float nearestShooterDist = 999f;

            if (snap.enemies != null)
            {
                for (int i = 0; i < snap.enemies.Count; i++)
                {
                    EnemyAssessment e = snap.enemies[i];

                    if (e == null || e.pawn == null)
                        continue;

                    if (e.pawn.Destroyed || e.pawn.Dead || e.pawn.Downed || !e.pawn.Spawned)
                        continue;

                    if (!e.IsRanged)
                        continue;

                    if (!e.hasLineOfSight)
                        continue;

                    if (!e.canShootNow)
                        continue;

                    if (e.estimatedDps <= 0.1f)
                        continue;

                    float practicalRange = UnityEngine.Mathf.Max(18f, e.weaponRange + 2f);

                    if (e.distanceToCaster > practicalRange)
                        continue;

                    rangedPressure++;
                    meaningfulIncomingDps += e.estimatedDps;

                    if (e.distanceToCaster < nearestShooterDist)
                        nearestShooterDist = e.distanceToCaster;
                }
            }

            bool invisible =
                snap.casterIsInvisible ||
                HasInvisibilityHediff(snap.caster);

            bool fullHp = snap.casterHpFraction >= 0.95f;
            bool healthy = snap.casterHpFraction >= 0.80f;
            bool lowHp = snap.casterHpFraction <= 0.65f;
            bool damaged = snap.casterHpFraction <= 0.80f;
            bool critical = snap.casterHpFraction <= 0.45f;

            bool survive = brain.CurrentStance == PsycasterStance.Survive;

            bool moderateRangedPressure =
                rangedPressure >= 2 ||
                meaningfulIncomingDps >= 24f;

            bool severeRangedPressure =
                rangedPressure >= 3 ||
                meaningfulIncomingDps >= 55f;

            /*
             * Нет реального стрелкового давления — smoke не нужен.
             */
            if (rangedPressure <= 0 && meaningfulIncomingDps < 10f)
                return ScoredAction.None;

            /*
             * Полный HP: smoke только под тяжёлым огнём.
             */
            if (fullHp && !severeRangedPressure)
                return ScoredAction.None;

            /*
             * Invisibility уже активна.
             * Smoke поверх invisibility — только если это реально критическая паника.
             */
            if (invisible && !(critical && severeRangedPressure))
                return ScoredAction.None;

            bool hpTrigger =
                lowHp && rangedPressure >= 1;

            bool pressureTrigger =
                damaged && moderateRangedPressure;

            bool panicTrigger =
                severeRangedPressure;

            bool surviveTrigger =
                survive && rangedPressure >= 1 && snap.casterHpFraction <= 0.85f;

            if (!hpTrigger && !pressureTrigger && !panicTrigger && !surviveTrigger)
                return ScoredAction.None;

            float raw = 3f;

            raw += rangedPressure * 1.5f;
            raw += meaningfulIncomingDps / 9f;

            if (hpTrigger)
                raw += (0.70f - snap.casterHpFraction) * 16f;

            if (pressureTrigger)
                raw += 4f;

            if (panicTrigger)
                raw += 8f;

            if (surviveTrigger)
                raw += 6f;

            /*
             * На высоком HP smoke должен проигрывать контролю/агрессии.
             */
            if (healthy && !severeRangedPressure)
                raw *= 0.45f;

            if (snap.casterHasBulletShield)
                raw *= 0.35f;

            ScoredAction action = new ScoredAction();
            action.abilityDefName = AbilityDefName;
            action.targetType = ScoredActionTargetType.Self;
            action.castWarmupTicks = PsycasterTuning.CastWarmupShort;
            action.score = raw;
            action.debugReason =
                "Smokepop hp=" + snap.casterHpFraction.ToString("F2") +
                " rangedPressure=" + rangedPressure +
                " meaningfulDps=" + meaningfulIncomingDps.ToString("F1") +
                " nearestShooter=" + nearestShooterDist.ToString("F1") +
                " invisible=" + invisible +
                " BulletShield=" + snap.casterHasBulletShield;

            return action;
        }
        private bool HasInvisibilityHediff(Pawn pawn)
        {
            if (pawn == null || pawn.health == null || pawn.health.hediffSet == null)
                return false;

            for (int i = 0; i < pawn.health.hediffSet.hediffs.Count; i++)
            {
                Hediff h = pawn.health.hediffSet.hediffs[i];

                if (h == null || h.def == null || string.IsNullOrEmpty(h.def.defName))
                    continue;

                string defName = h.def.defName;

                if (defName == "PsychicInvisibility")
                    return true;

                if (defName.IndexOf("Invisibility", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;

                if (defName.IndexOf("Invisible", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }

            return false;
        }
    }
}
