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
        private static readonly IntRange TimeoutDaysRange = new IntRange(1, 2);

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

            List<Faction> shuffledFactions = validFactions.InRandomOrder().ToList();
            bool foundValidQuestTarget = false;

            foreach (Faction factionCandidate in shuffledFactions)
            {
                List<VIPSubtype> subtypes = GetAvailableVIPSubtypes(factionCandidate, map).InRandomOrder().ToList();

                foreach (VIPSubtype subtypeCandidate in subtypes)
                {
                    PlanetTile candidateTile = PlanetTile.Invalid;
                    Settlement candidateOrigin = null;
                    Settlement candidateDestination = null;

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
                        if (!TileFinder.TryFindNewSiteTile(out candidateTile, minDist: 16, maxDist: 36))
                            continue;
                    }

                    realFaction = factionCandidate;
                    subtype = subtypeCandidate;
                    tile = candidateTile;
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

            float threatPoints = GetThreatPoints(subtype, realFaction);
            int timeoutTicks = TimeoutDaysRange.RandomInRange * 60000;

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

            string questName = GetQuestName(subtype);

            if (subtype == VIPSubtype.DoppelgangerVIP ||
                subtype == VIPSubtype.MechanitorSignalVIP)
            {
                site.customLabel = questName;
            }
            else
            {
                site.customLabel = GetSiteLabel(subtype);
            }

            string questDescription = GetQuestDescription(
                subtype,
                workerName,
                coloredFaction,
                originName,
                destinationName,
                shuttleSecurityDesc
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
             * Не используем quest.WorldObjectTimeout(site, timeoutTicks),
             * чтобы сайт не схлопнулся, если игрок уже вошёл на карту.
             * Истечение срока контролируется SignalInterceptorGameComponent.
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
                    sendStandardLetter: true
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
                    sendStandardLetter: true
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
            const float maxRouteDistance = 180f;
            const float maxAverageDistanceFromPlayer = 350f;

            List<ShuttleRoutePair> pairs = new List<ShuttleRoutePair>();

            for (int i = 0; i < settlements.Count; i++)
            {
                for (int j = i + 1; j < settlements.Count; j++)
                {
                    Settlement a = settlements[i];
                    Settlement b = settlements[j];

                    if (a == null || b == null)
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

                    /*
                     * Чем ближе маршрут к игроку — тем выше шанс.
                     * Но дальние маршруты не запрещены полностью, если они в пределах лимита.
                     */
                    float proximityWeight = Mathf.Lerp(
                        2.5f,
                        0.25f,
                        Mathf.Clamp01(averageDistanceFromPlayer / maxAverageDistanceFromPlayer)
                    );

                    /*
                     * Слишком короткие маршруты менее интересны.
                     */
                    float routeLengthWeight = Mathf.Clamp(routeDistance / 20f, 0.5f, 2f);

                    float weight = proximityWeight * routeLengthWeight;

                    pairs.Add(new ShuttleRoutePair(a, b, weight));
                }
            }

            if (pairs.Count == 0)
                return false;

            ShuttleRoutePair selected = pairs.RandomElementByWeight(p => p.weight);

            origin = selected.origin;
            destination = selected.destination;

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
                available.Add(VIPSubtype.PilgrimVIP);

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

            const int attempts = 3000;
            const int minDist = 1;
            const int maxDist = 350;
            const float maxDeviation = 8f;

            List<PlanetTile> candidates = new List<PlanetTile>();

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

                float distFromOrigin = Find.WorldGrid.ApproxDistanceInTiles(originTile, tile);
                float distToDestination = Find.WorldGrid.ApproxDistanceInTiles(tile, destinationTile);

                if (distFromOrigin < 1f || distToDestination < 1f)
                    continue;

                float totalDistance = distFromOrigin + distToDestination;
                float deviation = totalDistance - routeDistance;

                if (deviation < 0f || deviation > maxDeviation)
                    continue;

                candidates.Add(tile);
            }

            if (candidates.Count == 0)
            {
                Log.Message("[Signal Interceptor] Shuttle route tile not found. " +
                            "Faction: " + faction.Name +
                            " | Origin: " + originLabel +
                            " | Destination: " + destinationLabel +
                            " | Route distance: " + routeDistance +
                            " | Shuttle VIP skipped.");

                resultTile = PlanetTile.Invalid;
                return false;
            }

            resultTile = candidates
                .OrderBy(tile =>
                {
                    float distFromOrigin = Find.WorldGrid.ApproxDistanceInTiles(originTile, tile);
                    float distToDestination = Find.WorldGrid.ApproxDistanceInTiles(tile, destinationTile);

                    float totalDistance = distFromOrigin + distToDestination;
                    float deviation = Mathf.Abs(totalDistance - routeDistance);
                    float balance = Mathf.Abs(distFromOrigin - distToDestination);

                    return deviation * 10f + balance;
                })
                .First();

            Log.Message("[Signal Interceptor] Shuttle route tile selected. " +
                        "Faction: " + faction.Name +
                        " | Origin: " + originLabel +
                        " | Destination: " + destinationLabel +
                        " | Route distance: " + routeDistance +
                        " | Candidates: " + candidates.Count +
                        " | Tile: " + resultTile);

            return true;
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
                    "Резонанс",
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
                    return DefDatabase<SitePartDef>.GetNamed("DoppelgangerCamp");

                case VIPSubtype.MechanitorSignalVIP:
                    return DefDatabase<SitePartDef>.GetNamed("SI_MechanitorSignalSite");

                case VIPSubtype.ShuttleVIP:
                    return DefDatabase<SitePartDef>.GetNamed("SI_ShuttleVIPSite");

                default:
                    return DefDatabase<SitePartDef>.GetNamed("VIPCapture");
            }
        }

        private float GetThreatPoints(VIPSubtype subtype, Faction faction)
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
                    baseThreat = Rand.Range(900f, 5200f);
                    break;

                case VIPSubtype.PilgrimVIP:
                    baseThreat = Rand.Range(500f, 4300f);
                    break;

                case VIPSubtype.DoppelgangerVIP:
                    baseThreat = Rand.Range(900f, 5000f);
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
                    return "SI_VIP_Name_Psycaster".Translate();

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
            string shuttleSecurityDesc)
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
                    return FormatKeyed("SI_VIP_Desc_Psycaster", workerName);

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
