using RimWorld;
using RimWorld.Planet;
using System.Collections.Generic;
using System.Linq;
using Verse;
using Verse.AI.Group;

namespace SignalInterceptor
{
    public partial class SignalInterceptorGameComponent
    {
        private void SpawnShuttleVIP(Map map, VIPSiteData data)
        {
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

            int tier = GetVIPTier(data.threatPoints);

            BoostPawnSkills(vip, tier);
            AddImplantsToVIP(vip, tier);
            GiveVIPGear(vip, tier);

            GenSpawn.Spawn(vip, vipSpot, map);

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

            Log.Message("[Signal Interceptor] Shuttle VIP spawned: " +
                        vip.LabelShort +
                        " | Tier: " + tier +
                        " | Threat: " + data.threatPoints +
                        " | Position: " + vipSpot);
        }

        private void GiveVIPGear(Pawn vip, int tier)
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

            ThingDef robeDef = DefDatabase<ThingDef>.GetNamedSilentFail("Apparel_RobeRoyal");
            if (robeDef == null)
                robeDef = DefDatabase<ThingDef>.GetNamedSilentFail("Apparel_PrestigeRobe");
            if (robeDef == null)
                robeDef = DefDatabase<ThingDef>.GetNamedSilentFail("Apparel_Cape");

            if (robeDef != null)
            {
                ThingDef stuff = GenStuff.DefaultStuffFor(robeDef);
                Thing robe = ThingMaker.MakeThing(robeDef, stuff);

                QualityCategory quality = QualityCategory.Good;
                if (tier >= 3) quality = QualityCategory.Excellent;
                if (tier >= 5) quality = QualityCategory.Masterwork;
                if (tier >= 8) quality = QualityCategory.Legendary;

                robe.TryGetComp<CompQuality>()?.SetQuality(quality, ArtGenerationContext.Outsider);

                if (robe is Apparel robeApparel)
                {
                    vip.apparel.Wear(robeApparel, dropReplacedApparel: true);
                }
            }
        }

        private void BoostPawnSkills(Pawn pawn, int tier)
        {
            if (pawn.skills == null) return;

            List<SkillRecord> availableSkills = pawn.skills.skills
                .Where(s => !s.TotallyDisabled)
                .ToList();

            if (availableSkills.Count == 0)
                return;

            int boostCount;
            int minLevel;
            int maxLevel;
            float passionChance;
            float majorPassionChance;

            if (tier >= 9)
            {
                boostCount = Rand.RangeInclusive(8, 10);
                minLevel = 17;
                maxLevel = 20;
                passionChance = 0.90f;
                majorPassionChance = 0.70f;
            }
            else if (tier >= 7)
            {
                boostCount = Rand.RangeInclusive(6, 8);
                minLevel = 15;
                maxLevel = 20;
                passionChance = 0.75f;
                majorPassionChance = 0.55f;
            }
            else if (tier >= 5)
            {
                boostCount = Rand.RangeInclusive(5, 7);
                minLevel = 13;
                maxLevel = 18;
                passionChance = 0.60f;
                majorPassionChance = 0.40f;
            }
            else if (tier >= 3)
            {
                boostCount = Rand.RangeInclusive(4, 6);
                minLevel = 11;
                maxLevel = 16;
                passionChance = 0.45f;
                majorPassionChance = 0.25f;
            }
            else
            {
                boostCount = Rand.RangeInclusive(3, 4);
                minLevel = 9;
                maxLevel = 14;
                passionChance = 0.30f;
                majorPassionChance = 0.15f;
            }

            for (int i = 0; i < boostCount && availableSkills.Count > 0; i++)
            {
                SkillRecord skill = availableSkills.RandomElement();
                availableSkills.Remove(skill);

                int targetLevel = Rand.RangeInclusive(minLevel, maxLevel);
                if (skill.Level < targetLevel)
                {
                    skill.Level = targetLevel;
                }

                if (Rand.Chance(passionChance))
                {
                    skill.passion = Rand.Chance(majorPassionChance) ? Passion.Major : Passion.Minor;
                }
            }
        }

        private void AddImplantsToVIP(Pawn pawn, int tier)
        {
            if (pawn.health?.hediffSet == null)
                return;

            int implantCount;

            if (tier >= 9)
                implantCount = Rand.RangeInclusive(8, 10);
            else if (tier >= 7)
                implantCount = Rand.RangeInclusive(6, 8);
            else if (tier >= 5)
                implantCount = Rand.RangeInclusive(4, 6);
            else if (tier >= 3)
                implantCount = Rand.RangeInclusive(2, 4);
            else
                implantCount = Rand.RangeInclusive(1, 2);

            List<ThingDef> implantThings = DefDatabase<ThingDef>.AllDefs
                .Where(d => d.isTechHediff
                         && d.techHediffsTags != null
                         && d.techHediffsTags.Contains("Advanced"))
                .ToList();

            if (implantThings.Count == 0)
                return;

            for (int i = 0; i < implantCount; i++)
            {
                ThingDef implantThing = implantThings.RandomElementWithFallback(null);
                if (implantThing == null)
                    continue;

                RecipeDef recipe = DefDatabase<RecipeDef>.AllDefs
                    .FirstOrDefault(r => r.addsHediff != null
                                      && r.addsHediff.spawnThingOnRemoved == implantThing
                                      && r.appliedOnFixedBodyParts != null
                                      && r.appliedOnFixedBodyParts.Any());

                if (recipe == null)
                    continue;

                BodyPartRecord targetPart = recipe.appliedOnFixedBodyParts
                    .SelectMany(bpd => pawn.RaceProps.body.AllParts.Where(p => p.def == bpd))
                    .Where(p => !pawn.health.hediffSet.HasDirectlyAddedPartFor(p))
                    .RandomElementWithFallback(null);

                if (targetPart == null)
                    continue;

                pawn.health.AddHediff(recipe.addsHediff, targetPart);
            }

            TryAddSpecialVIPImplants(pawn, tier);
        }

        private void TryAddSpecialVIPImplants(Pawn pawn, int tier)
        {
            if (pawn == null || pawn.health?.hediffSet == null)
                return;

            BodyPartRecord brain = pawn.RaceProps.body.AllParts
                .FirstOrDefault(p => p.def.defName == "Brain");

            if (brain == null)
                return;

            if (pawn.health.hediffSet.HasDirectlyAddedPartFor(brain))
                return;

            if (tier >= 9 && Rand.Chance(0.65f))
            {
                HediffDef specialBrain = DefDatabase<HediffDef>.GetNamedSilentFail("ArchotechBrainImplant");
                if (specialBrain == null)
                    specialBrain = DefDatabase<HediffDef>.GetNamedSilentFail("ArchobraineImplant");

                if (specialBrain != null)
                {
                    pawn.health.AddHediff(specialBrain, brain);
                    return;
                }
            }

            if (tier >= 7 && Rand.Chance(0.45f))
            {
                HediffDef learningAssistant = DefDatabase<HediffDef>.GetNamedSilentFail("LearningAssistant");
                if (learningAssistant != null)
                {
                    pawn.health.AddHediff(learningAssistant, brain);
                    return;
                }
            }

            if (tier >= 5 && Rand.Chance(0.25f))
            {
                HediffDef neurocalculator = DefDatabase<HediffDef>.GetNamedSilentFail("Neurocalculator");
                if (neurocalculator != null)
                {
                    pawn.health.AddHediff(neurocalculator, brain);
                }
            }
        }
    }
}
