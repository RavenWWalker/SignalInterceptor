using RimWorld;
using RimWorld.Planet;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace SignalInterceptor
{
    public class SignalInterceptorGameComponent : GameComponent
    {
        private List<VIPSiteData> trackedVIPSites = new List<VIPSiteData>();
        private List<StashSiteData> trackedSites = new List<StashSiteData>();
        private List<PendingSlaveDelivery> pendingSlaveDeliveries = new List<PendingSlaveDelivery>();
        private List<PendingRaid> pendingRaids = new List<PendingRaid>();

        public SignalInterceptorGameComponent(Game game)
        {
            Log.Message("[Signal Interceptor] GameComponent initialized!");
        }

        public void TrackSite(Site site, float threatPoints, Faction faction = null, int tier = 0)
        {
            trackedSites.Add(new StashSiteData
            {
                site = site,
                threatPoints = threatPoints,
                lootSpawned = false,
                faction = faction,
                tier = (tier > 0) ? tier : GetTier(threatPoints)
            });
            Log.Message("[Signal Interceptor] Now tracking site. Threat: " + threatPoints +
                        " | Faction: " + (faction?.Name ?? "null") +
                        " | Tier: " + tier +
                        " | Total tracked: " + trackedSites.Count);
        }

        public List<StashSiteData> GetActiveStashes()
        {
            return trackedSites
                .Where(s => s.site != null && s.site.Spawned)
                .ToList();
        }

        public void RemoveStash(StashSiteData stash)
        {
            trackedSites.Remove(stash);
        }

        public void TrackVIPSite(Site site, float threatPoints, Faction faction, VIPSubtype subtype)
        {
            trackedVIPSites.Add(new VIPSiteData
            {
                site = site,
                threatPoints = threatPoints,
                faction = faction,
                enemyFaction = null,
                subtype = subtype,
                vipSpawned = false,
                rewardGiven = false,
                expireTick = Find.TickManager.TicksGame + 60000 * Rand.RangeInclusive(1, 2)
            });

            Log.Message("[Signal Interceptor] Tracking VIP site. " +
                        "Subtype: " + subtype +
                        " | Context faction: " + (faction?.Name ?? "null") +
                        " | Site faction: " + (site?.Faction?.Name ?? "null") +
                        " | Enemy faction: null" +
                        " | Threat: " + threatPoints);
        }

        public void ScheduleSlaveDelivery(Map map, int delayTicks)
        {
            pendingSlaveDeliveries.Add(new PendingSlaveDelivery
            {
                mapId = map.uniqueID,
                deliveryTick = Find.TickManager.TicksGame + delayTicks
            });
        }

        public void ScheduleCounterIntelRaid(Map map, Faction faction, float points, int delayTicks)
        {
            pendingRaids.Add(new PendingRaid
            {
                mapId = map.uniqueID,
                faction = faction,
                points = points,
                fireTick = Find.TickManager.TicksGame + delayTicks
            });
            Log.Message("[Signal Interceptor] Counter-intel raid scheduled. Faction: " + faction.Name +
                        " | Points: " + points + " | Fire tick: " + (Find.TickManager.TicksGame + delayTicks));
        }

        public bool doppelgangerFightActive = false;
        public int doppelgangerFightStartTick = -1;

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

        public void GiveVIPVictoryReward()
        {
            Faction rewardFaction = Find.FactionManager.AllFactions
                .Where(f => !f.IsPlayer && !f.defeated && !f.Hidden && !f.HostileTo(Faction.OfPlayer))
                .RandomElementWithFallback(null);

            if (rewardFaction != null)
            {
                int goodwillBonus = Rand.RangeInclusive(10, 25);
                int before = rewardFaction.GoodwillWith(Faction.OfPlayer);
                rewardFaction.TryAffectGoodwillWith(Faction.OfPlayer, goodwillBonus,
                    canSendMessage: false, canSendHostilityLetter: false);
                int after = rewardFaction.GoodwillWith(Faction.OfPlayer);
                int actualDelta = after - before;

                Find.LetterStack.ReceiveLetter(
                    "SI_VIP_SuccessTitle".Translate(),
                    "SI_VIP_SuccessText".Translate(rewardFaction.Name, actualDelta.ToString()),
                    LetterDefOf.PositiveEvent);
            }
        }

        private void SpawnVIP(VIPSiteData data)
        {
            Map map = data.site.Map;

            switch (data.subtype)
            {
                case VIPSubtype.ShuttleVIP:
                    SpawnShuttleVIP(map, data);
                    break;
                case VIPSubtype.PsycasterVIP:
                    SpawnShuttleVIP(map, data); // TODO: подтип 2
                    break;
                case VIPSubtype.MechanitorSignalVIP:
                    SpawnMechanitorSignalVIP(map, data);
                    break;
                case VIPSubtype.PilgrimVIP:
                    SpawnShuttleVIP(map, data); // TODO: подтип 4
                    break;
                case VIPSubtype.DoppelgangerVIP:
                    SpawnDoppelgangerVIP(map, data);
                    break;
            }

            Log.Message("[Signal Interceptor] VIP spawned. Subtype: " + data.subtype);
        }

        private int GetVIPTier(float points)
        {
            if (points >= 4500f) return 9;
            if (points >= 3600f) return 8;
            if (points >= 2800f) return 7;
            if (points >= 2200f) return 6;
            if (points >= 1700f) return 5;
            if (points >= 1200f) return 4;
            if (points >= 800f) return 3;
            if (points >= 450f) return 2;
            return 1;
        }

        private void SpawnMechanitorSignalVIP(Map map, VIPSiteData data)
        {
            if (!ModsConfig.BiotechActive)
            {
                Log.Warning("[Signal Interceptor] Tried to spawn MechanitorSignalVIP without Biotech.");
                return;
            }

            Faction signalFaction = CreateRogueMechanitorFactionForMap();

            if (signalFaction == null)
            {
                Log.Error("[Signal Interceptor] Failed to create rogue mechanitor faction.");
                data.rewardGiven = true;
                data.vipSpawned = true;
                data.enemyFaction = null;
                return;
            }

            data.enemyFaction = signalFaction;

            if (data.site != null)
            {
                data.site.SetFaction(signalFaction);
                data.site.factionMustRemainHostile = false;
            }

            IntVec3 center = FindSignalCampCenter(map);
            data.signalCampCenter = center;

            Pawn mechanitor = SpawnRogueMechanitor(map, signalFaction, data.threatPoints, center);

            if (mechanitor == null)
            {
                Log.Error("[Signal Interceptor] Failed to spawn rogue mechanitor.");
                DeactivateRogueMechanitorFaction(signalFaction);
                data.enemyFaction = null;
                return;
            }

            List<Pawn> mechs = SpawnMechanitorMechanoids(map, signalFaction, data.threatPoints, center, mechanitor);

            SpawnCampProps(map, center);

            // ВАЖНО:
            // Механоидов больше НЕ помещаем в LordJob_AssaultColony.
            // Иначе ванильный Lord всё равно может объявить отступление после потерь.
            StartMechanitorAssaultLord(map, signalFaction, mechs);

            // Механитор отдельно держится рядом с лагерем/мехами.
            StartMechanitorGuardLord(map, signalFaction, center, mechanitor);

            // Первый пинок поведения сразу после генерации.
            EnforceMechanitorSignalCombat(data);

            Find.LetterStack.ReceiveLetter(
                "SI_MechanitorSignal_Title".Translate(),
                "SI_MechanitorSignal_Text".Translate(mechanitor.LabelShort),
                LetterDefOf.ThreatBig,
                new LookTargets(mechanitor)
            );

            Log.Message("[Signal Interceptor] Mechanitor signal VIP spawned. " +
                        "Mechanitor: " + mechanitor.LabelShort +
                        " | faction=" + signalFaction.Name +
                        " | factionDef=" + signalFaction.def.defName +
                        " | mechs=" + mechs.Count +
                        " | threat=" + data.threatPoints +
                        " | center=" + center);
        }

        private IntVec3 FindSignalCampCenter(Map map)
        {
            IntVec3 result;

            if (CellFinder.TryFindRandomCellNear(
                map.Center,
                map,
                20,
                c => c.Standable(map) && !c.Roofed(map) && c.GetFirstPawn(map) == null,
                out result))
            {
                return result;
            }

            return map.Center;
        }

        private void StartMechanitorMechanoidHunt(Map map, Faction faction, List<Pawn> mechs)
        {
            if (map == null || faction == null || mechs.NullOrEmpty())
            {
                return;
            }

            List<Pawn> validMechs = mechs
                .Where(p => p != null && !p.Dead && p.Spawned && p.Map == map)
                .ToList();

            if (validMechs.NullOrEmpty())
            {
                return;
            }

            foreach (Pawn mech in validMechs)
            {
                try
                {
                    // Самое важное:
                    // убираем меха из любого Lord, чтобы ванильный raid-lord
                    // больше не мог перевести его в Flee/ExitMap.
                    Lord oldLord = mech.GetLord();
                    if (oldLord != null)
                    {
                        oldLord.RemovePawn(mech);
                    }

                    if (mech.mindState == null)
                    {
                        mech.mindState = new Pawn_MindState(mech);
                    }

                    // Duty можно оставить AssaultColony, но без Lord'а она не должна
                    // запускать ванильную логику отступления.
                    mech.mindState.duty = new PawnDuty(DutyDefOf.AssaultColony);

                    if (mech.jobs != null && mech.CurJob != null)
                    {
                        mech.jobs.EndCurrentJob(JobCondition.InterruptForced);
                    }

                    TryForceAttackNearestPlayerPawn(mech, map);
                }
                catch (Exception ex)
                {
                    Log.Warning("[Signal Interceptor] Failed to start mechanitor mechanoid hunt for " +
                                mech.LabelShort + ": " + ex);
                }
            }

            Log.Message("[Signal Interceptor] Rogue mechanitor mechanoid hunt started. Mechs: " + validMechs.Count + ". No vanilla assault lord used.");
        }

        private void StartMechanitorGuardLord(Map map, Faction faction, IntVec3 center, Pawn mechanitor)
        {
            if (map == null || faction == null || mechanitor == null || mechanitor.Dead || !mechanitor.Spawned)
            {
                return;
            }

            try
            {
                Lord oldLord = mechanitor.GetLord();
                if (oldLord != null)
                {
                    oldLord.RemovePawn(mechanitor);
                }

                LordJob_DefendPoint lordJob = new LordJob_DefendPoint(center);
                Lord lord = LordMaker.MakeNewLord(faction, lordJob, map);
                lord.AddPawn(mechanitor);

                if (mechanitor.mindState == null)
                {
                    mechanitor.mindState = new Pawn_MindState(mechanitor);
                }

                mechanitor.mindState.duty = new PawnDuty(DutyDefOf.Defend, center);

                if (mechanitor.jobs != null && mechanitor.CurJob != null)
                {
                    mechanitor.jobs.EndCurrentJob(JobCondition.InterruptForced);
                }

                Log.Message("[Signal Interceptor] Rogue mechanitor guard lord started at " + center + ".");
            }
            catch (Exception ex)
            {
                Log.Warning("[Signal Interceptor] Failed to start rogue mechanitor guard lord: " + ex);
            }
        }

        private FactionDef GetRogueMechanitorFactionDef()
        {
            FactionDef def = DefDatabase<FactionDef>.GetNamedSilentFail("SI_RogueMechanitorFaction");
            if (def != null)
                return def;

            Log.Warning("[Signal Interceptor] SI_RogueMechanitorFaction not found. Falling back to Pirate.");
            return FactionDefOf.Pirate;
        }

        private Faction CreateRogueMechanitorFactionForMap()
        {
            FactionDef factionDef = GetRogueMechanitorFactionDef();

            Faction generatorFaction = null;

            try
            {
                generatorFaction = FactionGenerator.NewGeneratedFaction(
                    new FactionGeneratorParms(factionDef)
                );
            }
            catch (System.Exception ex)
            {
                Log.Error("[Signal Interceptor] Failed to generate rogue mechanitor faction. Fallback to Pirate. Exception: " + ex);

                generatorFaction = FactionGenerator.NewGeneratedFaction(
                    new FactionGeneratorParms(FactionDefOf.Pirate)
                );

                if (factionDef != null)
                {
                    generatorFaction.def = factionDef;
                }
            }

            if (generatorFaction == null)
                return null;

            generatorFaction.temporary = true;
            generatorFaction.hidden = false;
            generatorFaction.defeated = false;
            generatorFaction.Name = GenerateRogueMechanitorFactionName();
            generatorFaction.leader = null;

            if (!Find.FactionManager.AllFactions.Contains(generatorFaction))
            {
                Find.FactionManager.Add(generatorFaction);
            }

            generatorFaction.TryMakeInitialRelationsWith(Faction.OfPlayer);
            generatorFaction.SetRelationDirect(
                Faction.OfPlayer,
                FactionRelationKind.Hostile,
                canSendHostilityLetter: false
            );

            foreach (Faction other in Find.FactionManager.AllFactions)
            {
                if (other == null || other == generatorFaction || other == Faction.OfPlayer)
                    continue;

                generatorFaction.TryMakeInitialRelationsWith(other);

                FactionRelation rel = generatorFaction.RelationWith(other, allowNull: true);
                if (rel != null)
                {
                    rel.baseGoodwill = 0;
                    rel.kind = FactionRelationKind.Neutral;
                }

                FactionRelation otherRel = other.RelationWith(generatorFaction, allowNull: true);
                if (otherRel != null)
                {
                    otherRel.baseGoodwill = 0;
                    otherRel.kind = FactionRelationKind.Neutral;
                }
            }

            Log.Message("[Signal Interceptor] Created rogue mechanitor faction: " +
                        generatorFaction.Name +
                        " | def=" + generatorFaction.def.defName +
                        " | temporary=" + generatorFaction.temporary +
                        " | hidden=" + generatorFaction.hidden +
                        " | defeated=" + generatorFaction.defeated +
                        " | loadID=" + generatorFaction.loadID);

            return generatorFaction;
        }

        private string GenerateRogueMechanitorFactionName()
        {
            List<string> nouns = GetTranslatedStringList(
                "SI_MechanitorSignal_FactionNouns",
                new List<string>
                {
            "Протокол",
            "Контур",
            "Сигнал",
            "Импульс",
            "Узел",
            "Код"
                }
            );

            List<string> adjectives = GetTranslatedStringList(
                "SI_MechanitorSignal_FactionAdjectives",
                new List<string>
                {
            "Блуждающего Ядра",
            "Железной Воли",
            "Чужого Разума",
            "Сломанного Контроля",
            "Стального Сердца",
            "Забытой Команды"
                }
            );

            return nouns.RandomElement() + " " + adjectives.RandomElement();
        }

        private Pawn SpawnRogueMechanitor(Map map, Faction faction, float threatPoints, IntVec3 center)
        {
            XenotypeDef xenotype = ChooseRogueMechanitorXenotype();

            PawnGenerationRequest request = new PawnGenerationRequest(
                kind: PawnKindDefOf.Colonist,
                faction: faction,
                context: PawnGenerationContext.NonPlayer,
                forceGenerateNewPawn: true,
                canGeneratePawnRelations: false,
                allowFood: true,
                mustBeCapableOfViolence: true
            );

            Pawn pawn = PawnGenerator.GeneratePawn(request);
            if (pawn == null)
                return null;

            pawn.SetFactionDirect(faction);

            ApplyRogueMechanitorXenotype(pawn, xenotype);
            ApplyRandomIdeology(pawn);
            int tier = GetVIPTier(threatPoints);

            if (pawn.skills != null)
            {
                SkillRecord melee = pawn.skills.GetSkill(SkillDefOf.Melee);
                if (melee != null)
                {
                    int target = tier >= 8 ? 20 : tier >= 6 ? 18 : tier >= 4 ? 16 : 15;

                    if (melee.Level < target)
                    {
                        melee.Level = target;
                    }

                    melee.passion = Passion.Major;
                }

                SkillRecord shooting = pawn.skills.GetSkill(SkillDefOf.Shooting);
                if (shooting != null)
                {
                    int target = tier >= 8 ? 16 : tier >= 6 ? 14 : tier >= 4 ? 12 : 10;

                    if (shooting.Level < target)
                    {
                        shooting.Level = target;
                    }

                    if (shooting.passion == Passion.None)
                    {
                        shooting.passion = Passion.Minor;
                    }
                }

                SkillRecord intellectual = pawn.skills.GetSkill(SkillDefOf.Intellectual);
                if (intellectual != null)
                {
                    int target = tier >= 8 ? 20 : tier >= 6 ? 17 : tier >= 4 ? 14 : 10;

                    if (intellectual.Level < target)
                    {
                        intellectual.Level = target;
                    }

                    intellectual.passion = Passion.Major;
                }

                SkillRecord crafting = pawn.skills.GetSkill(SkillDefOf.Crafting);
                if (crafting != null)
                {
                    int target = tier >= 8 ? 18 : tier >= 6 ? 15 : tier >= 4 ? 12 : 8;

                    if (crafting.Level < target)
                    {
                        crafting.Level = target;
                    }

                    if (crafting.passion == Passion.None)
                    {
                        crafting.passion = Passion.Minor;
                    }
                }
            }


            GiveRogueMechanitorImplants(pawn, threatPoints);
            GiveRogueMechanitorGear(pawn, threatPoints);

            IntVec3 spot;

            if (!CellFinder.TryFindRandomCellNear(
                center,
                map,
                6,
                c => c.Standable(map) && c.GetFirstPawn(map) == null,
                out spot))
            {
                spot = center;
            }

            GenSpawn.Spawn(pawn, spot, map);

            if (pawn.Faction != faction)
                pawn.SetFaction(faction);

            Log.Message("[Signal Interceptor] Rogue mechanitor spawned: " +
                        pawn.LabelShort +
                        " | faction=" + (pawn.Faction?.Name ?? "null") +
                        " | xenotype=" + (ModsConfig.BiotechActive && pawn.genes != null
                            ? pawn.genes.XenotypeLabelCap.ToString()
                            : "none"));

            return pawn;
        }

        private bool HasActiveHostileThreats(Map map, Faction expectedEnemyFaction = null)
        {
            if (map == null)
            {
                return false;
            }

            return map.mapPawns.AllPawnsSpawned
                .Any(p =>
                    p != null
                    && !p.Dead
                    && !p.Downed
                    && p.Faction != null
                    && p.Faction.HostileTo(Faction.OfPlayer)
                    && (expectedEnemyFaction == null || p.Faction == expectedEnemyFaction));
        }

        private void CompleteVIPQuest(VIPSiteData data)
        {
            if (data == null || data.rewardGiven)
            {
                return;
            }

            data.rewardGiven = true;

            switch (data.subtype)
            {
                case VIPSubtype.DoppelgangerVIP:
                    GiveVIPVictoryReward();
                    DeactivateDoppelgangerFaction(data.enemyFaction);
                    data.enemyFaction = null;
                    break;

                case VIPSubtype.MechanitorSignalVIP:
                    Find.LetterStack.ReceiveLetter(
                        "SI_VIP_CompletedTitle".Translate(),
                        "SI_MechanitorSignal_CompletedText".Translate(),
                        LetterDefOf.PositiveEvent
                    );

                    DeactivateRogueMechanitorFaction(data.enemyFaction);
                    data.enemyFaction = null;
                    break;

                default:
                    Find.LetterStack.ReceiveLetter(
                        "SI_VIP_CompletedTitle".Translate(),
                        "SI_VIP_CompletedText".Translate(),
                        LetterDefOf.PositiveEvent
                    );
                    break;
            }

            Log.Message("[Signal Interceptor] VIP quest completed. Subtype=" + data.subtype);
        }

        private void FailVIPQuest(VIPSiteData data, string reasonKey)
        {
            if (data == null || data.rewardGiven)
            {
                return;
            }

            data.rewardGiven = true;

            string title = "SI_VIP_FailedTitle".Translate();

            string text;

            if (!reasonKey.NullOrEmpty() && reasonKey.CanTranslate())
            {
                text = reasonKey.Translate().ToString();
            }
            else
            {
                text = "SI_VIP_FailedText".Translate().ToString();
            }

            Find.LetterStack.ReceiveLetter(
                title,
                text,
                LetterDefOf.NegativeEvent
            );

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

            Log.Message("[Signal Interceptor] VIP quest failed. Subtype=" +
                        data.subtype +
                        " | Reason=" + reasonKey);
        }

        private void ApplyRogueMechanitorXenotype(Pawn pawn, XenotypeDef xenotype)
        {
            if (!ModsConfig.BiotechActive)
                return;

            if (pawn?.genes == null || xenotype == null)
                return;

            try
            {
                pawn.genes.SetXenotype(xenotype);
                pawn.Drawer?.renderer?.SetAllGraphicsDirty();

                Log.Message("[Signal Interceptor] Applied rogue mechanitor xenotype: " +
                            xenotype.defName +
                            " | pawn=" + pawn.LabelShort +
                            " | displayed=" + pawn.genes.XenotypeLabelCap);
            }
            catch (System.Exception ex)
            {
                Log.Warning("[Signal Interceptor] Failed to apply rogue mechanitor xenotype: " + ex);
            }
        }

        private void ApplyRandomIdeology(Pawn pawn)
        {
            if (!ModsConfig.IdeologyActive)
                return;

            if (pawn == null || pawn.ideo == null)
                return;

            try
            {
                Ideo ideo = Find.IdeoManager.IdeosListForReading
                    .Where(i => i != null)
                    .RandomElementWithFallback(null);

                if (ideo != null)
                {
                    pawn.ideo.SetIdeo(ideo);
                }
            }
            catch (System.Exception ex)
            {
                Log.Warning("[Signal Interceptor] Failed to apply random ideology to rogue mechanitor: " + ex);
            }
        }

        private void GiveRogueMechanitorImplants(Pawn pawn, float threatPoints)
        {
            if (pawn?.health == null)
            {
                return;
            }

            int tier = GetVIPTier(threatPoints);

            // Базовый мехлинк. Это главный приз, если пешку удастся захватить.
            AddHediffToPawnByDefNames(
                pawn,
                "MechlinkImplant",
                "Mechlink"
            );

            int controlSublinkLevel = 0;
            int remoteRepairerLevel = 0;
            int gestationProcessorLevel = 0;
            int remoteShielderLevel = 0;
            int repairProbeLevel = 0;

            switch (tier)
            {
                case 1:
                    controlSublinkLevel = 0;
                    remoteRepairerLevel = 0;
                    gestationProcessorLevel = 0;
                    remoteShielderLevel = 0;
                    repairProbeLevel = 0;
                    break;

                case 2:
                    controlSublinkLevel = 1;
                    remoteRepairerLevel = 0;
                    gestationProcessorLevel = 1;
                    remoteShielderLevel = 0;
                    repairProbeLevel = 1;
                    break;

                case 3:
                    controlSublinkLevel = 1;
                    remoteRepairerLevel = 1;
                    gestationProcessorLevel = 1;
                    remoteShielderLevel = 0;
                    repairProbeLevel = 2;
                    break;

                case 4:
                    controlSublinkLevel = 2;
                    remoteRepairerLevel = 1;
                    gestationProcessorLevel = 2;
                    remoteShielderLevel = 1;
                    repairProbeLevel = 2;
                    break;

                case 5:
                    controlSublinkLevel = 3;
                    remoteRepairerLevel = 1;
                    gestationProcessorLevel = 3;
                    remoteShielderLevel = 1;
                    repairProbeLevel = 3;
                    break;

                case 6:
                    controlSublinkLevel = 4;
                    remoteRepairerLevel = 2;
                    gestationProcessorLevel = 4;
                    remoteShielderLevel = 2;
                    repairProbeLevel = 4;
                    break;

                case 7:
                    controlSublinkLevel = 5;
                    remoteRepairerLevel = 2;
                    gestationProcessorLevel = 5;
                    remoteShielderLevel = 2;
                    repairProbeLevel = 5;
                    break;

                case 8:
                    controlSublinkLevel = 6;
                    remoteRepairerLevel = 3;
                    gestationProcessorLevel = 6;
                    remoteShielderLevel = 3;
                    repairProbeLevel = 6;
                    break;

                case 9:
                default:
                    controlSublinkLevel = 6;
                    remoteRepairerLevel = 3;
                    gestationProcessorLevel = 6;
                    remoteShielderLevel = 3;
                    repairProbeLevel = 6;
                    break;
            }

            AddOrSetLevelHediffToPawn(pawn, "ControlSublinkImplant", controlSublinkLevel);
            AddOrSetLevelHediffToPawn(pawn, "RemoteRepairerImplant", remoteRepairerLevel);
            AddOrSetLevelHediffToPawn(pawn, "MechFormfeederImplant", gestationProcessorLevel);
            AddOrSetLevelHediffToPawn(pawn, "RemoteShielderImplant", remoteShielderLevel);
            AddOrSetLevelHediffToPawn(pawn, "RepairProbeImplant", repairProbeLevel);

            // Не механиторские, но боевые/защитные импланты для выживаемости.
            if (tier >= 4)
            {
                AddHediffToPawnByDefNames(pawn, "NeuralCalculator");
                AddHediffToPawnByDefNames(pawn, "LearningAssistant");
            }

            if (tier >= 5)
            {
                AddHediffToPawnByDefNames(pawn, "Coagulator");
                AddHediffToPawnByDefNames(pawn, "HealingEnhancer");
            }

            if (tier >= 6)
            {
                AddHediffToPawnByDefNames(pawn, "Immunoenhancer");
                AddHediffToPawnByDefNames(pawn, "Painstopper");
            }

            if (tier >= 7)
            {
                AddHediffToPawnByDefNames(pawn, "ToughskinGland", "StoneskinGland");
            }

            if (tier >= 8)
            {
                AddHediffToPawnByDefNames(pawn, "AestheticShaper");
                AddHediffToPawnByDefNames(pawn, "AestheticNose");
            }

            if (tier >= 9)
            {
                AddHediffToPawnByDefNames(pawn, "CircadianHalfCycler");
                AddHediffToPawnByDefNames(pawn, "PsychicHarmonizer");
            }

            Log.Message("[Signal Interceptor] Rogue mechanitor implants applied. " +
                        "Tier=" + tier +
                        " | ControlSublink=" + controlSublinkLevel +
                        " | RemoteRepairer=" + remoteRepairerLevel +
                        " | GestationProcessor=" + gestationProcessorLevel +
                        " | RemoteShielder=" + remoteShielderLevel +
                        " | RepairProbe=" + repairProbeLevel);
        }

        private void GiveRogueMechanitorGear(Pawn pawn, float threatPoints)
        {
            if (pawn == null)
            {
                return;
            }

            int tier = GetVIPTier(threatPoints);

            QualityCategory quality = QualityCategory.Good;

            if (tier >= 4)
            {
                quality = QualityCategory.Excellent;
            }

            if (tier >= 7)
            {
                quality = QualityCategory.Masterwork;
            }

            if (tier >= 9)
            {
                quality = QualityCategory.Legendary;
            }

            if (pawn.apparel != null)
            {
                pawn.apparel.DestroyAll();

                if (tier <= 2)
                {
                    TryWearApparelByDefNamesWithQuality(pawn, quality,
                        "Apparel_Pants",
                        "Apparel_BasicShirt",
                        "Apparel_Duster"
                    );

                    TryWearFirstAvailableApparelByDefNamesWithQuality(pawn, quality,
                        "Apparel_AirwireHeadset",
                        "Apparel_ArrayHeadset"
                    );
                }
                else if (tier <= 5)
                {
                    TryWearApparelByDefNamesWithQuality(pawn, quality,
                        "Apparel_MechlordSuit",
                        "Apparel_MechlordHelmet"
                    );

                    TryWearFirstAvailableApparelByDefNamesWithQuality(pawn, quality,
                        "Apparel_ArrayHeadset",
                        "Apparel_MechcommanderHelmet",
                        "Apparel_IntegratorHeadset"
                    );
                }
                else
                {
                    TryWearApparelByDefNamesWithQuality(pawn, quality,
                        "Apparel_MechlordSuit",
                        "Apparel_MechlordHelmet"
                    );
                }

                // Utility slot: выбираем ОДНУ штуку.
                // Для лора и механиторской темы лучше pack, а не shield belt.
                if (tier >= 8)
                {
                    TryWearFirstAvailableApparelByDefNamesWithQuality(pawn, quality,
                        "Apparel_ControlPack",
                        "Apparel_BandwidthPack"
                    );
                }
                else if (tier >= 4)
                {
                    TryWearFirstAvailableApparelByDefNamesWithQuality(pawn, quality,
                        "Apparel_BandwidthPack",
                        "Apparel_ControlPack"
                    );
                }
                else
                {
                    TryWearFirstAvailableApparelByDefNamesWithQuality(pawn, quality,
                        "Apparel_ShieldBelt"
                    );
                }
            }

            if (pawn.equipment != null)
            {
                pawn.equipment.DestroyAllEquipment();

                if (tier >= 8)
                {
                    TryGiveWeaponByDefNamesWithQuality(pawn, quality,
                        "MeleeWeapon_PersonaZeushammer",
                        "MeleeWeapon_PersonaPlasmasword",
                        "MeleeWeapon_PersonaMonosword",
                        "MeleeWeapon_PersonaMonoSword",
                        "MeleeWeapon_Zeushammer",
                        "MeleeWeapon_Plasmasword",
                        "MeleeWeapon_Monosword",
                        "MeleeWeapon_MonoSword"
                    );
                }
                else if (tier >= 5)
                {
                    TryGiveWeaponByDefNamesWithQuality(pawn, quality,
                        "MeleeWeapon_Zeushammer",
                        "MeleeWeapon_Plasmasword",
                        "MeleeWeapon_Monosword",
                        "MeleeWeapon_MonoSword"
                    );
                }
                else
                {
                    TryGiveWeaponByDefNamesWithQuality(pawn, quality,
                        "MeleeWeapon_LongSword",
                        "MeleeWeapon_Gladius",
                        "MeleeWeapon_Mace"
                    );
                }
            }

            Log.Message("[Signal Interceptor] Rogue mechanitor gear applied. Tier=" + tier + " | Quality=" + quality);
        }

        private void TryWearApparelByDefNamesWithQuality(Pawn pawn, QualityCategory quality, params string[] defNames)
        {
            if (pawn?.apparel == null || defNames == null)
            {
                return;
            }

            foreach (string defName in defNames)
            {
                TryWearSingleApparelByDefNameWithQuality(pawn, defName, quality);
            }
        }


        private bool TryWearFirstAvailableApparelByDefNamesWithQuality(Pawn pawn, QualityCategory quality, params string[] defNames)
        {
            if (pawn?.apparel == null || defNames == null)
            {
                return false;
            }

            foreach (string defName in defNames)
            {
                if (TryWearSingleApparelByDefNameWithQuality(pawn, defName, quality))
                {
                    return true;
                }
            }

            return false;
        }

        private bool TryWearSingleApparelByDefNameWithQuality(Pawn pawn, string defName, QualityCategory quality)
        {
            if (pawn?.apparel == null || defName.NullOrEmpty())
            {
                return false;
            }

            ThingDef apparelDef = DefDatabase<ThingDef>.GetNamedSilentFail(defName);
            if (apparelDef == null)
            {
                return false;
            }

            ThingDef stuff = apparelDef.MadeFromStuff ? GenStuff.DefaultStuffFor(apparelDef) : null;
            Thing thing = ThingMaker.MakeThing(apparelDef, stuff);

            if (thing is Apparel apparel)
            {
                apparel.TryGetComp<CompQuality>()?.SetQuality(quality, ArtGenerationContext.Outsider);
                pawn.apparel.Wear(apparel, dropReplacedApparel: true);
                return true;
            }

            return false;
        }

        private bool TryGiveWeaponByDefNamesWithQuality(Pawn pawn, QualityCategory quality, params string[] defNames)
        {
            if (pawn?.equipment == null || defNames == null)
            {
                return false;
            }

            foreach (string defName in defNames)
            {
                ThingDef weaponDef = DefDatabase<ThingDef>.GetNamedSilentFail(defName);
                if (weaponDef == null)
                {
                    continue;
                }

                ThingDef stuff = weaponDef.MadeFromStuff ? GenStuff.DefaultStuffFor(weaponDef) : null;
                Thing weapon = ThingMaker.MakeThing(weaponDef, stuff);

                weapon.TryGetComp<CompQuality>()?.SetQuality(quality, ArtGenerationContext.Outsider);

                if (weapon is ThingWithComps twc)
                {
                    pawn.equipment.AddEquipment(twc);

                    CompBiocodable biocode = twc.TryGetComp<CompBiocodable>();
                    if (biocode != null && !biocode.Biocoded)
                    {
                        biocode.CodeFor(pawn);
                    }

                    return true;
                }
            }

            return false;
        }

        private bool AddOrSetLevelHediffToPawn(Pawn pawn, string defName, int level)
        {
            if (pawn == null || pawn.health == null || pawn.health.hediffSet == null)
            {
                return false;
            }

            if (defName.NullOrEmpty() || level <= 0)
            {
                return false;
            }

            HediffDef hediffDef = DefDatabase<HediffDef>.GetNamedSilentFail(defName);
            if (hediffDef == null)
            {
                Log.Warning("[Signal Interceptor] Mechanitor implant HediffDef not found: " + defName);
                return false;
            }

            Hediff existing = pawn.health.hediffSet.GetFirstHediffOfDef(hediffDef);

            float targetSeverity = level;

            if (hediffDef.maxSeverity > 0f)
            {
                targetSeverity = Mathf.Min(targetSeverity, hediffDef.maxSeverity);
            }

            if (targetSeverity < hediffDef.minSeverity)
            {
                targetSeverity = hediffDef.minSeverity;
            }

            if (existing != null)
            {
                existing.Severity = Mathf.Max(existing.Severity, targetSeverity);
                return true;
            }

            BodyPartRecord targetPart = FindBestBodyPartForHediff(pawn, hediffDef);

            try
            {
                Hediff hediff = HediffMaker.MakeHediff(hediffDef, pawn, targetPart);
                pawn.health.AddHediff(hediff, targetPart);

                // ВАЖНО: severity ставим ПОСЛЕ AddHediff, иначе RimWorld может сбросить на initialSeverity = 1.
                hediff.Severity = targetSeverity;

                Log.Message("[Signal Interceptor] Added mechanitor implant: " +
                            defName +
                            " level=" + targetSeverity +
                            " to " + pawn.LabelShort);

                return true;
            }
            catch (Exception ex)
            {
                Log.Warning("[Signal Interceptor] Failed to add mechanitor implant " +
                            defName +
                            " level=" + targetSeverity +
                            " to " + pawn.LabelShort +
                            ": " + ex);

                return false;
            }
        }

        private void TryWearApparelByDefNames(Pawn pawn, params string[] defNames)
        {
            if (pawn?.apparel == null || defNames == null)
                return;

            foreach (string defName in defNames)
            {
                ThingDef apparelDef = DefDatabase<ThingDef>.GetNamedSilentFail(defName);
                if (apparelDef == null)
                    continue;

                ThingDef stuff = apparelDef.MadeFromStuff ? GenStuff.DefaultStuffFor(apparelDef) : null;
                Thing thing = ThingMaker.MakeThing(apparelDef, stuff);

                if (thing is Apparel apparel)
                {
                    apparel.TryGetComp<CompQuality>()?.SetQuality(QualityCategory.Excellent, ArtGenerationContext.Outsider);
                    pawn.apparel.Wear(apparel, dropReplacedApparel: true);
                }
            }
        }

        private void TryGiveWeaponByDefNames(Pawn pawn, params string[] defNames)
        {
            if (pawn?.equipment == null || defNames == null)
                return;

            foreach (string defName in defNames)
            {
                ThingDef weaponDef = DefDatabase<ThingDef>.GetNamedSilentFail(defName);
                if (weaponDef == null)
                    continue;

                ThingDef stuff = weaponDef.MadeFromStuff ? GenStuff.DefaultStuffFor(weaponDef) : null;
                Thing weapon = ThingMaker.MakeThing(weaponDef, stuff);

                weapon.TryGetComp<CompQuality>()?.SetQuality(QualityCategory.Excellent, ArtGenerationContext.Outsider);

                if (weapon is ThingWithComps twc)
                {
                    pawn.equipment.AddEquipment(twc);

                    CompBiocodable biocode = twc.TryGetComp<CompBiocodable>();
                    if (biocode != null && !biocode.Biocoded)
                    {
                        biocode.CodeFor(pawn);
                    }

                    return;
                }
            }
        }

        private List<Pawn> SpawnMechanitorMechanoids(Map map, Faction faction, float threatPoints, IntVec3 center, Pawn mechanitor)
        {
            List<Pawn> spawned = new List<Pawn>();

            int tier = GetVIPTier(threatPoints);

            int mechCount;

            switch (tier)
            {
                case 1:
                    mechCount = Rand.RangeInclusive(5, 7);
                    break;
                case 2:
                    mechCount = Rand.RangeInclusive(7, 9);
                    break;
                case 3:
                    mechCount = Rand.RangeInclusive(9, 12);
                    break;
                case 4:
                    mechCount = Rand.RangeInclusive(12, 15);
                    break;
                case 5:
                    mechCount = Rand.RangeInclusive(15, 18);
                    break;
                case 6:
                    mechCount = Rand.RangeInclusive(18, 22);
                    break;
                case 7:
                    mechCount = Rand.RangeInclusive(22, 28);
                    break;
                case 8:
                    mechCount = Rand.RangeInclusive(28, 34);
                    break;
                case 9:
                default:
                    mechCount = Rand.RangeInclusive(34, 42);
                    break;
            }

            for (int i = 0; i < mechCount; i++)
            {
                PawnKindDef kind = ChooseMechanitorMechanoidKindByTier(tier);
                if (kind == null)
                {
                    continue;
                }

                Pawn mech = GenerateMechanitorMechanoid(kind, faction);
                if (mech == null)
                {
                    continue;
                }

                IntVec3 spot;

                if (!CellFinder.TryFindRandomCellNear(
                    center,
                    map,
                    14,
                    c => c.Standable(map) && c.GetFirstPawn(map) == null,
                    out spot))
                {
                    spot = CellFinder.RandomClosewalkCellNear(center, map, 10);
                }

                GenSpawn.Spawn(mech, spot, map);

                if (mech.Faction != faction)
                {
                    mech.SetFaction(faction);
                }

                spawned.Add(mech);
            }

            Pawn boss = TrySpawnMechanitorBossMech(map, faction, center, tier);
            if (boss != null)
            {
                spawned.Add(boss);
            }

            Log.Message("[Signal Interceptor] Spawned mechanitor mechanoids. " +
                        "Tier=" + tier +
                        " | Count=" + spawned.Count +
                        " | Boss=" + (boss?.kindDef?.defName ?? "none"));

            return spawned;
        }

        private Pawn GenerateMechanitorMechanoid(PawnKindDef kind, Faction faction)
        {
            if (kind == null || faction == null)
            {
                return null;
            }

            PawnGenerationRequest request = new PawnGenerationRequest(
                kind: kind,
                faction: faction,
                context: PawnGenerationContext.NonPlayer,
                forceGenerateNewPawn: true,
                canGeneratePawnRelations: false,
                allowFood: false,
                mustBeCapableOfViolence: true
            );

            Pawn mech = PawnGenerator.GeneratePawn(request);

            if (mech != null)
            {
                mech.SetFactionDirect(faction);
            }

            return mech;
        }

        private PawnKindDef ChooseMechanitorMechanoidKindByTier(int tier)
        {
            List<string> pool = new List<string>();

            if (tier <= 2)
            {
                pool.AddRange(new[]
                {
            "Mech_Militor",
            "Mech_Scyther",
            "Mech_Pikeman"
        });
            }
            else if (tier <= 4)
            {
                pool.AddRange(new[]
                {
            "Mech_Militor",
            "Mech_Scyther",
            "Mech_Lancer",
            "Mech_Pikeman",
            "Mech_Tunneler"
        });
            }
            else if (tier <= 6)
            {
                pool.AddRange(new[]
                {
            "Mech_Scyther",
            "Mech_Lancer",
            "Mech_Pikeman",
            "Mech_Tunneler",
            "Mech_Legionary",
            "Mech_Tesseron",
            "Mech_CentipedeGunner",
            "Mech_CentipedeBlaster"
        });
            }
            else if (tier <= 8)
            {
                pool.AddRange(new[]
                {
            "Mech_Scyther",
            "Mech_Lancer",
            "Mech_Pikeman",
            "Mech_Tunneler",
            "Mech_Legionary",
            "Mech_Tesseron",
            "Mech_Termite",
            "Mech_Centurion",
            "Mech_CentipedeGunner",
            "Mech_CentipedeBlaster",
            "Mech_CentipedeBurner"
        });
            }
            else
            {
                pool.AddRange(new[]
                {
            "Mech_Scyther",
            "Mech_Lancer",
            "Mech_Pikeman",
            "Mech_Tunneler",
            "Mech_Legionary",
            "Mech_Tesseron",
            "Mech_Termite",
            "Mech_Centurion",
            "Mech_CentipedeGunner",
            "Mech_CentipedeBlaster",
            "Mech_CentipedeBurner"
        });
            }

            pool.Shuffle();

            foreach (string defName in pool)
            {
                PawnKindDef kind = DefDatabase<PawnKindDef>.GetNamedSilentFail(defName);
                if (kind != null)
                {
                    return kind;
                }
            }

            return DefDatabase<PawnKindDef>.GetNamedSilentFail("Mech_Militor");
        }

        private Pawn TrySpawnMechanitorBossMech(Map map, Faction faction, IntVec3 center, int tier)
        {
            if (map == null || faction == null || tier < 7)
            {
                return null;
            }

            PawnKindDef bossKind = null;

            // 9 тир: крайне редкий Апокритон.
            if (tier >= 9 && Rand.Chance(0.04f))
            {
                bossKind = GetFirstPawnKindByDefNames(
                    "Mech_Apocriton",
                    "Apocriton"
                );
            }

            // 8-9 тир: редкая Предводительница.
            if (bossKind == null && tier >= 8 && Rand.Chance(tier >= 9 ? 0.12f : 0.08f))
            {
                bossKind = GetFirstPawnKindByDefNames(
                    "Mech_WarQueen",
                    "Mech_Warqueen",
                    "WarQueen",
                    "Warqueen"
                );
            }

            // 7-9 тир: иногда Дьявол.
            if (bossKind == null && tier >= 7 && Rand.Chance(tier >= 9 ? 0.35f : tier >= 8 ? 0.28f : 0.20f))
            {
                bossKind = GetFirstPawnKindByDefNames(
                    "Mech_Diabolus",
                    "Diabolus"
                );
            }

            if (bossKind == null)
            {
                return null;
            }

            Pawn boss = GenerateMechanitorMechanoid(bossKind, faction);
            if (boss == null)
            {
                return null;
            }

            IntVec3 spot;

            if (!CellFinder.TryFindRandomCellNear(
                center,
                map,
                18,
                c => c.Standable(map) && c.GetFirstPawn(map) == null,
                out spot))
            {
                spot = CellFinder.RandomClosewalkCellNear(center, map, 10);
            }

            GenSpawn.Spawn(boss, spot, map);

            if (boss.Faction != faction)
            {
                boss.SetFaction(faction);
            }

            Log.Warning("[Signal Interceptor] Rogue mechanitor boss spawned: " +
                        bossKind.defName +
                        " | tier=" + tier +
                        " | faction=" + faction.Name);

            return boss;
        }

        private PawnKindDef GetFirstPawnKindByDefNames(params string[] defNames)
        {
            if (defNames == null)
            {
                return null;
            }

            foreach (string defName in defNames)
            {
                if (defName.NullOrEmpty())
                {
                    continue;
                }

                PawnKindDef kind = DefDatabase<PawnKindDef>.GetNamedSilentFail(defName);
                if (kind != null)
                {
                    return kind;
                }
            }

            return null;
        }

        private PawnKindDef ChooseMechanitorMechanoidKind(float threatPoints)
        {
            List<string> pool = new List<string>();

            if (threatPoints < 1600f)
            {
                pool.AddRange(new[]
                {
            "Mech_Militor",
            "Mech_Scyther",
            "Mech_Lancer",
            "Mech_Pikeman"
        });
            }
            else if (threatPoints < 2400f)
            {
                pool.AddRange(new[]
                {
            "Mech_Militor",
            "Mech_Scyther",
            "Mech_Lancer",
            "Mech_Pikeman",
            "Mech_Tunneler",
            "Mech_CentipedeGunner",
            "Mech_CentipedeBlaster"
        });
            }
            else
            {
                pool.AddRange(new[]
                {
            "Mech_Scyther",
            "Mech_Lancer",
            "Mech_Pikeman",
            "Mech_Tunneler",
            "Mech_Tesseron",
            "Mech_Legionary",
            "Mech_Termite",
            "Mech_CentipedeGunner",
            "Mech_CentipedeBlaster",
            "Mech_CentipedeBurner"
        });
            }

            pool.Shuffle();

            foreach (string defName in pool)
            {
                PawnKindDef kind = DefDatabase<PawnKindDef>.GetNamedSilentFail(defName);
                if (kind != null)
                    return kind;
            }

            return DefDatabase<PawnKindDef>.GetNamedSilentFail("Mech_Militor");
        }

        private void StartMechanitorAssaultLord(Map map, Faction faction, List<Pawn> pawns)
        {
            if (map == null || faction == null || pawns.NullOrEmpty())
            {
                return;
            }

            List<Pawn> validPawns = pawns
                .Where(p => p != null && !p.Dead && p.Spawned && p.Map == map)
                .ToList();

            if (validPawns.NullOrEmpty())
            {
                return;
            }

            foreach (Pawn pawn in validPawns)
            {
                try
                {
                    Lord oldLord = pawn.GetLord();
                    if (oldLord != null)
                    {
                        oldLord.RemovePawn(pawn);
                    }
                }
                catch
                {
                    // Не критично.
                }
            }

            LordJob_AssaultColony lordJob = new LordJob_AssaultColony(
                faction,
                false, // canKidnap
                false, // canTimeoutOrFlee
                false, // sappers
                false, // useAvoidGridSmart
                false  // canSteal
            );

            Lord lord = LordMaker.MakeNewLord(faction, lordJob, map, validPawns);

            foreach (Pawn pawn in validPawns)
            {
                if (pawn == null || pawn.Dead)
                {
                    continue;
                }

                if (pawn.mindState == null)
                {
                    pawn.mindState = new Pawn_MindState(pawn);
                }

                pawn.mindState.duty = new PawnDuty(DutyDefOf.AssaultColony);

                if (pawn.jobs != null && pawn.CurJob != null)
                {
                    pawn.jobs.EndCurrentJob(JobCondition.InterruptForced);
                }
            }

            Log.Message("[Signal Interceptor] Rogue mechanitor mechanoid assault lord started. Mechs: " + validPawns.Count + ". Flee disabled.");
        }

        private void EnforceMechanitorSignalCombat(VIPSiteData data)
        {
            if (data == null || data.subtype != VIPSubtype.MechanitorSignalVIP)
            {
                return;
            }

            if (data.site == null || !data.site.HasMap || data.enemyFaction == null)
            {
                return;
            }

            Map map = data.site.Map;
            Faction faction = data.enemyFaction;

            IntVec3 center = data.signalCampCenter.IsValid ? data.signalCampCenter : map.Center;

            List<Pawn> factionPawns = map.mapPawns.AllPawnsSpawned
                .Where(p => p != null
                         && !p.Dead
                         && p.Faction == faction)
                .ToList();

            if (factionPawns.NullOrEmpty())
            {
                return;
            }

            Pawn mechanitor = factionPawns
                .Where(p => p.RaceProps != null && p.RaceProps.Humanlike)
                .OrderBy(p => p.Position.DistanceTo(center))
                .FirstOrDefault();

            List<Pawn> mechs = factionPawns
                .Where(p => p.RaceProps != null && p.RaceProps.IsMechanoid)
                .Where(p => !p.Downed)
                .ToList();

            foreach (Pawn mech in mechs)
            {
                KeepMechanitorMechanoidFighting(mech, map);
            }

            if (mechanitor != null && !mechanitor.Downed)
            {
                ControlRogueMechanitorPosition(mechanitor, mechs, map, center);
            }
        }

        private void KeepMechanitorMechanoidFighting(Pawn mech, Map map)
        {
            if (mech == null || mech.Dead || mech.Downed || map == null)
            {
                return;
            }

            if (mech.mindState == null)
            {
                mech.mindState = new Pawn_MindState(mech);
            }

            bool hadFleeJob = IsFleeOrExitJob(mech.CurJob);
            bool hadFleeDuty = IsFleeOrExitDuty(mech.mindState.duty);

            if (hadFleeJob && mech.jobs != null)
            {
                mech.jobs.EndCurrentJob(JobCondition.InterruptForced);
            }

            if (hadFleeDuty || mech.mindState.duty == null)
            {
                mech.mindState.duty = new PawnDuty(DutyDefOf.AssaultColony);
            }
        }

        private bool IsIdleOrWaitJob(Job job)
        {
            if (job == null || job.def == null || job.def.defName == null)
            {
                return true;
            }

            string defName = job.def.defName;

            return defName == "Wait" ||
                   defName == "Wait_Combat" ||
                   defName == "Goto" ||
                   defName.IndexOf("Wander", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private bool IsFleeOrExitJob(Job job)
        {
            if (job == null || job.def == null || job.def.defName == null)
            {
                return false;
            }

            string defName = job.def.defName;

            return defName.IndexOf("Flee", StringComparison.OrdinalIgnoreCase) >= 0
                || defName.IndexOf("Exit", StringComparison.OrdinalIgnoreCase) >= 0
                || defName.IndexOf("Leave", StringComparison.OrdinalIgnoreCase) >= 0
                || defName.IndexOf("GotoMapEdge", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private bool IsFleeOrExitDuty(PawnDuty duty)
        {
            if (duty == null || duty.def == null || duty.def.defName == null)
            {
                return false;
            }

            string defName = duty.def.defName;

            return defName.IndexOf("Flee", StringComparison.OrdinalIgnoreCase) >= 0
                || defName.IndexOf("Exit", StringComparison.OrdinalIgnoreCase) >= 0
                || defName.IndexOf("Leave", StringComparison.OrdinalIgnoreCase) >= 0
                || defName.IndexOf("Travel", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void ControlRogueMechanitorPosition(Pawn mechanitor, List<Pawn> mechs, Map map, IntVec3 center)
        {
            if (mechanitor == null || mechanitor.Dead || mechanitor.Downed || map == null)
            {
                return;
            }

            if (mechanitor.mindState == null)
            {
                mechanitor.mindState = new Pawn_MindState(mechanitor);
            }

            Pawn nearestPlayer = map.mapPawns.AllPawnsSpawned
                .Where(p => p.Faction == Faction.OfPlayer)
                .Where(p => !p.Dead && !p.Downed)
                .OrderBy(p => p.Position.DistanceTo(mechanitor.Position))
                .FirstOrDefault();

            float distanceToPlayer = nearestPlayer != null
                ? mechanitor.Position.DistanceTo(nearestPlayer.Position)
                : 9999f;

            const float engageRadius = 12f;
            const float preferredDistanceFromCenter = 10f;

            // Если игрок подошёл критически близко — механитор вступает в бой.
            if (nearestPlayer != null && distanceToPlayer <= engageRadius)
            {
                mechanitor.mindState.duty = new PawnDuty(DutyDefOf.AssaultColony);

                if (mechanitor.jobs != null && IsFleeOrExitJob(mechanitor.CurJob))
                {
                    mechanitor.jobs.EndCurrentJob(JobCondition.InterruptForced);
                }

                TryForceAttackNearestPlayerPawn(mechanitor, map);
                return;
            }

            // Иначе механитор НЕ должен сам ломиться в рукопашку.
            // Держим его около центра лагеря или около ближайшего живого меха.
            IntVec3 guardPoint = center;

            Pawn nearestMech = null;
            if (!mechs.NullOrEmpty())
            {
                nearestMech = mechs
                    .Where(m => m != null && !m.Dead && !m.Downed && m.Spawned)
                    .OrderBy(m => m.Position.DistanceTo(mechanitor.Position))
                    .FirstOrDefault();
            }

            if (nearestMech != null)
            {
                guardPoint = nearestMech.Position;
            }

            mechanitor.mindState.duty = new PawnDuty(DutyDefOf.Defend, guardPoint);

            float distanceToGuardPoint = mechanitor.Position.DistanceTo(guardPoint);

            if (distanceToGuardPoint > preferredDistanceFromCenter)
            {
                TryMovePawnNear(mechanitor, map, guardPoint, 4);
            }
            else
            {
                // Если он пытался бежать или атаковать далеко — сбрасываем.
                if (mechanitor.jobs != null && (IsFleeOrExitJob(mechanitor.CurJob) || IsAggressiveFarAwayJob(mechanitor.CurJob, mechanitor, nearestPlayer, engageRadius)))
                {
                    mechanitor.jobs.EndCurrentJob(JobCondition.InterruptForced);
                }
            }
        }

        private void TryMovePawnNear(Pawn pawn, Map map, IntVec3 target, int radius)
        {
            if (pawn == null || pawn.Dead || pawn.Downed || pawn.jobs == null || map == null)
            {
                return;
            }

            IntVec3 moveCell;

            bool found = CellFinder.TryFindRandomCellNear(
                target,
                map,
                radius,
                c => c.Standable(map) && c.GetFirstPawn(map) == null,
                out moveCell
            );

            if (!found)
            {
                moveCell = target;
            }

            if (!moveCell.IsValid || !moveCell.InBounds(map) || !moveCell.Standable(map))
            {
                return;
            }

            if (pawn.CurJob != null && pawn.CurJob.def == JobDefOf.Goto && pawn.CurJob.targetA.Cell.DistanceTo(moveCell) <= 2f)
            {
                return;
            }

            Job job = JobMaker.MakeJob(JobDefOf.Goto, moveCell);
            job.expiryInterval = 300;
            job.checkOverrideOnExpire = true;

            pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc);
        }

        private bool IsAggressiveFarAwayJob(Job job, Pawn pawn, Pawn target, float allowedRadius)
        {
            if (job == null || pawn == null || target == null)
            {
                return false;
            }

            if (job.def == JobDefOf.AttackMelee || job.def == JobDefOf.AttackStatic)
            {
                return pawn.Position.DistanceTo(target.Position) > allowedRadius;
            }

            return false;
        }

        private void TryForceAttackNearestPlayerPawn(Pawn attacker, Map map)
        {
            if (attacker == null || attacker.Dead || attacker.Downed || attacker.jobs == null || map == null)
            {
                return;
            }

            Pawn target = map.mapPawns.AllPawnsSpawned
                .Where(p => p.Faction == Faction.OfPlayer)
                .Where(p => !p.Dead && !p.Downed)
                .OrderBy(p => p.Position.DistanceTo(attacker.Position))
                .FirstOrDefault();

            if (target == null)
            {
                return;
            }

            try
            {
                Job job = JobMaker.MakeJob(JobDefOf.AttackStatic, target);
                job.expiryInterval = Rand.RangeInclusive(180, 360);
                job.checkOverrideOnExpire = true;

                attacker.jobs.TryTakeOrderedJob(job, JobTag.Misc);
            }
            catch (Exception ex)
            {
                Log.Warning("[Signal Interceptor] Failed to force attack job for " +
                            attacker.LabelShort + ": " + ex);
            }
        }

        private void DeactivateRogueMechanitorFaction(Faction faction)
        {
            if (faction == null)
                return;

            if (!IsRogueMechanitorFactionDef(faction.def))
                return;

            faction.hidden = true;
            faction.temporary = true;
            faction.defeated = true;
            faction.leader = null;

            Log.Message("[Signal Interceptor] Deactivated rogue mechanitor faction: " +
                        faction.Name +
                        " | def=" + faction.def.defName +
                        " | loadID=" + faction.loadID);
        }

        private bool IsRogueMechanitorFactionDef(FactionDef def)
        {
            return def != null
                && def.defName == "SI_RogueMechanitorFaction";
        }

        private XenotypeDef ChooseRogueMechanitorXenotype()
        {
            List<XenotypeDef> options = new List<XenotypeDef>();

            void TryAdd(string defName)
            {
                XenotypeDef xenotype = DefDatabase<XenotypeDef>.GetNamedSilentFail(defName);
                if (xenotype != null)
                {
                    options.Add(xenotype);
                }
            }

            // Базовые / Biotech ксенотипы.
            TryAdd("Baseliner");
            TryAdd("Hussar");
            TryAdd("Pigskin");
            TryAdd("Impid");
            TryAdd("Yttakin");
            TryAdd("Waster");
            TryAdd("Dirtmole");
            TryAdd("Neanderthal");
            TryAdd("Genie");

            // Odyssey. Не проверяем ModsConfig.OdysseyActive напрямую,
            // чтобы код спокойно компилировался даже без жёсткой зависимости.
            TryAdd("Starjack");

            // Намеренно НЕ добавляем:
            // Highmate / "ангел"
            // Sanguophage / сангвиофаг

            if (options.NullOrEmpty())
            {
                return DefDatabase<XenotypeDef>.GetNamedSilentFail("Baseliner");
            }

            return options.RandomElement();
        }

        private bool AddHediffToPawnByDefNames(Pawn pawn, params string[] defNames)
        {
            if (pawn == null || defNames == null || defNames.Length == 0)
            {
                return false;
            }

            HediffDef hediffDef = null;

            foreach (string defName in defNames)
            {
                if (defName.NullOrEmpty())
                {
                    continue;
                }

                hediffDef = DefDatabase<HediffDef>.GetNamedSilentFail(defName);
                if (hediffDef != null)
                {
                    break;
                }
            }

            if (hediffDef == null)
            {
                Log.Warning("[SignalInterceptor] Could not find any hediff def from list: " + string.Join(", ", defNames));
                return false;
            }

            if (pawn.health == null || pawn.health.hediffSet == null)
            {
                return false;
            }

            // Не добавляем дубликат, если такой имплант/хеддиф уже есть.
            if (pawn.health.hediffSet.HasHediff(hediffDef))
            {
                return true;
            }

            BodyPartRecord targetPart = FindBestBodyPartForHediff(pawn, hediffDef);

            try
            {
                Hediff hediff = HediffMaker.MakeHediff(hediffDef, pawn, targetPart);
                pawn.health.AddHediff(hediff, targetPart);
                return true;
            }
            catch (Exception ex)
            {
                Log.Warning("[SignalInterceptor] Failed to add hediff " + hediffDef.defName + " to pawn " + pawn.LabelShort + ": " + ex);
                return false;
            }
        }

        private BodyPartRecord FindBestBodyPartForHediff(Pawn pawn, HediffDef hediffDef)
        {
            if (pawn == null ||
                pawn.RaceProps == null ||
                pawn.RaceProps.body == null ||
                pawn.health == null ||
                pawn.health.hediffSet == null)
            {
                return null;
            }

            List<BodyPartRecord> parts = pawn.health.hediffSet.GetNotMissingParts().ToList();

            if (parts.NullOrEmpty())
            {
                return null;
            }

            string hediffDefName = hediffDef != null ? hediffDef.defName : string.Empty;

            bool wantsBrainOrHead =
                hediffDefName.IndexOf("Mechlink", StringComparison.OrdinalIgnoreCase) >= 0 ||
                hediffDefName.IndexOf("Neural", StringComparison.OrdinalIgnoreCase) >= 0 ||
                hediffDefName.IndexOf("LearningAssistant", StringComparison.OrdinalIgnoreCase) >= 0 ||
                hediffDefName.IndexOf("ControlSubLink", StringComparison.OrdinalIgnoreCase) >= 0 ||
                hediffDefName.IndexOf("Bandwidth", StringComparison.OrdinalIgnoreCase) >= 0;

            if (wantsBrainOrHead)
            {
                BodyPartRecord brain = parts.FirstOrDefault(p => p.def != null && p.def.defName == "Brain");
                if (brain != null)
                {
                    return brain;
                }

                BodyPartRecord head = parts.FirstOrDefault(p => p.def != null && p.def.defName == "Head");
                if (head != null)
                {
                    return head;
                }
            }

            // Общий fallback: мозг -> голова -> торс -> любая доступная часть.
            BodyPartRecord fallbackBrain = parts.FirstOrDefault(p => p.def != null && p.def.defName == "Brain");
            if (fallbackBrain != null)
            {
                return fallbackBrain;
            }

            BodyPartRecord fallbackHead = parts.FirstOrDefault(p => p.def != null && p.def.defName == "Head");
            if (fallbackHead != null)
            {
                return fallbackHead;
            }

            BodyPartRecord torso = parts.FirstOrDefault(p => p.def != null && p.def.defName == "Torso");
            if (torso != null)
            {
                return torso;
            }

            return parts.FirstOrDefault();
        }

        private void CleanupDoppelgangerSettlements()
        {
            List<Settlement> settlements = Find.WorldObjects.AllWorldObjects
                .OfType<Settlement>()
                .Where(s => s.Faction != null
                         && IsDoppelgangerFactionDef(s.Faction.def))
                .ToList();

            foreach (Settlement settlement in settlements)
            {
                Log.Warning("[Signal Interceptor] Removing invalid doppelganger settlement: " +
                            settlement.Label +
                            " | tile=" + settlement.Tile +
                            " | faction=" + (settlement.Faction?.Name ?? "null") +
                            " | factionDef=" + (settlement.Faction?.def?.defName ?? "null"));

                Find.WorldObjects.Remove(settlement);
            }
        }

        private void SpawnShuttleVIP(Map map, VIPSiteData data)
        {
            // Ищем шаттл, который уже заспавнил GenStep
            Thing shuttle = map.listerThings.AllThings
                .FirstOrDefault(t => t.def.defName == "ShuttleCrashed" || t.def.defName == "Shuttle");

            IntVec3 vipSpot;
            if (shuttle != null)
            {
                CellFinder.TryFindRandomCellNear(shuttle.Position, map, 6,
                    (IntVec3 c) => c.Standable(map) && c.GetFirstPawn(map) == null,
                    out vipSpot);
            }
            else
            {
                // Фоллбэк — если шаттла почему-то нет
                vipSpot = map.Center;
                CellFinder.TryFindRandomCellNear(map.Center, map, 10,
                    (IntVec3 c) => c.Standable(map) && c.GetFirstPawn(map) == null,
                    out vipSpot);
                Log.Warning("[Signal Interceptor] No shuttle found on map, spawning VIP at center.");
            }

            PawnKindDef vipKind = PawnKindDefOf.Colonist;
            Faction vipFaction = data.faction;

            PawnGenerationRequest vipRequest = new PawnGenerationRequest(
                kind: vipKind,
                faction: vipFaction,
                context: PawnGenerationContext.NonPlayer,
                forceGenerateNewPawn: true,
                canGeneratePawnRelations: false,
                allowFood: true,
                mustBeCapableOfViolence: false
            );

            Pawn vip = PawnGenerator.GeneratePawn(vipRequest);
            if (vip == null) return;

            BoostPawnSkills(vip);
            AddImplantsToVIP(vip, data.threatPoints);
            GiveVIPGear(vip);

            GenSpawn.Spawn(vip, vipSpot, map);

            // Присоединяем VIP к существующему лорду обороны, или создаём нового
            IntVec3 defendPoint = shuttle?.Position ?? map.Center;
            Lord existingLord = map.lordManager.lords
                .FirstOrDefault(l => l.faction == vipFaction);

            if (existingLord != null)
            {
                existingLord.AddPawn(vip);
            }
            else
            {
                LordJob_DefendPoint lordJob = new LordJob_DefendPoint(defendPoint);
                Lord lord = LordMaker.MakeNewLord(vipFaction, lordJob, map);
                lord.AddPawn(vip);
            }

            Find.LetterStack.ReceiveLetter(
                "SI_VIP_SpottedTitle".Translate(),
                "SI_VIP_SpottedText".Translate(vip.LabelShort, data.faction.Name),
                LetterDefOf.NeutralEvent,
                new LookTargets(vip)
            );

            Log.Message("[Signal Interceptor] Shuttle VIP spawned: " + vip.LabelShort + " at " + vipSpot);
        }

        private void GiveVIPGear(Pawn vip)
        {
            if (vip.apparel == null) return;

            ThingDef shieldBelt = DefDatabase<ThingDef>.GetNamedSilentFail("Apparel_ShieldBelt");
            if (shieldBelt != null)
            {
                Thing shield = ThingMaker.MakeThing(shieldBelt);
                if (shield is Apparel shieldApparel)
                {
                    vip.apparel.Wear(shieldApparel, dropReplacedApparel: false);
                }
            }

            ThingDef prestigeRobe = DefDatabase<ThingDef>.GetNamedSilentFail("Apparel_RobeRoyal");
            if (prestigeRobe == null)
                prestigeRobe = DefDatabase<ThingDef>.GetNamedSilentFail("Apparel_PrestigeRobe");
            if (prestigeRobe == null)
                prestigeRobe = DefDatabase<ThingDef>.GetNamedSilentFail("Apparel_Cape");

            if (prestigeRobe != null)
            {
                ThingDef stuff = GenStuff.DefaultStuffFor(prestigeRobe);
                Thing robe = ThingMaker.MakeThing(prestigeRobe, stuff);
                robe.TryGetComp<CompQuality>()?.SetQuality(QualityCategory.Excellent, ArtGenerationContext.Outsider);
                if (robe is Apparel robeApparel)
                {
                    vip.apparel.Wear(robeApparel, dropReplacedApparel: true);
                }
            }
        }

        private void BoostPawnSkills(Pawn pawn)
        {
            if (pawn.skills == null) return;

            var allSkills = pawn.skills.skills.Where(s => !s.TotallyDisabled).ToList();
            int boostCount = Rand.RangeInclusive(3, 5);

            for (int i = 0; i < boostCount && allSkills.Count > 0; i++)
            {
                var skill = allSkills.RandomElement();
                allSkills.Remove(skill);

                int targetLevel = Rand.RangeInclusive(12, 20);
                if (skill.Level < targetLevel)
                {
                    skill.Level = targetLevel;
                }

                if (Rand.Chance(0.4f))
                {
                    skill.passion = Rand.Chance(0.3f) ? Passion.Major : Passion.Minor;
                }
            }
        }

        private void AddImplantsToVIP(Pawn pawn, float threatPoints)
        {
            if (pawn.health?.hediffSet == null) return;

            int implantCount;
            if (threatPoints >= 2000f) implantCount = Rand.RangeInclusive(4, 6);
            else if (threatPoints >= 1200f) implantCount = Rand.RangeInclusive(2, 4);
            else implantCount = Rand.RangeInclusive(1, 2);

            var validParts = pawn.RaceProps.body.AllParts
                .Where(p => p.def.tags != null && p.def.tags.Any())
                .ToList();

            for (int i = 0; i < implantCount && validParts.Count > 0; i++)
            {
                ThingDef implantThing = DefDatabase<ThingDef>.AllDefs
                    .Where(d => d.isTechHediff
                             && d.techHediffsTags != null
                             && d.techHediffsTags.Contains("Advanced"))
                    .RandomElementWithFallback(null);

                if (implantThing != null)
                {
                    var recipe = DefDatabase<RecipeDef>.AllDefs
                        .FirstOrDefault(r => r.addsHediff != null
                                          && r.addsHediff.spawnThingOnRemoved == implantThing
                                          && r.appliedOnFixedBodyParts?.Any() == true);

                    if (recipe != null)
                    {
                        var targetPart = recipe.appliedOnFixedBodyParts
                            .SelectMany(bpd => pawn.RaceProps.body.AllParts.Where(p => p.def == bpd))
                            .Where(p => !pawn.health.hediffSet.HasDirectlyAddedPartFor(p))
                            .RandomElementWithFallback(null);

                        if (targetPart != null)
                        {
                            pawn.health.AddHediff(recipe.addsHediff, targetPart);
                        }
                    }
                }
            }

            if (threatPoints >= 1800f && Rand.Chance(0.25f))
            {
                HediffDef archoBrain = DefDatabase<HediffDef>.GetNamedSilentFail("ArchobraineImplant");
                if (archoBrain == null)
                    archoBrain = DefDatabase<HediffDef>.GetNamedSilentFail("Psychic amplifier");

                if (archoBrain != null)
                {
                    var brain = pawn.RaceProps.body.AllParts.FirstOrDefault(p => p.def.defName == "Brain");
                    if (brain != null && !pawn.health.hediffSet.HasDirectlyAddedPartFor(brain))
                    {
                        pawn.health.AddHediff(archoBrain, brain);
                    }
                }
            }
        }

        private void ExecuteRaid(PendingRaid raid)
        {
            Map map = Find.Maps.FirstOrDefault(m => m.uniqueID == raid.mapId);
            if (map == null) return;
            if (raid.faction == null) return;
            if (raid.faction.defeated) return;

            IncidentParms parms = new IncidentParms();
            parms.target = map;
            parms.faction = raid.faction;
            parms.points = raid.points;
            parms.forced = true;

            bool success = IncidentDefOf.RaidEnemy.Worker.TryExecute(parms);

            if (!success)
            {
                if (!raid.faction.HostileTo(Faction.OfPlayer))
                {
                    raid.faction.TryAffectGoodwillWith(Faction.OfPlayer, -200, canSendMessage: false, canSendHostilityLetter: false);
                }

                IncidentParms parms2 = new IncidentParms();
                parms2.target = map;
                parms2.faction = raid.faction;
                parms2.points = raid.points * 2f;
                parms2.forced = true;

                IncidentDefOf.RaidEnemy.Worker.TryExecute(parms2);
            }
        }

        private void DeliverSlave(PendingSlaveDelivery delivery)
        {
            Map map = Find.Maps.FirstOrDefault(m => m.uniqueID == delivery.mapId);
            if (map == null) return;

            PawnGenerationRequest request = new PawnGenerationRequest(
                kind: PawnKindDefOf.Slave,
                faction: null,
                context: PawnGenerationContext.NonPlayer,
                forceGenerateNewPawn: true,
                canGeneratePawnRelations: false,
                allowFood: false
            );

            Pawn slave = PawnGenerator.GeneratePawn(request);
            if (slave == null) return;

            slave.guest?.SetGuestStatus(Faction.OfPlayer, GuestStatus.Slave);

            IntVec3 edgeCell;
            if (!CellFinder.TryFindRandomEdgeCellWith(
                    (IntVec3 c) => map.reachability.CanReachColony(c),
                    map, CellFinder.EdgeRoadChance_Neutral, out edgeCell))
            {
                edgeCell = CellFinder.RandomEdgeCell(map);
            }

            GenSpawn.Spawn(slave, edgeCell, map);

            Find.LetterStack.ReceiveLetter(
                "SI_LetterSlaveArrivedTitle".Translate(),
                "SI_LetterSlaveArrivedText".Translate(slave.LabelShort),
                LetterDefOf.PositiveEvent,
                new LookTargets(slave)
            );
        }

        private void SpawnDoppelgangerVIP(Map map, VIPSiteData data)
        {
            int cloneCount = Rand.RangeInclusive(10, 15);

            PawnKindDef templateKind = PawnKindDefOf.Colonist;

            /*
             * Выбираем ксенотип один раз.
             * Этот же ксенотип:
             * 1) выбирает FactionDef;
             * 2) применяется к шаблону;
             * 3) копируется всем клонам через CopyTemplate().
             */
            XenotypeDef chosenXenotype = ChooseDoppelgangerXenotype();

            Faction cloneFaction = CreateDoppelgangerFactionForMap(chosenXenotype);

            if (cloneFaction == null)
            {
                Log.Error("[Signal Interceptor] Failed to create doppelganger faction. Doppelganger VIP spawn aborted.");

                data.rewardGiven = true;
                data.vipSpawned = true;
                data.enemyFaction = null;

                return;
            }

            data.enemyFaction = cloneFaction;

            if (data.site != null)
            {
                data.site.SetFaction(cloneFaction);
                data.site.factionMustRemainHostile = false;
            }

            Log.Message("[Signal Interceptor] Doppelganger spawn faction check:" +
                        " | site=" + (data.site?.LabelCap ?? "null") +
                        " | siteFaction=" + (data.site?.Faction?.Name ?? "null") +
                        " | cloneFaction=" + (cloneFaction?.Name ?? "null") +
                        " | factionDef=" + (cloneFaction?.def?.defName ?? "null") +
                        " | contextFaction=" + (data.faction?.Name ?? "null") +
                        " | chosenXenotype=" + (chosenXenotype?.defName ?? "none") +
                        " | temporary=" + cloneFaction.temporary +
                        " | hiddenField=" + cloneFaction.hidden +
                        " | HiddenProperty=" + cloneFaction.Hidden +
                        " | leader=" + (cloneFaction.leader?.LabelShort ?? "null"));

            PawnGenerationRequest templateRequest = new PawnGenerationRequest(
                kind: templateKind,
                faction: cloneFaction,
                context: PawnGenerationContext.NonPlayer,
                forceGenerateNewPawn: true,
                canGeneratePawnRelations: false,
                allowFood: true,
                mustBeCapableOfViolence: true
            );

            Pawn template = PawnGenerator.GeneratePawn(templateRequest);
            if (template == null)
            {
                Log.Error("[Signal Interceptor] Failed to generate doppelganger template pawn.");
                DeactivateDoppelgangerFaction(cloneFaction);
                data.enemyFaction = null;
                return;
            }

            ApplyDoppelgangerTemplateXenotype(template, chosenXenotype);

            template.SetFactionDirect(cloneFaction);

            if (template.ageTracker.AgeBiologicalYears < 25)
            {
                template.ageTracker.AgeBiologicalTicks = 25 * 3600000L;
                template.ageTracker.AgeChronologicalTicks = 25 * 3600000L;
            }

            BoostPawnSkills(template);

            IntVec3 baseCenter = map.Center;

            IntVec3 foundCenter;
            bool found = CellFinder.TryFindRandomCellNear(
                map.Center,
                map,
                20,
                c => c.Standable(map) && !c.Roofed(map),
                out foundCenter
            );

            if (found)
            {
                baseCenter = foundCenter;
            }

            Name templateName = template.Name;
            Color templateHairColor = template.story?.HairColor ?? Color.white;
            HairDef templateHair = template.story?.hairDef;
            BeardDef templateBeard = template.style?.beardDef;
            HeadTypeDef templateHead = template.story?.headType;
            BodyTypeDef templateBody = template.story?.bodyType;
            Color templateSkinColor = template.story?.SkinColorBase ?? Color.white;
            Gender templateGender = template.gender;

            if (template.health?.hediffSet != null)
            {
                List<Hediff> templateInjuries = template.health.hediffSet.hediffs
                    .Where(h => h is Hediff_Injury || h is Hediff_MissingPart)
                    .ToList();

                foreach (Hediff h in templateInjuries)
                {
                    template.health.RemoveHediff(h);
                }
            }

            List<Pawn> allClones = new List<Pawn>();

            for (int i = 0; i < cloneCount; i++)
            {
                PawnGenerationRequest cloneRequest = new PawnGenerationRequest(
                    kind: templateKind,
                    faction: cloneFaction,
                    context: PawnGenerationContext.NonPlayer,
                    forceGenerateNewPawn: true,
                    canGeneratePawnRelations: false,
                    allowFood: true,
                    mustBeCapableOfViolence: true,
                    fixedGender: templateGender
                );

                Pawn clone = PawnGenerator.GeneratePawn(cloneRequest);
                if (clone == null)
                    continue;

                clone.SetFactionDirect(cloneFaction);

                CopyTemplate(template, clone);

                CopyAppearance(
                    clone,
                    templateName,
                    templateHairColor,
                    templateHair,
                    templateBeard,
                    templateHead,
                    templateBody,
                    templateSkinColor,
                    templateGender
                );

                /*
                 * ВАЖНО:
                 * После CopyTemplate/CopyAppearance на всякий случай фиксируем фракцию напрямую.
                 * SetFactionDirect не кидает warning при той же фракции.
                 */
                clone.SetFactionDirect(cloneFaction);

                GiveCloneRandomWeapon(clone, data.threatPoints);
                GiveCloneArmor(clone, data.threatPoints);

                ApplyCloneFragility(clone);
                AddDoppelgangerMark(clone);

                IntVec3 cloneSpot = baseCenter;

                bool foundSpot = CellFinder.TryFindRandomCellNear(
                    baseCenter,
                    map,
                    12,
                    c => c.Standable(map) && c.GetFirstPawn(map) == null,
                    out cloneSpot
                );

                if (!foundSpot)
                {
                    cloneSpot = CellFinder.RandomClosewalkCellNear(baseCenter, map, 8);
                }

                GenSpawn.Spawn(clone, cloneSpot, map);

                /*
                 * НЕ вызываем clone.SetFaction(cloneFaction), если фракция уже такая же.
                 * Иначе RimWorld пишет warning:
                 * "Used SetFaction to change pawn to same faction"
                 * и открывает консоль.
                 */
                if (clone.Faction != cloneFaction)
                {
                    clone.SetFaction(cloneFaction);
                }

                allClones.Add(clone);

                Log.Message("[Signal Interceptor] Doppelganger clone spawned: " +
                            clone.LabelShort +
                            " | faction=" + (clone.Faction?.Name ?? "null") +
                            " | expectedFaction=" + cloneFaction.Name +
                            " | factionDef=" + (clone.Faction?.def?.defName ?? "null") +
                            " | factionHidden=" + (clone.Faction?.Hidden.ToString() ?? "null") +
                            " | factionTemporary=" + (clone.Faction?.temporary.ToString() ?? "null") +
                            " | factionHiddenField=" + (clone.Faction?.hidden.ToString() ?? "null") +
                            " | xenotype=" + (ModsConfig.BiotechActive && clone.genes != null
                                ? clone.genes.XenotypeLabelCap.ToString()
                                : "none"));
            }

            if (allClones.Count == 0)
            {
                Log.Error("[Signal Interceptor] No doppelganger clones were spawned.");
                DeactivateDoppelgangerFaction(cloneFaction);
                data.enemyFaction = null;
                return;
            }

            LordJob_DefendPoint lordJob = new LordJob_DefendPoint(baseCenter);
            Lord lord = LordMaker.MakeNewLord(cloneFaction, lordJob, map);

            foreach (Pawn clone in allClones)
            {
                lord.AddPawn(clone);
            }

            SpawnCampProps(map, baseCenter);

            Find.LetterStack.ReceiveLetter(
                "SI_Doppelganger_Title".Translate(),
                "SI_Doppelganger_Text".Translate(
                    allClones.Count.ToString(),
                    cloneFaction.Name ?? GetDoppelgangerFactionName()
                ),
                LetterDefOf.ThreatBig,
                new LookTargets(allClones.First())
            );

            Log.Message("[Signal Interceptor] Doppelganger VIP spawned: " +
                        allClones.Count +
                        " clones of " + templateName +
                        " | clone faction: " + cloneFaction.Name +
                        " | clone faction def: " + cloneFaction.def.defName +
                        " | chosen xenotype: " + (chosenXenotype?.defName ?? "none") +
                        " | template xenotype: " + (ModsConfig.BiotechActive && template.genes != null
                            ? template.genes.XenotypeLabelCap.ToString()
                            : "none") +
                        " | site faction: " + (data.site?.Faction?.Name ?? "null") +
                        " | site faction equals clone faction: " + (data.site?.Faction == cloneFaction));
        }

        private string GetDoppelgangerFactionName()
        {
            const string key = "SI_Doppelganger_FactionName";

            if (key.CanTranslate())
                return key.Translate().ToString();

            return "Anomalous doppelgangers";
        }

        private FactionDef GetDoppelgangerFactionDef(XenotypeDef xenotype)
        {
            string defName = "SI_DoppelgangerFaction";

            if (ModsConfig.BiotechActive && xenotype != null)
            {
                switch (xenotype.defName)
                {
                    case "Hussar":
                        defName = "SI_DoppelgangerFaction_Hussar";
                        break;

                    case "Pigskin":
                        defName = "SI_DoppelgangerFaction_Pigskin";
                        break;

                    case "Impid":
                        defName = "SI_DoppelgangerFaction_Impid";
                        break;

                    case "Yttakin":
                        defName = "SI_DoppelgangerFaction_Yttakin";
                        break;

                    case "Waster":
                        defName = "SI_DoppelgangerFaction_Waster";
                        break;

                    case "Dirtmole":
                        defName = "SI_DoppelgangerFaction_Dirtmole";
                        break;

                    case "Neanderthal":
                        defName = "SI_DoppelgangerFaction_Neanderthal";
                        break;

                    case "Starjack":
                        defName = "SI_DoppelgangerFaction_Starjack";
                        break;

                    case "Baseliner":
                        defName = "SI_DoppelgangerFaction_Baseliner";
                        break;
                }
            }

            FactionDef def = DefDatabase<FactionDef>.GetNamedSilentFail(defName);
            if (def != null)
                return def;

            Log.Warning("[Signal Interceptor] Doppelganger faction def not found: " + defName +
                        ". Falling back to SI_DoppelgangerFaction.");

            def = DefDatabase<FactionDef>.GetNamedSilentFail("SI_DoppelgangerFaction");
            if (def != null)
                return def;

            Log.Warning("[Signal Interceptor] SI_DoppelgangerFaction FactionDef not found. Falling back to Pirate.");
            return FactionDefOf.Pirate;
        }

        private Faction CreateDoppelgangerFactionForMap(XenotypeDef chosenXenotype)
        {
            FactionDef wantedDef = GetDoppelgangerFactionDef(chosenXenotype);

            FactionDef generatorDef = wantedDef;

            if (generatorDef == null || generatorDef.factionNameMaker == null)
            {
                Log.Warning("[Signal Interceptor] Doppelganger faction def has no factionNameMaker. " +
                            "Using Pirate as generator base, then overriding faction.def.");

                generatorDef = FactionDefOf.Pirate;
            }

            Faction faction = null;

            try
            {
                faction = FactionGenerator.NewGeneratedFaction(
                    new FactionGeneratorParms(generatorDef)
                );
            }
            catch (System.Exception ex)
            {
                Log.Error("[Signal Interceptor] Failed to generate doppelganger faction through FactionGenerator. " +
                          "Fallback to Pirate generator. Exception: " + ex);

                faction = FactionGenerator.NewGeneratedFaction(
                    new FactionGeneratorParms(FactionDefOf.Pirate)
                );
            }

            if (faction == null)
            {
                Log.Error("[Signal Interceptor] FactionGenerator returned null for doppelganger faction.");
                return null;
            }

            if (wantedDef != null)
            {
                faction.def = wantedDef;
            }

            faction.temporary = true;
            faction.hidden = false;
            faction.defeated = false;
            faction.Name = GenerateDoppelgangerFactionName();
            faction.leader = null;

            if (!Find.FactionManager.AllFactions.Contains(faction))
            {
                Find.FactionManager.Add(faction);
            }

            CleanupDoppelgangerSettlements();

            faction.TryMakeInitialRelationsWith(Faction.OfPlayer);

            faction.SetRelationDirect(
                Faction.OfPlayer,
                FactionRelationKind.Hostile,
                canSendHostilityLetter: false
            );

            foreach (Faction other in Find.FactionManager.AllFactions)
            {
                if (other == null || other == faction || other == Faction.OfPlayer)
                    continue;

                faction.TryMakeInitialRelationsWith(other);

                FactionRelation rel = faction.RelationWith(other, allowNull: true);
                if (rel != null)
                {
                    rel.baseGoodwill = 0;
                    rel.kind = FactionRelationKind.Neutral;
                }

                FactionRelation otherRel = other.RelationWith(faction, allowNull: true);
                if (otherRel != null)
                {
                    otherRel.baseGoodwill = 0;
                    otherRel.kind = FactionRelationKind.Neutral;
                }
            }

            Log.Message("[Signal Interceptor] Created doppelganger map faction: " +
                        faction.Name +
                        " | def=" + faction.def.defName +
                        " | label=" + faction.def.LabelCap +
                        " | generatorDef=" + generatorDef.defName +
                        " | chosenXenotype=" + (chosenXenotype?.defName ?? "none") +
                        " | temporary=" + faction.temporary +
                        " | hidden=" + faction.hidden +
                        " | HiddenProperty=" + faction.Hidden +
                        " | defeated=" + faction.defeated +
                        " | leader=" + (faction.leader?.LabelShort ?? "null") +
                        " | loadID=" + faction.loadID);

            return faction;
        }

        private XenotypeDef ChooseDoppelgangerXenotype()
        {
            if (!ModsConfig.BiotechActive)
                return null;

            List<string> xenotypeDefNames = new List<string>
            {
                "Baseliner",
                "Hussar",
                "Pigskin",
                "Impid",
                "Yttakin",
                "Waster",
                "Dirtmole",
                "Neanderthal"
            };

            if (ModsConfig.IsActive("Ludeon.RimWorld.Odyssey"))
            {
                xenotypeDefNames.Add("Starjack");
            }

            List<XenotypeDef> xenotypes = xenotypeDefNames
                .Select(defName => DefDatabase<XenotypeDef>.GetNamedSilentFail(defName))
                .Where(x => x != null)
                .ToList();

            if (xenotypes.Count == 0)
                return null;

            return xenotypes.RandomElement();
        }

        private void ApplyDoppelgangerTemplateXenotype(Pawn pawn, XenotypeDef xenotype)
        {
            if (!ModsConfig.BiotechActive)
                return;

            if (pawn == null || pawn.genes == null || xenotype == null)
                return;

            try
            {
                pawn.genes.SetXenotype(xenotype);
                pawn.Drawer?.renderer?.SetAllGraphicsDirty();

                Log.Message("[Signal Interceptor] Applied doppelganger template xenotype: " +
                            xenotype.defName +
                            " | pawn=" + pawn.LabelShort +
                            " | displayed=" + pawn.genes.XenotypeLabelCap);
            }
            catch (System.Exception ex)
            {
                Log.Warning("[Signal Interceptor] Failed to apply doppelganger template xenotype " +
                            xenotype.defName +
                            ": " + ex);
            }
        }

        private void CopyAppearance(Pawn clone, Name name, Color hairColor,
        HairDef hair, BeardDef beard, HeadTypeDef head,
        BodyTypeDef body, Color skinColor, Gender gender)
        {
            clone.Name = name;
            clone.gender = gender;

            if (clone.story != null)
            {
                clone.story.HairColor = hairColor;
                clone.story.hairDef = hair;
                clone.story.headType = head;
                clone.story.bodyType = body;
                clone.story.SkinColorBase = skinColor;
            }

            if (clone.style != null && beard != null)
            {
                clone.style.beardDef = beard;
            }

            clone.Drawer?.renderer?.SetAllGraphicsDirty();
        }

        private void CopyTemplate(Pawn source, Pawn target)
        {
            // 1. Трейты
            if (source.story?.traits != null && target.story?.traits != null)
            {
                List<Trait> toRemove = target.story.traits.allTraits.ToList();

                foreach (Trait t in toRemove)
                {
                    target.story.traits.RemoveTrait(t);
                }

                foreach (Trait t in source.story.traits.allTraits)
                {
                    target.story.traits.GainTrait(new Trait(t.def, t.Degree));
                }
            }

            // 2. Предыстории
            if (target.story != null && source.story != null)
            {
                target.story.Childhood = source.story.Childhood;
                target.story.Adulthood = source.story.Adulthood;
            }

            // 3. Возраст
            if (source.ageTracker != null && target.ageTracker != null)
            {
                target.ageTracker.AgeBiologicalTicks = source.ageTracker.AgeBiologicalTicks;
                target.ageTracker.AgeChronologicalTicks = source.ageTracker.AgeChronologicalTicks;
            }

            // 4. Навыки
            if (source.skills != null && target.skills != null)
            {
                foreach (SkillRecord sourceSkill in source.skills.skills)
                {
                    SkillRecord targetSkill = target.skills.GetSkill(sourceSkill.def);
                    if (targetSkill == null)
                        continue;

                    targetSkill.Level = sourceSkill.Level;
                    targetSkill.xpSinceLastLevel = sourceSkill.xpSinceLastLevel;
                    targetSkill.xpSinceMidnight = sourceSkill.xpSinceMidnight;
                }

                foreach (SkillRecord sourceSkill in source.skills.skills)
                {
                    SkillRecord targetSkill = target.skills.GetSkill(sourceSkill.def);
                    if (targetSkill == null)
                        continue;

                    targetSkill.passion = sourceSkill.passion;
                }
            }

            // 5. Гены / ксенотип
            if (ModsConfig.BiotechActive && source.genes != null && target.genes != null)
            {
                try
                {
                    /*
                     * Сначала полностью чистим гены цели.
                     * Важно делать ToList(), потому что коллекция меняется во время удаления.
                     */
                    List<Gene> targetGenes = target.genes.GenesListForReading.ToList();

                    foreach (Gene gene in targetGenes)
                    {
                        target.genes.RemoveGene(gene);
                    }

                    /*
                     * Если у источника НЕ кастомный набор, а нормальный XenotypeDef,
                     * сначала ставим тот же XenotypeDef.
                     *
                     * Но затем всё равно проверяем дополнительные гены.
                     */
                    if (source.genes.Xenotype != null)
                    {
                        target.genes.SetXenotype(source.genes.Xenotype);
                    }

                    /*
                     * После SetXenotype у цели могли появиться стандартные гены этого ксенотипа.
                     * Чтобы избежать дублей, добавляем только те гены источника,
                     * которых ещё нет у цели.
                     */
                    foreach (Gene sourceGene in source.genes.GenesListForReading)
                    {
                        if (sourceGene?.def == null)
                            continue;

                        bool alreadyHas = target.genes.GenesListForReading
                            .Any(g => g.def == sourceGene.def);

                        if (alreadyHas)
                            continue;

                        bool xenogene = source.genes.Xenogenes.Contains(sourceGene);
                        target.genes.AddGene(sourceGene.def, xenogene);
                    }

                    /*
                     * Если источник кастомный/пересобранный, SetXenotype может не хватить.
                     * Поэтому дополнительно копируем имя и иконку через reflection.
                     * Это нужно именно для отображения в UI.
                     */
                    CopyGeneTrackerDisplayData(source, target);

                    target.Drawer?.renderer?.SetAllGraphicsDirty();

                    Log.Message("[Signal Interceptor] Copied genes from template to clone. " +
                                "Source xenotype: " + source.genes.XenotypeLabelCap +
                                " | Target xenotype: " + target.genes.XenotypeLabelCap +
                                " | Source genes: " + source.genes.GenesListForReading.Count +
                                " | Target genes: " + target.genes.GenesListForReading.Count);
                }
                catch (System.Exception ex)
                {
                    Log.Warning("[Signal Interceptor] Failed to copy genes from doppelganger template: " + ex);
                }
            }

            // 6. Hediff'ы без ран и отсутствующих частей
            if (source.health?.hediffSet != null && target.health?.hediffSet != null)
            {
                List<Hediff> existingHediffs = target.health.hediffSet.hediffs.ToList();

                foreach (Hediff h in existingHediffs)
                {
                    target.health.RemoveHediff(h);
                }

                foreach (Hediff h in source.health.hediffSet.hediffs)
                {
                    if (h == null || h.def == null)
                        continue;

                    if (h is Hediff_Injury)
                        continue;

                    if (h is Hediff_MissingPart)
                        continue;

                    /*
                     * Не копируем явно плохие смертельные состояния.
                     * Это грубый фильтр, но лучше, чем клонировать рак/инфекции/смертельные болезни.
                     */
                    if (h.def.isBad && h.def.initialSeverity > 0 && h.def.lethalSeverity > 0)
                        continue;

                    Hediff copy = HediffMaker.MakeHediff(h.def, target, h.Part);
                    copy.Severity = h.Severity;
                    target.health.AddHediff(copy);
                }
            }
        }

        private void ApplyCloneFragility(Pawn pawn)
        {
            HediffDef instability = DefDatabase<HediffDef>.GetNamedSilentFail("SI_CloneInstability");
            if (instability != null)
            {
                Hediff hediff = HediffMaker.MakeHediff(instability, pawn);
                hediff.Severity = 1.0f;
                pawn.health.AddHediff(hediff);
            }

            HediffDef bleedRate = DefDatabase<HediffDef>.GetNamedSilentFail("SI_CloneBleedRate");
            if (bleedRate != null)
            {
                Hediff bleed = HediffMaker.MakeHediff(bleedRate, pawn);
                bleed.Severity = 1.0f;
                pawn.health.AddHediff(bleed);
            }

            AddDeathAcidifier(pawn);
        }

        private void AddDeathAcidifier(Pawn pawn)
        {
            HediffDef acidifier = DefDatabase<HediffDef>.GetNamedSilentFail("DeathAcidifier");
            if (acidifier == null)
                return;

            BodyPartRecord torso = pawn.RaceProps.body.AllParts
                .FirstOrDefault(p => p.def.defName == "Torso");

            if (torso == null)
                return;

            bool alreadyHas = pawn.health.hediffSet.hediffs
                .Any(h => h.def == acidifier && h.Part == torso);

            if (!alreadyHas)
            {
                pawn.health.AddHediff(acidifier, torso);
            }
        }

        private void CopyGeneTrackerDisplayData(Pawn source, Pawn target)
        {
            if (!ModsConfig.BiotechActive)
                return;

            if (source?.genes == null || target?.genes == null)
                return;

            try
            {
                System.Type trackerType = source.genes.GetType();
                System.Reflection.BindingFlags flags =
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic;

                string[] fieldNames =
                {
            "xenotypeName",
            "iconDef",
            "xenotype"
        };

                foreach (string fieldName in fieldNames)
                {
                    System.Reflection.FieldInfo field = trackerType.GetField(fieldName, flags);
                    if (field == null)
                        continue;

                    object value = field.GetValue(source.genes);
                    field.SetValue(target.genes, value);
                }
            }
            catch (System.Exception ex)
            {
                Log.Warning("[Signal Interceptor] Failed to copy gene tracker display data: " + ex);
            }
        }

        private string GenerateDoppelgangerFactionName()
        {
            List<string> adjectives = GetTranslatedStringList(
                "SI_Doppelganger_NameAdjectives",
                new List<string>
                {
            "Безликие",
            "Зеркальные",
            "Искажённые",
            "Отражённые",
            "Невозможные",
            "Подменённые"
                }
            );

            List<string> nouns = GetTranslatedStringList(
                "SI_Doppelganger_NameNouns",
                new List<string>
                {
            "Отголоски",
            "Тени",
            "Слепки",
            "Лики",
            "Миражи",
            "Отражения"
                }
            );

            string adjective = adjectives.RandomElement();
            string noun = nouns.RandomElement();

            return adjective + " " + noun;
        }

        private List<string> GetTranslatedStringList(string key, List<string> fallback)
        {
            if (key.CanTranslate())
            {
                string raw = key.Translate().ToString();

                List<string> result = raw
                    .Split(new char[] { '|' }, System.StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.Trim())
                    .Where(s => !string.IsNullOrEmpty(s))
                    .ToList();

                if (result.Count > 0)
                    return result;
            }

            return fallback;
        }

        private void DeactivateDoppelgangerFaction(Faction faction)
        {
            if (faction == null)
                return;

            if (!IsDoppelgangerFactionDef(faction.def))
                return;

            faction.hidden = true;
            faction.temporary = true;
            faction.defeated = true;
            faction.leader = null;

            Log.Message("[Signal Interceptor] Deactivated doppelganger faction: " +
                        faction.Name +
                        " | def=" + faction.def.defName +
                        " | loadID=" + faction.loadID);
        }

        private bool IsDoppelgangerFactionDef(FactionDef def)
        {
            return def != null
                && def.defName != null
                && def.defName.StartsWith("SI_DoppelgangerFaction");
        }

        private void GiveCloneRandomWeapon(Pawn pawn, float threatPoints)
        {
            if (pawn.equipment == null) return;

            pawn.equipment.DestroyAllEquipment();

            List<string> weaponPool;

            if (threatPoints >= 1800f)
            {
                weaponPool = new List<string>
                {
                    "Gun_ChargeRifle", "Gun_ChargeLance", "MeleeWeapon_MonoSword",
                    "MeleeWeapon_Zeushammer", "Gun_AssaultRifle", "Gun_SniperRifle", "Gun_Minigun"
                };
            }
            else if (threatPoints >= 1200f)
            {
                weaponPool = new List<string>
                {
                    "Gun_AssaultRifle", "Gun_SniperRifle", "Gun_ChainShotgun",
                    "Gun_LMG", "Gun_ChargeRifle", "MeleeWeapon_LongSword", "MeleeWeapon_Mace"
                };
            }
            else
            {
                weaponPool = new List<string>
                {
                    "Gun_BoltActionRifle", "Gun_PumpShotgun", "Gun_AssaultRifle",
                    "Gun_MachinePistol", "Gun_Revolver", "MeleeWeapon_LongSword", "MeleeWeapon_Gladius"
                };
            }

            weaponPool.Shuffle();
            foreach (string defName in weaponPool)
            {
                ThingDef weaponDef = DefDatabase<ThingDef>.GetNamedSilentFail(defName);
                if (weaponDef == null) continue;

                ThingDef stuff = weaponDef.MadeFromStuff ? GenStuff.DefaultStuffFor(weaponDef) : null;
                Thing weapon = ThingMaker.MakeThing(weaponDef, stuff);

                QualityCategory quality;
                if (threatPoints >= 1800f) quality = QualityCategory.Excellent;
                else if (threatPoints >= 1200f) quality = QualityCategory.Good;
                else quality = QualityCategory.Normal;

                weapon.TryGetComp<CompQuality>()?.SetQuality(quality, ArtGenerationContext.Outsider);

                if (weapon is ThingWithComps twc)
                {
                    pawn.equipment.AddEquipment(twc);

                    CompBiocodable biocode = twc.TryGetComp<CompBiocodable>();
                    if (biocode != null && !biocode.Biocoded)
                    {
                        biocode.CodeFor(pawn);
                    }

                    return;
                }
            }
        }

        private void GiveCloneArmor(Pawn pawn, float threatPoints)
        {
            if (pawn.apparel == null) return;

            pawn.apparel.DestroyAll();

            List<string> armorSet;

            if (threatPoints >= 1800f)
            {
                armorSet = new List<string> { "Apparel_PowerArmor", "Apparel_PowerArmorHelmet" };
            }
            else if (threatPoints >= 1200f)
            {
                armorSet = new List<string> { "Apparel_ArmorMarineHelmet", "Apparel_FlakVest", "Apparel_FlakPants", "Apparel_Duster" };
            }
            else
            {
                armorSet = new List<string> { "Apparel_FlakVest", "Apparel_FlakPants", "Apparel_SimpleHelmet", "Apparel_Parka" };
            }

            foreach (string defName in armorSet)
            {
                ThingDef armorDef = DefDatabase<ThingDef>.GetNamedSilentFail(defName);
                if (armorDef == null) continue;

                ThingDef stuff = armorDef.MadeFromStuff ? GenStuff.DefaultStuffFor(armorDef) : null;
                Thing armor = ThingMaker.MakeThing(armorDef, stuff);

                QualityCategory quality;
                if (threatPoints >= 1800f) quality = QualityCategory.Excellent;
                else if (threatPoints >= 1200f) quality = QualityCategory.Good;
                else quality = QualityCategory.Normal;

                armor.TryGetComp<CompQuality>()?.SetQuality(quality, ArtGenerationContext.Outsider);

                if (armor is Apparel ap)
                {
                    pawn.apparel.Wear(ap, dropReplacedApparel: false);
                }
            }
        }

        private void AddDoppelgangerMark(Pawn pawn)
        {
            HediffDef mark = DefDatabase<HediffDef>.GetNamedSilentFail("SI_DoppelgangerMark");
            if (mark == null)
            {
                Log.Error("[Signal Interceptor] SI_DoppelgangerMark HediffDef not found.");
                return;
            }

            if (pawn.health?.hediffSet == null)
                return;

            if (pawn.health.hediffSet.HasHediff(mark))
                return;

            pawn.health.AddHediff(mark);
        }

        public void TrySpawnVIPOnMapGenerated(Map map)
        {
            for (int i = trackedVIPSites.Count - 1; i >= 0; i--)
            {
                VIPSiteData data = trackedVIPSites[i];

                if (data.site == null || !data.site.Spawned)
                    continue;

                if (data.vipSpawned)
                    continue;

                if (data.site.Map != map)
                    continue;

                try
                {
                    SpawnVIP(data);
                    data.vipSpawned = true;

                    Log.Message("[Signal Interceptor] VIP spawned during map generation for subtype: " + data.subtype);
                }
                catch (System.Exception ex)
                {
                    Log.Error("[Signal Interceptor] Exception while spawning VIP during map generation. " +
                              "Subtype: " + data.subtype +
                              " | Site: " + (data.site?.LabelCap ?? "null") +
                              " | Exception: " + ex);

                    if (data.subtype == VIPSubtype.DoppelgangerVIP)
                    {
                        DeactivateDoppelgangerFaction(data.enemyFaction);
                        data.enemyFaction = null;
                    }

                    /*
                     * Ставим true, чтобы ошибка не повторялась каждый тик бесконечно.
                     * Иначе будет тот самый спам Duplicate stacktrace / Random state stack.
                     */
                    data.vipSpawned = true;
                    data.rewardGiven = true;
                }

                break;
            }
        }

        private void SpawnCampProps(Map map, IntVec3 center)
        {
            ThingDef campfireDef = DefDatabase<ThingDef>.GetNamedSilentFail("Campfire");
            if (campfireDef != null)
            {
                IntVec3 fireSpot;
                if (CellFinder.TryFindRandomCellNear(center, map, 5,
                    (IntVec3 c) => c.Standable(map) && !c.Roofed(map) && c.GetFirstThing(map, campfireDef) == null,
                    out fireSpot))
                {
                    Thing campfire = ThingMaker.MakeThing(campfireDef);
                    GenSpawn.Spawn(campfire, fireSpot, map);

                    CompRefuelable fuel = campfire.TryGetComp<CompRefuelable>();
                    fuel?.Refuel(fuel.Props.fuelCapacity);
                }
            }

            ThingDef mealDef = DefDatabase<ThingDef>.GetNamedSilentFail("MealSurvivalPack");
            if (mealDef != null)
            {
                int packs = Rand.RangeInclusive(3, 6);
                for (int i = 0; i < packs; i++)
                {
                    IntVec3 mealSpot;
                    if (CellFinder.TryFindRandomCellNear(center, map, 4,
                        (IntVec3 c) => c.Standable(map) && c.GetFirstItem(map) == null,
                        out mealSpot))
                    {
                        Thing meal = ThingMaker.MakeThing(mealDef);
                        meal.stackCount = Rand.RangeInclusive(2, 5);
                        GenSpawn.Spawn(meal, mealSpot, map);
                    }
                }
            }
        }

        private void SpawnLoot(Map map, float threatPoints)
        {
            int tier = GetTier(threatPoints);
            List<Thing> loot = GenerateLoot(tier);
            IntVec3 lootSpot = FindLootSpot(map);

            foreach (Thing item in loot)
            {
                int totalCount = item.stackCount;
                while (totalCount > 0)
                {
                    int spawnCount = System.Math.Min(totalCount, item.def.stackLimit);
                    totalCount -= spawnCount;

                    Thing spawnItem;
                    if (spawnCount == item.stackCount)
                    {
                        spawnItem = item;
                    }
                    else
                    {
                        spawnItem = ThingMaker.MakeThing(item.def, item.Stuff);
                    }
                    spawnItem.stackCount = spawnCount;

                    IntVec3 spawnSpot = lootSpot;
                    CellFinder.TryFindRandomCellNear(lootSpot, map, 5,
                        (IntVec3 c) => c.Standable(map) && c.GetFirstItem(map) == null,
                        out spawnSpot);
                    GenSpawn.Spawn(spawnItem, spawnSpot, map);
                }
            }
        }

        private IntVec3 FindLootSpot(Map map)
        {
            IntVec3 spot = map.Center;

            List<Thing> buildings = map.listerThings.AllThings
                .Where(t => t.def.building != null && t.Faction != null && t.Faction != Faction.OfPlayer)
                .ToList();

            if (buildings.Any())
            {
                Thing building = buildings.RandomElement();
                for (int i = 0; i < 50; i++)
                {
                    IntVec3 candidate = building.Position + GenRadial.RadialPattern[i];
                    if (candidate.InBounds(map) && candidate.Standable(map) && candidate.GetFirstItem(map) == null)
                    {
                        return candidate;
                    }
                }
            }

            CellFinder.TryFindRandomCellNear(map.Center, map, 15,
                (IntVec3 c) => c.Standable(map) && c.GetFirstItem(map) == null,
                out spot);
            return spot;
        }

        public int GetTier(float points)
        {
            if (points >= 2200f) return 6;
            if (points >= 1700f) return 5;
            if (points >= 1200f) return 4;
            if (points >= 800f) return 3;
            if (points >= 450f) return 2;
            return 1;
        }

        private List<Thing> GenerateLoot(int tier)
        {
            List<Thing> loot = new List<Thing>();

            Thing silver = ThingMaker.MakeThing(ThingDefOf.Silver);
            switch (tier)
            {
                case 1: silver.stackCount = Rand.RangeInclusive(100, 250); break;
                case 2: silver.stackCount = Rand.RangeInclusive(250, 450); break;
                case 3: silver.stackCount = Rand.RangeInclusive(450, 700); break;
                case 4: silver.stackCount = Rand.RangeInclusive(700, 1000); break;
                case 5: silver.stackCount = Rand.RangeInclusive(1000, 1500); break;
                case 6: silver.stackCount = Rand.RangeInclusive(1500, 2500); break;
                default: silver.stackCount = 200; break;
            }
            loot.Add(silver);

            Thing gold = ThingMaker.MakeThing(ThingDefOf.Gold);
            switch (tier)
            {
                case 1: gold.stackCount = Rand.RangeInclusive(3, 8); break;
                case 2: gold.stackCount = Rand.RangeInclusive(8, 18); break;
                case 3: gold.stackCount = Rand.RangeInclusive(18, 35); break;
                case 4: gold.stackCount = Rand.RangeInclusive(35, 55); break;
                case 5: gold.stackCount = Rand.RangeInclusive(55, 80); break;
                case 6: gold.stackCount = Rand.RangeInclusive(80, 120); break;
                default: gold.stackCount = 5; break;
            }
            loot.Add(gold);

            Thing components = ThingMaker.MakeThing(ThingDefOf.ComponentIndustrial);
            switch (tier)
            {
                case 1: components.stackCount = Rand.RangeInclusive(2, 5); break;
                case 2: components.stackCount = Rand.RangeInclusive(5, 10); break;
                case 3: components.stackCount = Rand.RangeInclusive(10, 18); break;
                case 4: components.stackCount = Rand.RangeInclusive(18, 28); break;
                case 5: components.stackCount = Rand.RangeInclusive(28, 40); break;
                case 6: components.stackCount = Rand.RangeInclusive(40, 55); break;
                default: components.stackCount = 3; break;
            }
            loot.Add(components);

            Thing meds = ThingMaker.MakeThing(ThingDefOf.MedicineIndustrial);
            switch (tier)
            {
                case 1: meds.stackCount = Rand.RangeInclusive(2, 5); break;
                case 2: meds.stackCount = Rand.RangeInclusive(5, 10); break;
                case 3: meds.stackCount = Rand.RangeInclusive(10, 18); break;
                case 4: meds.stackCount = Rand.RangeInclusive(18, 25); break;
                case 5: meds.stackCount = Rand.RangeInclusive(20, 25); break;
                case 6: meds.stackCount = Rand.RangeInclusive(25, 25); break;
                default: meds.stackCount = 3; break;
            }
            loot.Add(meds);

            if (tier >= 2)
            {
                Thing plasteel = ThingMaker.MakeThing(ThingDefOf.Plasteel);
                switch (tier)
                {
                    case 2: plasteel.stackCount = Rand.RangeInclusive(10, 20); break;
                    case 3: plasteel.stackCount = Rand.RangeInclusive(20, 40); break;
                    case 4: plasteel.stackCount = Rand.RangeInclusive(40, 65); break;
                    case 5: plasteel.stackCount = Rand.RangeInclusive(65, 90); break;
                    case 6: plasteel.stackCount = Rand.RangeInclusive(90, 130); break;
                    default: plasteel.stackCount = 15; break;
                }
                loot.Add(plasteel);
            }

            if (tier >= 3 && Rand.Chance(0.3f + (tier - 3) * 0.15f))
            {
                Thing advComp = ThingMaker.MakeThing(ThingDefOf.ComponentSpacer);
                switch (tier)
                {
                    case 3: advComp.stackCount = Rand.RangeInclusive(1, 2); break;
                    case 4: advComp.stackCount = Rand.RangeInclusive(2, 4); break;
                    case 5: advComp.stackCount = Rand.RangeInclusive(4, 7); break;
                    case 6: advComp.stackCount = Rand.RangeInclusive(7, 12); break;
                    default: advComp.stackCount = 1; break;
                }
                loot.Add(advComp);
            }

            if (tier >= 4 && Rand.Chance(0.3f + (tier - 4) * 0.15f))
            {
                Thing ultMeds = ThingMaker.MakeThing(ThingDefOf.MedicineUltratech);
                switch (tier)
                {
                    case 4: ultMeds.stackCount = Rand.RangeInclusive(1, 3); break;
                    case 5: ultMeds.stackCount = Rand.RangeInclusive(3, 6); break;
                    case 6: ultMeds.stackCount = Rand.RangeInclusive(6, 10); break;
                    default: ultMeds.stackCount = 1; break;
                }
                loot.Add(ultMeds);
            }

            if (tier >= 4 && Rand.Chance(0.2f + (tier - 4) * 0.15f))
            {
                IEnumerable<ThingDef> neurotrainers = DefDatabase<ThingDef>.AllDefs
                    .Where(d => d.defName.StartsWith("Neurotrainer"));
                if (neurotrainers.Any())
                {
                    loot.Add(ThingMaker.MakeThing(neurotrainers.RandomElement()));
                    if (tier >= 6 && Rand.Chance(0.3f))
                    {
                        loot.Add(ThingMaker.MakeThing(neurotrainers.RandomElement()));
                    }
                }
            }

            if (tier >= 4 && Rand.Chance(0.1f + (tier - 4) * 0.1f))
            {
                ThingDef healSerum = DefDatabase<ThingDef>.GetNamedSilentFail("MechSerumHealer");
                if (healSerum != null)
                {
                    loot.Add(ThingMaker.MakeThing(healSerum));
                }
            }

            if (tier >= 5 && Rand.Chance(0.05f + (tier - 5) * 0.08f))
            {
                ThingDef resSerum = DefDatabase<ThingDef>.GetNamedSilentFail("MechSerumResurrector");
                if (resSerum != null)
                {
                    loot.Add(ThingMaker.MakeThing(resSerum));
                }
            }

            if (tier >= 4 && ModsConfig.IsActive("Ludeon.RimWorld.Odyssey"))
            {
                float weaponChance = 0.1f + (tier - 4) * 0.15f;
                if (Rand.Chance(weaponChance))
                {
                    Thing uniqueWeapon = SignalInterceptorUtility.TryGenerateUniqueWeapon();
                    if (uniqueWeapon != null)
                    {
                        loot.Add(uniqueWeapon);
                    }
                    if (tier >= 6 && Rand.Chance(0.25f))
                    {
                        Thing secondWeapon = SignalInterceptorUtility.TryGenerateUniqueWeapon();
                        if (secondWeapon != null)
                        {
                            loot.Add(secondWeapon);
                        }
                    }
                }
            }

            if (tier >= 6 && Rand.Chance(0.08f))
            {
                loot.Add(ThingMaker.MakeThing(ThingDefOf.AIPersonaCore));
            }

            return loot;
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref trackedSites, "trackedSites", LookMode.Deep);
            if (trackedSites == null)
                trackedSites = new List<StashSiteData>();

            Scribe_Collections.Look(ref pendingSlaveDeliveries, "pendingSlaveDeliveries", LookMode.Deep);
            if (pendingSlaveDeliveries == null)
                pendingSlaveDeliveries = new List<PendingSlaveDelivery>();

            Scribe_Collections.Look(ref pendingRaids, "pendingRaids", LookMode.Deep);
            if (pendingRaids == null)
                pendingRaids = new List<PendingRaid>();

            Scribe_Collections.Look(ref trackedVIPSites, "trackedVIPSites", LookMode.Deep);
            if (trackedVIPSites == null)
                trackedVIPSites = new List<VIPSiteData>();
            Scribe_Values.Look(ref doppelgangerFightActive, "doppelgangerFightActive", false);
            Scribe_Values.Look(ref doppelgangerFightStartTick, "doppelgangerFightStartTick", -1);
        }
    }

    public class StashSiteData : IExposable
    {
        public Site site;
        public float threatPoints;
        public bool lootSpawned;
        public Faction faction;
        public int tier;

        public void ExposeData()
        {
            Scribe_References.Look(ref site, "site");
            Scribe_Values.Look(ref threatPoints, "threatPoints", 0f);
            Scribe_Values.Look(ref lootSpawned, "lootSpawned", false);
            Scribe_References.Look(ref faction, "faction");
            Scribe_Values.Look(ref tier, "tier", 0);
        }
    }

    public class VIPSiteData : IExposable
    {
        public Site site;
        public float threatPoints;
        public int expireTick = -1;

        public Faction faction;
        public Faction enemyFaction;

        public VIPSubtype subtype;
        public bool vipSpawned;
        public bool rewardGiven;

        public IntVec3 signalCampCenter = IntVec3.Invalid;

        public void ExposeData()
        {
            Scribe_References.Look(ref site, "site");
            Scribe_Values.Look(ref threatPoints, "threatPoints", 0f);
            Scribe_References.Look(ref faction, "faction");
            Scribe_References.Look(ref enemyFaction, "enemyFaction");
            Scribe_Values.Look(ref subtype, "subtype", VIPSubtype.ShuttleVIP);
            Scribe_Values.Look(ref vipSpawned, "vipSpawned", false);
            Scribe_Values.Look(ref rewardGiven, "rewardGiven", false);
            Scribe_Values.Look(ref signalCampCenter, "signalCampCenter", IntVec3.Invalid);
            Scribe_Values.Look(ref expireTick, "expireTick", -1);
        }
    }

    public class PendingSlaveDelivery : IExposable
    {
        public int mapId;
        public int deliveryTick;

        public void ExposeData()
        {
            Scribe_Values.Look(ref mapId, "mapId", 0);
            Scribe_Values.Look(ref deliveryTick, "deliveryTick", 0);
        }
    }

    public class PendingRaid : IExposable
    {
        public int mapId;
        public Faction faction;
        public float points;
        public int fireTick;

        public void ExposeData()
        {
            Scribe_Values.Look(ref mapId, "mapId", 0);
            Scribe_References.Look(ref faction, "faction");
            Scribe_Values.Look(ref points, "points", 0f);
            Scribe_Values.Look(ref fireTick, "fireTick", 0);
        }
    }
}
