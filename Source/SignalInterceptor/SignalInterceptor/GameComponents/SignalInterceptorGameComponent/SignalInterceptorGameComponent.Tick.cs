using RimWorld;
using System.Collections.Generic;
using System.Linq;
using Verse;
using Verse.AI;

namespace SignalInterceptor
{
    public partial class SignalInterceptorGameComponent
    {
        public override void GameComponentTick()
        {
            if (Find.TickManager.TicksGame % 250 == 0)
            {
                CleanupDoppelgangerSettlements();
            }
            // VIP — каждый тик для мгновенного спавна + проверка победы/ухода с карты
            for (int i = trackedVIPSites.Count - 1; i >= 0; i--)
            {
                VIPSiteData data = trackedVIPSites[i];

                if (data == null)
                {
                    trackedVIPSites.RemoveAt(i);
                    continue;
                }

                if (!data.rewardGiven && data.expireTick > 0 && Find.TickManager.TicksGame >= data.expireTick)
                {
                    FailVIPQuest(data, "SI_VIP_FailedExpiredText");

                    if (data.site != null && data.site.Spawned)
                    {
                        Find.WorldObjects.Remove(data.site);
                    }

                    if (data.subtype == VIPSubtype.DoppelgangerVIP)
                    {
                        DeactivateDoppelgangerFaction(data.enemyFaction);
                        data.enemyFaction = null;
                    }

                    if (data.subtype == VIPSubtype.MechanitorSignalVIP)
                    {
                        DeactivateRogueMechanitorFaction(data.enemyFaction);
                        data.enemyFaction = null;
                    }

                    trackedVIPSites.RemoveAt(i);
                    continue;
                }

                if (data.site == null || !data.site.Spawned)
                {
                    if (!data.rewardGiven)
                    {
                        FailVIPQuest(data, "SI_VIP_FailedExpiredText");
                    }

                    if (data.subtype == VIPSubtype.DoppelgangerVIP)
                    {
                        DeactivateDoppelgangerFaction(data.enemyFaction);
                        data.enemyFaction = null;
                    }

                    if (data.subtype == VIPSubtype.MechanitorSignalVIP)
                    {
                        DeactivateRogueMechanitorFaction(data.enemyFaction);
                        data.enemyFaction = null;
                    }

                    trackedVIPSites.RemoveAt(i);
                    continue;
                }

                /*
                 * Если это сайт двойников, VIP уже был заспавнен,
                 * но карты больше нет — игрок покинул сайт.
                 * Значит временную фракцию нужно убрать из глобального списка.
                 */
                if (data.subtype == VIPSubtype.DoppelgangerVIP
                    && data.vipSpawned
                    && data.enemyFaction != null
                    && !data.site.HasMap)
                {
                    DeactivateDoppelgangerFaction(data.enemyFaction);
                    data.enemyFaction = null;

                    Log.Message("[Signal Interceptor] Doppelganger map left. Temporary faction deactivated.");
                }

                if (data.subtype == VIPSubtype.MechanitorSignalVIP
                    && data.vipSpawned
                    && data.enemyFaction != null
                    && data.site != null
                    && !data.site.HasMap
                    && !data.rewardGiven)
                {
                    FailVIPQuest(data, "SI_MechanitorSignal_FailedFledText");

                    DeactivateRogueMechanitorFaction(data.enemyFaction);
                    data.enemyFaction = null;

                    Log.Message("[Signal Interceptor] Mechanitor signal map left. Quest failed.");
                }

                if (!data.vipSpawned && data.site.HasMap)
                {
                    SpawnVIP(data);
                    data.vipSpawned = true;
                }

                if (data.subtype == VIPSubtype.MechanitorSignalVIP
                    && data.vipSpawned
                    && data.site != null
                    && data.site.HasMap
                    && data.enemyFaction != null
                    && Find.TickManager.TicksGame % 30 == 0)
                {
                    EnforceMechanitorSignalCombat(data);
                }

                // Проверяем победу — карта загружена, VIP был, врагов не осталось
                if (data.vipSpawned && !data.rewardGiven && data.site.HasMap)
                {
                    Map siteMap = data.site.Map;

                    bool enemiesAlive = siteMap.mapPawns.AllPawnsSpawned
                        .Any(p => p.Faction != null
                               && p.Faction.HostileTo(Faction.OfPlayer)
                               && !p.Dead
                               && !p.Downed);

                    if (!enemiesAlive)
                    {
                        data.rewardGiven = true;

                        if (data.subtype == VIPSubtype.DoppelgangerVIP)
                        {
                            GiveVIPVictoryReward();
                            DeactivateDoppelgangerFaction(data.enemyFaction);
                            data.enemyFaction = null;
                        }

                        if (data.subtype == VIPSubtype.MechanitorSignalVIP)
                        {
                            DeactivateRogueMechanitorFaction(data.enemyFaction);
                            data.enemyFaction = null;
                        }
                    }
                }
            }

            if (Find.TickManager.TicksGame % 60 != 0)
                return;

            // Обработка stash сайтов
            for (int i = trackedSites.Count - 1; i >= 0; i--)
            {
                StashSiteData data = trackedSites[i];

                if (data.site == null || !data.site.Spawned)
                {
                    trackedSites.RemoveAt(i);
                    continue;
                }

                if (!data.lootSpawned && data.site.HasMap)
                {
                    SpawnLoot(data.site.Map, data.threatPoints);
                    data.lootSpawned = true;
                }
            }

            // Обработка отложенных доставок рабов
            for (int i = pendingSlaveDeliveries.Count - 1; i >= 0; i--)
            {
                PendingSlaveDelivery delivery = pendingSlaveDeliveries[i];
                if (Find.TickManager.TicksGame >= delivery.deliveryTick)
                {
                    DeliverSlave(delivery);
                    pendingSlaveDeliveries.RemoveAt(i);
                }
            }

            // Обработка отложенных рейдов контрразведки
            for (int i = pendingRaids.Count - 1; i >= 0; i--)
            {
                PendingRaid raid = pendingRaids[i];
                if (Find.TickManager.TicksGame >= raid.fireTick)
                {
                    ExecuteRaid(raid);
                    pendingRaids.RemoveAt(i);
                }
            }

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
                                            DefDatabase<MentalStateDef>.GetNamed("MurderousRage"), forced: true);

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
                                if (originalTrait != null && survivor != null
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
                                    if (curTarget == null || curTarget.Dead
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
                                            DefDatabase<MentalStateDef>.GetNamed("MurderousRage"), forced: true);

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
