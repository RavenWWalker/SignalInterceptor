using RimWorld;
using System.Collections.Generic;
using System.Linq;
using Verse;
using Verse.AI;

namespace SignalInterceptor
{
    public partial class SignalInterceptorGameComponent
    {
        private void TickDoppelgangerFightRetargeting()
        {
            // Перенацеливание двойников — каждые 120 тиков
            if (doppelgangerFightActive && Find.TickManager.TicksGame % 120 == 0)
            {
                Map playerMap = Find.Maps.FirstOrDefault(m => m.IsPlayerHome);
                if (playerMap != null)
                {
                    HediffDef markDef = DefDatabase<HediffDef>.GetNamedSilentFail("SI_DoppelgangerMark");
                    if (markDef != null)
                    {
                        List<Pawn> allDopps = playerMap.mapPawns.AllPawnsSpawned
                            .Where(p => !p.Dead
                                     && p.health?.hediffSet != null
                                     && p.health.hediffSet.HasHediff(markDef)
                                     && (p.Faction == Faction.OfPlayer
                                         || p.IsPrisonerOfColony
                                         || p.HostFaction == Faction.OfPlayer))
                            .ToList();

                        var group = allDopps
                            .GroupBy(p => p.Name?.ToStringShort ?? "")
                            .FirstOrDefault(g => g.Count() >= 2);

                        if (group == null)
                        {
                            // Не завершать бой раньше чем через час после начала
                            if (Find.TickManager.TicksGame - doppelgangerFightStartTick < 2500)
                            {
                                foreach (Pawn p in allDopps)
                                {
                                    if (p.mindState?.mentalStateHandler?.CurState != null)
                                    {
                                        p.mindState.mentalStateHandler.CurState.RecoverFromState();
                                    }
                                }

                                return;
                            }

                            // Час прошёл — проверяем повторно с расширенным поиском
                            List<Pawn> allAlive = playerMap.mapPawns.AllPawnsSpawned
                                .Where(p => !p.Dead
                                         && p.health?.hediffSet != null
                                         && p.health.hediffSet.HasHediff(markDef)
                                         && (p.Faction == Faction.OfPlayer
                                             || p.IsPrisonerOfColony
                                             || p.HostFaction == Faction.OfPlayer))
                                .ToList();

                            var aliveGroup = allAlive
                                .GroupBy(p => p.Name?.ToStringShort ?? "")
                                .FirstOrDefault(g => g.Count() >= 2);

                            if (aliveGroup != null)
                            {
                                // Есть живые двойники — перезапускаем бой
                                List<Pawn> targets = aliveGroup.ToList();

                                foreach (Pawn attacker in targets.Where(p => !p.Downed && !p.IsPrisonerOfColony))
                                {
                                    Pawn victim = targets
                                        .Where(p => p != attacker && !p.Dead)
                                        .OrderBy(p => p.Position.DistanceTo(attacker.Position))
                                        .FirstOrDefault();

                                    if (victim != null)
                                    {
                                        if (attacker.CurJob != null && attacker.jobs != null)
                                        {
                                            attacker.jobs.EndCurrentJob(JobCondition.InterruptForced);
                                        }

                                        if (attacker.InBed())
                                        {
                                            Building_Bed bed = attacker.CurrentBed();
                                            if (bed != null)
                                            {
                                                RestUtility.KickOutOfBed(attacker, bed);
                                            }
                                        }

                                        int beforeCount = Find.LetterStack.LettersListForReading.Count;

                                        attacker.mindState?.mentalStateHandler?.TryStartMentalState(
                                            DefDatabase<MentalStateDef>.GetNamed("MurderousRage"),
                                            forced: true
                                        );

                                        while (Find.LetterStack.LettersListForReading.Count > beforeCount)
                                        {
                                            Find.LetterStack.RemoveLetter(Find.LetterStack.LettersListForReading.Last());
                                        }

                                        if (attacker.mindState?.mentalStateHandler?.CurState is MentalState_MurderousRage rage)
                                        {
                                            rage.target = victim;
                                        }
                                    }
                                }
                            }
                            else
                            {
                                // Реально остался один — завершаем и даём трейт
                                foreach (Pawn p in allAlive)
                                {
                                    if (p.mindState?.mentalStateHandler?.CurState != null)
                                    {
                                        p.mindState.mentalStateHandler.CurState.RecoverFromState();
                                    }
                                }

                                Pawn survivor = allAlive.FirstOrDefault(p => !p.Downed);

                                TraitDef originalTrait = DefDatabase<TraitDef>.GetNamedSilentFail("SI_TheOriginal");
                                if (originalTrait != null
                                    && survivor != null
                                    && survivor.story?.traits != null
                                    && !survivor.story.traits.HasTrait(originalTrait))
                                {
                                    survivor.story.traits.GainTrait(new Trait(originalTrait, 0, true));

                                    Find.LetterStack.ReceiveLetter(
                                        "SI_TheOriginal_Title".Translate(),
                                        "SI_TheOriginal_Text".Translate(survivor.LabelShort),
                                        LetterDefOf.PositiveEvent,
                                        new LookTargets(survivor)
                                    );

                                    Log.Message("[Signal Interceptor] Trait 'The Original' given to: " + survivor.LabelShort);
                                }

                                doppelgangerFightActive = false;
                                doppelgangerFightStartTick = -1;

                                Log.Message("[Signal Interceptor] Doppelganger fight ended.");
                            }
                        }
                        else
                        {
                            // Группа найдена — перенацеливаем активных двойников
                            List<Pawn> dopps = group.ToList();
                            List<Pawn> standing = dopps.Where(p => !p.Downed).ToList();
                            List<Pawn> downed = dopps.Where(p => p.Downed).ToList();

                            foreach (Pawn attacker in standing)
                            {
                                var curState = attacker.mindState?.mentalStateHandler?.CurState;
                                bool needsRetarget = false;

                                if (curState is MentalState_MurderousRage rage)
                                {
                                    Pawn curTarget = rage.target;

                                    if (curTarget == null
                                        || curTarget.Dead
                                        || !dopps.Contains(curTarget)
                                        || (curTarget.Downed && standing.Count > 1))
                                    {
                                        needsRetarget = true;
                                    }
                                }
                                else
                                {
                                    needsRetarget = true;
                                }

                                if (needsRetarget)
                                {
                                    Pawn newTarget = standing
                                        .Where(p => p != attacker && !p.IsPrisonerOfColony)
                                        .OrderBy(p => p.Position.DistanceTo(attacker.Position))
                                        .FirstOrDefault();

                                    if (newTarget == null)
                                    {
                                        newTarget = downed
                                            .Where(p => !p.IsPrisonerOfColony && p.CarriedBy == null)
                                            .OrderBy(p => p.Position.DistanceTo(attacker.Position))
                                            .FirstOrDefault();
                                    }

                                    if (newTarget == null)
                                    {
                                        newTarget = dopps
                                            .Where(p => p != attacker && !p.Dead && p.CarriedBy == null)
                                            .OrderBy(p => p.Position.DistanceTo(attacker.Position))
                                            .FirstOrDefault();
                                    }

                                    if (newTarget != null)
                                    {
                                        if (attacker.CurJob != null && attacker.jobs != null)
                                        {
                                            attacker.jobs.EndCurrentJob(JobCondition.InterruptForced);
                                        }

                                        if (attacker.InBed())
                                        {
                                            Building_Bed bed = attacker.CurrentBed();
                                            if (bed != null)
                                            {
                                                RestUtility.KickOutOfBed(attacker, bed);
                                            }
                                        }

                                        int beforeCount = Find.LetterStack.LettersListForReading.Count;

                                        attacker.mindState?.mentalStateHandler?.TryStartMentalState(
                                            DefDatabase<MentalStateDef>.GetNamed("MurderousRage"),
                                            forced: true
                                        );

                                        while (Find.LetterStack.LettersListForReading.Count > beforeCount)
                                        {
                                            Find.LetterStack.RemoveLetter(Find.LetterStack.LettersListForReading.Last());
                                        }

                                        if (attacker.mindState?.mentalStateHandler?.CurState is MentalState_MurderousRage newRage)
                                        {
                                            newRage.target = newTarget;
                                        }
                                    }
                                    else
                                    {
                                        if (attacker.mindState?.mentalStateHandler?.CurState != null)
                                        {
                                            attacker.mindState.mentalStateHandler.CurState.RecoverFromState();
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        private void TickDoppelgangerHatred()
        {
            // Проставление ненависти между двойниками — раз в день
            if (Find.TickManager.TicksGame % 60000 == 0)
            {
                Map playerMap = Find.Maps.FirstOrDefault(m => m.IsPlayerHome);
                if (playerMap != null)
                {
                    HediffDef markDef = DefDatabase<HediffDef>.GetNamedSilentFail("SI_DoppelgangerMark");
                    ThoughtDef hatredDef = DefDatabase<ThoughtDef>.GetNamedSilentFail("SI_DoppelgangerHatred");

                    if (markDef != null && hatredDef != null)
                    {
                        List<Pawn> allMarked = playerMap.mapPawns.AllPawnsSpawned
                            .Where(p => p.Faction == Faction.OfPlayer
                                     && !p.Dead
                                     && p.health?.hediffSet != null
                                     && p.health.hediffSet.HasHediff(markDef))
                            .ToList();

                        Log.Message("[Signal Interceptor] Hatred check. Marked pawns: " + allMarked.Count);

                        var groups = allMarked.GroupBy(p => p.Name?.ToStringShort ?? "");

                        foreach (var group in groups)
                        {
                            List<Pawn> dopps = group.ToList();

                            if (dopps.Count < 2)
                                continue;

                            for (int a = 0; a < dopps.Count; a++)
                            {
                                for (int b = a + 1; b < dopps.Count; b++)
                                {
                                    bool hasMemoryA = dopps[a].needs?.mood?.thoughts?.memories?.Memories
                                        ?.Any(m => m.def == hatredDef && m.otherPawn == dopps[b]) ?? false;

                                    dopps[a].needs?.mood?.thoughts?.memories?.TryGainMemory(hatredDef, dopps[b]);
                                    dopps[b].needs?.mood?.thoughts?.memories?.TryGainMemory(hatredDef, dopps[a]);

                                    bool hasMemoryAfterA = dopps[a].needs?.mood?.thoughts?.memories?.Memories
                                        ?.Any(m => m.def == hatredDef && m.otherPawn == dopps[b]) ?? false;

                                    Log.Message("[Signal Interceptor] " + dopps[a].LabelShort + " -> " + dopps[b].LabelShort +
                                                " | Before: " + hasMemoryA + " | After: " + hasMemoryAfterA);
                                }
                            }
                        }
                    }
                }
            }
        }

        private void TickDoppelgangerFightIncident()
        {
            if (Find.TickManager.TicksGame % 60000 == 0)
            {
                Map playerMap = Find.Maps.FirstOrDefault(m => m.IsPlayerHome);
                if (playerMap != null)
                {
                    HediffDef markDef = DefDatabase<HediffDef>.GetNamedSilentFail("SI_DoppelgangerMark");
                    if (markDef != null)
                    {
                        int doppCount = playerMap.mapPawns.FreeColonistsSpawned
                            .Count(p => p.health?.hediffSet != null && p.health.hediffSet.HasHediff(markDef));

                        if (doppCount >= 2 && Rand.Chance(0.08f))
                        {
                            IncidentDef fightDef = DefDatabase<IncidentDef>.GetNamedSilentFail("SI_DoppelgangerFight");
                            if (fightDef != null)
                            {
                                IncidentParms parms = new IncidentParms();
                                parms.target = playerMap;
                                fightDef.Worker.TryExecute(parms);
                            }
                        }
                    }
                }
            }
        }
    }
}
