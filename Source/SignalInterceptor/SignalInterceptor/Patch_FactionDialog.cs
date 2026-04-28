using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace SignalInterceptor
{
    [HarmonyPatch(typeof(FactionDialogMaker), nameof(FactionDialogMaker.FactionDialogFor))]
    public static class Patch_FactionDialog
    {
        static void Postfix(DiaNode __result, Pawn negotiator, Faction faction)
        {
            if (__result == null || negotiator == null || faction == null)
                return;
            if (negotiator.Map == null)
                return;
            if (faction.HostileTo(Faction.OfPlayer))
                return;

            var comp = Current.Game.GetComponent<SignalInterceptorGameComponent>();
            if (comp == null)
                return;

            var availableStashes = comp.GetActiveStashes()
                .Where(s => s.faction != null
                         && !s.faction.defeated
                         && s.faction != faction
                         && s.faction.HostileTo(faction))
                .ToList();

            if (availableStashes.Count == 0)
                return;

            DiaNode stashListNode = new DiaNode("SI_DialogStashListHeader".Translate(faction.Name));

            foreach (var stash in availableStashes)
            {
                var localStash = stash;
                string tierLabel = GetTierLabel(localStash.tier);
                int goodwillBonus = 5 + (localStash.tier * 4);

                string optionLabel = "SI_DialogStashEntry".Translate(
                    localStash.faction.Name,
                    tierLabel
                );

                bool isCaravanFaction = faction.def.techLevel <= TechLevel.Medieval;

                DiaNode confirmNode = new DiaNode("SI_DialogStashConfirm".Translate(
                    localStash.faction.Name,
                    tierLabel,
                    goodwillBonus.ToString(),
                    faction.Name
                ));

                // В мультиплеере НЕ бросаем Rand в UI —
                // все Rand-вызовы происходят внутри RewardPlayer
                DiaOption confirmYes = new DiaOption("SI_DialogConfirmYes".Translate());
                confirmYes.action = delegate
                {
                    RewardPlayer(faction, localStash.site, negotiator, null);
                };
                confirmYes.resolveTree = true;
                confirmNode.options.Add(confirmYes);

                DiaOption confirmNo = new DiaOption("SI_DialogConfirmNo".Translate());
                confirmNo.link = stashListNode;
                confirmNode.options.Add(confirmNo);

                DiaOption stashOption = new DiaOption(optionLabel);
                stashOption.link = confirmNode;
                stashListNode.options.Add(stashOption);
            }

            DiaOption backOption = new DiaOption("SI_DialogBack".Translate());
            backOption.link = __result;
            stashListNode.options.Add(backOption);

            DiaOption reportOption = new DiaOption("SI_DialogReportStash".Translate());
            reportOption.link = stashListNode;

            int insertIndex = __result.options.Count - 1;
            if (insertIndex < 0) insertIndex = 0;
            __result.options.Insert(insertIndex, reportOption);
        }

        // Этот метод синхронизируется через MP.RegisterSyncMethod.
        // Принимает только типы, которые Multiplayer API умеет сериализовать:
        // Faction, Site, Pawn — всё это RimWorld reference types.
        // TraderKindDef тоже поддерживается (это Def).
        public static void RewardPlayer(Faction ally, Site stashSite, Pawn negotiator, TraderKindDef chosenTrader)
        {
            var comp = Current.Game.GetComponent<SignalInterceptorGameComponent>();
            if (comp == null) return;

            StashSiteData stash = comp.GetActiveStashes()
                .FirstOrDefault(s => s.site == stashSite);

            if (stash == null)
            {
                Log.Warning("[Signal Interceptor] RewardPlayer: stash not found for site.");
                return;
            }

            // 1. Улучшаем отношения
            int goodwillBonus = 5 + (stash.tier * 4);
            HistoryEventDef sharedReason = DefDatabase<HistoryEventDef>.GetNamed("SI_StashInfoShared");
            ally.TryAffectGoodwillWith(
                Faction.OfPlayer,
                goodwillBonus,
                canSendMessage: true,
                canSendHostilityLetter: false,
                reason: sharedReason
            );

            // 2. Награда — ВСЕ Rand-вызовы теперь здесь, внутри синхронизированного метода
            bool isCaravanFaction = ally.def.techLevel <= TechLevel.Medieval;

            if (isCaravanFaction)
            {
                if (chosenTrader == null)
                {
                    chosenTrader = ally.def.caravanTraderKinds?
                        .Where(t => t != null)
                        .RandomElementWithFallback(null);
                }

                GiveRewardCaravan(ally, stash, negotiator, chosenTrader);
            }
            else
            {
                GiveRewardDropPods(ally, stash, negotiator);
            }

            // 3. Завершаем квест
            var quests = Find.QuestManager.QuestsListForReading;
            foreach (var q in quests)
            {
                if (q.State != QuestState.Ongoing)
                    continue;

                foreach (var part in q.PartsListForReading)
                {
                    if (part is QuestPart_FactionGoodwillOnTimeout goodwillPart
                        && goodwillPart.site == stash.site)
                    {
                        goodwillPart.MarkAsReported();
                    }
                }

                bool isLinked = q.QuestLookTargets.Any(t => t.WorldObject == stash.site)
                             || q.PartsListForReading.Any(p =>
                                    p is QuestPart_FactionGoodwillOnTimeout gp && gp.site == stash.site);

                if (isLinked)
                {
                    q.End(QuestEndOutcome.Success, sendLetter: false);
                    break;
                }
            }

            // 4. Удаляем тайник
            if (stash.site != null && stash.site.Spawned)
            {
                stash.site.Destroy();
            }

            // 5. Удаляем из отслеживания
            comp.RemoveStash(stash);

            // 6. Уведомление
            string letterText = isCaravanFaction
                ? "SI_LetterStashReportedTextCaravan".Translate(ally.Name, stash.faction.Name, goodwillBonus.ToString())
                : "SI_LetterStashReportedText".Translate(ally.Name, stash.faction.Name, goodwillBonus.ToString());

            Find.LetterStack.ReceiveLetter(
                "SI_LetterStashReported".Translate(),
                letterText,
                LetterDefOf.PositiveEvent
            );

            // 7. Контрразведка — Rand теперь безопасен внутри SyncMethod
            if (Rand.Chance(0.30f))
            {
                HistoryEventDef compromisedReason = DefDatabase<HistoryEventDef>.GetNamed("SI_StashInfoCompromised");
                int currentGoodwill = stash.faction.GoodwillWith(Faction.OfPlayer);
                int dropAmount = -100 - currentGoodwill;
                if (dropAmount < 0)
                {
                    stash.faction.TryAffectGoodwillWith(
                        Faction.OfPlayer,
                        dropAmount,
                        canSendMessage: true,
                        canSendHostilityLetter: true,
                        reason: compromisedReason
                    );
                }

                int raidDelay = Rand.Range(60000, 180000);
                float raidPoints = System.Math.Max(stash.threatPoints * 0.8f, 300f);
                var gameComp = Current.Game.GetComponent<SignalInterceptorGameComponent>();
                gameComp?.ScheduleCounterIntelRaid(negotiator.Map, stash.faction, raidPoints, raidDelay);

                Find.LetterStack.ReceiveLetter(
                    "SI_LetterCounterIntelTitle".Translate(),
                    "SI_LetterCounterIntel".Translate(stash.faction.Name),
                    LetterDefOf.ThreatBig
                );
            }
        }

        private static string GetTierLabel(int tier)
        {
            switch (tier)
            {
                case 1: return "SI_TierLabel1".Translate();
                case 2: return "SI_TierLabel2".Translate();
                case 3: return "SI_TierLabel3".Translate();
                case 4: return "SI_TierLabel4".Translate();
                case 5: return "SI_TierLabel5".Translate();
                case 6: return "SI_TierLabel6".Translate();
                default: return "SI_TierLabel1".Translate();
            }
        }

        private static void GiveRewardCaravan(Faction ally, StashSiteData stash, Pawn negotiator, TraderKindDef chosenTrader)
        {
            Map map = negotiator.Map;
            int delayTicks = Rand.Range(60000, 120000);

            if (chosenTrader == null)
            {
                Log.Warning("[Signal Interceptor] GiveRewardCaravan: chosenTrader is null for faction " + ally.Name + ". Falling back to drop pod reward.");
                GiveRewardDropPods(ally, stash, negotiator);
                return;
            }

            IncidentParms parms = new IncidentParms();
            parms.target = map;
            parms.faction = ally;
            parms.traderKind = chosenTrader;

            Find.Storyteller.incidentQueue.Add(
                IncidentDefOf.TraderCaravanArrival,
                Find.TickManager.TicksGame + delayTicks,
                parms
            );

            Find.LetterStack.ReceiveLetter(
                "SI_LetterTribalTraderTitle".Translate(),
                "SI_LetterTribalTraderText".Translate(ally.Name),
                LetterDefOf.PositiveEvent
            );

            float slaveChance = 0.02f + (stash.tier - 1) * 0.026f;
            if (ModsConfig.IdeologyActive && ally.def.techLevel <= TechLevel.Medieval && Rand.Chance(slaveChance))
            {
                var comp = Current.Game.GetComponent<SignalInterceptorGameComponent>();
                comp?.ScheduleSlaveDelivery(map, delayTicks);

                Find.LetterStack.ReceiveLetter(
                    "SI_LetterSlaveTitle".Translate(),
                    "SI_LetterSlaveText".Translate(ally.Name),
                    LetterDefOf.PositiveEvent
                );
            }
        }

        private static void GiveRewardDropPods(Faction ally, StashSiteData stash, Pawn negotiator)
        {
            Map map = negotiator.Map;
            IntVec3 dropSpot = DropCellFinder.TradeDropSpot(map);
            List<Thing> rewards = new List<Thing>();

            int tier = stash.tier;

            int silverAmount;
            switch (tier)
            {
                case 1: silverAmount = Rand.RangeInclusive(20, 50); break;
                case 2: silverAmount = Rand.RangeInclusive(50, 90); break;
                case 3: silverAmount = Rand.RangeInclusive(90, 140); break;
                case 4: silverAmount = Rand.RangeInclusive(140, 200); break;
                case 5: silverAmount = Rand.RangeInclusive(200, 300); break;
                case 6: silverAmount = Rand.RangeInclusive(300, 500); break;
                default: silverAmount = 40; break;
            }
            Thing silver = ThingMaker.MakeThing(ThingDefOf.Silver);
            silver.stackCount = silverAmount;
            rewards.Add(silver);

            if (tier >= 2)
            {
                Thing gold = ThingMaker.MakeThing(ThingDefOf.Gold);
                switch (tier)
                {
                    case 2: gold.stackCount = Rand.RangeInclusive(2, 4); break;
                    case 3: gold.stackCount = Rand.RangeInclusive(4, 7); break;
                    case 4: gold.stackCount = Rand.RangeInclusive(7, 11); break;
                    case 5: gold.stackCount = Rand.RangeInclusive(11, 16); break;
                    case 6: gold.stackCount = Rand.RangeInclusive(16, 24); break;
                    default: gold.stackCount = 2; break;
                }
                rewards.Add(gold);
            }

            if (tier >= 2)
            {
                Thing components = ThingMaker.MakeThing(ThingDefOf.ComponentIndustrial);
                switch (tier)
                {
                    case 2: components.stackCount = Rand.RangeInclusive(1, 2); break;
                    case 3: components.stackCount = Rand.RangeInclusive(2, 4); break;
                    case 4: components.stackCount = Rand.RangeInclusive(4, 6); break;
                    case 5: components.stackCount = Rand.RangeInclusive(6, 8); break;
                    case 6: components.stackCount = Rand.RangeInclusive(8, 11); break;
                    default: components.stackCount = 1; break;
                }
                rewards.Add(components);
            }

            if (tier >= 3)
            {
                Thing plasteel = ThingMaker.MakeThing(ThingDefOf.Plasteel);
                switch (tier)
                {
                    case 3: plasteel.stackCount = Rand.RangeInclusive(4, 8); break;
                    case 4: plasteel.stackCount = Rand.RangeInclusive(8, 13); break;
                    case 5: plasteel.stackCount = Rand.RangeInclusive(13, 18); break;
                    case 6: plasteel.stackCount = Rand.RangeInclusive(18, 26); break;
                    default: plasteel.stackCount = 4; break;
                }
                rewards.Add(plasteel);
            }

            if (tier >= 4 && Rand.Chance(0.5f))
            {
                Thing advComp = ThingMaker.MakeThing(ThingDefOf.ComponentSpacer);
                switch (tier)
                {
                    case 4: advComp.stackCount = 1; break;
                    case 5: advComp.stackCount = Rand.RangeInclusive(1, 2); break;
                    case 6: advComp.stackCount = Rand.RangeInclusive(2, 3); break;
                    default: advComp.stackCount = 1; break;
                }
                rewards.Add(advComp);
            }

            if (tier >= 4)
            {
                float bionicChance = 0.2f + (tier - 4) * 0.15f;
                int bionicCount = (tier >= 6) ? 2 : 1;
                for (int i = 0; i < bionicCount; i++)
                {
                    if (Rand.Chance(bionicChance))
                    {
                        ThingDef bionic = DefDatabase<ThingDef>.AllDefsListForReading
                            .Where(d => d.isTechHediff
                                     && d.techHediffsTags != null
                                     && d.techHediffsTags.Contains("Advanced"))
                            .RandomElementWithFallback(null);
                        if (bionic != null)
                            rewards.Add(ThingMaker.MakeThing(bionic));
                    }
                }
            }

            if (tier >= 5 && ModsConfig.IsActive("Ludeon.RimWorld.Odyssey"))
            {
                float weaponChance = (tier == 5) ? 0.15f : 0.30f;
                if (Rand.Chance(weaponChance))
                {
                    Thing weapon = SignalInterceptorUtility.TryGenerateUniqueWeapon();
                    if (weapon != null)
                        rewards.Add(weapon);
                }
            }

            if (rewards.Count > 0)
            {
                DropPodUtility.DropThingsNear(
                    dropSpot,
                    map,
                    rewards,
                    110,
                    canInstaDropDuringInit: false,
                    leaveSlag: false,
                    canRoofPunch: true
                );
            }
        }
    }
}
