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
    }
}
