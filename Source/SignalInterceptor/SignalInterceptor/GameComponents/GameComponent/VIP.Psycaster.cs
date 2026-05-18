using RimWorld;
using RimWorld.Planet;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace SignalInterceptor
{
    public partial class SignalInterceptorGameComponent
    {
        private void SpawnPsycasterVIP(Map map, VIPSiteData data)
        {
            if (!ModsConfig.RoyaltyActive)
            {
                Log.Warning("[Signal Interceptor] Tried to spawn PsycasterVIP without Royalty.");
                data.rewardGiven = true;
                data.vipSpawned = true;
                return;
            }

            Faction hostileFaction = CreatePsycasterVIPFactionForMap();

            if (hostileFaction == null)
            {
                Log.Error("[Signal Interceptor] Failed to create psycaster VIP faction. Psycaster VIP spawn aborted.");

                data.rewardGiven = true;
                data.vipSpawned = true;
                data.enemyFaction = null;

                return;
            }

            data.enemyFaction = hostileFaction;

            if (data.site != null)
            {
                data.site.SetFaction(hostileFaction);
                data.site.factionMustRemainHostile = false;
            }

            Log.Message("[Signal Interceptor] Psycaster spawn faction check:" +
                        " | site=" + (data.site?.LabelCap ?? "null") +
                        " | siteFaction=" + (data.site?.Faction?.Name ?? "null") +
                        " | psycasterFaction=" + (hostileFaction?.Name ?? "null") +
                        " | factionDef=" + (hostileFaction?.def?.defName ?? "null") +
                        " | contextFaction=" + (data.faction?.Name ?? "null") +
                        " | temporary=" + hostileFaction.temporary +
                        " | hiddenField=" + hostileFaction.hidden +
                        " | HiddenProperty=" + hostileFaction.Hidden +
                        " | leader=" + (hostileFaction.leader?.LabelShort ?? "null"));

            PawnGenerationRequest request = new PawnGenerationRequest(
                kind: PawnKindDefOf.Colonist,
                faction: hostileFaction,
                context: PawnGenerationContext.NonPlayer,
                forceGenerateNewPawn: true,
                canGeneratePawnRelations: false,
                allowFood: true,
                mustBeCapableOfViolence: true
            );

            Pawn psycaster = PawnGenerator.GeneratePawn(request);

            if (psycaster == null)
            {
                Log.Error("[Signal Interceptor] Failed to generate PsycasterVIP pawn.");
                data.rewardGiven = true;
                data.vipSpawned = true;
                return;
            }

            psycaster.SetFactionDirect(hostileFaction);

            int tier = GetVIPTier(data.threatPoints);
            int psylinkLevel = Mathf.Clamp(tier, 3, 6);

            PreparePsycasterVIPPawn(psycaster, tier, psylinkLevel);

            IntVec3 anchor = map.Center;
            Plant animaTree;

            if (TryFindPsycasterAnimaTree(map, out animaTree))
            {
                data.psycasterAnimaTree = animaTree;
                data.psycasterAnimaTreeLinked = true;
                data.signalCampCenter = animaTree.Position;
                anchor = animaTree.Position;
            }
            else
            {
                Log.Warning("[Signal Interceptor] Psycaster anima tree was not found on generated map. Falling back to map center.");
            }

            IntVec3 spot;

            if (!TryFindPsycasterSpawnSpotNearTree(map, anchor, out spot))
            {
                if (!CellFinder.TryFindRandomCellNear(
                    anchor,
                    map,
                    18,
                    c => c.Standable(map) && !c.Roofed(map) && c.GetFirstPawn(map) == null,
                    out spot))
                {
                    spot = anchor;
                }
            }

            GenSpawn.Spawn(psycaster, spot, map);

            if (psycaster.Faction != hostileFaction)
            {
                psycaster.SetFaction(hostileFaction);
            }

            data.psycasterPawn = psycaster;

            if (data.signalCampCenter == IntVec3.Invalid)
            {
                data.signalCampCenter = spot;
            }

            TickPsycasterSiteResonance(data);

            if (psycaster.mindState == null)
            {
                psycaster.mindState = new Pawn_MindState(psycaster);
            }

            psycaster.mindState.duty = new PawnDuty(DutyDefOf.AssaultColony);

            TryForcePsycasterAttackNearestPlayerPawn(psycaster, map);

            Find.LetterStack.ReceiveLetter(
                "SI_PsycasterVIP_Title".Translate(),
                "SI_PsycasterVIP_Text".Translate(
                    psycaster.LabelShort,
                    hostileFaction.Name
                ),
                LetterDefOf.ThreatBig,
                new LookTargets(psycaster)
            );

            Log.Message("[Signal Interceptor] Psycaster VIP spawned near anima tree. " +
                        "Pawn=" + psycaster.LabelShort +
                        " | Faction=" + hostileFaction.Name +
                        " | Tier=" + tier +
                        " | Psylink=" + psylinkLevel +
                        " | Tree=" + (data.psycasterAnimaTree != null ? data.psycasterAnimaTree.Position.ToString() : "null") +
                        " | Spawn=" + spot);
        }

        private void CleanupPsycasterVIPSettlements()
        {
            List<Settlement> settlements = Find.WorldObjects.AllWorldObjects
                .OfType<Settlement>()
                .Where(s => s.Faction != null
                         && IsPsycasterVIPFactionDef(s.Faction.def))
                .ToList();

            foreach (Settlement settlement in settlements)
            {
                Log.Warning("[Signal Interceptor] Removing invalid psycaster VIP settlement: " +
                            settlement.Label +
                            " | tile=" + settlement.Tile +
                            " | faction=" + (settlement.Faction?.Name ?? "null") +
                            " | factionDef=" + (settlement.Faction?.def?.defName ?? "null"));

                Find.WorldObjects.Remove(settlement);
            }
        }

        private bool IsPsycasterVIPFactionDef(FactionDef def)
        {
            return def != null && def.defName == "SI_PsycasterVIPFaction";
        }

        private FactionDef GetPsycasterVIPFactionDef()
        {
            FactionDef def = DefDatabase<FactionDef>.GetNamedSilentFail("SI_PsycasterVIPFaction");

            if (def != null)
                return def;

            Log.Warning("[Signal Interceptor] SI_PsycasterVIPFaction FactionDef not found. Falling back to Pirate.");
            return FactionDefOf.Pirate;
        }

        private Faction CreatePsycasterVIPFactionForMap()
        {
            FactionDef wantedDef = GetPsycasterVIPFactionDef();

            FactionDef generatorDef = wantedDef;

            if (generatorDef == null || generatorDef.factionNameMaker == null)
            {
                Log.Warning("[Signal Interceptor] Psycaster VIP faction def has no factionNameMaker. " +
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
            catch (Exception ex)
            {
                Log.Error("[Signal Interceptor] Failed to generate psycaster VIP faction through FactionGenerator. " +
                          "Fallback to Pirate generator. Exception: " + ex);

                faction = FactionGenerator.NewGeneratedFaction(
                    new FactionGeneratorParms(FactionDefOf.Pirate)
                );
            }

            if (faction == null)
            {
                Log.Error("[Signal Interceptor] FactionGenerator returned null for psycaster VIP faction.");
                return null;
            }

            if (wantedDef != null)
            {
                faction.def = wantedDef;
            }

            faction.temporary = true;
            faction.hidden = false;
            faction.defeated = false;
            faction.Name = GeneratePsycasterVIPFactionName();
            faction.leader = null;

            if (!Find.FactionManager.AllFactions.Contains(faction))
            {
                Find.FactionManager.Add(faction);
            }

            CleanupPsycasterVIPSettlements();

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

            Log.Message("[Signal Interceptor] Created psycaster VIP map faction: " +
                        faction.Name +
                        " | def=" + faction.def.defName +
                        " | label=" + faction.def.LabelCap +
                        " | generatorDef=" + generatorDef.defName +
                        " | temporary=" + faction.temporary +
                        " | hidden=" + faction.hidden +
                        " | HiddenProperty=" + faction.Hidden +
                        " | defeated=" + faction.defeated +
                        " | leader=" + (faction.leader?.LabelShort ?? "null") +
                        " | loadID=" + faction.loadID);

            return faction;
        }

        private string GeneratePsycasterVIPFactionName()
        {
            List<string> nouns = GetTranslatedStringListSafe(
                "SI_PsycasterFaction_NameNouns",
                new List<string>
                {
            "Контур",
            "Резонанс",
            "Импульс",
            "Отголосок",
            "Разлом",
            "Шёпот",
            "След",
            "Мираж"
                }
            );

            List<string> adjectives = GetTranslatedStringListSafe(
                "SI_PsycasterFaction_NameAdjectives",
                new List<string>
                {
            "Сухой Травы",
            "Жёлтой Пыли",
            "Тусклого Солнца",
            "Молчаливого Разума",
            "Пепельной Воли",
            "Холодного Сознания",
            "Глухой Мысли",
            "Выжженного Нерва"
                }
            );

            return nouns.RandomElement() + " " + adjectives.RandomElement();
        }

        private List<string> GetTranslatedStringListSafe(string key, List<string> fallback)
        {
            if (key.CanTranslate())
            {
                string raw = key.Translate().ToString();

                List<string> result = raw
                    .Split(new char[] { '|' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.Trim())
                    .Where(s => !string.IsNullOrEmpty(s))
                    .ToList();

                if (result.Count > 0)
                    return result;
            }

            return fallback;
        }

        private void PreparePsycasterVIPPawn(Pawn pawn, int tier, int psylinkLevel)
        {
            if (pawn == null)
                return;

            if (pawn.skills != null)
            {
                SetSkillLevel(pawn, SkillDefOf.Melee, Mathf.Clamp(8 + tier, 10, 18), Passion.Minor);
                SetSkillLevel(pawn, SkillDefOf.Shooting, Mathf.Clamp(7 + tier, 9, 16), Passion.Minor);
                SetSkillLevel(pawn, SkillDefOf.Social, Mathf.Clamp(10 + tier, 12, 20), Passion.Major);
                SetSkillLevel(pawn, SkillDefOf.Intellectual, Mathf.Clamp(10 + tier, 12, 20), Passion.Major);
            }

            AddPsycasterTraitIfPossible(pawn, "PsychicSensitivity", 2);
            EnsurePsycasterVIPSurvivalKit(pawn);

            AddOrSetPsycasterPsylinkLevel(pawn, psylinkLevel);
            GivePsycasterVIPAbilities(pawn, psylinkLevel);
            GivePsycasterVIPGear(pawn, tier);
            RefillPsycasterPsyfocus(pawn);
        }

        private void SetSkillLevel(Pawn pawn, SkillDef skillDef, int level, Passion passion)
        {
            SkillRecord skill = pawn.skills?.GetSkill(skillDef);

            if (skill == null)
                return;

            if (skill.Level < level)
                skill.Level = level;

            if (skill.passion == Passion.None)
                skill.passion = passion;
        }

        private void EnsurePsycasterVIPSurvivalKit(Pawn pawn)
        {
            if (pawn == null || pawn.Destroyed || pawn.Dead)
                return;

            EnsureRunnerTrait(pawn);
            TryAddStoneskinGland(pawn);
            TryAddPsycasterVIPHediff(pawn, "SI_RestoringMechanisms");
            TryAddPsycasterVIPHediff(pawn, "SI_EntropyStabilizer");
        }

        private void EnsureRunnerTrait(Pawn pawn)
        {
            if (pawn == null || pawn.story == null || pawn.story.traits == null)
                return;

            TraitDef speedOffset = DefDatabase<TraitDef>.GetNamedSilentFail("SpeedOffset");

            if (speedOffset == null)
            {
                Log.Warning("[Signal Interceptor] SpeedOffset trait def not found for Psycaster VIP.");
                return;
            }

            try
            {
                List<Trait> existingSpeedTraits = pawn.story.traits.allTraits
                    .Where(t => t != null && t.def == speedOffset)
                    .ToList();

                for (int i = 0; i < existingSpeedTraits.Count; i++)
                {
                    pawn.story.traits.RemoveTrait(existingSpeedTraits[i]);
                }

                // SpeedOffset degrees:
                // -1 = Slowpoke
                //  1 = Fast walker
                //  2 = Jogger
                pawn.story.traits.GainTrait(new Trait(speedOffset, 2, true));

                Log.Message("[Signal Interceptor] Psycaster VIP runner trait applied: "
                            + pawn.LabelShort
                            + " | Trait=SpeedOffset"
                            + " | Degree=2");
            }
            catch (Exception ex)
            {
                Log.Warning("[Signal Interceptor] Failed to add runner trait to Psycaster VIP. Pawn="
                            + pawn.LabelShort
                            + " | Exception="
                            + ex);
            }
        }

        private void TryAddStoneskinGland(Pawn pawn)
        {
            if (pawn == null || pawn.health == null || pawn.health.hediffSet == null)
                return;

            string[] possibleDefs =
            {
        "StoneskinGland",
        "StoneSkinGland",
        "ArmorSkinGland_Stone"
    };

            for (int i = 0; i < possibleDefs.Length; i++)
            {
                HediffDef def = DefDatabase<HediffDef>.GetNamedSilentFail(possibleDefs[i]);

                if (def == null)
                    continue;

                if (pawn.health.hediffSet.HasHediff(def))
                    return;

                BodyPartRecord part = FindPsycasterTorsoPart(pawn);

                if (part == null)
                {
                    Log.Warning("[Signal Interceptor] Could not find torso for stoneskin gland. Pawn="
                                + pawn.LabelShort
                                + " | Hediff="
                                + def.defName);
                    return;
                }

                try
                {
                    Hediff hediff = HediffMaker.MakeHediff(def, pawn, part);
                    pawn.health.AddHediff(hediff, part);

                    Log.Message("[Signal Interceptor] Psycaster VIP stoneskin gland applied: "
                                + pawn.LabelShort
                                + " | Hediff="
                                + def.defName
                                + " | Part="
                                + part.Label);

                    return;
                }
                catch (Exception ex)
                {
                    Log.Warning("[Signal Interceptor] Failed to add stoneskin gland. Pawn="
                                + pawn.LabelShort
                                + " | Hediff="
                                + def.defName
                                + " | Part="
                                + part.Label
                                + " | Exception="
                                + ex);
                    return;
                }
            }

            Log.Warning("[Signal Interceptor] Stoneskin gland HediffDef not found for Psycaster VIP.");
        }

        private BodyPartRecord FindPsycasterTorsoPart(Pawn pawn)
        {
            if (pawn == null || pawn.RaceProps == null || pawn.RaceProps.body == null)
                return null;

            List<BodyPartRecord> parts = pawn.RaceProps.body.AllParts;

            if (parts == null)
                return null;

            BodyPartRecord torso = parts.FirstOrDefault(p =>
                p != null &&
                p.def != null &&
                p.def == BodyPartDefOf.Torso);

            if (torso != null)
                return torso;

            torso = parts.FirstOrDefault(p =>
                p != null &&
                p.def != null &&
                p.def.defName == "Torso");

            if (torso != null)
                return torso;

            torso = parts.FirstOrDefault(p =>
                p != null &&
                p.Label != null &&
                p.Label.ToLowerInvariant().Contains("torso"));

            if (torso != null)
                return torso;

            torso = parts.FirstOrDefault(p =>
                p != null &&
                p.Label != null &&
                p.Label.ToLowerInvariant().Contains("торс"));

            return torso;
        }

        private void TryAddPsycasterVIPHediff(Pawn pawn, string defName)
        {
            if (pawn == null || pawn.health == null || pawn.health.hediffSet == null)
                return;

            HediffDef def = DefDatabase<HediffDef>.GetNamedSilentFail(defName);

            if (def == null)
            {
                Log.Warning("[Signal Interceptor] Missing Psycaster VIP hediff def: " + defName);
                return;
            }

            if (pawn.health.hediffSet.HasHediff(def))
                return;

            pawn.health.AddHediff(def);
        }

        private void AddPsycasterTraitIfPossible(Pawn pawn, string traitDefName, int degree)
        {
            if (pawn?.story?.traits == null || traitDefName.NullOrEmpty())
                return;

            TraitDef traitDef = DefDatabase<TraitDef>.GetNamedSilentFail(traitDefName);

            if (traitDef == null)
                return;

            try
            {
                List<Trait> existing = pawn.story.traits.allTraits
                    .Where(t => t != null && t.def == traitDef)
                    .ToList();

                foreach (Trait trait in existing)
                {
                    pawn.story.traits.RemoveTrait(trait);
                }

                pawn.story.traits.GainTrait(new Trait(traitDef, degree));
            }
            catch (System.Exception ex)
            {
                Log.Warning("[Signal Interceptor] Failed to add psycaster trait. Trait=" +
                            traitDefName +
                            " | Degree=" +
                            degree +
                            " | Pawn=" +
                            pawn.LabelShort +
                            " | Exception=" +
                            ex);
            }
        }

        private bool AddOrSetPsycasterPsylinkLevel(Pawn pawn, int level)
        {
            if (pawn?.health?.hediffSet == null)
                return false;

            HediffDef psylinkDef = DefDatabase<HediffDef>.GetNamedSilentFail("PsychicAmplifier");

            if (psylinkDef == null)
            {
                Log.Warning("[Signal Interceptor] PsychicAmplifier HediffDef not found.");
                return false;
            }

            level = Mathf.Clamp(level, 1, 6);

            Hediff existing = pawn.health.hediffSet.GetFirstHediffOfDef(psylinkDef);

            if (existing == null)
            {
                BodyPartRecord part = FindBestBodyPartForHediff(pawn, psylinkDef);
                existing = HediffMaker.MakeHediff(psylinkDef, pawn, part);
                pawn.health.AddHediff(existing, part);
            }

            SetLevelHediff(existing, level);
            return true;
        }

        private void SetLevelHediff(Hediff hediff, int level)
        {
            if (hediff == null)
                return;

            level = Mathf.Max(1, level);

            try
            {
                hediff.Severity = level;

                BindingFlags flags =
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.NonPublic;

                Type type = hediff.GetType();

                while (type != null)
                {
                    MethodInfo method = type.GetMethod("SetLevelTo", flags);

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
                    FieldInfo field = type.GetField("level", flags);

                    if (field != null && field.FieldType == typeof(int))
                    {
                        field.SetValue(hediff, level);
                        hediff.Severity = level;
                        return;
                    }

                    type = type.BaseType;
                }
            }
            catch (Exception ex)
            {
                Log.Warning("[Signal Interceptor] Failed to set Psycaster psylink level: " + ex);
            }
        }

        private void GivePsycasterVIPAbilities(Pawn pawn, int psylinkLevel)
        {
            if (pawn?.abilities == null)
            {
                Log.Warning("[Signal Interceptor] Psycaster has no ability tracker: " + pawn?.LabelShort);
                return;
            }

            psylinkLevel = Mathf.Clamp(psylinkLevel, 1, 6);

            List<string> abilityNames = new List<string>();

            if (psylinkLevel >= 1)
            {
                abilityNames.AddRange(new[]
                {
                    "Stun"
                });
            }

            if (psylinkLevel >= 2)
            {
                abilityNames.AddRange(new[]
                {
                    "BlindingPulse",
                    "Waterskip"
                });
            }

            if (psylinkLevel >= 3)
            {
                abilityNames.AddRange(new[]
                {
                    "Beckon",
                    "ChaosSkip",
                    "VertigoPulse"
                });
            }

            if (psylinkLevel >= 4)
            {
                abilityNames.AddRange(new[]
                {
                    "Smokepop",
                    "Skip",
                    "Focus"
                });
            }

            if (psylinkLevel >= 5)
            {
                abilityNames.AddRange(new[]
                {
                    "Berserk",
                    "Wallraise"
                });
            }

            if (psylinkLevel >= 6)
            {
                abilityNames.AddRange(new[]
                {
                    "Invisibility",
                    "BerserkPulse",
                    "MassChaosSkip",
                    "ManhunterPulse",
                    "BulletShield"
                });
            }


            foreach (string defName in abilityNames.Distinct())
            {
                AbilityDef abilityDef = FindAbilityDefByPossibleName(defName);

                if (abilityDef == null)
                {
                    Log.Warning("[Signal Interceptor] Psycaster ability def not found: " + defName);
                    continue;
                }

                try
                {
                    if (GetPawnAbility(pawn, abilityDef) == null)
                    {
                        pawn.abilities.GainAbility(abilityDef);
                    }

                    object ability = GetPawnAbility(pawn, abilityDef);

                    Log.Message("[Signal Interceptor] Psycaster ability prepared. " +
                                "Pawn=" + pawn.LabelShort +
                                " | Requested=" + defName +
                                " | Def=" + abilityDef.defName +
                                " | Psylink=" + psylinkLevel +
                                " | FoundAfterGain=" + (ability != null));
                }
                catch (System.Exception ex)
                {
                    Log.Warning("[Signal Interceptor] Failed to give psycaster ability. " +
                                "Pawn=" + pawn.LabelShort +
                                " | Ability=" + defName +
                                " | Exception=" + ex);
                }
            }
        }

        private AbilityDef FindAbilityDefByPossibleName(string name)
        {
            if (name.NullOrEmpty())
                return null;

            AbilityDef direct = DefDatabase<AbilityDef>.GetNamedSilentFail(name);
            if (direct != null)
                return direct;

            return DefDatabase<AbilityDef>.AllDefsListForReading
                .FirstOrDefault(def =>
                    def != null &&
                    def.defName != null &&
                    def.defName.Equals(name, StringComparison.OrdinalIgnoreCase));
        }

        private void GivePsycasterVIPGear(Pawn pawn, int tier)
        {
            if (pawn == null)
                return;

            QualityCategory quality = tier >= 8
                ? QualityCategory.Masterwork
                : tier >= 5
                    ? QualityCategory.Excellent
                    : QualityCategory.Good;

            if (pawn.apparel != null)
            {
                pawn.apparel.DestroyAll();

                TryWearSingleApparelByDefNameWithQuality(pawn, "Apparel_PsyfocusHelmet", quality);
                TryWearSingleApparelByDefNameWithQuality(pawn, "Apparel_PsyfocusShirt", quality);
                TryWearSingleApparelByDefNameWithQuality(pawn, "Apparel_RobeRoyal", quality);

                if (tier >= 5)
                {
                    TryWearSingleApparelByDefNameWithQuality(pawn, "Apparel_ShieldBelt", quality);
                }
            }

            if (pawn.equipment != null)
            {
                pawn.equipment.DestroyAllEquipment();

                if (tier >= 7)
                {
                    TryGiveWeaponByDefNamesWithQuality(pawn, quality,
                        "MeleeWeapon_PersonaMonosword",
                        "MeleeWeapon_PersonaMonoSword",
                        "MeleeWeapon_Monosword",
                        "MeleeWeapon_MonoSword",
                        "MeleeWeapon_Plasmasword");
                }
                else
                {
                    TryGiveWeaponByDefNamesWithQuality(pawn, quality,
                        "MeleeWeapon_LongSword",
                        "MeleeWeapon_Gladius",
                        "Gun_ChargePistol",
                        "Gun_Revolver");
                }
            }
        }

        private void TickPsycasterCombatAI(VIPSiteData data, Pawn psycaster)
        {
            if (data == null || psycaster == null)
                return;

            if (psycaster.Destroyed || psycaster.Dead || psycaster.Downed
                || !psycaster.Spawned || psycaster.Map == null)
                return;

            // Ленивая инициализация мозга. Создаётся при первом тике
            // включая первый тик после загрузки сейва.
            if (data.psycasterBrain == null)
            {
                data.psycasterBrain = new global::SignalInterceptor.AI.Psycaster.PsycasterBrain(this, psycaster);

                data.psycasterBrain.HomeAnchor = data.signalCampCenter.IsValid
                    ? data.signalCampCenter
                    : psycaster.Position;
            }

            data.psycasterBrain.Tick();
        }

        private bool TryCastPsyAbilityAtCellControlled(
            Pawn caster,
            string abilityDefName,
            IntVec3 cell,
            float maxRange,
            bool requireLineOfSight)
        {
            if (caster == null || abilityDefName.NullOrEmpty())
            {
                return false;
            }

            if (!caster.Spawned || caster.Map == null)
            {
                return false;
            }

            Map map = caster.Map;

            if (!cell.IsValid || !cell.InBounds(map))
            {
                return false;
            }

            if (caster.Position.DistanceTo(cell) > maxRange)
            {
                return false;
            }

            if (requireLineOfSight && !GenSight.LineOfSight(caster.Position, cell, map))
            {
                return false;
            }

            return TryCastPsyAbilityAtCell(caster, abilityDefName, cell);
        }
        private void InterruptBadPsycasterCombatJob(Pawn psycaster, Pawn intendedTarget)
        {
            if (psycaster == null || psycaster.jobs == null || psycaster.CurJob == null)
            {
                return;
            }

            Job job = psycaster.CurJob;
            string defName = job.def?.defName ?? string.Empty;

            bool shouldInterrupt = false;

            if (IsFleeOrExitJob(job))
            {
                shouldInterrupt = true;
            }

            if (defName.IndexOf("Wait", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                shouldInterrupt = true;
            }

            if (defName.IndexOf("Wander", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                shouldInterrupt = true;
            }

            if (job.def == JobDefOf.Goto && intendedTarget != null)
            {
                float distToTarget = psycaster.Position.DistanceTo(intendedTarget.Position);

                if (distToTarget <= 8f)
                {
                    shouldInterrupt = true;
                }
                else if (job.targetA.IsValid && job.targetA.Cell.DistanceTo(intendedTarget.Position) > 8f)
                {
                    shouldInterrupt = true;
                }
            }

            if (shouldInterrupt)
            {
                psycaster.jobs.EndCurrentJob(JobCondition.InterruptForced, true);
            }
        }

        private bool TryForcePsycasterMeleeAttack(Pawn psycaster, Pawn target)
        {
            if (psycaster == null || target == null)
                return false;

            if (psycaster.Destroyed || psycaster.Dead || psycaster.Downed || !psycaster.Spawned)
                return false;

            if (target.Destroyed || target.Dead || target.Downed || !target.Spawned)
                return false;

            if (psycaster.Map == null || psycaster.Map != target.Map)
                return false;

            if (psycaster.jobs == null)
                return false;

            if (!psycaster.CanReach(target, PathEndMode.Touch, Danger.Deadly))
                return false;

            // Если уже выполняет правильный melee job по этой цели — не перебиваем.
            // Это важно: постоянный EndCurrentJob/StartJob сбрасывает атаку и создаёт "тупняк".
            if (psycaster.CurJob != null &&
                psycaster.CurJob.def == JobDefOf.AttackMelee &&
                psycaster.CurJob.targetA.Thing == target)
            {
                return true;
            }

            // Если сейчас идёт к точке через Goto — это старое поведение.
            // Оно плохо работает против бегущей цели: кастер может пройти мимо.
            // Прерываем Goto и заменяем на AttackMelee по самой пешке.
            if (psycaster.CurJob != null &&
                psycaster.CurJob.def == JobDefOf.Goto)
            {
                psycaster.jobs.EndCurrentJob(JobCondition.InterruptForced, true);
            }

            // Не прерываем активный каст. Если пси-кастер сейчас в warmup,
            // Brain позже повторно вызовет melee после завершения каста.
            if (psycaster.stances != null && psycaster.stances.curStance != null)
            {
                string stanceName = psycaster.stances.curStance.GetType().Name;

                if (stanceName == "PawnStance_Warmup")
                    return true;
            }

            Job attackJob = JobMaker.MakeJob(JobDefOf.AttackMelee, target);
            attackJob.expiryInterval = Rand.RangeInclusive(180, 240);
            attackJob.checkOverrideOnExpire = true;
            attackJob.playerForced = false;

            if (psycaster.CurJob != null &&
                psycaster.CurJob.def != JobDefOf.AttackMelee)
            {
                psycaster.jobs.EndCurrentJob(JobCondition.InterruptForced, true);
            }

            return psycaster.jobs.TryTakeOrderedJob(attackJob, JobTag.Misc);
        }

        private bool ShouldApplyPsycasterCastPause(Pawn psycaster)
        {
            if (psycaster == null || psycaster.CurJob == null || psycaster.CurJob.def == null)
                return true;

            if (psycaster.CurJob.def == JobDefOf.AttackMelee)
                return false;

            if (psycaster.CurJob.def == JobDefOf.AttackStatic)
                return false;

            if (psycaster.CurJob.def == JobDefOf.Goto)
                return false;

            return true;
        }
        private bool IsValidPsycastTarget(Pawn caster, Pawn target, float maxRange, bool requireLineOfSight)
        {
            if (caster == null || target == null)
                return false;

            if (!caster.Spawned || !target.Spawned || caster.Map == null || caster.Map != target.Map)
                return false;

            if (target.Dead || target.Downed)
                return false;

            float distance = caster.Position.DistanceTo(target.Position);

            if (distance > maxRange)
                return false;

            if (requireLineOfSight && !GenSight.LineOfSight(caster.Position, target.Position, caster.Map))
                return false;

            return true;
        }

        private bool IsRangedCombatPawn(Pawn pawn)
        {
            if (pawn == null || pawn.equipment == null || pawn.equipment.Primary == null)
                return false;

            ThingWithComps weapon = pawn.equipment.Primary;

            if (weapon.def?.Verbs == null)
                return false;

            return weapon.def.Verbs.Any(v => v != null && !v.IsMeleeAttack);
        }
        private bool TryFindWallraiseCell(Pawn caster, Pawn shooter, Map map, out IntVec3 result)
        {
            result = IntVec3.Invalid;

            if (caster == null || shooter == null || map == null)
                return false;

            // Направление ОТ caster'а К shooter'у — стена должна быть в эту сторону.
            IntVec3 toShooter = shooter.Position - caster.Position;
            int dx = Math.Sign(toShooter.x);
            int dz = Math.Sign(toShooter.z);

            // Кандидаты: клетки В НАПРАВЛЕНИИ shooter'а на расстоянии 2-3 от caster'а.
            // Это разорвёт LOS, но не запрёт самого caster'а.
            List<IntVec3> candidates = new List<IntVec3>
            {
                caster.Position + new IntVec3(dx * 2, 0, dz * 2),
                caster.Position + new IntVec3(dx * 2, 0, dz),
                caster.Position + new IntVec3(dx, 0, dz * 2),
                caster.Position + new IntVec3(dx * 3, 0, dz * 3),
                caster.Position + new IntVec3(dx * 2, 0, 0),
                caster.Position + new IntVec3(0, 0, dz * 2),
            };

            foreach (IntVec3 cell in candidates)
            {
                if (!cell.IsValid || !cell.InBounds(map)) continue;
                if (!cell.Standable(map)) continue;
                if (cell.GetFirstPawn(map) != null) continue;

                // Проверка: эта клетка ДЕЙСТВИТЕЛЬНО блокирует LOS от shooter к caster?
                // Если LOS уже разорван без неё — стена бесполезна.
                if (!GenSight.LineOfSight(shooter.Position, caster.Position, map))
                    return false;

                result = cell;
                return true;
            }

            return false;
        }
        private bool TryCastSelfPsyAbility(Pawn caster, string abilityDefName)
        {
            if (caster == null || abilityDefName.NullOrEmpty())
                return false;

            return TryCastPsyAbility(caster, abilityDefName, caster, targetCell: false);
        }

        private bool TryCastPsyAbilityAtCell(Pawn caster, string abilityDefName, IntVec3 cell)
        {
            if (caster == null || abilityDefName.NullOrEmpty())
                return false;

            if (!caster.Spawned || caster.Map == null)
                return false;

            if (!cell.IsValid || !cell.InBounds(caster.Map))
                return false;

            AbilityDef abilityDef = FindAbilityDefByPossibleName(abilityDefName);

            if (abilityDef == null)
                return false;

            try
            {
                object ability = GetPawnAbility(caster, abilityDef);

                if (ability == null)
                    return false;

                if (IsAbilityOnCooldown(ability))
                    return false;

                LocalTargetInfo targetInfo = new LocalTargetInfo(cell);
                LocalTargetInfo destinationInfo = targetInfo;

                if (!CanPsyAbilityApplyOn(ability, targetInfo, destinationInfo))
                    return false;

                MethodInfo activateMethod = ability.GetType()
                    .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(m =>
                    {
                        if (m.Name != "Activate")
                            return false;

                        ParameterInfo[] args = m.GetParameters();

                        return args.Length == 2
                            && args[0].ParameterType == typeof(LocalTargetInfo)
                            && args[1].ParameterType == typeof(LocalTargetInfo);
                    });

                if (activateMethod == null)
                    return false;

                if (caster.jobs != null && caster.CurJob != null)
                {
                    caster.jobs.EndCurrentJob(JobCondition.InterruptForced);
                }

                activateMethod.Invoke(ability, new object[]
                {
            targetInfo,
            destinationInfo
                });

                ApplyPsycasterCastPause(caster);

                Log.Message("[Signal Interceptor] Psycaster VIP cast " +
                            abilityDef.defName +
                            " at cell " +
                            cell);

                return true;
            }
            catch (System.Exception ex)
            {
                Log.Warning("[Signal Interceptor] Psycaster VIP failed to cast " +
                            abilityDefName +
                            " at cell " +
                            cell +
                            ". Exception=" +
                            ex);

                return false;
            }
        }

        private bool TryCastPsyAbilityToDestination(Pawn caster, AbilityDef abilityDef, Pawn target, IntVec3 destination)
        {
            if (caster == null || abilityDef == null || target == null)
                return false;

            if (!caster.Spawned || !target.Spawned || caster.Map == null || caster.Map != target.Map)
                return false;

            if (!destination.IsValid || !destination.InBounds(caster.Map))
                return false;

            try
            {
                object ability = GetPawnAbility(caster, abilityDef);

                if (ability == null)
                    return false;

                if (IsAbilityOnCooldown(ability))
                    return false;

                LocalTargetInfo targetInfo = new LocalTargetInfo(target);
                LocalTargetInfo destinationInfo = new LocalTargetInfo(destination);

                if (!CanPsyAbilityApplyOn(ability, targetInfo, destinationInfo))
                    return false;

                MethodInfo activateMethod = ability.GetType()
                    .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(m =>
                    {
                        if (m.Name != "Activate")
                            return false;

                        ParameterInfo[] args = m.GetParameters();

                        return args.Length == 2
                            && args[0].ParameterType == typeof(LocalTargetInfo)
                            && args[1].ParameterType == typeof(LocalTargetInfo);
                    });

                if (activateMethod == null)
                    return false;

                if (caster.jobs != null && caster.CurJob != null)
                {
                    caster.jobs.EndCurrentJob(JobCondition.InterruptForced);
                }

                activateMethod.Invoke(ability, new object[]
                {
            targetInfo,
            destinationInfo
                });

                ApplyPsycasterCastPause(caster);

                Log.Message("[Signal Interceptor] Psycaster VIP cast " +
                            abilityDef.defName +
                            " on " +
                            target.LabelShort +
                            " to " +
                            destination);

                return true;
            }
            catch (System.Exception ex)
            {
                Log.Warning("[Signal Interceptor] Psycaster VIP failed destination cast. " +
                            "Ability=" + abilityDef.defName +
                            " | Target=" + target.LabelShort +
                            " | Destination=" + destination +
                            " | Exception=" + ex);

                return false;
            }
        }

        private bool TryCastPsyAbilityControlled(
            Pawn caster,
            string abilityDefName,
            Pawn target,
            float maxRange,
            bool requireLineOfSight,
            bool targetCell)
        {
            if (!IsValidPsycastTarget(caster, target, maxRange, requireLineOfSight))
                return false;

            return TryCastPsyAbility(caster, abilityDefName, target, targetCell);
        }

        private void ForcePsycasterNoFlee(Pawn pawn, Map map)
        {
            if (pawn == null || pawn.Dead || pawn.Downed || map == null)
                return;

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
                // Не критично.
            }

            pawn.mindState.duty = new PawnDuty(DutyDefOf.AssaultColony);

            bool badJob =
                IsFleeOrExitJob(pawn.CurJob) ||
                IsSuspiciousMapEdgeGotoJob(pawn, map);

            if (badJob && pawn.jobs != null)
            {
                pawn.jobs.EndCurrentJob(JobCondition.InterruptForced);
            }

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
                // Поле может отличаться между версиями.
            }
        }

        private bool TryCastPsyAbility(Pawn caster, string abilityDefName, Pawn target, bool targetCell)
        {
            if (caster == null || target == null || abilityDefName.NullOrEmpty())
                return false;

            if (caster.abilities == null)
                return false;

            if (!caster.Spawned || !target.Spawned || caster.Map != target.Map)
                return false;

            AbilityDef abilityDef = FindAbilityDefByPossibleName(abilityDefName);

            if (abilityDef == null)
                return false;

            try
            {
                object ability = GetPawnAbility(caster, abilityDef);

                if (ability == null)
                    return false;

                if (IsAbilityOnCooldown(ability))
                    return false;

                LocalTargetInfo targetInfo = targetCell
                    ? new LocalTargetInfo(target.Position)
                    : new LocalTargetInfo(target);

                LocalTargetInfo destinationInfo = targetInfo;

                if (!CanPsyAbilityApplyOn(ability, targetInfo, destinationInfo))
                    return false;

                MethodInfo activateMethod = ability.GetType()
                    .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(m =>
                    {
                        if (m.Name != "Activate")
                            return false;

                        ParameterInfo[] args = m.GetParameters();

                        return args.Length == 2
                            && args[0].ParameterType == typeof(LocalTargetInfo)
                            && args[1].ParameterType == typeof(LocalTargetInfo);
                    });

                if (activateMethod == null)
                    return false;

                if (caster.jobs != null && caster.CurJob != null)
                {
                    caster.jobs.EndCurrentJob(JobCondition.InterruptForced);
                }

                activateMethod.Invoke(ability, new object[]
                {
            targetInfo,
            destinationInfo
                });

                ApplyPsycasterCastPause(caster);

                Log.Message("[Signal Interceptor] Psycaster VIP cast " +
                            abilityDef.defName +
                            " on " +
                            target.LabelShort);

                return true;
            }
            catch (System.Exception ex)
            {
                Log.Warning("[Signal Interceptor] Psycaster VIP failed to cast " +
                            abilityDefName +
                            " on " +
                            target.LabelShort +
                            ". Exception=" +
                            ex);

                return false;
            }
        }

        private void ApplyPsycasterCastPause(Pawn psycaster)
        {
            if (psycaster == null || psycaster.jobs == null || psycaster.Dead || psycaster.Downed)
                return;

            if (!ShouldApplyPsycasterCastPause(psycaster))
                return;

            try
            {
                Job job = JobMaker.MakeJob(JobDefOf.Wait_Combat);
                job.expiryInterval = Rand.RangeInclusive(40, 70);
                job.checkOverrideOnExpire = true;

                psycaster.jobs.TryTakeOrderedJob(job, JobTag.Misc);
            }
            catch
            {
            }
        }

        private bool CanPsyAbilityApplyOn(object ability, LocalTargetInfo target, LocalTargetInfo destination)
        {
            if (ability == null)
                return false;

            try
            {
                MethodInfo canApplyMethod = ability.GetType()
                    .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(m =>
                    {
                        if (m.Name != "CanApplyOn")
                            return false;

                        ParameterInfo[] args = m.GetParameters();

                        return args.Length == 2
                            && args[0].ParameterType == typeof(LocalTargetInfo)
                            && args[1].ParameterType == typeof(LocalTargetInfo);
                    });

                if (canApplyMethod == null)
                    return true;

                object result = canApplyMethod.Invoke(ability, new object[]
                {
            target,
            destination
                });

                if (result is bool canApply)
                    return canApply;

                return true;
            }
            catch
            {
                return true;
            }
        }

        private void TryAttackPlayerShuttleOrBuilding(Pawn attacker, Map map)
        {
            if (attacker == null || map == null || attacker.Dead || attacker.Downed || attacker.jobs == null)
                return;

            Thing target = map.listerThings.AllThings
                .Where(t => t != null)
                .Where(t => t.Spawned && t.Map == map)
                .Where(t => t.Faction == Faction.OfPlayer)
                .Where(t => t.def != null && t.def.defName != null)
                .Where(t =>
                    t.def.defName.IndexOf("Shuttle", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    t.def.defName.IndexOf("Ship", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    t.def.defName.IndexOf("Transport", StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(t => t.Position.DistanceTo(attacker.Position))
                .FirstOrDefault();

            if (target == null)
                return;

            TryForceAttackThing(attacker, target);
        }

        private void TryForceAttackThing(Pawn attacker, Thing target)
        {
            if (attacker == null || target == null)
                return;

            if (attacker.Dead || attacker.Downed || attacker.jobs == null)
                return;

            if (!attacker.Spawned || !target.Spawned || attacker.Map != target.Map)
                return;

            try
            {
                Verb attackVerb = attacker.TryGetAttackVerb(target, allowManualCastWeapons: true);

                if (!CanUseAttackThingFromCurrentPosition(attacker, target, attackVerb))
                {
                    TryMoveTowardsThing(attacker, target);
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
            catch
            {
            }
        }

        private bool CanUseAttackThingFromCurrentPosition(Pawn attacker, Thing target, Verb verb)
        {
            if (attacker == null || target == null || attacker.Map == null)
                return false;

            float distance = attacker.Position.DistanceTo(target.Position);

            if (verb == null)
                return distance <= 1.9f;

            if (verb.IsMeleeAttack)
                return attacker.Position.AdjacentTo8WayOrInside(target.Position);

            float range = verb.verbProps?.range ?? 1.9f;

            if (distance > range * 0.95f)
                return false;

            if (!GenSight.LineOfSight(attacker.Position, target.Position, attacker.Map))
                return false;

            return true;
        }

        private void TryMoveTowardsThing(Pawn pawn, Thing target)
        {
            if (pawn == null || target == null || pawn.Map == null || pawn.jobs == null)
                return;

            Map map = pawn.Map;

            IntVec3 moveCell;

            bool found = CellFinder.TryFindRandomCellNear(
                target.Position,
                map,
                3,
                c => c.Standable(map)
                     && c.GetFirstPawn(map) == null
                     && pawn.CanReach(c, PathEndMode.OnCell, Danger.Deadly),
                out moveCell);

            if (!found)
                moveCell = target.Position;

            if (!moveCell.IsValid || !moveCell.InBounds(map) || !moveCell.Standable(map))
                return;

            Job job = JobMaker.MakeJob(JobDefOf.Goto, moveCell);
            job.expiryInterval = Rand.RangeInclusive(180, 300);
            job.checkOverrideOnExpire = true;

            pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc);
        }

        private object GetPawnAbility(Pawn pawn, AbilityDef abilityDef)
        {
            if (pawn?.abilities == null || abilityDef == null)
                return null;

            try
            {
                MethodInfo getAbilityMethod = pawn.abilities.GetType()
                    .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(m =>
                    {
                        if (m.Name != "GetAbility")
                            return false;

                        ParameterInfo[] args = m.GetParameters();
                        return args.Length == 1 && args[0].ParameterType == typeof(AbilityDef);
                    });

                if (getAbilityMethod != null)
                {
                    object result = getAbilityMethod.Invoke(pawn.abilities, new object[] { abilityDef });

                    if (result != null)
                        return result;
                }

                FieldInfo abilitiesField = pawn.abilities.GetType()
                    .GetField("abilities", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                if (abilitiesField != null)
                {
                    object value = abilitiesField.GetValue(pawn.abilities);

                    if (value is IEnumerable<Ability> abilityList)
                    {
                        Ability ability = abilityList.FirstOrDefault(a => a != null && a.def == abilityDef);

                        if (ability != null)
                            return ability;
                    }
                }

                PropertyInfo abilitiesProperty = pawn.abilities.GetType()
                    .GetProperty("Abilities", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                if (abilitiesProperty != null)
                {
                    object value = abilitiesProperty.GetValue(pawn.abilities, null);

                    if (value is IEnumerable<Ability> abilityList)
                    {
                        Ability ability = abilityList.FirstOrDefault(a => a != null && a.def == abilityDef);

                        if (ability != null)
                            return ability;
                    }
                }
            }
            catch (System.Exception ex)
            {
                Log.Warning("[Signal Interceptor] Failed to get pawn ability. " +
                            "Pawn=" + pawn.LabelShort +
                            " | AbilityDef=" + abilityDef.defName +
                            " | Exception=" + ex);
            }

            return null;
        }

        private bool IsAbilityOnCooldown(object ability)
        {
            if (ability == null)
                return true;

            try
            {
                BindingFlags flags =
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.NonPublic;

                PropertyInfo cooldownTicksRemaining = ability.GetType().GetProperty("CooldownTicksRemaining", flags);

                if (cooldownTicksRemaining != null && cooldownTicksRemaining.PropertyType == typeof(int))
                {
                    int value = (int)cooldownTicksRemaining.GetValue(ability, null);
                    return value > 0;
                }

                PropertyInfo cooldownTicksLeft = ability.GetType().GetProperty("CooldownTicksLeft", flags);

                if (cooldownTicksLeft != null && cooldownTicksLeft.PropertyType == typeof(int))
                {
                    int value = (int)cooldownTicksLeft.GetValue(ability, null);
                    return value > 0;
                }

                FieldInfo cooldownTicks = ability.GetType().GetField("cooldownTicks", flags);

                if (cooldownTicks != null && cooldownTicks.FieldType == typeof(int))
                {
                    int value = (int)cooldownTicks.GetValue(ability);
                    return value > 0;
                }

                FieldInfo cooldownTicksRemainingField = ability.GetType().GetField("cooldownTicksRemaining", flags);

                if (cooldownTicksRemainingField != null && cooldownTicksRemainingField.FieldType == typeof(int))
                {
                    int value = (int)cooldownTicksRemainingField.GetValue(ability);
                    return value > 0;
                }
            }
            catch
            {
                return false;
            }

            return false;
        }

        private void RefillPsycasterPsyfocus(Pawn pawn)
        {
            if (pawn == null)
                return;

            try
            {
                object entropy = pawn.psychicEntropy;

                if (entropy == null)
                    return;

                BindingFlags flags =
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.NonPublic;

                PropertyInfo psyfocusProperty = entropy.GetType().GetProperty("CurrentPsyfocus", flags);

                if (psyfocusProperty != null && psyfocusProperty.CanWrite)
                {
                    psyfocusProperty.SetValue(entropy, 1f, null);
                }

                FieldInfo psyfocusField = entropy.GetType().GetField("currentPsyfocus", flags);

                if (psyfocusField != null && psyfocusField.FieldType == typeof(float))
                {
                    psyfocusField.SetValue(entropy, 1f);
                }
            }
            catch
            {
                // Не критично. Без этого псикастер просто будет кастовать реже.
            }
        }

        private void TryForcePsycasterAttackNearestPlayerPawn(Pawn attacker, Map map)
        {
            if (attacker == null || attacker.Dead || attacker.Downed || attacker.jobs == null || map == null)
                return;

            Pawn target = map.mapPawns.AllPawnsSpawned
                .Where(p => p.Faction == Faction.OfPlayer)
                .Where(p => !p.Dead && !p.Downed)
                .Where(p => p.Spawned && p.Map == map)
                .OrderBy(p => p.Position.DistanceTo(attacker.Position))
                .FirstOrDefault();

            if (target == null)
                return;

            TryForceAttackPawn(attacker, target);
        }

        // ============================================================
        // Публичные обёртки для PsycasterBrain.
        // PsycasterBrain живёт в отдельном namespace и не имеет доступа
        // к private-методам этого partial-класса. Поэтому обёртки.
        // ============================================================

        public bool TryFindWallraiseCell_Public(Pawn caster, Pawn target, Map map, out IntVec3 cell)
        {
            return TryFindWallraiseCell(caster, target, map, out cell);
        }

        public bool TryCastSelfPsyAbility_Public(Pawn caster, string abilityDefName)
        {
            return TryCastSelfPsyAbility(caster, abilityDefName);
        }

        public bool TryCastPsyAbilityControlled_Public(
            Pawn caster, string abilityDefName, Pawn target,
            float maxRange, bool requireLos, bool targetCell)
        {
            return TryCastPsyAbilityControlled(caster, abilityDefName, target, maxRange, requireLos, targetCell);
        }

        public bool TryCastPsyAbilityAtCellControlled_Public(
            Pawn caster, string abilityDefName, IntVec3 cell,
            float maxRange, bool requireLos)
        {
            return TryCastPsyAbilityAtCellControlled(caster, abilityDefName, cell, maxRange, requireLos);
        }

        public bool TryCastPsyAbilityToDestination_Public(
            Pawn caster, string abilityDefName, Pawn target, IntVec3 destination)
        {
            AbilityDef def = FindAbilityDefByPossibleName(abilityDefName);
            if (def == null) return false;
            return TryCastPsyAbilityToDestination(caster, def, target, destination);
        }

        public AbilityDef FindAbilityDefByPossibleName_Public(string name)
        {
            return FindAbilityDefByPossibleName(name);
        }

        public object GetPawnAbility_Public(Pawn pawn, AbilityDef def)
        {
            return GetPawnAbility(pawn, def);
        }

        public bool IsAbilityOnCooldown_Public(object ability)
        {
            return IsAbilityOnCooldown(ability);
        }

        public bool IsRangedCombatPawn_Public(Pawn p)
        {
            return IsRangedCombatPawn(p);
        }

        public void ForcePsycasterNoFlee_Public(Pawn pawn, Map map)
        {
            ForcePsycasterNoFlee(pawn, map);
        }

        public void TryAttackPlayerShuttleOrBuilding_Public(Pawn attacker, Map map)
        {
            TryAttackPlayerShuttleOrBuilding(attacker, map);
        }

        public void TryForcePsycasterAttackNearestPlayerPawn_Public(Pawn attacker, Map map)
        {
            TryForcePsycasterAttackNearestPlayerPawn(attacker, map);
        }

        public bool TryForcePsycasterMeleeAttack_Public(Pawn psycaster, Pawn target)
        {
            return TryForcePsycasterMeleeAttack(psycaster, target);
        }

        public void InterruptBadPsycasterCombatJob_Public(Pawn psycaster, Pawn intendedTarget)
        {
            InterruptBadPsycasterCombatJob(psycaster, intendedTarget);
        }
    }
}
