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

            return Find.FactionManager.AllFactions
                .Any(f => !f.IsPlayer
                       && !f.defeated
                       && !f.Hidden
                       && !f.temporary
                       && f.def.humanlikeFaction);
        }

        protected override void RunInt()
        {
            Quest quest = QuestGen.quest;
            Slate slate = QuestGen.slate;

            Map map = slate.Get<Map>("map");
            Pawn worker = slate.Get<Pawn>("worker");

            Faction realFaction = Find.FactionManager.AllFactions
                .Where(f => !f.IsPlayer
                         && !f.defeated
                         && !f.Hidden
                         && !f.temporary
                         && f.def.humanlikeFaction)
                .RandomElement();

            VIPSubtype subtype = ChooseSubtype(realFaction);

            Settlement shuttleOrigin = null;
            Settlement shuttleDestination = null;

            PlanetTile tile;

            if (subtype == VIPSubtype.ShuttleVIP)
            {
                if (!TryFindShuttleRouteTile(map, realFaction, out tile, out shuttleOrigin, out shuttleDestination))
                {
                    if (!TileFinder.TryFindNewSiteTile(out tile, minDist: 16, maxDist: 36))
                        return;
                }
            }
            else
            {
                if (!TileFinder.TryFindNewSiteTile(out tile, minDist: 16, maxDist: 36))
                    return;
            }

            float threatPoints = GetThreatPoints(subtype, realFaction);
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

            int timeoutTicks = TimeoutDaysRange.RandomInRange * 60000;

            string workerName = worker?.LabelShort ?? "A colonist";
            string coloredFaction = FactionColored(realFaction);

            string originName = shuttleOrigin != null
                ? shuttleOrigin.LabelCap.ToString()
                : "неизвестного поселения";

            string destinationName = shuttleDestination != null
                ? shuttleDestination.LabelCap.ToString()
                : "другого поселения";

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
                destinationName
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
                        " | Shuttle origin: " + (shuttleOrigin?.LabelCap.ToString() ?? "null") +
                        " | Shuttle destination: " + (shuttleDestination?.LabelCap.ToString() ?? "null"));
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

            List<Settlement> settlements = Find.WorldObjects.Settlements
                .Where(s => s != null
                         && !s.Destroyed
                         && s.Faction == faction
                         && s.Tile.Valid)
                .OrderBy(s => Find.WorldGrid.ApproxDistanceInTiles(playerTile, s.Tile))
                .Take(2)
                .ToList();

            if (settlements.Count < 2)
                return false;

            origin = settlements[0];
            destination = settlements[1];

            float routeDistance = Find.WorldGrid.ApproxDistanceInTiles(origin.Tile, destination.Tile);

            if (routeDistance <= 0f)
                return false;

            List<PlanetTile> candidates = new List<PlanetTile>();

            /*
             * Ищем тайлы, которые лежат примерно на прямом маршруте между двумя поселениями.
             * Условие:
             * distance(origin -> tile) + distance(tile -> destination)
             * примерно равно distance(origin -> destination).
             */
            int tilesCount = Find.WorldGrid.TilesCount;

            for (int i = 0; i < tilesCount; i++)
            {
                PlanetTile tile = new PlanetTile(i);

                if (!IsValidSiteTile(tile))
                    continue;

                float distFromOrigin = Find.WorldGrid.ApproxDistanceInTiles(origin.Tile, tile);
                float distToDestination = Find.WorldGrid.ApproxDistanceInTiles(tile, destination.Tile);

                if (distFromOrigin < 3f || distToDestination < 3f)
                    continue;

                float totalRouteDist = distFromOrigin + distToDestination;
                float deviation = totalRouteDist - routeDistance;

                /*
                 * До 2 тайлов погрешности — достаточно похоже на прямую линию.
                 */
                if (deviation >= 0f && deviation <= 2f)
                {
                    candidates.Add(tile);
                }
            }

            if (candidates.Count == 0)
            {
                Log.Warning("[Signal Interceptor] Could not find shuttle route tile between settlements. Falling back to default site tile.");
                return false;
            }

            resultTile = candidates.RandomElement();

            Log.Message("[Signal Interceptor] Shuttle route tile selected. " +
                        "Origin: " + origin.LabelCap +
                        " | Destination: " + destination.LabelCap +
                        " | Route distance: " + routeDistance +
                        " | Candidates: " + candidates.Count +
                        " | Tile: " + resultTile);

            return true;
        }

        private bool IsValidSiteTile(PlanetTile tile)
        {
            if (!tile.Valid)
                return false;

            if (Find.WorldGrid[tile].WaterCovered)
                return false;

            if (Find.WorldGrid[tile].hilliness == Hilliness.Impassable)
                return false;

            if (Find.WorldObjects.ObjectsAt(tile).Any())
                return false;

            return true;
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

        private VIPSubtype ChooseSubtype(Faction faction)
        {
            List<VIPSubtype> available = new List<VIPSubtype>();

            if (faction.def.techLevel >= TechLevel.Industrial)
                available.Add(VIPSubtype.ShuttleVIP);

            if (ModsConfig.RoyaltyActive)
                available.Add(VIPSubtype.PsycasterVIP);

            if (ModsConfig.BiotechActive)
                available.Add(VIPSubtype.MechanitorSignalVIP);

            if (ModsConfig.IdeologyActive)
                available.Add(VIPSubtype.PilgrimVIP);

            if (ModsConfig.AnomalyActive)
                available.Add(VIPSubtype.DoppelgangerVIP);

            if (available.Count == 0)
                available.Add(VIPSubtype.ShuttleVIP);

            return available.RandomElement();
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
                    baseThreat = Rand.Range(450f, 4200f);
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

        private string GetQuestName(VIPSubtype subtype)
        {
            switch (subtype)
            {
                case VIPSubtype.ShuttleVIP:
                    return "SI_VIP_Name_Shuttle".Translate();

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
            string destinationSettlement)
        {
            switch (subtype)
            {
                case VIPSubtype.ShuttleVIP:
                    return FormatKeyed(
                        "SI_VIP_Desc_Shuttle",
                        workerName,
                        coloredFaction,
                        originSettlement,
                        destinationSettlement
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
    }
}
