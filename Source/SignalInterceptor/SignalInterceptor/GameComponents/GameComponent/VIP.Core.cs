using RimWorld;
using RimWorld.Planet;
using System.Collections.Generic;
using System.Linq;
using Verse;

namespace SignalInterceptor
{
    public partial class SignalInterceptorGameComponent
    {
        public void TrackVIPSite(Site site, float threatPoints, Faction faction, VIPSubtype subtype, int timeoutTicks)
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
                expireTick = timeoutTicks > 0 ? Find.TickManager.TicksGame + timeoutTicks : -1
            });

            Log.Message("[Signal Interceptor] Tracking VIP site. " +
                        "Subtype: " + subtype +
                        " | Context faction: " + (faction?.Name ?? "null") +
                        " | Site faction: " + (site?.Faction?.Name ?? "null") +
                        " | Enemy faction: null" +
                        " | Threat: " + threatPoints +
                        " | Expire tick: " + (timeoutTicks > 0 ? (Find.TickManager.TicksGame + timeoutTicks).ToString() : "handled by quest timeout"));
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
                    SpawnPsycasterVIP(map, data);
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

            Quest linkedQuest = FindLinkedVIPQuest(data);
            LookTargets lookTargets = data.site != null ? new LookTargets(data.site) : LookTargets.Invalid;

            switch (data.subtype)
            {
                case VIPSubtype.DoppelgangerVIP:
                    Find.LetterStack.ReceiveLetter(
                        "SI_VIP_CompletedTitle".Translate(),
                        "SI_VIP_CompletedText".Translate(),
                        LetterDefOf.PositiveEvent,
                        lookTargets,
                        null,
                        linkedQuest
                    );

                    GiveVIPVictoryReward();
                    DeactivateDoppelgangerFaction(data.enemyFaction);
                    data.enemyFaction = null;
                    break;

                case VIPSubtype.MechanitorSignalVIP:
                    Find.LetterStack.ReceiveLetter(
                        "SI_VIP_CompletedTitle".Translate(),
                        "SI_MechanitorSignal_CompletedText".Translate(),
                        LetterDefOf.PositiveEvent,
                        lookTargets,
                        null,
                        linkedQuest
                    );

                    DeactivateRogueMechanitorFaction(data.enemyFaction);
                    data.enemyFaction = null;
                    break;

                case VIPSubtype.PsycasterVIP:
                    Find.LetterStack.ReceiveLetter(
                        "SI_VIP_CompletedTitle".Translate(),
                        "SI_PsycasterVIP_ExtractedText".Translate(data.psycasterPawn?.LabelShort ?? "SI_PsycasterUnknown".Translate()),
                        LetterDefOf.PositiveEvent,
                        lookTargets,
                        null,
                        linkedQuest
                    );
                    break;

                default:
                    Find.LetterStack.ReceiveLetter(
                        "SI_VIP_CompletedTitle".Translate(),
                        "SI_VIP_CompletedText".Translate(),
                        LetterDefOf.PositiveEvent,
                        lookTargets,
                        null,
                        linkedQuest
                    );
                    break;
            }

            SendVIPQuestSignal(data, "SI_VIPSucceeded");

            Log.Message("[Signal Interceptor] VIP quest completed. Subtype=" + data.subtype);
        }

        private void FailVIPQuest(VIPSiteData data, string reasonKey)
        {
            if (data == null || data.rewardGiven)
            {
                return;
            }

            data.rewardGiven = true;

            Quest linkedQuest = FindLinkedVIPQuest(data);
            string questName = linkedQuest != null && !linkedQuest.name.NullOrEmpty()
                ? linkedQuest.name
                : (data.site?.LabelCap.ToString() ?? "SI_VIP_UnknownQuest".Translate().ToString());

            string title = "SI_VIP_FailedTitle".Translate();
            string text = "SI_VIP_FailedQuestText".Translate(questName);

            if (!reasonKey.NullOrEmpty() && reasonKey.CanTranslate())
            {
                text += "\n\n" + reasonKey.Translate();
            }

            LookTargets lookTargets = data.site != null ? new LookTargets(data.site) : LookTargets.Invalid;

            Find.LetterStack.ReceiveLetter(
                title,
                text,
                LetterDefOf.NegativeEvent,
                lookTargets,
                null,
                linkedQuest
            );

            SendVIPQuestSignal(data, "SI_VIPFailed");

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

            Log.Message("[Signal Interceptor] VIP quest failed. Subtype=" + data.subtype + " | Reason=" + reasonKey);
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

        private void TickVIPSites()
        {
            for (int i = trackedVIPSites.Count - 1; i >= 0; i--)
            {
                VIPSiteData data = trackedVIPSites[i];

                if (data == null)
                {
                    trackedVIPSites.RemoveAt(i);
                    continue;
                }

                if (data.rewardGiven)
                {
                    trackedVIPSites.RemoveAt(i);
                    continue;
                }

                if (data.expireTick > 0 && Find.TickManager.TicksGame >= data.expireTick)
                {
                    if (data.site != null && data.site.HasMap)
                    {
                        data.expireTick = -1;

                        Log.Message("[Signal Interceptor] VIP site timer expired, but map is active. Timeout disabled. Subtype=" +
                                    data.subtype +
                                    " | Site=" +
                                    (data.site?.LabelCap.ToString() ?? "null"));

                        continue;
                    }

                    FailVIPQuest(data, "SI_VIP_FailedExpiredText");

                    if (data.site != null && data.site.Spawned)
                    {
                        Find.WorldObjects.Remove(data.site);
                    }

                    trackedVIPSites.RemoveAt(i);
                    continue;
                }

                if (data.site == null || !data.site.Spawned)
                {
                    /*
                     * Старые сейвы/старые квесты могли потерять world object через quest.WorldObjectTimeout.
                     * Если это PsycasterVIP и пешка ещё существует — даём логике VIP обработать её,
                     * вместо мгновенного фейла по отсутствующему site.
                     */
                    if (data.subtype == VIPSubtype.PsycasterVIP)
                    {
                        if (data.psycasterPawn != null
                            && !data.psycasterPawn.Destroyed
                            && !data.psycasterPawn.Dead)
                        {
                            TickPsycasterVIP(data);
                            continue;
                        }
                    }

                    FailVIPQuest(data, "SI_VIP_FailedExpiredText");
                    trackedVIPSites.RemoveAt(i);
                    continue;
                }

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
                    && data.site != null
                    && !data.site.HasMap
                    && data.enemyFaction != null)
                {
                    FailVIPQuest(data, "SI_MechanitorSignal_FailedFledText");

                    DeactivateRogueMechanitorFaction(data.enemyFaction);
                    data.enemyFaction = null;

                    Log.Message("[Signal Interceptor] Mechanitor signal map left. Quest failed.");

                    trackedVIPSites.RemoveAt(i);
                    continue;
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

                if (data.subtype == VIPSubtype.PsycasterVIP
                    && data.vipSpawned
                    && Find.TickManager.TicksGame % 15 == 0)
                {
                    TickPsycasterVIP(data);
                }

                if (data.rewardGiven)
                {
                    trackedVIPSites.RemoveAt(i);
                    continue;
                }

                if (data.vipSpawned && data.site.HasMap)
                {
                    Map siteMap = data.site.Map;

                    if (data.subtype == VIPSubtype.PsycasterVIP)
                    {
                        continue;
                    }

                    bool enemiesAlive = siteMap.mapPawns.AllPawnsSpawned
                        .Any(p => p.Faction != null && p.Faction.HostileTo(Faction.OfPlayer) && !p.Dead && !p.Downed);

                    if (!enemiesAlive)
                    {
                        CompleteVIPQuest(data);
                        trackedVIPSites.RemoveAt(i);
                        continue;
                    }
                }
            }
        }

        private void TickPsycasterVIP(VIPSiteData data)
        {
            if (data == null || data.subtype != VIPSubtype.PsycasterVIP)
            {
                return;
            }

            Pawn psycaster = data.psycasterPawn;

            if (psycaster == null || psycaster.Destroyed)
            {
                FailVIPQuest(data, "SI_PsycasterVIP_FailedDestroyedText");
                return;
            }

            if (psycaster.Dead)
            {
                FailVIPQuest(data, "SI_PsycasterVIP_FailedDeadText");
                return;
            }

            if (IsPsycasterExtractedByPlayer(psycaster))
            {
                data.psycasterDelivered = true;
                CompleteVIPQuest(data);
                return;
            }

            /*
             * ВАЖНО:
             * Когда колонист несёт поваленного псионика, сам псионик обычно !Spawned.
             * Это не побег и не уничтожение. Нужно ждать, пока его донесут до шаттла/каравана/поселения.
             */
            if (!psycaster.Spawned)
            {
                if (IsPsycasterCarriedByPlayerPawn(psycaster))
                {
                    return;
                }

                if (IsPsycasterInTransportWithPlayerPawn(psycaster))
                {
                    data.psycasterDelivered = true;
                    CompleteVIPQuest(data);
                    return;
                }

                if (IsPsycasterInPlayerCaravan(psycaster))
                {
                    data.psycasterDelivered = true;
                    CompleteVIPQuest(data);
                    return;
                }

                if (psycaster.IsPrisonerOfColony)
                {
                    return;
                }

                FailVIPQuest(data, "SI_PsycasterVIP_FailedFledText");
                return;
            }

            Map siteMap = data.site?.Map;

            /*
             * Если псионик заспавнен уже не на карте сайта:
             * - в поселении игрока и пленник => успех;
             * - иначе это побег/нештатное перемещение.
             */
            if (siteMap == null || psycaster.Map != siteMap)
            {
                if (IsPsycasterDeliveredToPlayerSettlement(psycaster))
                {
                    data.psycasterDelivered = true;
                    CompleteVIPQuest(data);
                    return;
                }

                FailVIPQuest(data, "SI_PsycasterVIP_FailedFledText");
                return;
            }

            TickPsycasterSiteResonance(data);
            TickPsycasterCombatAI(data, psycaster);
        }

        private bool IsPsycasterCarriedByPlayerPawn(Pawn psycaster)
        {
            if (psycaster == null || psycaster.Destroyed || psycaster.Dead)
                return false;

            List<Map> maps = Find.Maps;

            for (int m = 0; m < maps.Count; m++)
            {
                Map map = maps[m];

                if (map == null)
                    continue;

                IReadOnlyList<Pawn> pawns = map.mapPawns.AllPawnsSpawned;

                for (int i = 0; i < pawns.Count; i++)
                {
                    Pawn carrier = pawns[i];

                    if (carrier == null || carrier.Destroyed || carrier.Dead || !carrier.Spawned)
                        continue;

                    if (!IsPlayerExtractionPawn(carrier))
                        continue;

                    if (carrier.carryTracker == null)
                        continue;

                    Thing carriedThing = carrier.carryTracker.CarriedThing;

                    if (carriedThing == psycaster)
                        return true;
                }
            }

            return false;
        }

        private bool IsPsycasterExtractedByPlayer(Pawn psycaster)
        {
            if (psycaster == null || psycaster.Dead || psycaster.Destroyed)
            {
                return false;
            }

            if (IsPsycasterInPlayerCaravan(psycaster))
            {
                return true;
            }

            if (IsPsycasterInTransportWithPlayerPawn(psycaster))
            {
                return true;
            }

            if (IsPsycasterDeliveredToPlayerSettlement(psycaster))
            {
                return true;
            }

            return false;
        }

        private bool IsPsycasterInPlayerCaravan(Pawn psycaster)
        {
            if (psycaster == null)
            {
                return false;
            }

            List<Caravan> caravans = Find.WorldObjects.Caravans;

            for (int i = 0; i < caravans.Count; i++)
            {
                Caravan caravan = caravans[i];

                if (caravan == null)
                {
                    continue;
                }

                if (!caravan.IsPlayerControlled)
                {
                    continue;
                }

                if (caravan.PawnsListForReading != null && caravan.PawnsListForReading.Contains(psycaster))
                {
                    return true;
                }
            }

            return false;
        }

        private bool IsPsycasterInTransportWithPlayerPawn(Pawn psycaster)
        {
            if (psycaster == null)
            {
                return false;
            }

            IThingHolder holder = psycaster.ParentHolder;

            while (holder != null)
            {
                if (IsTransportLikeHolder(holder))
                {
                    List<Thing> containedThings = ThingOwnerUtility.GetAllThingsRecursively(holder, allowUnreal: true);

                    bool containsPsycaster = false;
                    bool containsPlayerPawn = false;

                    for (int i = 0; i < containedThings.Count; i++)
                    {
                        Pawn pawn = containedThings[i] as Pawn;

                        if (pawn == null)
                        {
                            continue;
                        }

                        if (pawn == psycaster)
                        {
                            containsPsycaster = true;
                            continue;
                        }

                        if (IsPlayerExtractionPawn(pawn))
                        {
                            containsPlayerPawn = true;
                        }
                    }

                    if (containsPsycaster && containsPlayerPawn)
                    {
                        return true;
                    }
                }

                holder = holder.ParentHolder;
            }

            return false;
        }

        private bool IsTransportLikeHolder(IThingHolder holder)
        {
            if (holder == null)
            {
                return false;
            }

            if (holder is Caravan caravan)
            {
                return caravan.IsPlayerControlled;
            }

            string typeName = holder.GetType().Name;

            if (typeName.Contains("Transport"))
            {
                return true;
            }

            if (typeName.Contains("DropPod"))
            {
                return true;
            }

            if (typeName.Contains("Shuttle"))
            {
                return true;
            }

            if (typeName.Contains("Launchable"))
            {
                return true;
            }

            return false;
        }

        private bool IsPlayerExtractionPawn(Pawn pawn)
        {
            if (pawn == null || pawn.Dead || pawn.Destroyed)
            {
                return false;
            }

            if (pawn.Faction == Faction.OfPlayer)
            {
                return true;
            }

            if (pawn.IsColonist)
            {
                return true;
            }

            if (pawn.IsPrisonerOfColony)
            {
                return true;
            }

            return false;
        }

        private Quest FindLinkedVIPQuest(VIPSiteData data)
        {
            if (data == null || data.site == null || data.site.questTags.NullOrEmpty())
            {
                return null;
            }

            List<string> siteTags = data.site.questTags;

            List<Quest> quests = Find.QuestManager.QuestsListForReading;

            for (int i = 0; i < quests.Count; i++)
            {
                Quest quest = quests[i];

                if (quest == null)
                {
                    continue;
                }

                if (quest.State != QuestState.Ongoing)
                {
                    continue;
                }

                if (quest.tags.NullOrEmpty())
                {
                    continue;
                }

                for (int j = 0; j < quest.tags.Count; j++)
                {
                    if (siteTags.Contains(quest.tags[j]))
                    {
                        return quest;
                    }
                }
            }

            return null;
        }

        private void SendVIPQuestSignal(VIPSiteData data, string signalPart)
        {
            if (data == null || data.site == null || data.site.questTags.NullOrEmpty())
            {
                return;
            }

            QuestUtility.SendQuestTargetSignals(data.site.questTags, signalPart);
        }


        private bool IsPsycasterDeliveredToPlayerSettlement(Pawn psycaster)
        {
            if (psycaster == null || psycaster.Dead || psycaster.Destroyed)
                return false;

            if (!psycaster.Spawned || psycaster.Map == null)
                return false;

            if (!psycaster.IsPrisonerOfColony)
                return false;

            return psycaster.Map.IsPlayerHome;
        }

    }
}
