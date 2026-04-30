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

            PlanetTile tile;
            if (!TileFinder.TryFindNewSiteTile(out tile, minDist: 16, maxDist: 36))
                return;

            float threatPoints = GetThreatPoints(subtype, realFaction);
            SitePartDef vipPartDef = GetSitePartDef(subtype);

            /*
             * Для DoppelgangerVIP фракцию НЕ создаём здесь.
             * Она создаётся только при генерации карты сайта в SpawnDoppelgangerVIP().
             */
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

            /*
             * ВАЖНО:
             * Название генерируется один раз.
             * Для двойников это будет что-то вроде:
             * "Парад Опечаленных", "Рой Безликих", "Марш Подменённых".
             */
            string questName = GetQuestName(subtype);

            /*
             * ВАЖНО:
             * Для DoppelgangerVIP метка сайта получает ТО ЖЕ название, что и квест.
             * Именно это меняет верхнюю строку на глобальной карте,
             * которую ты выделил красным на скрине.
             */
            if (subtype == VIPSubtype.DoppelgangerVIP ||
                subtype == VIPSubtype.MechanitorSignalVIP)
            {
                site.customLabel = questName;
            }
            else
            {
                site.customLabel = GetSiteLabel(subtype);
            }

            string questDescription = GetQuestDescription(subtype, workerName, coloredFaction);

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
            quest.WorldObjectTimeout(site, timeoutTicks);

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
                comp.TrackVIPSite(site, threatPoints, realFaction, subtype);
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
                        " | Threat: " + threatPoints);
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
                    "Зов",
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
                    "Ненастоящих",
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

        private string GetQuestDescription(VIPSubtype subtype, string workerName, string coloredFaction)
        {
            switch (subtype)
            {
                case VIPSubtype.ShuttleVIP:
                    return "SI_VIP_Desc_Shuttle".Translate(workerName, coloredFaction);

                case VIPSubtype.PsycasterVIP:
                    return "SI_VIP_Desc_Psycaster".Translate(workerName);

                case VIPSubtype.MechanitorSignalVIP:
                    return "SI_VIP_Desc_MechanitorSignal".Translate(workerName);

                case VIPSubtype.PilgrimVIP:
                    return "SI_VIP_Desc_Pilgrim".Translate(workerName, coloredFaction);

                case VIPSubtype.DoppelgangerVIP:
                    return "SI_VIP_Desc_Doppelganger".Translate(workerName);

                default:
                    return "";
            }
        }

        private string FactionColored(Faction faction)
        {
            string colorHex = ColorUtility.ToHtmlStringRGB(faction.Color);
            return "<color=#" + colorHex + ">" + faction.Name + "</color>";
        }
    }
}
