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

            // Все боевые единицы сигнала должны сразу атаковать игрока.
            // Используем AssaultColony, но ниже EnforceMechanitorSignalCombat будет
            // постоянно сбивать flee/exit jobs, если vanilla AI вдруг попытается увести их с карты.
            List<Pawn> assaultPawns = new List<Pawn>();

            if (!mechs.NullOrEmpty())
            {
                assaultPawns.AddRange(mechs.Where(p => p != null && !p.Dead));
            }

            if (mechanitor != null && !mechanitor.Dead)
            {
                assaultPawns.Add(mechanitor);
            }

            StartMechanitorAssaultLord(map, signalFaction, assaultPawns);

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

            CleanupRogueMechanitorSettlements();
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
                    gestationProcessorLevel = 2;
                    remoteShielderLevel = 0;
                    repairProbeLevel = 2;
                    break;

                case 4:
                    controlSublinkLevel = 2;
                    remoteRepairerLevel = 1;
                    gestationProcessorLevel = 3;
                    remoteShielderLevel = 1;
                    repairProbeLevel = 3;
                    break;

                case 5:
                    controlSublinkLevel = 3;
                    remoteRepairerLevel = 1;
                    gestationProcessorLevel = 4;
                    remoteShielderLevel = 1;
                    repairProbeLevel = 4;
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

            AddOrSetMechanitorImplantQuantity(pawn, "ControlSublinkImplant", controlSublinkLevel);
            AddOrSetMechanitorImplantQuantity(pawn, "RemoteRepairerImplant", remoteRepairerLevel);
            AddOrSetMechanitorImplantQuantity(pawn, "MechFormfeederImplant", gestationProcessorLevel);
            AddOrSetMechanitorImplantQuantity(pawn, "RemoteShielderImplant", remoteShielderLevel);
            AddOrSetMechanitorImplantQuantity(pawn, "RepairProbeImplant", repairProbeLevel);

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

        private bool AddOrSetMechanitorImplantQuantity(Pawn pawn, string defName, int quantity)
        {
            if (pawn?.health?.hediffSet == null)
                return false;

            if (defName.NullOrEmpty())
                return false;

            if (quantity <= 0)
                return true;

            HediffDef hediffDef = DefDatabase<HediffDef>.GetNamedSilentFail(defName);

            if (hediffDef == null)
            {
                Log.Warning("[Signal Interceptor] Mechanitor implant HediffDef not found: " + defName);
                return false;
            }

            try
            {
                int maxQuantity = GetMechanitorImplantMaxQuantity(defName, hediffDef, quantity);
                int finalQuantity = Mathf.Clamp(quantity, 1, maxQuantity);

                List<Hediff> existing = pawn.health.hediffSet.hediffs
                    .Where(h => h != null && h.def == hediffDef)
                    .ToList();

                Hediff hediff = existing.FirstOrDefault();

                for (int i = 1; i < existing.Count; i++)
                {
                    pawn.health.RemoveHediff(existing[i]);
                }

                BodyPartRecord part = FindBestBodyPartForHediff(pawn, hediffDef);

                if (hediff == null)
                {
                    hediff = HediffMaker.MakeHediff(hediffDef, pawn, part);
                    pawn.health.AddHediff(hediff, part);
                }

                SetMechanitorImplantHediffLevel(hediff, finalQuantity);

                Log.Message("[Signal Interceptor] Mechanitor implant quantity set. " +
                            "Pawn=" + pawn.LabelShort +
                            " | Hediff=" + defName +
                            " | Requested=" + quantity +
                            " | Final=" + finalQuantity +
                            " | Max=" + maxQuantity +
                            " | Severity=" + hediff.Severity +
                            " | Type=" + hediff.GetType().Name);

                return true;
            }
            catch (System.Exception ex)
            {
                Log.Warning("[Signal Interceptor] Failed to set mechanitor implant quantity. " +
                            "Pawn=" + pawn.LabelShort +
                            " | Hediff=" + defName +
                            " | Quantity=" + quantity +
                            " | Exception=" + ex);

                return false;
            }
        }

        private int GetMechanitorImplantMaxQuantity(string defName, HediffDef hediffDef, int fallback)
        {
            switch (defName)
            {
                case "ControlSublinkImplant":
                    return 6;

                case "RemoteRepairerImplant":
                    return 3;

                case "MechFormfeederImplant":
                    return 6;

                case "RemoteShielderImplant":
                    return 3;

                case "RepairProbeImplant":
                    return 6;
            }

            if (hediffDef != null && hediffDef.maxSeverity > 1f)
            {
                return Mathf.Max(1, Mathf.RoundToInt(hediffDef.maxSeverity));
            }

            return Mathf.Max(1, fallback);
        }

        private void SetMechanitorImplantHediffLevel(Hediff hediff, int level)
        {
            if (hediff == null)
                return;

            level = Mathf.Max(1, level);

            try
            {
                hediff.Severity = level;

                System.Reflection.BindingFlags flags =
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic;

                System.Type type = hediff.GetType();

                while (type != null)
                {
                    System.Reflection.MethodInfo method = type
                        .GetMethods(flags)
                        .FirstOrDefault(m =>
                            m.Name == "SetLevelTo"
                            && m.GetParameters().Length == 1
                            && m.GetParameters()[0].ParameterType == typeof(int));

                    if (method != null)
                    {
                        method.Invoke(hediff, new object[] { level });
                        hediff.Severity = level;
                        return;
                    }

                    type = type.BaseType;
                }

                type = hediff.GetType();

                while (type != null)
                {
                    System.Reflection.FieldInfo field = type.GetField("level", flags);

                    if (field != null && field.FieldType == typeof(int))
                    {
                        field.SetValue(hediff, level);
                        hediff.Severity = level;
                        return;
                    }

                    type = type.BaseType;
                }
            }
            catch (System.Exception ex)
            {
                Log.Warning("[Signal Interceptor] Failed to set mechanitor implant hediff level. " +
                            "Hediff=" + hediff.def?.defName +
                            " | Level=" + level +
                            " | Exception=" + ex);
            }
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

        private void StartMechanitorAssaultLord(Map map, Faction faction, List<Pawn> pawns)
        {
            if (map == null || faction == null || pawns.NullOrEmpty())
            {
                return;
            }

            List<Pawn> validPawns = pawns
                .Where(p => p != null && !p.Dead && !p.Downed && p.Spawned && p.Map == map)
                .ToList();

            if (validPawns.NullOrEmpty())
            {
                return;
            }

            foreach (Pawn pawn in validPawns)
            {
                try
                {
                    ForceMechanitorPawnNoFlee(pawn, map);

                    if (pawn.jobs != null && pawn.CurJob != null)
                    {
                        pawn.jobs.EndCurrentJob(JobCondition.InterruptForced);
                    }

                    Pawn target = FindNearestPlayerPawnForAttack(pawn, map);
                    if (target != null)
                    {
                        TryForceAttackPawn(pawn, target);
                    }
                }
                catch (System.Exception ex)
                {
                    Log.Warning("[Signal Interceptor] Failed to start no-flee mechanitor combat for " +
                                pawn.LabelShort + ": " + ex);
                }
            }

            Log.Message("[Signal Interceptor] Rogue mechanitor no-flee combat started. Pawns: " + validPawns.Count + ".");
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

            List<Pawn> factionPawns = map.mapPawns.AllPawnsSpawned
                .Where(p => p != null
                         && !p.Dead
                         && !p.Downed
                         && p.Spawned
                         && p.Map == map
                         && p.Faction == faction)
                .ToList();

            if (factionPawns.NullOrEmpty())
            {
                return;
            }

            foreach (Pawn pawn in factionPawns)
            {
                KeepMechanitorCombatPawnFighting(pawn, map);
            }
        }

        private void KeepMechanitorCombatPawnFighting(Pawn pawn, Map map)
        {
            if (pawn == null || pawn.Dead || pawn.Downed || map == null)
            {
                return;
            }

            ForceMechanitorPawnNoFlee(pawn, map);

            bool badJob =
                IsFleeOrExitJob(pawn.CurJob) ||
                IsSuspiciousMapEdgeGotoJob(pawn, map);

            if (badJob && pawn.jobs != null)
            {
                pawn.jobs.EndCurrentJob(JobCondition.InterruptForced);
            }

            Pawn target = FindNearestPlayerPawnForAttack(pawn, map);

            if (target == null)
            {
                return;
            }

            if (ShouldForceNewAttackJob(pawn, target))
            {
                TryForceAttackPawn(pawn, target);
            }
        }

        private void ForceMechanitorPawnNoFlee(Pawn pawn, Map map)
        {
            if (pawn == null || pawn.Dead || pawn.Downed || map == null)
            {
                return;
            }

            if (pawn.mindState == null)
            {
                pawn.mindState = new Pawn_MindState(pawn);
            }

            try
            {
                Lord lord = pawn.GetLord();
                if (lord != null)
                {
                    lord.RemovePawn(pawn);
                }
            }
            catch
            {
                // Не критично. Главное — не дать Lord'у увести пешку с карты.
            }

            pawn.mindState.duty = new PawnDuty(DutyDefOf.AssaultColony);

            try
            {
                System.Reflection.BindingFlags flags =
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic;

                System.Reflection.FieldInfo field = pawn.mindState.GetType().GetField("canFleeIndividual", flags);

                if (field != null && field.FieldType == typeof(bool))
                {
                    field.SetValue(pawn.mindState, false);
                }
            }
            catch
            {
                // В разных версиях RimWorld поле может отличаться. Если его нет — просто игнорируем.
            }
        }

        private bool IsSuspiciousMapEdgeGotoJob(Pawn pawn, Map map)
        {
            if (pawn == null || map == null || pawn.CurJob == null || pawn.CurJob.def == null)
            {
                return false;
            }

            Job job = pawn.CurJob;

            if (job.def != JobDefOf.Goto)
            {
                return false;
            }

            if (!job.targetA.IsValid)
            {
                return false;
            }

            IntVec3 cell = job.targetA.Cell;

            if (!cell.IsValid || !cell.InBounds(map))
            {
                return false;
            }

            return cell.CloseToEdge(map, 5);
        }


        private Pawn FindNearestPlayerPawnForAttack(Pawn attacker, Map map)
        {
            if (attacker == null || map == null)
                return null;

            return map.mapPawns.AllPawnsSpawned
                .Where(p => p != null)
                .Where(p => p.Faction == Faction.OfPlayer)
                .Where(p => !p.Dead && !p.Downed)
                .Where(p => p.Spawned && p.Map == map)
                .OrderBy(p => p.Position.DistanceTo(attacker.Position))
                .FirstOrDefault();
        }

        private bool ShouldForceNewAttackJob(Pawn attacker, Pawn target)
        {
            if (attacker == null || target == null)
                return false;

            Job curJob = attacker.CurJob;

            if (curJob == null)
                return true;

            if (IsFleeOrExitJob(curJob))
                return true;

            if (attacker.Map != null && IsSuspiciousMapEdgeGotoJob(attacker, attacker.Map))
                return true;

            string defName = curJob.def?.defName ?? "";

            bool alreadyAttacking =
                curJob.def == JobDefOf.AttackMelee ||
                curJob.def == JobDefOf.AttackStatic ||
                defName.Contains("Attack");

            if (alreadyAttacking)
            {
                if (!curJob.targetA.HasThing || curJob.targetA.Thing != target)
                    return true;

                Verb attackVerb = attacker.TryGetAttackVerb(target, allowManualCastWeapons: true);

                if (!CanUseAttackFromCurrentPosition(attacker, target, attackVerb))
                    return true;

                return false;
            }

            if (curJob.def == JobDefOf.Goto)
                return false;

            return IsIdleOrWaitJob(curJob);
        }

        private void TryForceAttackPawn(Pawn attacker, Pawn target)
        {
            if (attacker == null || target == null)
                return;

            if (attacker.Dead || attacker.Downed)
                return;

            if (attacker.jobs == null)
                return;

            if (!attacker.Spawned || !target.Spawned || attacker.Map != target.Map)
                return;

            try
            {
                Verb attackVerb = attacker.TryGetAttackVerb(target, allowManualCastWeapons: true);

                if (!CanUseAttackFromCurrentPosition(attacker, target, attackVerb))
                {
                    TryMoveTowardsAttackTarget(attacker, target, attackVerb);
                    return;
                }

                JobDef attackJobDef = attackVerb != null && attackVerb.IsMeleeAttack
                    ? JobDefOf.AttackMelee
                    : JobDefOf.AttackStatic;

                Job job = JobMaker.MakeJob(attackJobDef, target);
                job.expiryInterval = Rand.RangeInclusive(240, 420);
                job.checkOverrideOnExpire = true;

                attacker.jobs.TryTakeOrderedJob(job, JobTag.Misc);
            }
            catch (System.Exception ex)
            {
                Log.Warning("[Signal Interceptor] Failed to force mechanitor signal attack job. Pawn=" +
                            attacker.LabelShort +
                            " | Target=" +
                            target.LabelShort +
                            " | Exception=" + ex);
            }
        }

        private bool CanUseAttackFromCurrentPosition(Pawn attacker, Pawn target, Verb verb)
        {
            if (attacker == null || target == null || attacker.Map == null)
                return false;

            if (!attacker.Position.InBounds(attacker.Map) || !target.Position.InBounds(attacker.Map))
                return false;

            float distance = attacker.Position.DistanceTo(target.Position);

            if (verb == null)
            {
                return distance <= 1.9f;
            }

            if (verb.IsMeleeAttack)
            {
                return attacker.Position.AdjacentTo8WayOrInside(target.Position);
            }

            float range = verb.verbProps?.range ?? 1.9f;

            if (distance > range * 0.95f)
                return false;

            if (!GenSight.LineOfSight(attacker.Position, target.Position, attacker.Map))
                return false;

            return true;
        }

        private void TryMoveTowardsAttackTarget(Pawn attacker, Pawn target, Verb verb)
        {
            if (attacker == null || target == null || attacker.Map == null || attacker.jobs == null)
                return;

            if (attacker.Dead || attacker.Downed)
                return;

            Map map = attacker.Map;

            float range = verb?.verbProps?.range ?? 1.9f;

            int searchRadius;

            if (verb != null && !verb.IsMeleeAttack)
            {
                searchRadius = Mathf.Clamp(Mathf.RoundToInt(range * 0.65f), 5, 18);
            }
            else
            {
                searchRadius = 2;
            }

            IntVec3 moveCell;

            bool found = CellFinder.TryFindRandomCellNear(
                target.Position,
                map,
                searchRadius,
                c => c.Standable(map)
                     && c.GetFirstPawn(map) == null
                     && attacker.CanReach(c, PathEndMode.OnCell, Danger.Deadly),
                out moveCell
            );

            if (!found)
            {
                moveCell = target.Position;
            }

            if (!moveCell.IsValid || !moveCell.InBounds(map) || !moveCell.Standable(map))
                return;

            if (!attacker.CanReach(moveCell, PathEndMode.OnCell, Danger.Deadly))
                return;

            if (attacker.CurJob != null
                && attacker.CurJob.def == JobDefOf.Goto
                && attacker.CurJob.targetA.IsValid
                && attacker.CurJob.targetA.Cell.DistanceTo(moveCell) <= 4f)
            {
                return;
            }

            Job job = JobMaker.MakeJob(JobDefOf.Goto, moveCell);
            job.expiryInterval = Rand.RangeInclusive(180, 300);
            job.checkOverrideOnExpire = true;

            attacker.jobs.TryTakeOrderedJob(job, JobTag.Misc);
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

        private void CleanupRogueMechanitorSettlements()
        {
            List<Settlement> settlements = Find.WorldObjects.AllWorldObjects
                .OfType<Settlement>()
                .Where(s => s != null
                         && s.Faction != null
                         && IsRogueMechanitorFactionDef(s.Faction.def))
                .ToList();

            foreach (Settlement settlement in settlements)
            {
                Log.Warning("[Signal Interceptor] Removing invalid rogue mechanitor settlement: " +
                            settlement.Label +
                            " | tile=" + settlement.Tile +
                            " | faction=" + (settlement.Faction?.Name ?? "null") +
                            " | factionDef=" + (settlement.Faction?.def?.defName ?? "null"));

                Find.WorldObjects.Remove(settlement);
            }
        }

        private void TickRogueMechanitorSettlementCleanup()
        {
            if (Find.TickManager.TicksGame % 250 != 0)
                return;

            CleanupRogueMechanitorSettlements();
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
