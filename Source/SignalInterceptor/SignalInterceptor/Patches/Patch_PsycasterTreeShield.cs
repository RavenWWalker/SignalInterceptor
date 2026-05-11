using HarmonyLib;
using RimWorld;
using System;
using System.Collections.Generic;
using System.Linq;
using Verse;

namespace SignalInterceptor
{
    [HarmonyPatch(typeof(Ability), nameof(Ability.Activate), new Type[] { typeof(LocalTargetInfo), typeof(LocalTargetInfo) })]
    public static class Patch_PsycasterTreeShield_AbilityActivate
    {
        public static bool Prefix(Ability __instance, LocalTargetInfo target, LocalTargetInfo dest)
        {
            if (!PsycasterTreeShieldUtility.ShouldBlockAbility(__instance, target))
                return true;

            PsycasterTreeShieldUtility.NotifyBlocked(__instance, target);
            return false;
        }
    }

    [HarmonyPatch(typeof(CompAbilityEffect), nameof(CompAbilityEffect.Apply))]
    public static class Patch_PsycasterTreeShield_CompAbilityEffectApply
    {
        public static bool Prefix(CompAbilityEffect __instance, LocalTargetInfo target, LocalTargetInfo dest)
        {
            Ability ability = __instance?.parent;

            if (!PsycasterTreeShieldUtility.ShouldBlockAbility(ability, target))
                return true;

            PsycasterTreeShieldUtility.NotifyBlocked(ability, target);
            return false;
        }
    }

    public static class PsycasterTreeShieldUtility
    {
        public static bool ShouldBlockAbility(Ability ability, LocalTargetInfo target)
        {
            if (ability == null || ability.def == null)
                return false;

            if (!IsPsycast(ability))
                return false;

            Pawn caster = ability.pawn;

            if (caster == null || caster.Destroyed || caster.Dead || caster.Map == null)
                return false;

            Pawn directTarget = target.Thing as Pawn;

            if (ShouldBlockPsycastAgainstPawn(caster, directTarget))
                return true;

            if (target.Cell.IsValid && caster.Map != null)
            {
                IReadOnlyList<Pawn> pawns = caster.Map.mapPawns.AllPawnsSpawned;

                for (int i = 0; i < pawns.Count; i++)
                {
                    Pawn pawn = pawns[i];

                    if (pawn == null || pawn.Destroyed || pawn.Dead || !pawn.Spawned || pawn.Map != caster.Map)
                        continue;

                    if (!SignalInterceptorGameComponent.HasPsycasterTreeShield(pawn))
                        continue;

                    if (pawn.Position.DistanceTo(target.Cell) > 5.9f)
                        continue;

                    if (ShouldBlockPsycastAgainstPawn(caster, pawn))
                        return true;
                }
            }

            return false;
        }

        private static bool IsPsycast(Ability ability)
        {
            if (ability == null || ability.def == null)
                return false;

            if (ability.def.category != null && ability.def.category.defName == "Psycast")
                return true;

            if (ability.def.defName == "Stun" ||
                ability.def.defName == "Skip" ||
                ability.def.defName == "Beckon" ||
                ability.def.defName == "Burden" ||
                ability.def.defName == "Berserk" ||
                ability.def.defName == "BerserkPulse" ||
                ability.def.defName == "BlindingPulse" ||
                ability.def.defName == "VertigoPulse" ||
                ability.def.defName == "ChaosSkip" ||
                ability.def.defName == "MassChaosSkip" ||
                ability.def.defName == "ManhunterPulse")
            {
                return true;
            }

            return false;
        }

        private static bool ShouldBlockPsycastAgainstPawn(Pawn caster, Pawn target)
        {
            if (caster == null || target == null)
                return false;

            if (caster == target)
                return false;

            if (!SignalInterceptorGameComponent.HasPsycasterTreeShield(target))
                return false;

            if (!IsHostileCaster(caster, target))
                return false;

            return true;
        }

        private static bool IsHostileCaster(Pawn caster, Pawn target)
        {
            if (caster == null || target == null)
                return false;

            if (caster.Faction != null && target.Faction != null)
                return caster.Faction.HostileTo(target.Faction);

            return caster.HostileTo(target);
        }

        public static void NotifyBlocked(Ability ability, LocalTargetInfo target)
        {
            Pawn caster = ability?.pawn;

            if (caster == null)
                return;

            Pawn directTarget = target.Thing as Pawn;

            if (caster.Faction == Faction.OfPlayer)
            {
                Messages.Message(
                    "SI_PsycasterTreeShieldBlocked".Translate(),
                    directTarget != null ? new LookTargets(directTarget) : null,
                    MessageTypeDefOf.RejectInput,
                    historical: false
                );
            }

            if (Prefs.DevMode)
            {
                Log.Message("[Signal Interceptor] Psycast blocked by anima shield. " +
                            "Caster=" + caster.LabelShort +
                            " | Ability=" + (ability?.def?.defName ?? "null") +
                            " | Target=" + (directTarget?.LabelShort ?? target.Cell.ToString()));
            }
        }
    }
}
