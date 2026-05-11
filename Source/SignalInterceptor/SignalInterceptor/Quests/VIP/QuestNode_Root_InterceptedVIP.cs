using RimWorld;
using RimWorld.Planet;
using RimWorld.QuestGen;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;
using Verse.Grammar;

namespace SignalInterceptor
{
    public enum VIPSubtype
    {
        ShuttleVIP,
        PsycasterVIP,
        MechanitorSignalVIP,
        PilgrimVIP,
        DoppelgangerVIP
    }

    public class QuestNode_Root_InterceptedVIP : QuestNode
    {
        private static readonly IntRange TimeoutDaysRange = new IntRange(1, 15);

        protected override bool TestRunInt(Slate slate)
        {
            Map map = slate.Get<Map>("map");
            if (map == null)
                return false;

            return GetValidFactionsForVIP(map).Any();
        }

        protected override void RunInt()
        {
            Quest quest = QuestGen.quest;
            Slate slate = QuestGen.slate;

            Map map = slate.Get<Map>("map");
            Pawn worker = slate.Get<Pawn>("worker");

            List<Faction> validFactions = GetValidFactionsForVIP(map);

            if (validFactions.Count == 0)
            {
                AddFallbackQuestRules();
                Log.Warning("[Signal Interceptor] No valid factions available for VIP quest.");
                return;
            }

            Faction realFaction = null;
            VIPSubtype subtype = VIPSubtype.ShuttleVIP;

            Settlement shuttleOrigin = null;
            Settlement shuttleDestination = null;

            PlanetTile tile = PlanetTile.Invalid;
            int siteSignalTier = 0;

            List<Faction> shuffledFactions = validFactions
                .InRandomOrder()
                .Take(8)
                .ToList();

            bool foundValidQuestTarget = false;

            foreach (Faction factionCandidate in shuffledFactions)
            {
                List<VIPSubtype> subtypes = GetAvailableVIPSubtypes(factionCandidate, map).InRandomOrder().ToList();

                foreach (VIPSubtype subtypeCandidate in subtypes)
                {
                    PlanetTile candidateTile = PlanetTile.Invalid;
                    Settlement candidateOrigin = null;
                    Settlement candidateDestination = null;
                    int candidateSignalTier = 0;

                    if (subtypeCandidate == VIPSubtype.ShuttleVIP)
                    {
                        if (!TryFindShuttleRouteTile(
                                map,
                                factionCandidate,
                                out candidateTile,
                                out candidateOrigin,
                                out candidateDestination))
                        {
                            Log.Message("[Signal Interceptor] Shuttle VIP candidate skipped: no valid route tile found for faction " +
                                        (factionCandidate?.Name ?? "null"));

                            continue;
                        }
                    }
                    else
                    {
                        if (!TryFindSiteTileForSubtype(
                                map,
                                factionCandidate,
                                subtypeCandidate,
                                out candidateTile,
                                out candidateSignalTier))
                        {
                            Log.Message("[Signal Interceptor] VIP candidate skipped: no valid site tile found. Subtype=" +
                                        subtypeCandidate +
                                        " | Faction=" +
                                        (factionCandidate?.Name ?? "null"));

                            continue;
                        }
                    }

                    realFaction = factionCandidate;
                    subtype = subtypeCandidate;
                    tile = candidateTile;
                    siteSignalTier = candidateSignalTier;
                    shuttleOrigin = candidateOrigin;
                    shuttleDestination = candidateDestination;
                    foundValidQuestTarget = true;
                    break;
                }

                if (foundValidQuestTarget)
                    break;
            }

            if (!foundValidQuestTarget || realFaction == null || !tile.Valid)
            {
                AddFallbackQuestRules();
                Log.Warning("[Signal Interceptor] Failed to find valid VIP quest target.");
                return;
            }

            float threatPoints = GetThreatPoints(subtype, realFaction, siteSignalTier);
            int timeoutTicks = TimeoutDaysRange.RandomInRange * 10000;

            SitePartDef vipPartDef = GetSitePartDef(subtype);

            Faction siteFaction =
                subtype == VIPSubtype.DoppelgangerVIP ||
                subtype == VIPSubtype.MechanitorSignalVIP
                    ? null
                    : realFaction;

            Site site = SiteMaker.MakeSite(
                vipPartDef,
                tile,
                siteFaction,
                threatPoints: threatPoints
            );

            if (subtype != VIPSubtype.DoppelgangerVIP && site.Faction != siteFaction)
            {
                site.SetFaction(siteFaction);
            }

            site.factionMustRemainHostile = false;

            string workerName = worker?.LabelShort ?? "A colonist";
            string coloredFaction = FactionColored(realFaction);

            string originName = shuttleOrigin != null
                ? SettlementColored(shuttleOrigin)
                : "SI_ShuttleUnknownOrigin".Translate().ToString();

            string destinationName = shuttleDestination != null
                ? SettlementColored(shuttleDestination)
                : "SI_ShuttleUnknownDestination".Translate().ToString();

            int vipTier = GetVIPTierForQuest(threatPoints);
            string shuttleSecurityDesc = GetShuttleSecurityDescription(vipTier);
            string psycasterThreatDesc = GetPsycasterThreatDescription(vipTier);

            string questName = GetQuestName(subtype);

            site.customLabel = questName;

            string questDescription = GetQuestDescription(
                subtype,
                workerName,
                coloredFaction,
                originName,
                destinationName,
                shuttleSecurityDesc,
                psycasterThreatDesc
            );

            List<Rule> nameRules = new List<Rule>
    {
        new Rule_String("questName", questName)
    };
            QuestGen.AddQuestNameRules(nameRules);

            List<Rule> descRules = new List<Rule>
    {
        new Rule_String("questDescription", questDescription)
    };
            QuestGen.AddQuestDescriptionRules(descRules);

            string questTag = QuestGenUtility.HardcodedTargetQuestTagWithQuestID("InterceptedVIP");
            QuestUtility.AddQuestTag(ref site.questTags, questTag);

            quest.SpawnWorldObject(site);

            /*
             * ВАЖНО:
             * Не используем quest.WorldObjectTimeout(site, timeoutTicks).
             *
             * Vanilla QuestPart таймаута может уничтожить world object,
             * даже если игрок уже вошёл на карту сайта и бой идёт прямо сейчас.
             *
             * Таймаут теперь полностью контролируется SignalInterceptorGameComponent.TickVIPSites():
             * - если карта сайта ещё не загружена — квест фейлится по истечении срока;
             * - если карта уже активна — таймаут отключается и бой/событие доигрывается нормально.
             */

            string allEnemiesDefeatedSignal = QuestGenUtility.QuestTagSignal(questTag, "AllEnemiesDefeated");
            quest.SignalPass(delegate
            {
                quest.End(
                    QuestEndOutcome.Success,
                    0,
                    null,
                    null,
                    QuestPart.SignalListenMode.OngoingOnly,
                    sendStandardLetter: false
                );
            }, allEnemiesDefeatedSignal);

            string mapRemovedSignal = QuestGenUtility.QuestTagSignal(questTag, "MapRemoved");
            quest.SignalPass(delegate
            {
                quest.End(
                    QuestEndOutcome.Fail,
                    0,
                    null,
                    null,
                    QuestPart.SignalListenMode.OngoingOnly,
                    sendStandardLetter: false
                );
            }, mapRemovedSignal);

            string siteDestroyedSignal = QuestGenUtility.HardcodedSignalWithQuestID("site.Destroyed");
            quest.SignalPass(delegate
            {
                quest.End(
                    QuestEndOutcome.Fail,
                    0,
                    null,
                    null,
                    QuestPart.SignalListenMode.OngoingOnly,
                    sendStandardLetter: false
                );
            }, siteDestroyedSignal);

            SignalInterceptorGameComponent comp = Current.Game.GetComponent<SignalInterceptorGameComponent>();
            if (comp != null)
            {
                comp.TrackVIPSite(site, threatPoints, realFaction, subtype, timeoutTicks);
            }

            slate.Set("site", site);
            slate.Set("faction", realFaction);
            slate.Set("timeoutTicks", timeoutTicks);

            Log.Message("[Signal Interceptor] VIP quest generated. " +
                        "Subtype: " + subtype +
                        " | Quest name: " + questName +
                        " | Site custom label: " + site.customLabel +
                        " | Real faction: " + realFaction.Name +
                        " | Site faction: " + (site.Faction?.Name ?? "null") +
                        " | Intended site faction: " + (siteFaction?.Name ?? "null") +
                        " | Threat: " + threatPoints +
                        " | Tier: " + vipTier +
                        " | Timeout ticks: " + timeoutTicks +
                        " | Timeout handled by GameComponent" +
                        " | Shuttle origin: " + (shuttleOrigin?.LabelCap.ToString() ?? "null") +
                        " | Shuttle destination: " + (shuttleDestination?.LabelCap.ToString() ?? "null"));
        }

        private void AddFallbackQuestRules()
        {
            QuestGen.AddQuestNameRules(new List<Rule>
            {
                new Rule_String("questName", "Intercepted signal")
            });

            QuestGen.AddQuestDescriptionRules(new List<Rule>
            {
                new Rule_String("questDescription", "The intercepted signal was too distorted to decode.")
            });
        }

        private string GetPsycasterThreatDescription(int tier)
        {
            if (tier >= 9)
                return "SI_PsycasterThreatDesc5".Translate();

            if (tier >= 7)
                return "SI_PsycasterThreatDesc4".Translate();

            if (tier >= 5)
                return "SI_PsycasterThreatDesc3".Translate();

            if (tier >= 3)
                return "SI_PsycasterThreatDesc2".Translate();

            return "SI_PsycasterThreatDesc1".Translate();
        }

        private List<Faction> GetValidFactionsForVIP(Map map)
        {
            return Find.FactionManager.AllFactions
                .Where(f => IsValidBaseVIPFaction(f))
                .Where(f => GetAvailableVIPSubtypes(f, map).Count > 0)
                .ToList();
        }

        private struct ShuttleRoutePair
        {
            public Settlement origin;
            public Settlement destination;
            public float weight;

            public ShuttleRoutePair(Settlement origin, Settlement destination, float weight)
            {
                this.origin = origin;
                this.destination = destination;
                this.weight = weight;
            }
        }

        private bool TryChooseShuttleRouteSettlements(
            Map playerMap,
            Faction faction,
            out Settlement origin,
            out Settlement destination)
        {
            origin = null;
            destination = null;

            if (playerMap == null || faction == null)
                return false;

            PlanetTile playerTile = playerMap.Tile;

            List<Settlement> settlements = GetValidShuttleSettlements(faction, playerMap);

            if (settlements.Count < 2)
                return false;

            const float minRouteDistance = 4f;
            const float maxRouteDistance = 260f;
            const float maxAverageDistanceFromPlayer = 420f;

            const int maxPairChecks = 500;

            List<ShuttleRoutePair> pairs = new List<ShuttleRoutePair>();

            for (int attempt = 0; attempt < maxPairChecks; attempt++)
            {
                Settlement a = settlements.RandomElementWithFallback(null);
                Settlement b = settlements.RandomElementWithFallback(null);

                if (a == null || b == null || a == b)
                    continue;

                PlanetTile aTile = a.Tile;
                PlanetTile bTile = b.Tile;

                if (!IsValidDistancePair(aTile, bTile))
                    continue;

                if (!IsValidDistancePair(playerTile, aTile))
                    continue;

                if (!IsValidDistancePair(playerTile, bTile))
                    continue;

                float routeDistance = Find.WorldGrid.ApproxDistanceInTiles(aTile, bTile);

                if (routeDistance < minRouteDistance || routeDistance > maxRouteDistance)
                    continue;

                float distAFromPlayer = Find.WorldGrid.ApproxDistanceInTiles(playerTile, aTile);
                float distBFromPlayer = Find.WorldGrid.ApproxDistanceInTiles(playerTile, bTile);
                float averageDistanceFromPlayer = (distAFromPlayer + distBFromPlayer) / 2f;

                if (averageDistanceFromPlayer > maxAverageDistanceFromPlayer)
                    continue;

                float proximityWeight = Mathf.Lerp(
                    2.5f,
                    0.25f,
                    Mathf.Clamp01(averageDistanceFromPlayer / maxAverageDistanceFromPlayer)
                );

                float routeLengthWeight = Mathf.Clamp(routeDistance / 30f, 0.5f, 2.5f);

                float weight = proximityWeight * routeLengthWeight;

                pairs.Add(new ShuttleRoutePair(a, b, weight));
            }

            if (pairs.Count > 0)
            {
                ShuttleRoutePair selected = pairs.RandomElementByWeight(p => p.weight);

                origin = selected.origin;
                destination = selected.destination;

                return true;
            }

            // Fallback: если строгий подбор пары не сработал, берём любые две базы этой фракции.
            // ВАЖНО: нельзя использовать out-параметр origin внутри LINQ/lambda, поэтому используем local.
            Settlement originLocal = settlements.RandomElementWithFallback(null);

            if (originLocal == null)
                return false;

            List<Settlement> destinationCandidates = new List<Settlement>();

            for (int i = 0; i < settlements.Count; i++)
            {
                Settlement candidate = settlements[i];

                if (candidate == null)
                    continue;

                if (candidate == originLocal)
                    continue;

                destinationCandidates.Add(candidate);
            }

            Settlement destinationLocal = destinationCandidates.RandomElementWithFallback(null);

            if (destinationLocal == null)
                return false;

            origin = originLocal;
            destination = destinationLocal;

            return true;
        }

        private bool IsValidBaseVIPFaction(Faction faction)
        {
            if (faction == null)
                return false;

            if (faction.IsPlayer)
                return false;

            if (faction.defeated)
                return false;

            if (faction.Hidden)
                return false;

            if (faction.temporary)
                return false;

            if (faction.def == null || !faction.def.humanlikeFaction)
                return false;

            return true;
        }

        private VIPSubtype ChooseSubtype(Faction faction, Map map)
        {
            List<VIPSubtype> available = GetAvailableVIPSubtypes(faction, map);

            if (available.Count == 0)
            {
                Log.Warning("[Signal Interceptor] ChooseSubtype called with no available VIP subtypes. Falling back to ShuttleVIP.");
                return VIPSubtype.ShuttleVIP;
            }

            return available.RandomElement();
        }

        private List<VIPSubtype> GetAvailableVIPSubtypes(Faction faction, Map map)
        {
            List<VIPSubtype> available = new List<VIPSubtype>();

            /*
             * ShuttleVIP требует две наземные базы фракции.
             * Это отсекает космических торговцев и похожие фракции.
             */
            if (CanUseShuttleVIP(faction, map))
            {
                available.Add(VIPSubtype.ShuttleVIP);
            }

            if (ModsConfig.RoyaltyActive)
                available.Add(VIPSubtype.PsycasterVIP);

            if (ModsConfig.BiotechActive)
                available.Add(VIPSubtype.MechanitorSignalVIP);

            if (ModsConfig.IdeologyActive)
            {
                // Pilgrim VIP спавнится только для племенных/неолитических фракций
                if (faction.def.techLevel <= TechLevel.Neolithic)
                {
                    available.Add(VIPSubtype.PilgrimVIP);
                }
            }

            if (ModsConfig.AnomalyActive)
                available.Add(VIPSubtype.DoppelgangerVIP);

            return available;
        }

        private bool CanUseShuttleVIP(Faction faction, Map map)
        {
            if (faction == null || map == null)
                return false;

            if (faction.def == null)
                return false;

            if (faction.def.techLevel < TechLevel.Industrial)
                return false;

            return GetValidShuttleSettlements(faction, map).Count >= 2;
        }

        private List<Settlement> GetValidShuttleSettlements(Faction faction, Map map)
        {
            if (faction == null || map == null)
                return new List<Settlement>();

            PlanetTile playerTile = map.Tile;

            return Find.WorldObjects.Settlements
                .Where(s => s != null)
                .Where(s => !s.Destroyed)
                .Where(s => s.Faction == faction)
                .Where(s => s.Tile.Valid)
                .Where(s => s.Tile.LayerDef == playerTile.LayerDef)
                .Where(s => IsValidDistancePair(playerTile, s.Tile))
                .ToList();
        }

        private bool IsValidDistancePair(PlanetTile a, PlanetTile b)
        {
            if (!a.Valid || !b.Valid)
                return false;

            if (a.LayerDef != b.LayerDef)
                return false;

            try
            {
                Find.WorldGrid.ApproxDistanceInTiles(a, b);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private bool TryFindShuttleRouteTile(
            Map playerMap,
            Faction faction,
            out PlanetTile resultTile,
            out Settlement origin,
            out Settlement destination)
        {
            resultTile = PlanetTile.Invalid;
            origin = null;
            destination = null;

            if (playerMap == null || faction == null)
                return false;

            PlanetTile playerTile = playerMap.Tile;

            if (!TryChooseShuttleRouteSettlements(playerMap, faction, out origin, out destination))
                return false;

            PlanetTile originTile = origin.Tile;
            PlanetTile destinationTile = destination.Tile;

            string originLabel = origin.LabelCap;
            string destinationLabel = destination.LabelCap;

            if (!IsValidDistancePair(originTile, destinationTile))
                return false;

            float routeDistance = Find.WorldGrid.ApproxDistanceInTiles(originTile, destinationTile);

            if (routeDistance <= 0f)
                return false;

            const int attempts = 700;
            const int minDist = 6;
            const int maxDist = 280;
            const float strictMaxDeviation = 18f;
            const float looseMaxDeviation = 45f;

            List<PlanetTile> strictCandidates = new List<PlanetTile>();
            List<PlanetTile> looseCandidates = new List<PlanetTile>();

            for (int i = 0; i < attempts; i++)
            {
                if (!TileFinder.TryFindNewSiteTile(out PlanetTile tile, minDist, maxDist))
                    continue;

                if (!IsValidSiteTile(tile))
                    continue;

                if (tile.LayerDef != originTile.LayerDef)
                    continue;

                if (!IsValidDistancePair(originTile, tile))
                    continue;

                if (!IsValidDistancePair(tile, destinationTile))
                    continue;

                if (!IsValidDistancePair(playerTile, tile))
                    continue;

                float distFromOrigin = Find.WorldGrid.ApproxDistanceInTiles(originTile, tile);
                float distToDestination = Find.WorldGrid.ApproxDistanceInTiles(tile, destinationTile);
                float distFromPlayer = Find.WorldGrid.ApproxDistanceInTiles(playerTile, tile);

                if (distFromOrigin < 1f || distToDestination < 1f)
                    continue;

                if (distFromPlayer > maxDist)
                    continue;

                float totalDistance = distFromOrigin + distToDestination;
                float deviation = totalDistance - routeDistance;

                if (deviation >= 0f && deviation <= strictMaxDeviation)
                {
                    strictCandidates.Add(tile);
                    continue;
                }

                if (deviation >= -20f && deviation <= looseMaxDeviation)
                {
                    looseCandidates.Add(tile);
                }
            }

            List<PlanetTile> candidates = strictCandidates.Count > 0
                ? strictCandidates
                : looseCandidates;

            if (candidates.Count > 0)
            {
                resultTile = candidates
                    .OrderBy(tile =>
                    {
                        float distFromOrigin = Find.WorldGrid.ApproxDistanceInTiles(originTile, tile);
                        float distToDestination = Find.WorldGrid.ApproxDistanceInTiles(tile, destinationTile);

                        float totalDistance = distFromOrigin + distToDestination;
                        float deviation = Mathf.Abs(totalDistance - routeDistance);
                        float balance = Mathf.Abs(distFromOrigin - distToDestination);

                        return deviation * 5f + balance;
                    })
                    .First();

                Log.Message("[Signal Interceptor] Shuttle route tile selected. " +
                            "Faction: " + faction.Name +
                            " | Origin: " + originLabel +
                            " | Destination: " + destinationLabel +
                            " | Route distance: " + routeDistance +
                            " | Strict candidates: " + strictCandidates.Count +
                            " | Loose candidates: " + looseCandidates.Count +
                            " | Tile: " + resultTile);

                return true;
            }

            // Последний fallback: если маршрутная логика не нашла точку,
            // спавним обычный сайт в расширенном радиусе от игрока.
            if (TryFindLooseSiteTile(playerMap, 12, 320, out resultTile))
            {
                Log.Warning("[Signal Interceptor] Shuttle route tile fallback used. " +
                            "Faction: " + faction.Name +
                            " | Origin: " + originLabel +
                            " | Destination: " + destinationLabel +
                            " | Route distance: " + routeDistance +
                            " | Tile: " + resultTile);

                return true;
            }

            Log.Message("[Signal Interceptor] Shuttle route tile not found even with fallback. " +
                        "Faction: " + faction.Name +
                        " | Origin: " + originLabel +
                        " | Destination: " + destinationLabel +
                        " | Route distance: " + routeDistance +
                        " | Shuttle VIP skipped.");

            resultTile = PlanetTile.Invalid;
            return false;
        }

        private bool TryFindLooseSiteTile(Map map, int minDist, int maxDist, out PlanetTile resultTile)
        {
            resultTile = PlanetTile.Invalid;

            if (map == null || !map.Tile.Valid)
                return false;

            PlanetTile playerTile = map.Tile;

            const int attempts = 500;

            for (int i = 0; i < attempts; i++)
            {
                if (!TileFinder.TryFindNewSiteTile(out PlanetTile tile, minDist, maxDist))
                    continue;

                if (!tile.Valid)
                    continue;

                if (tile.LayerDef != playerTile.LayerDef)
                    continue;

                if (!IsValidDistancePair(playerTile, tile))
                    continue;

                if (!IsValidSiteTile(tile))
                    continue;

                resultTile = tile;
                return true;
            }

            // Совсем мягкий fallback: доверяем vanilla TileFinder.
            if (TileFinder.TryFindNewSiteTile(out resultTile, minDist, maxDist))
            {
                if (resultTile.Valid && resultTile.LayerDef == playerTile.LayerDef)
                    return true;
            }

            resultTile = PlanetTile.Invalid;
            return false;
        }

        private bool IsValidSiteTile(PlanetTile tile)
        {
            if (!tile.Valid)
                return false;

            try
            {
                if (Find.WorldGrid[tile].WaterCovered)
                    return false;

                if (Find.WorldGrid[tile].hilliness == Hilliness.Impassable)
                    return false;

                if (Find.WorldObjects.ObjectsAt(tile).Any())
                    return false;
            }
            catch
            {
                return false;
            }

            return true;
        }

        private struct WeightedSiteTile
        {
            public PlanetTile tile;
            public float weight;
            public int signalTier;

            public WeightedSiteTile(PlanetTile tile, float weight, int signalTier)
            {
                this.tile = tile;
                this.weight = weight;
                this.signalTier = signalTier;
            }
        }

        private bool TryFindSiteTileForSubtype(
            Map map,
            Faction faction,
            VIPSubtype subtype,
            out PlanetTile tile,
            out int signalTier)
        {
            tile = PlanetTile.Invalid;
            signalTier = 0;

            switch (subtype)
            {
                case VIPSubtype.DoppelgangerVIP:
                    return TryFindDoppelgangerSiteTile(map, out tile, out signalTier);

                case VIPSubtype.MechanitorSignalVIP:
                    return TryFindMechanitorSignalSiteTile(map, out tile, out signalTier);

                case VIPSubtype.PsycasterVIP:
                    signalTier = 0;
                    return TryFindPsycasterSiteTile(map, out tile);

                case VIPSubtype.PilgrimVIP:
                    signalTier = 0;
                    return TryFindPilgrimSiteTile(map, faction, out tile);

                default:
                    signalTier = 0;
                    return TryFindLooseSiteTile(map, 12, 95, out tile);
            }
        }

        private bool TryFindDoppelgangerSiteTile(Map map, out PlanetTile resultTile, out int signalTier)
        {
            resultTile = PlanetTile.Invalid;
            signalTier = 0;

            if (map == null)
                return false;

            PlanetTile playerTile = map.Tile;

            const int attempts = 1200;
            const float minDistanceFromPlayer = 16f;
            const float maxDistanceFromPlayer = 95f;
            const float minSettlementDistance = 10f;

            List<WeightedSiteTile> candidates = new List<WeightedSiteTile>();

            for (int i = 0; i < attempts; i++)
            {
                if (!TileFinder.TryFindNewSiteTile(out PlanetTile tile, minDist: 16, maxDist: 95))
                    continue;

                if (!IsValidSiteTile(tile))
                    continue;

                if (tile.LayerDef != playerTile.LayerDef)
                    continue;

                if (!IsValidDistancePair(playerTile, tile))
                    continue;

                float distanceFromPlayer = Find.WorldGrid.ApproxDistanceInTiles(playerTile, tile);

                if (distanceFromPlayer < minDistanceFromPlayer || distanceFromPlayer > maxDistanceFromPlayer)
                    continue;

                float nearestSpecialSiteDistance = DistanceToNearestSignalInterceptorSpecialSite(tile, playerTile);

                if (nearestSpecialSiteDistance >= 0f && nearestSpecialSiteDistance < 30f)
                    continue;

                float nearestSettlementDistance = DistanceToNearestSettlement(tile, playerTile);

                if (nearestSettlementDistance >= 0f && nearestSettlementDistance < minSettlementDistance)
                    continue;

                int tier;
                float bandWeight;

                if (distanceFromPlayer < 35f)
                {
                    // Ближний сигнал: 16–35
                    tier = 1;
                    bandWeight = 1.0f;
                }
                else if (distanceFromPlayer < 60f)
                {
                    // Дальний сигнал: 35–60
                    tier = 2;
                    bandWeight = 0.75f;
                }
                else if (distanceFromPlayer < 80f)
                {
                    // 60–80: в основном дальний сигнал, иногда глухая зона
                    if (Rand.Chance(0.25f))
                    {
                        tier = 3;
                        bandWeight = 0.25f;
                    }
                    else
                    {
                        tier = 2;
                        bandWeight = 0.65f;
                    }
                }
                else
                {
                    // Глухая зона: 80–120
                    tier = 3;
                    bandWeight = 0.18f;
                }

                float weight = bandWeight;

                Hilliness hilliness = Find.WorldGrid[tile].hilliness;

                if (hilliness == Hilliness.SmallHills)
                    weight *= 1.25f;
                else if (hilliness == Hilliness.LargeHills)
                    weight *= 1.75f;
                else if (hilliness == Hilliness.Mountainous)
                    weight *= 2.25f;

                if (IsPreferredDoppelgangerBiome(tile))
                    weight *= 1.8f;

                if (TileHasRoad(tile))
                    weight *= 0.35f;
                else
                    weight *= 1.35f;

                if (nearestSettlementDistance >= 0f)
                    weight *= Mathf.Clamp(nearestSettlementDistance / 25f, 0.75f, 2.25f);

                candidates.Add(new WeightedSiteTile(tile, weight, tier));
            }

            if (candidates.Count == 0)
            {
                Log.Message("[Signal Interceptor] Doppelganger site tile not found.");
                return false;
            }

            WeightedSiteTile selected = candidates.RandomElementByWeight(c => c.weight);

            resultTile = selected.tile;
            signalTier = selected.signalTier;

            Log.Message("[Signal Interceptor] Doppelganger site tile selected. " +
                        "Tile=" + resultTile +
                        " | Signal tier=" + signalTier +
                        " | Candidates=" + candidates.Count);

            return true;
        }

        private bool TryFindMechanitorSignalSiteTile(Map map, out PlanetTile resultTile, out int signalTier)
        {
            resultTile = PlanetTile.Invalid;
            signalTier = 0;

            if (map == null) return false;
            PlanetTile playerTile = map.Tile;

            // Дистанция: подальше от игрока (от 45 до 140 тайлов)
            const int attempts = 800;
            List<PlanetTile> candidates = new List<PlanetTile>();

            for (int i = 0; i < attempts; i++)
            {
                if (!TileFinder.TryFindNewSiteTile(out PlanetTile tile, 45, 140)) continue;
                if (!IsValidSiteTile(tile)) continue;
                if (tile.LayerDef != playerTile.LayerDef) continue;
                if (!IsValidDistancePair(playerTile, tile)) continue;

                float distanceFromPlayer = Find.WorldGrid.ApproxDistanceInTiles(playerTile, tile);
                if (distanceFromPlayer < 45f || distanceFromPlayer > 140f) continue;

                candidates.Add(tile);
            }

            if (candidates.Count > 0)
            {
                resultTile = candidates.RandomElement();
                float dist = Find.WorldGrid.ApproxDistanceInTiles(playerTile, resultTile);

                if (dist < 70f) signalTier = 1;
                else if (dist < 100f) signalTier = 2;
                else signalTier = 3;

                return true;
            }

            // Fallback
            if (TryFindLooseSiteTile(map, 45, 155, out resultTile))
            {
                signalTier = 2;
                return true;
            }

            return false;
        }

        private bool TryFindPilgrimSiteTile(Map map, Faction faction, out PlanetTile resultTile)
        {
            resultTile = PlanetTile.Invalid;
            if (map == null || faction == null) return false;

            // Ищем поселения этой фракции
            List<Settlement> tribalSettlements = Find.WorldObjects.Settlements
                .Where(s => s.Faction == faction && s.Tile.Valid && s.Tile.LayerDef == map.Tile.LayerDef)
                .ToList();

            if (tribalSettlements.Count == 0)
            {
                // На случай, если у племени почему-то нет баз на карте
                return TryFindLooseSiteTile(map, 12, 60, out resultTile);
            }

            Settlement anchor = tribalSettlements.RandomElement();
            PlanetTile anchorTile = anchor.Tile;

            List<PlanetTile> candidates = new List<PlanetTile>();
            for (int i = 0; i < 500; i++)
            {
                // Ищем обычные тайлы
                if (!TileFinder.TryFindNewSiteTile(out PlanetTile tile, 12, 95)) continue;
                if (!IsValidSiteTile(tile)) continue;
                if (tile.LayerDef != anchorTile.LayerDef) continue;
                if (!IsValidDistancePair(anchorTile, tile)) continue;

                float distToAnchor = Find.WorldGrid.ApproxDistanceInTiles(anchorTile, tile);

                // Пилигримы должны быть недалеко от родного поселения (от 2 до 15 тайлов)
                if (distToAnchor >= 2f && distToAnchor <= 15f)
                {
                    candidates.Add(tile);
                }
            }

            if (candidates.Count > 0)
            {
                resultTile = candidates.RandomElement();
                return true;
            }

            return TryFindLooseSiteTile(map, 12, 60, out resultTile);
        }

        private bool TryFindPsycasterSiteTile(Map map, out PlanetTile resultTile)
        {
            resultTile = PlanetTile.Invalid;
            if (map == null) return false;

            PlanetTile playerTile = map.Tile;
            List<PlanetTile> candidates = new List<PlanetTile>();

            for (int i = 0; i < 600; i++)
            {
                if (!TileFinder.TryFindNewSiteTile(out PlanetTile tile, 15, 80)) continue;
                if (!IsValidSiteTile(tile)) continue;
                if (tile.LayerDef != playerTile.LayerDef) continue;
                if (!IsValidDistancePair(playerTile, tile)) continue;

                // БЕЗ landmarks: запрещаем пещеры, которые могут заспавнить спящих инсектоидов (причина зависания)
                if (Find.WorldGrid[tile].caves) continue;

                candidates.Add(tile);
            }

            if (candidates.Count > 0)
            {
                // Если есть тайлы с дорогами — берем их в приоритете, иначе случайный
                var withRoad = candidates.Where(t => TileHasRoad(t)).ToList();
                if (withRoad.Count > 0)
                {
                    resultTile = withRoad.RandomElement();
                }
                else
                {
                    resultTile = candidates.RandomElement();
                }
                return true;
            }

            return TryFindLooseSiteTile(map, 15, 80, out resultTile);
        }


        private float DistanceToNearestSettlement(PlanetTile tile, PlanetTile layerReferenceTile)
        {
            float best = -1f;

            foreach (Settlement settlement in Find.WorldObjects.Settlements)
            {
                if (settlement == null || settlement.Destroyed)
                    continue;

                if (!settlement.Tile.Valid)
                    continue;

                if (settlement.Tile.LayerDef != layerReferenceTile.LayerDef)
                    continue;

                if (!IsValidDistancePair(tile, settlement.Tile))
                    continue;

                float distance = Find.WorldGrid.ApproxDistanceInTiles(tile, settlement.Tile);

                if (best < 0f || distance < best)
                    best = distance;
            }

            return best;
        }

        private bool IsPreferredDoppelgangerBiome(PlanetTile tile)
        {
            BiomeDef biome = GetTileBiome(tile);

            if (biome == null || biome.defName == null)
                return false;

            string defName = biome.defName.ToLowerInvariant();

            return defName.Contains("swamp")
                || defName.Contains("marsh")
                || defName.Contains("bog")
                || defName.Contains("tundra")
                || defName.Contains("ice")
                || defName.Contains("desert")
                || defName.Contains("wasteland")
                || defName.Contains("polluted");
        }

        private BiomeDef GetTileBiome(PlanetTile tile)
        {
            try
            {
                object worldTile = Find.WorldGrid[tile];

                if (worldTile == null)
                    return null;

                System.Type type = worldTile.GetType();

                System.Reflection.PropertyInfo primaryBiomeProperty = type.GetProperty("PrimaryBiome");
                if (primaryBiomeProperty != null)
                {
                    object value = primaryBiomeProperty.GetValue(worldTile, null);
                    if (value is BiomeDef biome)
                        return biome;
                }

                System.Reflection.PropertyInfo biomeProperty = type.GetProperty("Biome");
                if (biomeProperty != null)
                {
                    object value = biomeProperty.GetValue(worldTile, null);
                    if (value is BiomeDef biome)
                        return biome;
                }

                System.Reflection.FieldInfo biomeField =
                    type.GetField("biome") ??
                    type.GetField("Biome") ??
                    type.GetField("primaryBiome") ??
                    type.GetField("PrimaryBiome");

                if (biomeField != null)
                {
                    object value = biomeField.GetValue(worldTile);
                    if (value is BiomeDef biome)
                        return biome;
                }
            }
            catch
            {
                return null;
            }

            return null;
        }

        private bool IsPreferredMechanitorBiome(PlanetTile tile)
        {
            BiomeDef biome = GetTileBiome(tile);

            if (biome == null || biome.defName == null)
                return false;

            string defName = biome.defName.ToLowerInvariant();

            return defName.Contains("desert")
                || defName.Contains("tundra")
                || defName.Contains("ice")
                || defName.Contains("wasteland")
                || defName.Contains("polluted")
                || defName.Contains("boreal");
        }

        private bool TileHasRoad(PlanetTile tile)
        {
            try
            {
                object worldTile = Find.WorldGrid[tile];
                System.Type type = worldTile.GetType();

                System.Reflection.PropertyInfo property = type.GetProperty("Roads");
                if (property != null)
                {
                    object value = property.GetValue(worldTile, null);
                    if (EnumerableHasAny(value))
                        return true;
                }

                System.Reflection.FieldInfo field = type.GetField("roads") ?? type.GetField("Roads");
                if (field != null)
                {
                    object value = field.GetValue(worldTile);
                    if (EnumerableHasAny(value))
                        return true;
                }
            }
            catch
            {
                return false;
            }

            return false;
        }

        private bool EnumerableHasAny(object value)
        {
            if (value == null)
                return false;

            System.Collections.IEnumerable enumerable = value as System.Collections.IEnumerable;

            if (enumerable == null)
                return false;

            foreach (object _ in enumerable)
                return true;

            return false;
        }

        private bool IsSignalInterceptorSpecialSite(Site site)
        {
            if (site == null || site.Destroyed || site.parts == null)
                return false;

            return site.parts.Any(part =>
                part?.def?.defName == "SI_MechanitorVIPSite"
                || part?.def?.defName == "SI_DoppelgangerVIPSite");
        }

        private float DistanceToNearestSignalInterceptorSpecialSite(PlanetTile tile, PlanetTile layerReferenceTile)
        {
            float best = -1f;

            foreach (Site site in Find.WorldObjects.Sites)
            {
                if (!IsSignalInterceptorSpecialSite(site))
                    continue;

                if (!site.Tile.Valid)
                    continue;

                if (site.Tile.LayerDef != layerReferenceTile.LayerDef)
                    continue;

                if (!IsValidDistancePair(tile, site.Tile))
                    continue;

                float dist = Find.WorldGrid.ApproxDistanceInTiles(tile, site.Tile);

                if (best < 0f || dist < best)
                    best = dist;
            }

            return best;
        }

        private float GetTilePollution(PlanetTile tile)
        {
            try
            {
                object worldTile = Find.WorldGrid[tile];
                System.Type type = worldTile.GetType();

                System.Reflection.PropertyInfo property = type.GetProperty("pollution") ?? type.GetProperty("Pollution");
                if (property != null)
                {
                    object value = property.GetValue(worldTile, null);
                    if (value is float f)
                        return f;
                }

                System.Reflection.FieldInfo field = type.GetField("pollution") ?? type.GetField("Pollution");
                if (field != null)
                {
                    object value = field.GetValue(worldTile);
                    if (value is float f)
                        return f;
                }
            }
            catch
            {
                return 0f;
            }

            return 0f;
        }

        private string GenerateShuttleQuestName()
        {
            List<string> adjectives = GetTranslatedStringList(
                "SI_Shuttle_QuestAdjectives",
                new List<string>
                {
                    "Аварийная",
                    "Сорванная",
                    "Вынужденная",
                    "Обесточенная",
                    "Потерянная",
                    "Слепая"
                }
            );

            List<string> nouns = GetTranslatedStringList(
                "SI_Shuttle_QuestNouns",
                new List<string>
                {
                    "Посадка",
                    "Стоянка",
                    "Дозаправка",
                    "Эвакуация",
                    "Остановка",
                    "Посадочная зона"
                }
            );

            return adjectives.RandomElement() + " " + nouns.RandomElement();
        }

        private string GenerateMechanitorSignalQuestName()
        {
            List<string> nouns = GetTranslatedStringList(
                "SI_MechanitorSignal_QuestNouns",
                new List<string>
                {
                    "Протокол",
                    "Сигнал",
                    "Контур",
                    "Импульс",
                    "Шёпот",
                    "Пульс",
                    "Код",
                    "Отклик",
                    "Маршрут",
                    "След",
                    "Узел"
                }
            );

            List<string> adjectives = GetTranslatedStringList(
                "SI_MechanitorSignal_QuestAdjectives",
                new List<string>
                {
                    "Блуждающего Ядра",
                    "Железной Воли",
                    "Чужого Разума",
                    "Мёртвой Машины",
                    "Потерянного Механитора",
                    "Сломанного Контроля",
                    "Стального Сердца",
                    "Пепельного Сигнала",
                    "Одинокого Повелителя",
                    "Холодного Сознания",
                    "Неподвижной Души",
                    "Забытой Команды"
                }
            );

            return nouns.RandomElement() + " " + adjectives.RandomElement();
        }

        private string GeneratePsycasterQuestName()
        {
            List<string> adjectives = GetTranslatedStringList(
                "SI_Psycaster_QuestAdjectives",
                new List<string>
                {
            "Псионический",
            "Нейронный",
            "Ментальный",
            "Безмолвный",
            "Запредельный",
            "Осколочный"
                }
            );

            List<string> nouns = GetTranslatedStringList(
                "SI_Psycaster_QuestNouns",
                new List<string>
                {
            "Резонанс",
            "Разлом",
            "Отголосок",
            "Мираж",
            "Разрыв"
                }
            );

            return adjectives.RandomElement() + " " + nouns.RandomElement();
        }

        private string GenerateDoppelgangerQuestName()
        {
            List<string> nouns = GetTranslatedStringList(
                "SI_Doppelganger_QuestNouns",
                new List<string>
                {
                    "Парад",
                    "Хор",
                    "Сонм",
                    "Шествие",
                    "Карнавал",
                    "Марш",
                    "Сход",
                    "Сборище",
                    "Круг",
                    "Рой",
                    "Легион",
                    "Отряд",
                    "Зов"
                }
            );

            List<string> adjectives = GetTranslatedStringList(
                "SI_Doppelganger_QuestAdjectives",
                new List<string>
                {
                    "Опечаленных",
                    "Безликих",
                    "Подменённых",
                    "Отражённых",
                    "Искажённых",
                    "Невозможных",
                    "Забытых",
                    "Пустых",
                    "Одинаковых",
                    "Ложных",
                    "Стертых",
                    "Раздвоенных",
                    "Ненастоящих"
                }
            );

            return nouns.RandomElement() + " " + adjectives.RandomElement();
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

        private SitePartDef GetSitePartDef(VIPSubtype subtype)
        {
            switch (subtype)
            {
                case VIPSubtype.DoppelgangerVIP:
                    return DefDatabase<SitePartDef>.GetNamed("SI_DoppelgangerVIPSite");

                case VIPSubtype.MechanitorSignalVIP:
                    return DefDatabase<SitePartDef>.GetNamed("SI_MechanitorVIPSite");

                case VIPSubtype.PsycasterVIP:
                    return DefDatabase<SitePartDef>.GetNamed("SI_PsycasterVIPSite");

                case VIPSubtype.ShuttleVIP:
                    return DefDatabase<SitePartDef>.GetNamed("SI_ShuttleVIPSite");

                case VIPSubtype.PilgrimVIP:
                    return DefDatabase<SitePartDef>.GetNamed("SI_PilgrimVIPSite");

                default:
                    return DefDatabase<SitePartDef>.GetNamed("VIPCapture");
            }
        }

        private float GetThreatPoints(VIPSubtype subtype, Faction faction, int signalTier = 0)
        {
            float baseThreat;

            switch (subtype)
            {
                case VIPSubtype.ShuttleVIP:
                    baseThreat = Rand.Range(450f, 5200f);
                    break;

                case VIPSubtype.PsycasterVIP:
                    baseThreat = Rand.Range(650f, 4700f);
                    break;

                case VIPSubtype.MechanitorSignalVIP:
                    if (signalTier >= 3)
                        baseThreat = Rand.Range(3000f, 5200f);
                    else if (signalTier == 2)
                        baseThreat = Rand.Range(1800f, 3400f);
                    else
                        baseThreat = Rand.Range(900f, 1900f);
                    break;

                case VIPSubtype.PilgrimVIP:
                    baseThreat = Rand.Range(500f, 4300f);
                    break;

                case VIPSubtype.DoppelgangerVIP:
                    if (signalTier >= 3)
                        baseThreat = Rand.Range(3000f, 5000f);
                    else if (signalTier == 2)
                        baseThreat = Rand.Range(1600f, 3100f);
                    else
                        baseThreat = Rand.Range(700f, 1700f);
                    break;

                default:
                    baseThreat = Rand.Range(450f, 4200f);
                    break;
            }

            float multiplier = GetFactionMultiplier(faction);
            float randomizer = Rand.Range(0.9f, 1.15f);

            return Mathf.Clamp(baseThreat * multiplier * randomizer, 350f, 5200f);
        }

        private float GetFactionMultiplier(Faction faction)
        {
            TechLevel tech = faction.def.techLevel;

            if (tech <= TechLevel.Medieval)
                return 0.6f;

            if (faction.def.permanentEnemy)
                return 1.4f;

            if (tech >= TechLevel.Spacer)
                return 1.3f;

            return 1.0f;
        }

        private int GetVIPTierForQuest(float points)
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

        private string GetShuttleSecurityDescription(int tier)
        {
            if (tier >= 9)
                return "SI_ShuttleSecurityDesc5".Translate();

            if (tier >= 7)
                return "SI_ShuttleSecurityDesc4".Translate();

            if (tier >= 5)
                return "SI_ShuttleSecurityDesc3".Translate();

            if (tier >= 3)
                return "SI_ShuttleSecurityDesc2".Translate();

            return "SI_ShuttleSecurityDesc1".Translate();
        }

        private string GetQuestName(VIPSubtype subtype)
        {
            switch (subtype)
            {
                case VIPSubtype.ShuttleVIP:
                    return GenerateShuttleQuestName();

                case VIPSubtype.PsycasterVIP:
                    return GeneratePsycasterQuestName();

                case VIPSubtype.MechanitorSignalVIP:
                    return GenerateMechanitorSignalQuestName();

                case VIPSubtype.PilgrimVIP:
                    return "SI_VIP_Name_Pilgrim".Translate();

                case VIPSubtype.DoppelgangerVIP:
                    return GenerateDoppelgangerQuestName();

                default:
                    return "Intercepted VIP";
            }
        }

        private string GetSiteLabel(VIPSubtype subtype)
        {
            switch (subtype)
            {
                case VIPSubtype.ShuttleVIP:
                    return "SI_VIP_Site_Shuttle".Translate();

                case VIPSubtype.PsycasterVIP:
                    return "SI_VIP_Site_Psycaster".Translate();

                case VIPSubtype.MechanitorSignalVIP:
                    return "SI_VIP_Site_MechanitorSignal".Translate();

                case VIPSubtype.PilgrimVIP:
                    return "SI_VIP_Site_Pilgrim".Translate();

                case VIPSubtype.DoppelgangerVIP:
                    return "SI_VIP_Site_Doppelganger".Translate();

                default:
                    return "VIP Location";
            }
        }

        private string GetQuestDescription(
            VIPSubtype subtype,
            string workerName,
            string coloredFaction,
            string originSettlement,
            string destinationSettlement,
            string shuttleSecurityDesc,
            string psycasterThreatDesc)
        {
            switch (subtype)
            {
                case VIPSubtype.ShuttleVIP:
                    return FormatKeyed(
                        "SI_VIP_Desc_Shuttle",
                        workerName,
                        coloredFaction,
                        originSettlement,
                        destinationSettlement,
                        shuttleSecurityDesc
                    );

                case VIPSubtype.PsycasterVIP:
                    return FormatKeyed(
                        "SI_VIP_Desc_Psycaster",
                        workerName,
                        psycasterThreatDesc
                    );

                case VIPSubtype.MechanitorSignalVIP:
                    return FormatKeyed("SI_VIP_Desc_MechanitorSignal", workerName);

                case VIPSubtype.PilgrimVIP:
                    return FormatKeyed("SI_VIP_Desc_Pilgrim", workerName, coloredFaction);

                case VIPSubtype.DoppelgangerVIP:
                    return FormatKeyed("SI_VIP_Desc_Doppelganger", workerName);

                default:
                    return "";
            }
        }

        private string FormatKeyed(string key, params object[] args)
        {
            string template = key.Translate().ToString();
            return string.Format(template, args);
        }

        private string FactionColored(Faction faction)
        {
            string colorHex = ColorUtility.ToHtmlStringRGB(faction.Color);
            return "<color=#" + colorHex + ">" + faction.Name + "</color>";
        }

        private string SettlementColored(Settlement settlement)
        {
            if (settlement == null)
                return "";

            Faction faction = settlement.Faction;
            if (faction == null)
                return settlement.LabelCap.ToString();

            string colorHex = ColorUtility.ToHtmlStringRGB(faction.Color);
            return "<color=#" + colorHex + ">" + settlement.LabelCap + "</color>";
        }
    }
}
