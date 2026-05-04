using RimWorld;
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

            Faction hostileFaction = Faction.OfAncientsHostile;

            if (hostileFaction == null)
            {
                hostileFaction = Find.FactionManager.AllFactions
                    .Where(f => f != null && !f.IsPlayer && f.HostileTo(Faction.OfPlayer))
                    .RandomElementWithFallback(null);
            }

            if (hostileFaction == null)
            {
                Log.Error("[Signal Interceptor] Failed to find hostile faction for PsycasterVIP.");
                data.rewardGiven = true;
                data.vipSpawned = true;
                return;
            }

            data.enemyFaction = hostileFaction;

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

            IntVec3 center = map.Center;
            IntVec3 spot;

            if (!CellFinder.TryFindRandomCellNear(
                center,
                map,
                18,
                c => c.Standable(map) && !c.Roofed(map) && c.GetFirstPawn(map) == null,
                out spot))
            {
                spot = center;
            }

            GenSpawn.Spawn(psycaster, spot, map);

            if (psycaster.Faction != hostileFaction)
            {
                psycaster.SetFaction(hostileFaction);
            }

            data.psycasterPawn = psycaster;

            if (psycaster.mindState == null)
            {
                psycaster.mindState = new Pawn_MindState(psycaster);
            }

            psycaster.mindState.duty = new PawnDuty(DutyDefOf.AssaultColony);

            TryForcePsycasterAttackNearestPlayerPawn(psycaster, map);

            Find.LetterStack.ReceiveLetter(
                "SI_VIP_Name_Psycaster".Translate(),
                "SI_VIP_Desc_Psycaster".Translate("SI_PsycasterVIP_UnknownWorker".Translate()),
                LetterDefOf.ThreatBig,
                new LookTargets(psycaster)
            );

            Log.Message("[Signal Interceptor] Psycaster VIP spawned. " +
                        "Pawn=" + psycaster.LabelShort +
                        " | Faction=" + hostileFaction.Name +
                        " | Tier=" + tier +
                        " | Psylink=" + psylinkLevel);
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

            AddOrSetPsycasterPsylinkLevel(pawn, psylinkLevel);
            GivePsycasterVIPAbilities(pawn, tier);
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

        private void GivePsycasterVIPAbilities(Pawn pawn, int tier)
        {
            if (pawn?.abilities == null)
            {
                Log.Warning("[Signal Interceptor] Psycaster has no ability tracker: " + pawn?.LabelShort);
                return;
            }

            List<string> abilityNames = new List<string>
    {
        "Focus",
        "Stun",
        "BlindingPulse",
        "VertigoPulse"
    };

            if (tier >= 4)
            {
                abilityNames.AddRange(new[]
                {
            "Beckon",
            "Skip",
            "Smokepop",
            "ChaosSkip"
        });
            }

            if (tier >= 6)
            {
                abilityNames.AddRange(new[]
                {
            "Berserk",
            "Invisibility",
            "Wallraise"
        });
            }

            if (tier >= 8)
            {
                abilityNames.AddRange(new[]
                {
            "BerserkPulse",
            "MassChaosSkip",
            "ManhunterPulse"
        });
            }

            foreach (string defName in abilityNames)
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
                    def != null
                    && def.defName != null
                    && def.defName.Equals(name, StringComparison.OrdinalIgnoreCase));
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

        private void TickPsycasterCombatAI(VIPSiteData data, Pawn psycaster, Map map)
        {
            if (data == null || psycaster == null || map == null)
                return;

            if (psycaster.Dead || psycaster.Downed || !psycaster.Spawned)
                return;

            if (psycaster.Faction == Faction.OfPlayer)
                return;

            ForcePsycasterNoFlee(psycaster, map);

            List<Pawn> targets = map.mapPawns.AllPawnsSpawned
                .Where(p => p != null)
                .Where(p => p.Faction == Faction.OfPlayer)
                .Where(p => !p.Dead && !p.Downed)
                .Where(p => p.Spawned && p.Map == map)
                .OrderBy(p => p.Position.DistanceTo(psycaster.Position))
                .ToList();

            if (targets.Count == 0)
                return;

            int currentTick = Find.TickManager.TicksGame;

            if (data.psycasterNextCastTick < 0)
                data.psycasterNextCastTick = currentTick + Rand.RangeInclusive(60, 120);

            if (data.psycasterNextDefensiveCastTick < 0)
                data.psycasterNextDefensiveCastTick = currentTick;

            if (data.psycasterNextWallraiseTick < 0)
                data.psycasterNextWallraiseTick = currentTick;

            if (data.psycasterNextSmokepopTick < 0)
                data.psycasterNextSmokepopTick = currentTick;

            bool casted = false;

            // Экстренная реакция может сработать раньше обычного кулдауна.
            casted = TryPsycasterEmergencyDefense(data, psycaster, map, targets);

            if (casted)
            {
                ApplyPsycasterCastPause(psycaster);
                return;
            }

            if (currentTick < data.psycasterNextCastTick)
            {
                TryForcePsycasterAttackNearestPlayerPawn(psycaster, map);
                return;
            }

            RefillPsycasterPsyfocus(psycaster);

            casted = TryPsycasterCastBestAbility(data, psycaster, map, targets);

            if (casted)
            {
                data.psycasterNextCastTick = currentTick + Rand.RangeInclusive(360, 600);
                ApplyPsycasterCastPause(psycaster);
                return;
            }

            data.psycasterNextCastTick = currentTick + Rand.RangeInclusive(120, 240);
            TryForcePsycasterAttackNearestPlayerPawn(psycaster, map);
        }

        private bool TryPsycasterCastBestAbility(VIPSiteData data, Pawn psycaster, Map map, List<Pawn> targets)
        {
            if (data == null || psycaster == null || map == null || targets.NullOrEmpty())
                return false;

            int currentTick = Find.TickManager.TicksGame;

            List<Pawn> validTargets = targets
                .Where(t => IsValidPsycastTarget(psycaster, t, 20f, requireLineOfSight: true))
                .ToList();

            if (validTargets.Count == 0)
                return false;

            List<Pawn> animals = validTargets
                .Where(p => p.RaceProps != null && p.RaceProps.Animal)
                .ToList();

            List<Pawn> humanTargets = validTargets
                .Where(p => p.RaceProps != null && p.RaceProps.Humanlike)
                .ToList();

            List<Pawn> rangedTargets = humanTargets
                .Where(IsRangedCombatPawn)
                .ToList();

            List<Pawn> meleeTargets = humanTargets
                .Where(p => !IsRangedCombatPawn(p))
                .ToList();

            List<Pawn> closeEnemies = validTargets
                .Where(p => p.Position.DistanceTo(psycaster.Position) <= 3.0f)
                .ToList();

            List<Pawn> nearEnemies = validTargets
                .Where(p => p.Position.DistanceTo(psycaster.Position) <= 8.0f)
                .ToList();

            // 1. Бафф Focus один раз за бой.
            if (!data.psycasterFocusUsed)
            {
                if (TryCastSelfPsyAbility(psycaster, "Focus"))
                {
                    data.psycasterFocusUsed = true;
                    return true;
                }

                // Если способности нет или не сработала — больше не пытаемся спамить.
                data.psycasterFocusUsed = true;
            }

            // 2. Если есть животные игрока — пробуем ManhunterPulse.
            if (animals.Count >= 2)
            {
                Pawn animalCenter = animals.RandomElement();

                if (TryCastPsyAbilityControlled(psycaster, "ManhunterPulse", animalCenter, 20f, true, targetCell: true))
                    return true;
            }

            // 3. Если врагов много — сначала ярость/дезориентация по группе.
            if (humanTargets.Count >= 3)
            {
                IntVec3? safeClusterCell = FindSafePulseCell(psycaster, humanTargets, 20f, 7f, minTargets: 2);

                if (safeClusterCell.HasValue)
                {
                    if (TryCastPsyAbilityAtCell(psycaster, "BerserkPulse", safeClusterCell.Value))
                        return true;

                    if (TryCastPsyAbilityAtCell(psycaster, "VertigoPulse", safeClusterCell.Value))
                        return true;

                    if (TryCastPsyAbilityAtCell(psycaster, "BlindingPulse", safeClusterCell.Value))
                        return true;
                }

                Pawn berserkTarget = humanTargets
                    .Where(p => p.Position.DistanceTo(psycaster.Position) <= 18f)
                    .OrderByDescending(p => CountPlayerPawnsNearCell(map, p.Position, 6f))
                    .FirstOrDefault();

                if (berserkTarget != null && TryCastPsyAbilityControlled(psycaster, "Berserk", berserkTarget, 18f, true, targetCell: false))
                    return true;
            }

            // 4. Против большого числа дальников — защита и попытка втянуть их в ближний бой.
            if (rangedTargets.Count >= 2 || rangedTargets.Count > meleeTargets.Count)
            {
                Pawn nearestRanged = rangedTargets
                    .OrderBy(p => p.Position.DistanceTo(psycaster.Position))
                    .FirstOrDefault();

                if (nearestRanged != null)
                {
                    if (currentTick >= data.psycasterNextWallraiseTick)
                    {
                        IntVec3 wallCell;

                        if (TryFindWallraiseCell(psycaster, nearestRanged, map, out wallCell))
                        {
                            if (TryCastPsyAbilityAtCell(psycaster, "Wallraise", wallCell))
                            {
                                data.psycasterNextWallraiseTick = currentTick + Rand.RangeInclusive(1200, 1800);
                                return true;
                            }
                        }

                        data.psycasterNextWallraiseTick = currentTick + Rand.RangeInclusive(600, 900);
                    }

                    if (currentTick >= data.psycasterNextSmokepopTick)
                    {
                        if (TryCastSelfPsyAbility(psycaster, "Smokepop"))
                        {
                            data.psycasterNextSmokepopTick = currentTick + Rand.RangeInclusive(900, 1400);
                            return true;
                        }

                        data.psycasterNextSmokepopTick = currentTick + Rand.RangeInclusive(500, 700);
                    }

                    if (TryCastPsyAbilityControlled(psycaster, "Beckon", nearestRanged, 18f, true, targetCell: false))
                        return true;

                    if (TrySkipEnemyToPsycaster(psycaster, nearestRanged, map))
                        return true;
                }
            }

            // 5. Если ближник подходит близко — Stun.
            Pawn closestMelee = meleeTargets
                .Where(p => p.Position.DistanceTo(psycaster.Position) <= 10f)
                .OrderBy(p => p.Position.DistanceTo(psycaster.Position))
                .FirstOrDefault();

            if (closestMelee != null)
            {
                if (TryCastPsyAbilityControlled(psycaster, "Stun", closestMelee, 18f, true, targetCell: false))
                    return true;

                IntVec3? safePulseCell = FindSafePulseCell(psycaster, new List<Pawn> { closestMelee }, 18f, 7f, minTargets: 1);

                if (safePulseCell.HasValue)
                {
                    if (TryCastPsyAbilityAtCell(psycaster, "BlindingPulse", safePulseCell.Value))
                        return true;

                    if (TryCastPsyAbilityAtCell(psycaster, "VertigoPulse", safePulseCell.Value))
                        return true;
                }
            }

            // 6. Если один дальник — подтягиваем к себе.
            Pawn singleRanged = rangedTargets
                .OrderBy(p => p.Position.DistanceTo(psycaster.Position))
                .FirstOrDefault();

            if (singleRanged != null)
            {
                if (TrySkipEnemyToPsycaster(psycaster, singleRanged, map))
                    return true;

                if (TryCastPsyAbilityControlled(psycaster, "Beckon", singleRanged, 18f, true, targetCell: false))
                    return true;

                if (TryCastPsyAbilityControlled(psycaster, "Stun", singleRanged, 18f, true, targetCell: false))
                    return true;
            }

            return false;
        }

        private bool TryPsycasterEmergencyDefense(VIPSiteData data, Pawn psycaster, Map map, List<Pawn> targets)
        {
            if (data == null || psycaster == null || map == null || targets.NullOrEmpty())
                return false;

            int currentTick = Find.TickManager.TicksGame;

            if (currentTick < data.psycasterNextDefensiveCastTick)
                return false;

            List<Pawn> closeEnemies = targets
                .Where(p => p != null)
                .Where(p => !p.Dead && !p.Downed)
                .Where(p => p.Spawned && p.Map == map)
                .Where(p => p.Position.DistanceTo(psycaster.Position) <= 2.2f)
                .ToList();

            if (closeEnemies.Count < 2)
                return false;

            RefillPsycasterPsyfocus(psycaster);

            if (TryCastSelfPsyAbility(psycaster, "Invisibility"))
            {
                data.psycasterNextDefensiveCastTick = currentTick + Rand.RangeInclusive(480, 720);
                return true;
            }

            if (TryCastPsyAbilityAtCell(psycaster, "MassChaosSkip", psycaster.Position))
            {
                data.psycasterNextDefensiveCastTick = currentTick + Rand.RangeInclusive(480, 720);
                return true;
            }

            if (TryCastSelfPsyAbility(psycaster, "ChaosSkip"))
            {
                data.psycasterNextDefensiveCastTick = currentTick + Rand.RangeInclusive(480, 720);
                return true;
            }

            Pawn nearest = closeEnemies
                .OrderBy(p => p.Position.DistanceTo(psycaster.Position))
                .FirstOrDefault();

            if (nearest != null && TryCastPsyAbilityControlled(psycaster, "Stun", nearest, 18f, true, targetCell: false))
            {
                data.psycasterNextDefensiveCastTick = currentTick + Rand.RangeInclusive(300, 480);
                return true;
            }

            data.psycasterNextDefensiveCastTick = currentTick + Rand.RangeInclusive(180, 300);
            return false;
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

        private int CountPlayerPawnsNearCell(Map map, IntVec3 cell, float radius)
        {
            if (map == null)
                return 0;

            return map.mapPawns.AllPawnsSpawned
                .Where(p => p != null)
                .Where(p => p.Faction == Faction.OfPlayer)
                .Where(p => !p.Dead && !p.Downed)
                .Where(p => p.Spawned && p.Map == map)
                .Count(p => p.Position.DistanceTo(cell) <= radius);
        }

        private IntVec3? FindSafePulseCell(Pawn caster, List<Pawn> targets, float maxCastRange, float unsafeRadiusFromCaster, int minTargets)
        {
            if (caster == null || caster.Map == null || targets.NullOrEmpty())
                return null;

            Map map = caster.Map;

            List<IntVec3> candidateCells = targets
                .Where(t => t != null && t.Spawned && t.Map == map && !t.Dead && !t.Downed)
                .Select(t => t.Position)
                .Distinct()
                .Where(c => c.DistanceTo(caster.Position) <= maxCastRange)
                .Where(c => c.DistanceTo(caster.Position) > unsafeRadiusFromCaster)
                .Where(c => GenSight.LineOfSight(caster.Position, c, map))
                .ToList();

            if (candidateCells.Count == 0)
                return null;

            IntVec3 bestCell = IntVec3.Invalid;
            int bestScore = -1;

            foreach (IntVec3 cell in candidateCells)
            {
                int score = targets.Count(t =>
                    t != null
                    && t.Spawned
                    && t.Map == map
                    && !t.Dead
                    && !t.Downed
                    && t.Position.DistanceTo(cell) <= 5.9f);

                if (score > bestScore)
                {
                    bestScore = score;
                    bestCell = cell;
                }
            }

            if (!bestCell.IsValid || bestScore < minTargets)
                return null;

            return bestCell;
        }

        private bool TryFindWallraiseCell(Pawn caster, Pawn shooter, Map map, out IntVec3 result)
        {
            result = IntVec3.Invalid;

            if (caster == null || shooter == null || map == null)
                return false;

            IntVec3 direction = caster.Position - shooter.Position;

            int dx = Math.Sign(direction.x);
            int dz = Math.Sign(direction.z);

            List<IntVec3> candidates = new List<IntVec3>
    {
        caster.Position + new IntVec3(dx, 0, dz),
        caster.Position + new IntVec3(dx, 0, 0),
        caster.Position + new IntVec3(0, 0, dz),
        caster.Position + new IntVec3(-dx, 0, dz),
        caster.Position + new IntVec3(dx, 0, -dz)
    };

            foreach (IntVec3 cell in candidates)
            {
                if (!cell.IsValid || !cell.InBounds(map))
                    continue;

                if (!cell.Standable(map))
                    continue;

                if (cell.GetFirstPawn(map) != null)
                    continue;

                result = cell;
                return true;
            }

            return CellFinder.TryFindRandomCellNear(
                caster.Position,
                map,
                3,
                c => c.Standable(map) && c.GetFirstPawn(map) == null,
                out result);
        }

        private bool TrySkipEnemyToPsycaster(Pawn caster, Pawn enemy, Map map)
        {
            if (caster == null || enemy == null || map == null)
                return false;

            if (!IsValidPsycastTarget(caster, enemy, 18f, requireLineOfSight: true))
                return false;

            AbilityDef abilityDef = FindAbilityDefByPossibleName("Skip");

            if (abilityDef == null)
                return false;

            IntVec3 destination;

            bool found = CellFinder.TryFindRandomCellNear(
                caster.Position,
                map,
                2,
                c => c.Standable(map)
                     && c.GetFirstPawn(map) == null
                     && c.DistanceTo(caster.Position) <= 1.9f,
                out destination);

            if (!found)
                return false;

            return TryCastPsyAbilityToDestination(caster, abilityDef, enemy, destination);
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

            try
            {
                Job job = JobMaker.MakeJob(JobDefOf.Wait_Combat);
                job.expiryInterval = Rand.RangeInclusive(60, 120);
                job.checkOverrideOnExpire = true;

                psycaster.jobs.TryTakeOrderedJob(job, JobTag.Misc);
            }
            catch
            {
                // Не критично. Если пауза не сработала, AI продолжит обычное поведение.
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
    }
}
