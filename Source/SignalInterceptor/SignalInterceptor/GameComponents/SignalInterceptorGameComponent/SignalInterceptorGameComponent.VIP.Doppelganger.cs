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
        private void CleanupDoppelgangerSettlements()
        {
            List<Settlement> settlements = Find.WorldObjects.AllWorldObjects
                .OfType<Settlement>()
                .Where(s => s.Faction != null
                         && IsDoppelgangerFactionDef(s.Faction.def))
                .ToList();

            foreach (Settlement settlement in settlements)
            {
                Log.Warning("[Signal Interceptor] Removing invalid doppelganger settlement: " +
                            settlement.Label +
                            " | tile=" + settlement.Tile +
                            " | faction=" + (settlement.Faction?.Name ?? "null") +
                            " | factionDef=" + (settlement.Faction?.def?.defName ?? "null"));

                Find.WorldObjects.Remove(settlement);
            }
        }

        private void SpawnDoppelgangerVIP(Map map, VIPSiteData data)
        {
            int cloneCount = Rand.RangeInclusive(10, 15);

            PawnKindDef templateKind = PawnKindDefOf.Colonist;

            /*
             * Выбираем ксенотип один раз.
             * Этот же ксенотип:
             * 1) выбирает FactionDef;
             * 2) применяется к шаблону;
             * 3) копируется всем клонам через CopyTemplate().
             */
            XenotypeDef chosenXenotype = ChooseDoppelgangerXenotype();

            Faction cloneFaction = CreateDoppelgangerFactionForMap(chosenXenotype);

            if (cloneFaction == null)
            {
                Log.Error("[Signal Interceptor] Failed to create doppelganger faction. Doppelganger VIP spawn aborted.");

                data.rewardGiven = true;
                data.vipSpawned = true;
                data.enemyFaction = null;

                return;
            }

            data.enemyFaction = cloneFaction;

            if (data.site != null)
            {
                data.site.SetFaction(cloneFaction);
                data.site.factionMustRemainHostile = false;
            }

            Log.Message("[Signal Interceptor] Doppelganger spawn faction check:" +
                        " | site=" + (data.site?.LabelCap ?? "null") +
                        " | siteFaction=" + (data.site?.Faction?.Name ?? "null") +
                        " | cloneFaction=" + (cloneFaction?.Name ?? "null") +
                        " | factionDef=" + (cloneFaction?.def?.defName ?? "null") +
                        " | contextFaction=" + (data.faction?.Name ?? "null") +
                        " | chosenXenotype=" + (chosenXenotype?.defName ?? "none") +
                        " | temporary=" + cloneFaction.temporary +
                        " | hiddenField=" + cloneFaction.hidden +
                        " | HiddenProperty=" + cloneFaction.Hidden +
                        " | leader=" + (cloneFaction.leader?.LabelShort ?? "null"));

            PawnGenerationRequest templateRequest = new PawnGenerationRequest(
                kind: templateKind,
                faction: cloneFaction,
                context: PawnGenerationContext.NonPlayer,
                forceGenerateNewPawn: true,
                canGeneratePawnRelations: false,
                allowFood: true,
                mustBeCapableOfViolence: true
            );

            Pawn template = PawnGenerator.GeneratePawn(templateRequest);
            if (template == null)
            {
                Log.Error("[Signal Interceptor] Failed to generate doppelganger template pawn.");
                DeactivateDoppelgangerFaction(cloneFaction);
                data.enemyFaction = null;
                return;
            }

            ApplyDoppelgangerTemplateXenotype(template, chosenXenotype);

            template.SetFactionDirect(cloneFaction);

            if (template.ageTracker.AgeBiologicalYears < 25)
            {
                template.ageTracker.AgeBiologicalTicks = 25 * 3600000L;
                template.ageTracker.AgeChronologicalTicks = 25 * 3600000L;
            }

            BoostPawnSkills(template);

            IntVec3 baseCenter = map.Center;

            IntVec3 foundCenter;
            bool found = CellFinder.TryFindRandomCellNear(
                map.Center,
                map,
                20,
                c => c.Standable(map) && !c.Roofed(map),
                out foundCenter
            );

            if (found)
            {
                baseCenter = foundCenter;
            }

            Name templateName = template.Name;
            Color templateHairColor = template.story?.HairColor ?? Color.white;
            HairDef templateHair = template.story?.hairDef;
            BeardDef templateBeard = template.style?.beardDef;
            HeadTypeDef templateHead = template.story?.headType;
            BodyTypeDef templateBody = template.story?.bodyType;
            Color templateSkinColor = template.story?.SkinColorBase ?? Color.white;
            Gender templateGender = template.gender;

            if (template.health?.hediffSet != null)
            {
                List<Hediff> templateInjuries = template.health.hediffSet.hediffs
                    .Where(h => h is Hediff_Injury || h is Hediff_MissingPart)
                    .ToList();

                foreach (Hediff h in templateInjuries)
                {
                    template.health.RemoveHediff(h);
                }
            }

            List<Pawn> allClones = new List<Pawn>();

            for (int i = 0; i < cloneCount; i++)
            {
                PawnGenerationRequest cloneRequest = new PawnGenerationRequest(
                    kind: templateKind,
                    faction: cloneFaction,
                    context: PawnGenerationContext.NonPlayer,
                    forceGenerateNewPawn: true,
                    canGeneratePawnRelations: false,
                    allowFood: true,
                    mustBeCapableOfViolence: true,
                    fixedGender: templateGender
                );

                Pawn clone = PawnGenerator.GeneratePawn(cloneRequest);
                if (clone == null)
                    continue;

                clone.SetFactionDirect(cloneFaction);

                CopyTemplate(template, clone);

                CopyAppearance(
                    clone,
                    templateName,
                    templateHairColor,
                    templateHair,
                    templateBeard,
                    templateHead,
                    templateBody,
                    templateSkinColor,
                    templateGender
                );

                /*
                 * ВАЖНО:
                 * После CopyTemplate/CopyAppearance на всякий случай фиксируем фракцию напрямую.
                 * SetFactionDirect не кидает warning при той же фракции.
                 */
                clone.SetFactionDirect(cloneFaction);

                GiveCloneRandomWeapon(clone, data.threatPoints);
                GiveCloneArmor(clone, data.threatPoints);

                ApplyCloneFragility(clone);
                AddDoppelgangerMark(clone);

                IntVec3 cloneSpot = baseCenter;

                bool foundSpot = CellFinder.TryFindRandomCellNear(
                    baseCenter,
                    map,
                    12,
                    c => c.Standable(map) && c.GetFirstPawn(map) == null,
                    out cloneSpot
                );

                if (!foundSpot)
                {
                    cloneSpot = CellFinder.RandomClosewalkCellNear(baseCenter, map, 8);
                }

                GenSpawn.Spawn(clone, cloneSpot, map);

                /*
                 * НЕ вызываем clone.SetFaction(cloneFaction), если фракция уже такая же.
                 * Иначе RimWorld пишет warning:
                 * "Used SetFaction to change pawn to same faction"
                 * и открывает консоль.
                 */
                if (clone.Faction != cloneFaction)
                {
                    clone.SetFaction(cloneFaction);
                }

                allClones.Add(clone);

                Log.Message("[Signal Interceptor] Doppelganger clone spawned: " +
                            clone.LabelShort +
                            " | faction=" + (clone.Faction?.Name ?? "null") +
                            " | expectedFaction=" + cloneFaction.Name +
                            " | factionDef=" + (clone.Faction?.def?.defName ?? "null") +
                            " | factionHidden=" + (clone.Faction?.Hidden.ToString() ?? "null") +
                            " | factionTemporary=" + (clone.Faction?.temporary.ToString() ?? "null") +
                            " | factionHiddenField=" + (clone.Faction?.hidden.ToString() ?? "null") +
                            " | xenotype=" + (ModsConfig.BiotechActive && clone.genes != null
                                ? clone.genes.XenotypeLabelCap.ToString()
                                : "none"));
            }

            if (allClones.Count == 0)
            {
                Log.Error("[Signal Interceptor] No doppelganger clones were spawned.");
                DeactivateDoppelgangerFaction(cloneFaction);
                data.enemyFaction = null;
                return;
            }

            LordJob_DefendPoint lordJob = new LordJob_DefendPoint(baseCenter);
            Lord lord = LordMaker.MakeNewLord(cloneFaction, lordJob, map);

            foreach (Pawn clone in allClones)
            {
                lord.AddPawn(clone);
            }

            SpawnCampProps(map, baseCenter);

            Find.LetterStack.ReceiveLetter(
                "SI_Doppelganger_Title".Translate(),
                "SI_Doppelganger_Text".Translate(
                    allClones.Count.ToString(),
                    cloneFaction.Name ?? GetDoppelgangerFactionName()
                ),
                LetterDefOf.ThreatBig,
                new LookTargets(allClones.First())
            );

            Log.Message("[Signal Interceptor] Doppelganger VIP spawned: " +
                        allClones.Count +
                        " clones of " + templateName +
                        " | clone faction: " + cloneFaction.Name +
                        " | clone faction def: " + cloneFaction.def.defName +
                        " | chosen xenotype: " + (chosenXenotype?.defName ?? "none") +
                        " | template xenotype: " + (ModsConfig.BiotechActive && template.genes != null
                            ? template.genes.XenotypeLabelCap.ToString()
                            : "none") +
                        " | site faction: " + (data.site?.Faction?.Name ?? "null") +
                        " | site faction equals clone faction: " + (data.site?.Faction == cloneFaction));
        }

        private string GetDoppelgangerFactionName()
        {
            const string key = "SI_Doppelganger_FactionName";

            if (key.CanTranslate())
                return key.Translate().ToString();

            return "Anomalous doppelgangers";
        }

        private FactionDef GetDoppelgangerFactionDef(XenotypeDef xenotype)
        {
            string defName = "SI_DoppelgangerFaction";

            if (ModsConfig.BiotechActive && xenotype != null)
            {
                switch (xenotype.defName)
                {
                    case "Hussar":
                        defName = "SI_DoppelgangerFaction_Hussar";
                        break;

                    case "Pigskin":
                        defName = "SI_DoppelgangerFaction_Pigskin";
                        break;

                    case "Impid":
                        defName = "SI_DoppelgangerFaction_Impid";
                        break;

                    case "Yttakin":
                        defName = "SI_DoppelgangerFaction_Yttakin";
                        break;

                    case "Waster":
                        defName = "SI_DoppelgangerFaction_Waster";
                        break;

                    case "Dirtmole":
                        defName = "SI_DoppelgangerFaction_Dirtmole";
                        break;

                    case "Neanderthal":
                        defName = "SI_DoppelgangerFaction_Neanderthal";
                        break;

                    case "Starjack":
                        defName = "SI_DoppelgangerFaction_Starjack";
                        break;

                    case "Baseliner":
                        defName = "SI_DoppelgangerFaction_Baseliner";
                        break;
                }
            }

            FactionDef def = DefDatabase<FactionDef>.GetNamedSilentFail(defName);
            if (def != null)
                return def;

            Log.Warning("[Signal Interceptor] Doppelganger faction def not found: " + defName +
                        ". Falling back to SI_DoppelgangerFaction.");

            def = DefDatabase<FactionDef>.GetNamedSilentFail("SI_DoppelgangerFaction");
            if (def != null)
                return def;

            Log.Warning("[Signal Interceptor] SI_DoppelgangerFaction FactionDef not found. Falling back to Pirate.");
            return FactionDefOf.Pirate;
        }

        private Faction CreateDoppelgangerFactionForMap(XenotypeDef chosenXenotype)
        {
            FactionDef wantedDef = GetDoppelgangerFactionDef(chosenXenotype);

            FactionDef generatorDef = wantedDef;

            if (generatorDef == null || generatorDef.factionNameMaker == null)
            {
                Log.Warning("[Signal Interceptor] Doppelganger faction def has no factionNameMaker. " +
                            "Using Pirate as generator base, then overriding faction.def.");

                generatorDef = FactionDefOf.Pirate;
            }

            Faction faction = null;

            try
            {
                faction = FactionGenerator.NewGeneratedFaction(
                    new FactionGeneratorParms(generatorDef)
                );
            }
            catch (System.Exception ex)
            {
                Log.Error("[Signal Interceptor] Failed to generate doppelganger faction through FactionGenerator. " +
                          "Fallback to Pirate generator. Exception: " + ex);

                faction = FactionGenerator.NewGeneratedFaction(
                    new FactionGeneratorParms(FactionDefOf.Pirate)
                );
            }

            if (faction == null)
            {
                Log.Error("[Signal Interceptor] FactionGenerator returned null for doppelganger faction.");
                return null;
            }

            if (wantedDef != null)
            {
                faction.def = wantedDef;
            }

            faction.temporary = true;
            faction.hidden = false;
            faction.defeated = false;
            faction.Name = GenerateDoppelgangerFactionName();
            faction.leader = null;

            if (!Find.FactionManager.AllFactions.Contains(faction))
            {
                Find.FactionManager.Add(faction);
            }

            CleanupDoppelgangerSettlements();

            faction.TryMakeInitialRelationsWith(Faction.OfPlayer);

            faction.SetRelationDirect(
                Faction.OfPlayer,
                FactionRelationKind.Hostile,
                canSendHostilityLetter: false
            );

            foreach (Faction other in Find.FactionManager.AllFactions)
            {
                if (other == null || other == faction || other == Faction.OfPlayer)
                    continue;

                faction.TryMakeInitialRelationsWith(other);

                FactionRelation rel = faction.RelationWith(other, allowNull: true);
                if (rel != null)
                {
                    rel.baseGoodwill = 0;
                    rel.kind = FactionRelationKind.Neutral;
                }

                FactionRelation otherRel = other.RelationWith(faction, allowNull: true);
                if (otherRel != null)
                {
                    otherRel.baseGoodwill = 0;
                    otherRel.kind = FactionRelationKind.Neutral;
                }
            }

            Log.Message("[Signal Interceptor] Created doppelganger map faction: " +
                        faction.Name +
                        " | def=" + faction.def.defName +
                        " | label=" + faction.def.LabelCap +
                        " | generatorDef=" + generatorDef.defName +
                        " | chosenXenotype=" + (chosenXenotype?.defName ?? "none") +
                        " | temporary=" + faction.temporary +
                        " | hidden=" + faction.hidden +
                        " | HiddenProperty=" + faction.Hidden +
                        " | defeated=" + faction.defeated +
                        " | leader=" + (faction.leader?.LabelShort ?? "null") +
                        " | loadID=" + faction.loadID);

            return faction;
        }

        private XenotypeDef ChooseDoppelgangerXenotype()
        {
            if (!ModsConfig.BiotechActive)
                return null;

            List<string> xenotypeDefNames = new List<string>
            {
                "Baseliner",
                "Hussar",
                "Pigskin",
                "Impid",
                "Yttakin",
                "Waster",
                "Dirtmole",
                "Neanderthal"
            };

            if (ModsConfig.IsActive("Ludeon.RimWorld.Odyssey"))
            {
                xenotypeDefNames.Add("Starjack");
            }

            List<XenotypeDef> xenotypes = xenotypeDefNames
                .Select(defName => DefDatabase<XenotypeDef>.GetNamedSilentFail(defName))
                .Where(x => x != null)
                .ToList();

            if (xenotypes.Count == 0)
                return null;

            return xenotypes.RandomElement();
        }

        private void ApplyDoppelgangerTemplateXenotype(Pawn pawn, XenotypeDef xenotype)
        {
            if (!ModsConfig.BiotechActive)
                return;

            if (pawn == null || pawn.genes == null || xenotype == null)
                return;

            try
            {
                pawn.genes.SetXenotype(xenotype);
                pawn.Drawer?.renderer?.SetAllGraphicsDirty();

                Log.Message("[Signal Interceptor] Applied doppelganger template xenotype: " +
                            xenotype.defName +
                            " | pawn=" + pawn.LabelShort +
                            " | displayed=" + pawn.genes.XenotypeLabelCap);
            }
            catch (System.Exception ex)
            {
                Log.Warning("[Signal Interceptor] Failed to apply doppelganger template xenotype " +
                            xenotype.defName +
                            ": " + ex);
            }
        }

        private void CopyAppearance(Pawn clone, Name name, Color hairColor,
        HairDef hair, BeardDef beard, HeadTypeDef head,
        BodyTypeDef body, Color skinColor, Gender gender)
        {
            clone.Name = name;
            clone.gender = gender;

            if (clone.story != null)
            {
                clone.story.HairColor = hairColor;
                clone.story.hairDef = hair;
                clone.story.headType = head;
                clone.story.bodyType = body;
                clone.story.SkinColorBase = skinColor;
            }

            if (clone.style != null && beard != null)
            {
                clone.style.beardDef = beard;
            }

            clone.Drawer?.renderer?.SetAllGraphicsDirty();
        }

        private void CopyTemplate(Pawn source, Pawn target)
        {
            // 1. Трейты
            if (source.story?.traits != null && target.story?.traits != null)
            {
                List<Trait> toRemove = target.story.traits.allTraits.ToList();

                foreach (Trait t in toRemove)
                {
                    target.story.traits.RemoveTrait(t);
                }

                foreach (Trait t in source.story.traits.allTraits)
                {
                    target.story.traits.GainTrait(new Trait(t.def, t.Degree));
                }
            }

            // 2. Предыстории
            if (target.story != null && source.story != null)
            {
                target.story.Childhood = source.story.Childhood;
                target.story.Adulthood = source.story.Adulthood;
            }

            // 3. Возраст
            if (source.ageTracker != null && target.ageTracker != null)
            {
                target.ageTracker.AgeBiologicalTicks = source.ageTracker.AgeBiologicalTicks;
                target.ageTracker.AgeChronologicalTicks = source.ageTracker.AgeChronologicalTicks;
            }

            // 4. Навыки
            if (source.skills != null && target.skills != null)
            {
                foreach (SkillRecord sourceSkill in source.skills.skills)
                {
                    SkillRecord targetSkill = target.skills.GetSkill(sourceSkill.def);
                    if (targetSkill == null)
                        continue;

                    targetSkill.Level = sourceSkill.Level;
                    targetSkill.xpSinceLastLevel = sourceSkill.xpSinceLastLevel;
                    targetSkill.xpSinceMidnight = sourceSkill.xpSinceMidnight;
                }

                foreach (SkillRecord sourceSkill in source.skills.skills)
                {
                    SkillRecord targetSkill = target.skills.GetSkill(sourceSkill.def);
                    if (targetSkill == null)
                        continue;

                    targetSkill.passion = sourceSkill.passion;
                }
            }

            // 5. Гены / ксенотип
            if (ModsConfig.BiotechActive && source.genes != null && target.genes != null)
            {
                try
                {
                    /*
                     * Сначала полностью чистим гены цели.
                     * Важно делать ToList(), потому что коллекция меняется во время удаления.
                     */
                    List<Gene> targetGenes = target.genes.GenesListForReading.ToList();

                    foreach (Gene gene in targetGenes)
                    {
                        target.genes.RemoveGene(gene);
                    }

                    /*
                     * Если у источника НЕ кастомный набор, а нормальный XenotypeDef,
                     * сначала ставим тот же XenotypeDef.
                     *
                     * Но затем всё равно проверяем дополнительные гены.
                     */
                    if (source.genes.Xenotype != null)
                    {
                        target.genes.SetXenotype(source.genes.Xenotype);
                    }

                    /*
                     * После SetXenotype у цели могли появиться стандартные гены этого ксенотипа.
                     * Чтобы избежать дублей, добавляем только те гены источника,
                     * которых ещё нет у цели.
                     */
                    foreach (Gene sourceGene in source.genes.GenesListForReading)
                    {
                        if (sourceGene?.def == null)
                            continue;

                        bool alreadyHas = target.genes.GenesListForReading
                            .Any(g => g.def == sourceGene.def);

                        if (alreadyHas)
                            continue;

                        bool xenogene = source.genes.Xenogenes.Contains(sourceGene);
                        target.genes.AddGene(sourceGene.def, xenogene);
                    }

                    /*
                     * Если источник кастомный/пересобранный, SetXenotype может не хватить.
                     * Поэтому дополнительно копируем имя и иконку через reflection.
                     * Это нужно именно для отображения в UI.
                     */
                    CopyGeneTrackerDisplayData(source, target);

                    target.Drawer?.renderer?.SetAllGraphicsDirty();

                    Log.Message("[Signal Interceptor] Copied genes from template to clone. " +
                                "Source xenotype: " + source.genes.XenotypeLabelCap +
                                " | Target xenotype: " + target.genes.XenotypeLabelCap +
                                " | Source genes: " + source.genes.GenesListForReading.Count +
                                " | Target genes: " + target.genes.GenesListForReading.Count);
                }
                catch (System.Exception ex)
                {
                    Log.Warning("[Signal Interceptor] Failed to copy genes from doppelganger template: " + ex);
                }
            }

            // 6. Hediff'ы без ран и отсутствующих частей
            if (source.health?.hediffSet != null && target.health?.hediffSet != null)
            {
                List<Hediff> existingHediffs = target.health.hediffSet.hediffs.ToList();

                foreach (Hediff h in existingHediffs)
                {
                    target.health.RemoveHediff(h);
                }

                foreach (Hediff h in source.health.hediffSet.hediffs)
                {
                    if (h == null || h.def == null)
                        continue;

                    if (h is Hediff_Injury)
                        continue;

                    if (h is Hediff_MissingPart)
                        continue;

                    /*
                     * Не копируем явно плохие смертельные состояния.
                     * Это грубый фильтр, но лучше, чем клонировать рак/инфекции/смертельные болезни.
                     */
                    if (h.def.isBad && h.def.initialSeverity > 0 && h.def.lethalSeverity > 0)
                        continue;

                    Hediff copy = HediffMaker.MakeHediff(h.def, target, h.Part);
                    copy.Severity = h.Severity;
                    target.health.AddHediff(copy);
                }
            }
        }

        private void ApplyCloneFragility(Pawn pawn)
        {
            HediffDef instability = DefDatabase<HediffDef>.GetNamedSilentFail("SI_CloneInstability");
            if (instability != null)
            {
                Hediff hediff = HediffMaker.MakeHediff(instability, pawn);
                hediff.Severity = 1.0f;
                pawn.health.AddHediff(hediff);
            }

            HediffDef bleedRate = DefDatabase<HediffDef>.GetNamedSilentFail("SI_CloneBleedRate");
            if (bleedRate != null)
            {
                Hediff bleed = HediffMaker.MakeHediff(bleedRate, pawn);
                bleed.Severity = 1.0f;
                pawn.health.AddHediff(bleed);
            }

            AddDeathAcidifier(pawn);
        }

        private void AddDeathAcidifier(Pawn pawn)
        {
            HediffDef acidifier = DefDatabase<HediffDef>.GetNamedSilentFail("DeathAcidifier");
            if (acidifier == null)
                return;

            BodyPartRecord torso = pawn.RaceProps.body.AllParts
                .FirstOrDefault(p => p.def.defName == "Torso");

            if (torso == null)
                return;

            bool alreadyHas = pawn.health.hediffSet.hediffs
                .Any(h => h.def == acidifier && h.Part == torso);

            if (!alreadyHas)
            {
                pawn.health.AddHediff(acidifier, torso);
            }
        }

        private void CopyGeneTrackerDisplayData(Pawn source, Pawn target)
        {
            if (!ModsConfig.BiotechActive)
                return;

            if (source?.genes == null || target?.genes == null)
                return;

            try
            {
                System.Type trackerType = source.genes.GetType();
                System.Reflection.BindingFlags flags =
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic;

                string[] fieldNames =
                {
            "xenotypeName",
            "iconDef",
            "xenotype"
        };

                foreach (string fieldName in fieldNames)
                {
                    System.Reflection.FieldInfo field = trackerType.GetField(fieldName, flags);
                    if (field == null)
                        continue;

                    object value = field.GetValue(source.genes);
                    field.SetValue(target.genes, value);
                }
            }
            catch (System.Exception ex)
            {
                Log.Warning("[Signal Interceptor] Failed to copy gene tracker display data: " + ex);
            }
        }

        private string GenerateDoppelgangerFactionName()
        {
            List<string> adjectives = GetTranslatedStringList(
                "SI_Doppelganger_NameAdjectives",
                new List<string>
                {
            "Безликие",
            "Зеркальные",
            "Искажённые",
            "Отражённые",
            "Невозможные",
            "Подменённые"
                }
            );

            List<string> nouns = GetTranslatedStringList(
                "SI_Doppelganger_NameNouns",
                new List<string>
                {
            "Отголоски",
            "Тени",
            "Слепки",
            "Лики",
            "Миражи",
            "Отражения"
                }
            );

            string adjective = adjectives.RandomElement();
            string noun = nouns.RandomElement();

            return adjective + " " + noun;
        }

        private void DeactivateDoppelgangerFaction(Faction faction)
        {
            if (faction == null)
                return;

            if (!IsDoppelgangerFactionDef(faction.def))
                return;

            faction.hidden = true;
            faction.temporary = true;
            faction.defeated = true;
            faction.leader = null;

            Log.Message("[Signal Interceptor] Deactivated doppelganger faction: " +
                        faction.Name +
                        " | def=" + faction.def.defName +
                        " | loadID=" + faction.loadID);
        }

        private bool IsDoppelgangerFactionDef(FactionDef def)
        {
            return def != null
                && def.defName != null
                && def.defName.StartsWith("SI_DoppelgangerFaction");
        }

        private void GiveCloneRandomWeapon(Pawn pawn, float threatPoints)
        {
            if (pawn.equipment == null) return;

            pawn.equipment.DestroyAllEquipment();

            List<string> weaponPool;

            if (threatPoints >= 1800f)
            {
                weaponPool = new List<string>
                {
                    "Gun_ChargeRifle", "Gun_ChargeLance", "MeleeWeapon_MonoSword",
                    "MeleeWeapon_Zeushammer", "Gun_AssaultRifle", "Gun_SniperRifle", "Gun_Minigun"
                };
            }
            else if (threatPoints >= 1200f)
            {
                weaponPool = new List<string>
                {
                    "Gun_AssaultRifle", "Gun_SniperRifle", "Gun_ChainShotgun",
                    "Gun_LMG", "Gun_ChargeRifle", "MeleeWeapon_LongSword", "MeleeWeapon_Mace"
                };
            }
            else
            {
                weaponPool = new List<string>
                {
                    "Gun_BoltActionRifle", "Gun_PumpShotgun", "Gun_AssaultRifle",
                    "Gun_MachinePistol", "Gun_Revolver", "MeleeWeapon_LongSword", "MeleeWeapon_Gladius"
                };
            }

            weaponPool.Shuffle();
            foreach (string defName in weaponPool)
            {
                ThingDef weaponDef = DefDatabase<ThingDef>.GetNamedSilentFail(defName);
                if (weaponDef == null) continue;

                ThingDef stuff = weaponDef.MadeFromStuff ? GenStuff.DefaultStuffFor(weaponDef) : null;
                Thing weapon = ThingMaker.MakeThing(weaponDef, stuff);

                QualityCategory quality;
                if (threatPoints >= 1800f) quality = QualityCategory.Excellent;
                else if (threatPoints >= 1200f) quality = QualityCategory.Good;
                else quality = QualityCategory.Normal;

                weapon.TryGetComp<CompQuality>()?.SetQuality(quality, ArtGenerationContext.Outsider);

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

        private void GiveCloneArmor(Pawn pawn, float threatPoints)
        {
            if (pawn.apparel == null) return;

            pawn.apparel.DestroyAll();

            List<string> armorSet;

            if (threatPoints >= 1800f)
            {
                armorSet = new List<string> { "Apparel_PowerArmor", "Apparel_PowerArmorHelmet" };
            }
            else if (threatPoints >= 1200f)
            {
                armorSet = new List<string> { "Apparel_ArmorMarineHelmet", "Apparel_FlakVest", "Apparel_FlakPants", "Apparel_Duster" };
            }
            else
            {
                armorSet = new List<string> { "Apparel_FlakVest", "Apparel_FlakPants", "Apparel_SimpleHelmet", "Apparel_Parka" };
            }

            foreach (string defName in armorSet)
            {
                ThingDef armorDef = DefDatabase<ThingDef>.GetNamedSilentFail(defName);
                if (armorDef == null) continue;

                ThingDef stuff = armorDef.MadeFromStuff ? GenStuff.DefaultStuffFor(armorDef) : null;
                Thing armor = ThingMaker.MakeThing(armorDef, stuff);

                QualityCategory quality;
                if (threatPoints >= 1800f) quality = QualityCategory.Excellent;
                else if (threatPoints >= 1200f) quality = QualityCategory.Good;
                else quality = QualityCategory.Normal;

                armor.TryGetComp<CompQuality>()?.SetQuality(quality, ArtGenerationContext.Outsider);

                if (armor is Apparel ap)
                {
                    pawn.apparel.Wear(ap, dropReplacedApparel: false);
                }
            }
        }

        private void AddDoppelgangerMark(Pawn pawn)
        {
            HediffDef mark = DefDatabase<HediffDef>.GetNamedSilentFail("SI_DoppelgangerMark");
            if (mark == null)
            {
                Log.Error("[Signal Interceptor] SI_DoppelgangerMark HediffDef not found.");
                return;
            }

            if (pawn.health?.hediffSet == null)
                return;

            if (pawn.health.hediffSet.HasHediff(mark))
                return;

            pawn.health.AddHediff(mark);
        }

        private void TickDoppelgangerSettlementCleanup()
        {
            if (Find.TickManager.TicksGame % 250 == 0)
            {
                CleanupDoppelgangerSettlements();
            }
        }
    }
}
