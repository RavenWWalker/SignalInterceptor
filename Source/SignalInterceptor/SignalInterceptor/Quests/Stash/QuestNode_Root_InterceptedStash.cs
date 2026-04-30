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
    public class QuestNode_Root_InterceptedStash : QuestNode
    {
        private static readonly IntRange TimeoutDaysRange = new IntRange(10, 20);
        private static readonly IntRange GoodwillBonusRange = new IntRange(10, 20);
        private static readonly FloatRange BaseThreatRange = new FloatRange(300f, 1800f);

        private static readonly string[] nameAdjectiveKeys = new string[]
        {
            "SI_NameAdj_Hidden", "SI_NameAdj_Secret", "SI_NameAdj_Concealed",
            "SI_NameAdj_Buried", "SI_NameAdj_Forgotten", "SI_NameAdj_Abandoned",
            "SI_NameAdj_Remote", "SI_NameAdj_Distant", "SI_NameAdj_Covert", "SI_NameAdj_Classified"
        };

        private static readonly string[] nameNounKeys = new string[]
        {
            "SI_NameNoun_Stash", "SI_NameNoun_Cache", "SI_NameNoun_Stockpile",
            "SI_NameNoun_SupplyDepot", "SI_NameNoun_Storehouse", "SI_NameNoun_Reserves",
            "SI_NameNoun_Trove", "SI_NameNoun_Hoard", "SI_NameNoun_Arsenal", "SI_NameNoun_Vault"
        };

        private static readonly string[] siteNameKeys = new string[]
        {
            "SI_SiteName1", "SI_SiteName2", "SI_SiteName3", "SI_SiteName4",
            "SI_SiteName5", "SI_SiteName6", "SI_SiteName7", "SI_SiteName8"
        };

        protected override bool TestRunInt(Slate slate)
        {
            Map map = slate.Get<Map>("map");
            if (map == null)
                return false;

            return Find.FactionManager.AllFactions
                .Any(f => !f.IsPlayer && !f.defeated && !f.Hidden && f.def.humanlikeFaction);
        }

        protected override void RunInt()
        {
            Quest quest = QuestGen.quest;
            Slate slate = QuestGen.slate;

            Map map = slate.Get<Map>("map");
            Pawn worker = slate.Get<Pawn>("worker");

            Faction faction = Find.FactionManager.AllFactions
                .Where(f => !f.IsPlayer && !f.defeated && !f.Hidden && f.def.humanlikeFaction)
                .RandomElement();

            PlanetTile tile;
            if (!TileFinder.TryFindNewSiteTile(out tile, minDist: 4, maxDist: 20))
                return;

            float baseThreat = BaseThreatRange.RandomInRange;
            float factionMultiplier = GetFactionMultiplier(faction);
            float threatPoints = baseThreat * factionMultiplier;

            if (threatPoints < 200f) threatPoints = 200f;
            if (threatPoints > 3000f) threatPoints = 3000f;

            int tier = GetTier(threatPoints);

            Log.Message("[Signal Interceptor] Stash quest generated. Faction: " + faction.Name +
                        " (" + faction.def.techLevel + ") | Base threat: " + baseThreat +
                        " | Multiplier: " + factionMultiplier +
                        " | Final threat: " + threatPoints + " | Tier: " + tier);

            SitePartDef stashPartDef = DefDatabase<SitePartDef>.GetNamed("SecretStash");
            Site site = SiteMaker.MakeSite(
                stashPartDef,
                tile,
                faction,
                threatPoints: threatPoints
            );

            // Кастомное название сайта из локализации
            string siteNameKey = siteNameKeys[Rand.Range(0, siteNameKeys.Length)];
            site.customLabel = siteNameKey.Translate();

            int timeoutTicks = TimeoutDaysRange.RandomInRange * 60000;
            int goodwillBonus = GoodwillBonusRange.RandomInRange;

            string coloredFaction = FactionColored(faction);
            string workerName = worker?.LabelShort ?? "A colonist";
            bool isFriendly = !faction.HostileTo(Faction.OfPlayer);

            string adjKey = nameAdjectiveKeys.RandomElement();
            string nounKey = nameNounKeys.RandomElement();
            string questName = adjKey.Translate() + " " + nounKey.Translate();

            string tierDescription = GetTierDescription(tier);

            string descTemplate;
            if (isFriendly)
                descTemplate = (string)"SI_QuestDescFriendly".Translate();
            else
                descTemplate = (string)"SI_QuestDescHostile".Translate();

            string questDescription = string.Format(descTemplate, workerName, coloredFaction, tierDescription);

            List<Rule> nameRules = new List<Rule>();
            nameRules.Add(new Rule_String("questName", questName));
            QuestGen.AddQuestNameRules(nameRules);

            List<Rule> descRules = new List<Rule>();
            descRules.Add(new Rule_String("questDescription", questDescription));
            QuestGen.AddQuestDescriptionRules(descRules);

            string questTag = QuestGenUtility.HardcodedTargetQuestTagWithQuestID("InterceptedStash");
            QuestUtility.AddQuestTag(ref site.questTags, questTag);

            string allEnemiesDefeatedSignal = QuestGenUtility.QuestTagSignal(questTag, "AllEnemiesDefeated");
            string mapRemovedSignal = QuestGenUtility.QuestTagSignal(questTag, "MapRemoved");

            quest.SpawnWorldObject(site);
            // Не завершать квест при смене отношений с фракцией
            site.factionMustRemainHostile = false;
            quest.WorldObjectTimeout(site, timeoutTicks);

            quest.SignalPassAllSequence(delegate
            {
                quest.End(QuestEndOutcome.Success, 0, null, null, QuestPart.SignalListenMode.OngoingOnly, sendStandardLetter: true);
            }, new List<string> { allEnemiesDefeatedSignal, mapRemovedSignal });

            QuestPart_FactionGoodwillOnTimeout questPart = new QuestPart_FactionGoodwillOnTimeout();
            questPart.faction = faction;
            questPart.goodwillBonus = goodwillBonus;
            questPart.site = site;
            questPart.isFriendly = isFriendly;
            quest.AddPart(questPart);

            string siteDestroyedSignal = QuestGenUtility.HardcodedSignalWithQuestID("site.Destroyed");
            quest.SignalPass(delegate
            {
                quest.End(QuestEndOutcome.Fail, 0, null, null, QuestPart.SignalListenMode.OngoingOnly, sendStandardLetter: true);
            }, siteDestroyedSignal);

            // Регистрируем сайт в GameComponent для спавна лута
            SignalInterceptorGameComponent comp = Current.Game.GetComponent<SignalInterceptorGameComponent>();
            if (comp != null)
            {
                comp.TrackSite(site, threatPoints, faction, tier);
            }

            slate.Set("site", site);
            slate.Set("faction", faction);
            slate.Set("timeoutTicks", timeoutTicks);
        }

        private float GetFactionMultiplier(Faction faction)
        {
            TechLevel tech = faction.def.techLevel;

            if (tech <= TechLevel.Medieval)
                return 0.5f;

            if (faction.def.permanentEnemy)
                return 1.5f;

            if (tech >= TechLevel.Spacer)
                return 1.5f;

            return 1.0f;
        }

        private int GetTier(float threatPoints)
        {
            if (threatPoints >= 2200f) return 6;
            if (threatPoints >= 1700f) return 5;
            if (threatPoints >= 1200f) return 4;
            if (threatPoints >= 800f) return 3;
            if (threatPoints >= 450f) return 2;
            return 1;
        }

        private string GetTierDescription(int tier)
        {
            switch (tier)
            {
                case 1: return "SI_TierDesc1".Translate();
                case 2: return "SI_TierDesc2".Translate();
                case 3: return "SI_TierDesc3".Translate();
                case 4: return "SI_TierDesc4".Translate();
                case 5: return "SI_TierDesc5".Translate();
                case 6: return "SI_TierDesc6".Translate();
                default: return "The stash contains supplies of unknown value.";
            }
        }

        private string FactionColored(Faction faction)
        {
            string colorHex = ColorUtility.ToHtmlStringRGB(faction.Color);
            return "<color=#" + colorHex + ">" + faction.Name + "</color>";
        }
    }
}
