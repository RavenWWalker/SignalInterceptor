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
    public partial class SignalInterceptorGameComponent
    {
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

    }
}
