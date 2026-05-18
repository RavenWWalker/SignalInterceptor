using RimWorld;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;
using Verse.AI;

namespace SignalInterceptor.AI.Psycaster
{
    public partial class PsycasterBrain
    {

        // ============================================================
        // Soft-cooldown
        // ============================================================

        public bool IsOnSoftCooldown(string abilityDefName)
        {
            if (string.IsNullOrEmpty(abilityDefName)) return false;
            int until;
            if (!softCooldowns.TryGetValue(abilityDefName, out until)) return false;
            return Find.TickManager.TicksGame < until;
        }

        public void MarkPawnRecentlyMoved(Pawn p, int holdTicks)
        {
            if (p == null) return;
            recentlyMovedPawns[p.thingIDNumber] = Find.TickManager.TicksGame + holdTicks;
        }

        public bool WasPawnRecentlyMoved(Pawn p)
        {
            if (p == null) return false;
            int until;
            if (!recentlyMovedPawns.TryGetValue(p.thingIDNumber, out until)) return false;
            return Find.TickManager.TicksGame < until;
        }

        public void ApplySoftCooldown(string abilityDefName)
        {
            if (string.IsNullOrEmpty(abilityDefName)) return;

            int min = 0;
            int max = 0;
            switch (abilityDefName)
            {
                case "Stun":
                    min = PsycasterTuning.StunSoftCooldownMin;
                    max = PsycasterTuning.StunSoftCooldownMax;
                    break;
                case "Skip":
                    min = PsycasterTuning.SkipSoftCooldownMin;
                    max = PsycasterTuning.SkipSoftCooldownMax;
                    break;
                case "ChaosSkip":
                    min = PsycasterTuning.ChaosSkipSoftCooldownMin;
                    max = PsycasterTuning.ChaosSkipSoftCooldownMax;
                    break;
                case "MassChaosSkip":
                    min = PsycasterTuning.MassChaosSkipSoftCooldownMin;
                    max = PsycasterTuning.MassChaosSkipSoftCooldownMax;
                    break;
                case "Beckon":
                    min = PsycasterTuning.BeckonSoftCooldownMin;
                    max = PsycasterTuning.BeckonSoftCooldownMax;
                    break;
                case "BlindingPulse":
                    min = PsycasterTuning.BlindingPulseSoftCooldownMin;
                    max = PsycasterTuning.BlindingPulseSoftCooldownMax;
                    break;
                case "VertigoPulse":
                    min = PsycasterTuning.VertigoPulseSoftCooldownMin;
                    max = PsycasterTuning.VertigoPulseSoftCooldownMax;
                    break;
                case "BerserkPulse":
                    min = PsycasterTuning.BerserkPulseSoftCooldownMin;
                    max = PsycasterTuning.BerserkPulseSoftCooldownMax;
                    break;
                case "Invisibility":
                    min = PsycasterTuning.InvisibilitySoftCooldownMin;
                    max = PsycasterTuning.InvisibilitySoftCooldownMax;
                    break;
                case "Smokepop":
                    min = PsycasterTuning.SmokepopSoftCooldownMin;
                    max = PsycasterTuning.SmokepopSoftCooldownMax;
                    break;
                case "Wallraise":
                    min = PsycasterTuning.WallraiseSoftCooldownMin;
                    max = PsycasterTuning.WallraiseSoftCooldownMax;
                    break;
                case "BulletShield":
                    min = PsycasterTuning.BulletShieldSoftCooldownMin;
                    max = PsycasterTuning.BulletShieldSoftCooldownMax;
                    break;
                case "ManhunterPulse":
                    min = PsycasterTuning.ManhunterPulseSoftCooldownMin;
                    max = PsycasterTuning.ManhunterPulseSoftCooldownMax;
                    break;
                case "Focus":
                    min = PsycasterTuning.FocusSoftCooldownMin;
                    max = PsycasterTuning.FocusSoftCooldownMax;
                    break;
                default:
                    return; // Способность без soft-cooldown.
            }

            int until = Find.TickManager.TicksGame + Rand.RangeInclusive(min, max);
            softCooldowns[abilityDefName] = until;
        }

        // ============================================================
        // Прокси к хелперам SignalInterceptorGameComponent.
        // Скореры дёргают эти методы, не публичные у GameComp.
        // ============================================================

        public AbilityDef GetAbilityDef(string defName)
        {
            return gc.FindAbilityDefByPossibleName_Public(defName);
        }

        public object GetPawnAbilityObject(AbilityDef def)
        {
            return gc.GetPawnAbility_Public(caster, def);
        }

        public bool IsAbilityOnCooldown(object ability)
        {
            return gc.IsAbilityOnCooldown_Public(ability);
        }

        public bool IsRangedCombatPawn(Pawn p)
        {
            return gc.IsRangedCombatPawn_Public(p);
        }

        private int GetCasterPsylinkLevelSafe()
        {
            if (caster == null || caster.health == null || caster.health.hediffSet == null)
                return 0;

            HediffDef psylinkDef = DefDatabase<HediffDef>.GetNamedSilentFail("PsychicAmplifier");

            if (psylinkDef == null)
                return 0;

            Hediff hediff = caster.health.hediffSet.GetFirstHediffOfDef(psylinkDef);

            if (hediff == null)
                return 0;

            int level = Mathf.RoundToInt(hediff.Severity);

            if (level < 0)
                level = 0;

            if (level > 6)
                level = 6;

            return level;
        }

        private float GetCurrentPsyfocusFraction()
        {
            if (caster == null || caster.psychicEntropy == null)
                return 0f;

            return caster.psychicEntropy.CurrentPsyfocus;
        }

        private bool HasEnoughPsyfocusForAbility(string abilityDefName)
        {
            float current = GetCurrentPsyfocusFraction();
            float required = GetMinimumPsyfocusForAbility(abilityDefName);

            return current >= required;
        }

        private float GetMinimumPsyfocusForAbility(string abilityDefName)
        {
            if (string.IsNullOrEmpty(abilityDefName))
                return 0.10f;

            switch (abilityDefName)
            {
                case "Focus":
                    return 0.05f;

                case "Smokepop":
                    return 0.10f;

                case "Waterskip":
                    return 0.10f;

                case "Stun":
                    return 0.12f;

                case "BlindingPulse":
                    return 0.18f;

                case "VertigoPulse":
                    return 0.18f;

                case "Invisibility":
                    return 0.20f;

                case "Skip":
                    return 0.20f;

                case "ChaosSkip":
                    return 0.20f;

                case "Beckon":
                    return 0.20f;

                case "Wallraise":
                    return 0.20f;

                case "Berserk":
                    return 0.24f;

                case "BerserkPulse":
                    return 0.28f;

                case "MassChaosSkip":
                    return 0.28f;

                case "ManhunterPulse":
                    return 0.30f;

                case "BulletShield":
                    return 0.20f;

                default:
                    return 0.15f;
            }
        }

        private HediffComp_PsycasterRestoringMechanisms GetRestoringMechanisms()
        {
            return PsycasterRecoveryUtility.GetRestoringComp(caster);
        }

        private bool CanRegenerateNow()
        {
            HediffComp_PsycasterRestoringMechanisms restore = GetRestoringMechanisms();

            return restore != null && restore.CanRegenerateNow;
        }

    }
}
