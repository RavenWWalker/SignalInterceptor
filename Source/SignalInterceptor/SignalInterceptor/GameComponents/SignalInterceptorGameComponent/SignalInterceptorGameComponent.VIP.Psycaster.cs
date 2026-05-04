using RimWorld;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Verse;
using Verse.AI;

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

            AddPsycasterTraitIfPossible(pawn, "PsychicSensitivity");
            AddPsycasterTraitIfPossible(pawn, "PsychicallyHypersensitive");

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

        private void AddPsycasterTraitIfPossible(Pawn pawn, string traitDefName)
        {
            if (pawn?.story?.traits == null || traitDefName.NullOrEmpty())
                return;

            TraitDef traitDef = DefDatabase<TraitDef>.GetNamedSilentFail(traitDefName);

            if (traitDef == null)
                return;

            if (pawn.story.traits.HasTrait(traitDef))
                return;

            try
            {
                pawn.story.traits.GainTrait(new Trait(traitDef));
            }
            catch
            {
                // Некоторые трейты могут конфликтовать. Не критично.
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
                return;

            List<string> abilityNames = new List<string>
            {
                "Stun",
                "Burden",
                "Blind",
                "BlindingPulse",
                "VertigoPulse"
            };

            if (tier >= 4)
            {
                abilityNames.AddRange(new[]
                {
                    "Beckon",
                    "Skip",
                    "Smokepop"
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
                    "MassChaosSkip"
                });
            }

            foreach (string defName in abilityNames)
            {
                AbilityDef abilityDef = DefDatabase<AbilityDef>.GetNamedSilentFail(defName);

                if (abilityDef == null)
                    continue;

                try
                {
                    pawn.abilities.GainAbility(abilityDef);
                }
                catch
                {
                    // Если способность уже есть или не подходит — пропускаем.
                }
            }
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

        private void TickPsycasterCombatAI(Pawn psycaster, Map map)
        {
            if (psycaster == null || map == null)
                return;

            if (psycaster.Dead || psycaster.Downed || !psycaster.Spawned)
                return;

            if (psycaster.Faction == Faction.OfPlayer)
                return;

            if (Find.TickManager.TicksGame % 180 != psycaster.thingIDNumber % 180)
                return;

            RefillPsycasterPsyfocus(psycaster);

            List<Pawn> targets = map.mapPawns.AllPawnsSpawned
                .Where(p => p != null)
                .Where(p => p.Faction == Faction.OfPlayer)
                .Where(p => !p.Dead && !p.Downed)
                .Where(p => p.Spawned && p.Map == map)
                .OrderBy(p => p.Position.DistanceTo(psycaster.Position))
                .ToList();

            if (targets.Count == 0)
                return;

            Pawn nearest = targets.First();

            List<Pawn> clusteredTargets = targets
                .Where(p => p.Position.DistanceTo(psycaster.Position) <= 18f)
                .ToList();

            if (clusteredTargets.Count >= 3)
            {
                Pawn clusterCenter = clusteredTargets.RandomElement();

                if (TryCastPsyAbility(psycaster, "VertigoPulse", clusterCenter))
                    return;

                if (TryCastPsyAbility(psycaster, "BlindingPulse", clusterCenter))
                    return;

                if (TryCastPsyAbility(psycaster, "BerserkPulse", clusterCenter))
                    return;
            }

            if (nearest.Position.DistanceTo(psycaster.Position) <= 10f)
            {
                if (TryCastPsyAbility(psycaster, "Stun", nearest))
                    return;

                if (TryCastPsyAbility(psycaster, "Burden", nearest))
                    return;

                if (TryCastPsyAbility(psycaster, "Beckon", nearest))
                    return;
            }

            Pawn randomTarget = targets.RandomElement();

            if (TryCastPsyAbility(psycaster, "Blind", randomTarget))
                return;

            if (TryCastPsyAbility(psycaster, "Burden", randomTarget))
                return;

            if (TryCastPsyAbility(psycaster, "Stun", randomTarget))
                return;

            TryForcePsycasterAttackNearestPlayerPawn(psycaster, map);
        }

        private bool TryCastPsyAbility(Pawn caster, string abilityDefName, Pawn target)
        {
            if (caster == null || target == null || abilityDefName.NullOrEmpty())
                return false;

            if (caster.abilities == null)
                return false;

            AbilityDef abilityDef = DefDatabase<AbilityDef>.GetNamedSilentFail(abilityDefName);

            if (abilityDef == null)
                return false;

            try
            {
                object ability = GetPawnAbility(caster, abilityDef);

                if (ability == null)
                    return false;

                if (IsAbilityOnCooldown(ability))
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

                LocalTargetInfo targetInfo = new LocalTargetInfo(target);

                activateMethod.Invoke(ability, new object[]
                {
                    targetInfo,
                    targetInfo
                });

                Log.Message("[Signal Interceptor] Psycaster VIP cast " +
                            abilityDefName +
                            " on " +
                            target.LabelShort);

                return true;
            }
            catch
            {
                return false;
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
                    return getAbilityMethod.Invoke(pawn.abilities, new object[] { abilityDef });
                }

                PropertyInfo abilitiesProperty = pawn.abilities.GetType()
                    .GetProperty("Abilities", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                if (abilitiesProperty != null)
                {
                    object value = abilitiesProperty.GetValue(pawn.abilities, null);

                    if (value is IEnumerable<Ability> abilities)
                    {
                        return abilities.FirstOrDefault(a => a?.def == abilityDef);
                    }
                }
            }
            catch
            {
                return null;
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

                PropertyInfo property = ability.GetType().GetProperty("CooldownTicksRemaining", flags);

                if (property != null && property.PropertyType == typeof(int))
                {
                    int value = (int)property.GetValue(ability, null);
                    return value > 0;
                }

                FieldInfo field = ability.GetType().GetField("cooldownTicks", flags);

                if (field != null && field.FieldType == typeof(int))
                {
                    int value = (int)field.GetValue(ability);
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

            Job job = JobMaker.MakeJob(JobDefOf.AttackStatic, target);
            job.expiryInterval = Rand.RangeInclusive(240, 420);
            job.checkOverrideOnExpire = true;

            attacker.jobs.TryTakeOrderedJob(job, JobTag.Misc);
        }
    }
}
