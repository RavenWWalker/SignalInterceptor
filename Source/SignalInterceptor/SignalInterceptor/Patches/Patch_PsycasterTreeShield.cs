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
            if (__instance == null || !IsPsycast(__instance))
                return true;

            Pawn caster = __instance.pawn;

            if (caster == null || caster.Destroyed || caster.Dead || caster.Map == null)
                return true;

            Pawn directPawn = target.Thing as Pawn;

            if (ShouldBlockPsycast(caster, directPawn))
            {
                NotifyBlocked(caster, directPawn);
                return false;
            }

            if (target.Cell.IsValid)
            {
                List<Pawn> protectedPawns = caster.Map.mapPawns.AllPawnsSpawned
                    .Where(p => p != null
                             && !p.Destroyed
                             && !p.Dead
                             && p.Spawned
                             && p.Map == caster.Map
                             && SignalInterceptorGameComponent.HasPsycasterTreeShield(p)
                             && p.Position.DistanceTo(target.Cell) <= 5.9f)
                    .ToList();

                for (int i = 0; i < protectedPawns.Count; i++)
                {
                    Pawn protectedPawn = protectedPawns[i];

                    if (ShouldBlockPsycast(caster, protectedPawn))
                    {
                        NotifyBlocked(caster, protectedPawn);
                        return false;
                    }
                }
            }

            return true;
        }

        private static bool IsPsycast(Ability ability)
        {
            if (ability == null || ability.def == null)
                return false;

            if (ability.def.category != null && ability.def.category.defName == "Psycast")
                return true;

            return false;
        }

        private static bool ShouldBlockPsycast(Pawn caster, Pawn target)
        {
            if (caster == null || target == null)
                return false;

            if (caster == target)
                return false;

            if (!SignalInterceptorGameComponent.HasPsycasterTreeShield(target))
                return false;

            if (caster.Faction == null || target.Faction == null)
                return caster.HostileTo(target);

            return caster.Faction.HostileTo(target.Faction);
        }

        private static void NotifyBlocked(Pawn caster, Pawn target)
        {
            if (caster != null && caster.Faction == Faction.OfPlayer)
            {
                Messages.Message(
                    "The psycast is dispersed by the anima tree resonance.",
                    target,
                    MessageTypeDefOf.RejectInput,
                    historical: false);
            }
        }
    }
}
