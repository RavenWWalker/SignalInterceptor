using RimWorld;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;
using Verse.AI;

namespace SignalInterceptor.AI.Psycaster
{
    public partial class PsycasterBrain
    {
        private bool TryRunPreEngageInvisibility(BattlefieldSnapshot snap)
        {
            if (snap == null || caster == null || caster.Destroyed || caster.Dead || caster.Downed || !caster.Spawned)
                return false;

            if (caster.Map == null || !snap.HasEnemies)
                return false;

            if (!IsCasterFreeToAct())
                return false;

            if (IsCasterInvisibleNow())
                return false;

            if (IsOnSoftCooldown("Invisibility"))
                return false;

            /*
             * Не ломаем активный melee/kill contract.
             */
            int now = Find.TickManager.TicksGame;

            if (HasActiveKillContract)
                return false;

            if (pendingMeleeTargetThingId >= 0 && pendingMeleeUntilTick > now)
                return false;

            float hp = caster.health != null && caster.health.summaryHealth != null
                ? caster.health.summaryHealth.SummaryHealthPercent
                : 1f;

            /*
             * Если он уже сильно ранен — это не pre-engage.
             * Тогда пусть работает recovery logic.
             */
            if (hp <= 0.70f)
                return false;

            int enemyCount = 0;
            int rangedCount = 0;
            int rangedLos = 0;
            int meleeLikeCount = 0;
            float nearestEnemy = 999f;
            float nearestRanged = 999f;
            float totalRangedDps = 0f;

            Map map = caster.Map;

            if (snap.enemies != null)
            {
                for (int i = 0; i < snap.enemies.Count; i++)
                {
                    EnemyAssessment e = snap.enemies[i];

                    if (e == null || e.pawn == null)
                        continue;

                    Pawn p = e.pawn;

                    if (p.Destroyed || p.Dead || p.Downed || !p.Spawned || p.Map != map)
                        continue;

                    enemyCount++;

                    if (e.distanceToCaster < nearestEnemy)
                        nearestEnemy = e.distanceToCaster;

                    bool meleeLike =
                        e.IsMelee ||
                        e.IsAnimal ||
                        e.role == EnemyRole.Wimp;

                    if (meleeLike)
                        meleeLikeCount++;

                    if (e.IsRanged)
                    {
                        rangedCount++;

                        if (e.distanceToCaster < nearestRanged)
                            nearestRanged = e.distanceToCaster;

                        if (e.hasLineOfSight && e.canShootNow)
                            rangedLos++;

                        if (e.estimatedDps > 0f)
                            totalRangedDps += e.estimatedDps;
                    }
                }
            }

            /*
             * Главный trigger:
             * большой pack с несколькими стрелками.
             *
             * Это как раз твой тест:
             * 6 врагов, 4 ranged, общий cluster=6.
             */
            bool largeRangedCluster =
                enemyCount >= 5 &&
                rangedCount >= 3 &&
                snap.largestClusterSize >= 4;

            /*
             * Альтернативный trigger:
             * меньше врагов, но они уже держат LOS и могут быстро снести shield.
             */
            bool dangerousOpenApproach =
                rangedLos >= 3 ||
                totalRangedDps >= 30f;

            /*
             * Если враги уже в упор, Invisibility может быть поздно/не то.
             * Тогда пусть emergency/recovery/melee решают.
             */
            if (nearestEnemy < 10f)
                return false;

            /*
             * Если враги очень далеко, не тратим invis заранее.
             * Пусть сначала подойдёт до разумной зоны.
             */
            if (nearestEnemy > 45f && rangedLos <= 0)
                return false;

            if (!largeRangedCluster && !dangerousOpenApproach)
                return false;

            if (!HasEnoughPsyfocusForAbility("Invisibility"))
            {
                if (Prefs.DevMode)
                {
                    Log.Message("[Signal Interceptor] Psycaster pre-engage Invisibility blocked by psyfocus: "
                                + caster.LabelShort
                                + " | psyfocus=" + GetCurrentPsyfocusFraction().ToString("F2")
                                + " | required=" + GetMinimumPsyfocusForAbility("Invisibility").ToString("F2")
                                + " | enemies=" + enemyCount
                                + " | ranged=" + rangedCount
                                + " | rangedLos=" + rangedLos);
                }

                return false;
            }

            bool casted = TryCastSelfCombatAbility(
                "Invisibility",
                PsycasterTuning.InvisibilitySoftCooldownMin,
                PsycasterTuning.InvisibilitySoftCooldownMax,
                "pre-engage stealth");

            if (!casted)
                return false;

            nextActionSelectTick = now + PsycasterTuning.CastWarmupMedium + 60;
            nextStanceReevalTick = now + 60;

            Log.Message("[Signal Interceptor] Psycaster pre-engage Invisibility: "
                        + caster.LabelShort
                        + " | hp=" + hp.ToString("F2")
                        + " | enemies=" + enemyCount
                        + " | ranged=" + rangedCount
                        + " | rangedLos=" + rangedLos
                        + " | meleeLike=" + meleeLikeCount
                        + " | cluster=" + snap.largestClusterSize
                        + " | nearestEnemy=" + nearestEnemy.ToString("F1")
                        + " | nearestRanged=" + nearestRanged.ToString("F1")
                        + " | rangedDps=" + totalRangedDps.ToString("F1"));

            return true;
        }

        private bool TryRunSingleEnemyDuelResolution(BattlefieldSnapshot snap)
        {
            if (snap == null || caster == null || caster.Destroyed || caster.Dead || caster.Downed || !caster.Spawned)
                return false;

            if (caster.Map == null || snap.enemies == null)
                return false;

            if (!IsCasterFreeToAct())
                return false;

            Map map = caster.Map;

            EnemyAssessment only = null;
            int standingEnemies = 0;

            for (int i = 0; i < snap.enemies.Count; i++)
            {
                EnemyAssessment e = snap.enemies[i];

                if (e == null || e.pawn == null)
                    continue;

                if (e.pawn.Destroyed || e.pawn.Dead || e.pawn.Downed || !e.pawn.Spawned || e.pawn.Map != map)
                    continue;

                standingEnemies++;
                only = e;
            }

            if (standingEnemies != 1 || only == null || only.pawn == null)
                return false;

            Pawn target = only.pawn;

            float hp = caster.health != null && caster.health.summaryHealth != null
                ? caster.health.summaryHealth.SummaryHealthPercent
                : 1f;

            float d = caster.Position.DistanceTo(target.Position);

            bool bleeding = HasDangerousBleeding();

            /*
             * Если он реально ранен/кровоточит — не ломаем recovery.
             */
            if (hp <= 0.70f)
                return false;

            if (bleeding && hp <= 0.86f)
                return false;

            /*
             * Если текущая работа уже melee по этому же врагу — не перезапускаем её.
             */
            Job curJob = caster.CurJob;

            if (curJob != null &&
                curJob.def == JobDefOf.AttackMelee &&
                curJob.targetA.HasThing &&
                curJob.targetA.Thing == target)
            {
                return true;
            }

            /*
             * Главный случай из лога:
             * один снайпер, дистанция 10-15, кастер ходит туда-сюда.
             *
             * В этой ситуации он должен не "сохранять ranged tools",
             * а коммититься в добивание.
             */
            bool rangedDuel =
                only.IsRanged ||
                only.role == EnemyRole.Sniper ||
                only.role == EnemyRole.Heavy;

            bool meleeDuel =
                only.IsMelee ||
                only.IsAnimal ||
                only.role == EnemyRole.Wimp;

            if (!rangedDuel && !meleeDuel)
                return false;

            /*
             * Если враг далеко, пусть обычная логика подводит его на дистанцию.
             * Но если уже в пределах 30 клеток — melee job нормально догонит цель.
             */
            if (d > 30f)
                return false;

            if (!caster.CanReach(target, PathEndMode.Touch, Danger.Deadly))
                return false;

            /*
             * Для одного melee-врага:
             * если кастер здоров, не надо бесконечно убегать.
             * Пусть принимает дуэль.
             */
            if (meleeDuel && hp >= 0.82f && !bleeding)
            {
                Job meleeJob = JobMaker.MakeJob(JobDefOf.AttackMelee, target);
                meleeJob.locomotionUrgency = LocomotionUrgency.Sprint;
                meleeJob.expiryInterval = 240;
                meleeJob.checkOverrideOnExpire = true;
                meleeJob.maxNumMeleeAttacks = 1;

                caster.jobs.StartJob(
                    meleeJob,
                    JobCondition.InterruptForced,
                    null,
                    resumeCurJobAfterwards: false,
                    cancelBusyStances: true
                );

                StartKillContract(target, 480, "SingleMeleeDuel");

                nextActionSelectTick = Find.TickManager.TicksGame + 90;
                nextStanceReevalTick = Find.TickManager.TicksGame + 90;

                Log.Message("[Signal Interceptor] Psycaster single-melee duel commit: "
                            + target.LabelShort
                            + " | d=" + d.ToString("F1")
                            + " | hp=" + hp.ToString("F2")
                            + " | bleeding=" + bleeding);

                return true;
            }

            /*
             * Для одного дальника:
             * если кастер уже достаточно близко, не ходим туда-сюда.
             * Дожимаем.
             */
            if (rangedDuel && hp >= 0.74f)
            {
                Job meleeJob = JobMaker.MakeJob(JobDefOf.AttackMelee, target);
                meleeJob.locomotionUrgency = LocomotionUrgency.Sprint;
                meleeJob.expiryInterval = 300;
                meleeJob.checkOverrideOnExpire = true;
                meleeJob.maxNumMeleeAttacks = 1;

                caster.jobs.StartJob(
                    meleeJob,
                    JobCondition.InterruptForced,
                    null,
                    resumeCurJobAfterwards: false,
                    cancelBusyStances: true
                );

                StartKillContract(target, 600, "SingleRangedDuel");

                pendingMeleeTargetThingId = target.thingIDNumber;
                pendingMeleeUntilTick = Find.TickManager.TicksGame + 360;
                pendingMeleeReason = "SingleRangedDuel";

                nextActionSelectTick = Find.TickManager.TicksGame + 90;
                nextStanceReevalTick = Find.TickManager.TicksGame + 90;

                Log.Message("[Signal Interceptor] Psycaster single-ranged duel commit: "
                            + target.LabelShort
                            + " | d=" + d.ToString("F1")
                            + " | role=" + only.role
                            + " | hp=" + hp.ToString("F2")
                            + " | bleeding=" + bleeding);

                return true;
            }

            return false;
        }

        private bool TryRunWeakPsycasterForcedAssault(BattlefieldSnapshot snap)
        {
            if (snap == null || caster == null || caster.Destroyed || caster.Dead || caster.Downed || !caster.Spawned)
                return false;

            if (caster.Map == null || snap.enemies == null || !snap.HasEnemies)
                return false;

            if (!IsCasterFreeToAct())
                return false;

            int now = Find.TickManager.TicksGame;

            if (HasActiveKillContract)
                return false;

            if (pendingMeleeTargetThingId >= 0 && pendingMeleeUntilTick > now)
                return false;

            int psylink = GetCasterPsylinkLevelSafe();

            if (psylink <= 0 || psylink >= 5)
                return false;

            float hp = caster.health != null && caster.health.summaryHealth != null
                ? caster.health.summaryHealth.SummaryHealthPercent
                : 1f;

            if (hp < 0.72f)
                return false;

            if (HasDangerousBleeding())
                return false;

            if (recoveryMode)
                return false;

            if (now < graceUntilTick + 900)
                return false;

            int standingEnemies = 0;
            int rangedEnemies = 0;
            int meleeLikeEnemies = 0;
            int closeMeleeLikeEnemies = 0;

            EnemyAssessment best = null;
            float bestScore = float.MinValue;

            Map map = caster.Map;

            for (int i = 0; i < snap.enemies.Count; i++)
            {
                EnemyAssessment e = snap.enemies[i];

                if (e == null || e.pawn == null)
                    continue;

                Pawn p = e.pawn;

                if (p.Destroyed || p.Dead || p.Downed || !p.Spawned || p.Map != map)
                    continue;

                standingEnemies++;

                if (e.IsRanged)
                    rangedEnemies++;

                bool meleeLike =
                    e.IsMelee ||
                    e.IsAnimal ||
                    e.role == EnemyRole.Wimp;

                if (meleeLike)
                {
                    meleeLikeEnemies++;

                    if (e.distanceToCaster <= 10f)
                        closeMeleeLikeEnemies++;
                }

                if (e.distanceToCaster > 24f)
                    continue;

                if (!caster.CanReach(p, PathEndMode.Touch, Danger.Deadly))
                    continue;

                float score = 0f;

                score += 80f;
                score -= e.distanceToCaster * 2.0f;
                score += (1f - e.hpFraction) * 45f;

                if (e.isStunned)
                    score += 35f;

                if (e.isMindControlled)
                    score += 20f;

                if (e.IsRanged)
                    score += 20f;

                if (e.role == EnemyRole.Sniper)
                    score += 25f;

                if (e.role == EnemyRole.Heavy)
                    score += 20f;

                if (e.role == EnemyRole.Wimp)
                    score += 25f;

                if (e.IsAnimal)
                    score -= 15f;

                if (e.IsMelee)
                    score -= 10f;

                if (e.alliesInClusterRadius >= 3)
                    score -= 25f;

                if (closeMeleeLikeEnemies >= 2)
                    score -= 30f;

                if (score > bestScore)
                {
                    bestScore = score;
                    best = e;
                }
            }

            if (standingEnemies < 2)
                return false;

            if (standingEnemies > 7)
                return false;

            if (best == null || best.pawn == null)
                return false;

            if (bestScore < 45f)
                return false;

            if (closeMeleeLikeEnemies >= 2 && hp < 0.88f)
                return false;

            Job curJob = caster.CurJob;

            if (curJob != null &&
                curJob.def == JobDefOf.AttackMelee &&
                curJob.targetA.HasThing &&
                curJob.targetA.Thing == best.pawn)
            {
                return true;
            }

            Job meleeJob = JobMaker.MakeJob(JobDefOf.AttackMelee, best.pawn);
            meleeJob.locomotionUrgency = LocomotionUrgency.Sprint;
            meleeJob.expiryInterval = 360;
            meleeJob.checkOverrideOnExpire = true;
            meleeJob.maxNumMeleeAttacks = 1;

            caster.jobs.StartJob(
                meleeJob,
                JobCondition.InterruptForced,
                null,
                resumeCurJobAfterwards: false,
                cancelBusyStances: true
            );

            StartKillContract(best.pawn, 600, "WeakPsycasterForcedAssault");

            pendingMeleeTargetThingId = best.pawn.thingIDNumber;
            pendingMeleeUntilTick = now + 420;
            pendingMeleeReason = "WeakPsycasterForcedAssault";

            nextActionSelectTick = now + 120;
            nextStanceReevalTick = now + 120;

            Log.Message("[Signal Interceptor] Psycaster weak-tier forced assault: "
                        + best.pawn.LabelShort
                        + " | d=" + best.distanceToCaster.ToString("F1")
                        + " | hp=" + hp.ToString("F2")
                        + " | psylink=" + psylink
                        + " | enemies=" + standingEnemies
                        + " | ranged=" + rangedEnemies
                        + " | meleeLike=" + meleeLikeEnemies
                        + " | closeMeleeLike=" + closeMeleeLikeEnemies
                        + " | score=" + bestScore.ToString("F1"));

            return true;
        }

        private bool TryCastSelfCombatAbility(string abilityDefName, int softCooldownMin, int softCooldownMax, string reason)
        {
            if (string.IsNullOrEmpty(abilityDefName))
                return false;

            if (IsOnSoftCooldown(abilityDefName))
                return false;

            if (!HasEnoughPsyfocusForAbility(abilityDefName))
            {
                if (Prefs.DevMode)
                {
                    Log.Message("[Signal Interceptor] Psycaster self combat cast blocked by psyfocus: "
                                + abilityDefName
                                + " | psyfocus=" + GetCurrentPsyfocusFraction().ToString("F2")
                                + " | required=" + GetMinimumPsyfocusForAbility(abilityDefName).ToString("F2")
                                + " | reason=" + reason);
                }

                return false;
            }

            AbilityDef def = GetAbilityDef(abilityDefName);

            if (def == null)
                return false;

            object ability = GetPawnAbilityObject(def);

            if (ability == null)
                return false;

            if (IsAbilityOnCooldown(ability))
                return false;

            bool casted = gc.TryCastSelfPsyAbility_Public(caster, abilityDefName);

            if (!casted)
                return false;

            softCooldowns[abilityDefName] = Find.TickManager.TicksGame + Rand.RangeInclusive(softCooldownMin, softCooldownMax);

            pendingMeleeTargetThingId = -1;
            pendingMeleeUntilTick = -1;
            pendingMeleeReason = null;

            return true;
        }

        private bool WasRecentlyDamaged(int ticks = 180)
        {
            HediffComp_PsycasterRestoringMechanisms restore = GetRestoringMechanisms();

            if (restore == null)
                return false;

            return restore.TicksSinceDamage >= 0 && restore.TicksSinceDamage <= ticks;
        }

        private int CountMeaningfulRangedPressure(
    BattlefieldSnapshot snap,
    out float meaningfulIncomingDps,
    out float nearestMeaningfulShooterDist)
        {
            meaningfulIncomingDps = 0f;
            nearestMeaningfulShooterDist = 999f;

            if (snap == null || snap.enemies == null || caster == null || caster.Map == null)
                return 0;

            int count = 0;
            Map map = caster.Map;

            for (int i = 0; i < snap.enemies.Count; i++)
            {
                EnemyAssessment e = snap.enemies[i];

                if (e == null || e.pawn == null)
                    continue;

                Pawn p = e.pawn;

                if (p.Destroyed || p.Dead || p.Downed || !p.Spawned || p.Map != map)
                    continue;

                if (!e.IsRanged)
                    continue;

                if (!e.hasLineOfSight)
                    continue;

                if (!e.canShootNow)
                    continue;

                if (e.estimatedDps <= 0.1f)
                    continue;

                /*
                 * Главное отличие от старого rangedLos:
                 * дальний враг вне своей практической дистанции не должен мгновенно триггерить smoke.
                 */
                float practicalRange = Mathf.Max(18f, e.weaponRange + 2f);

                if (e.distanceToCaster > practicalRange)
                    continue;

                count++;
                meaningfulIncomingDps += e.estimatedDps;

                if (e.distanceToCaster < nearestMeaningfulShooterDist)
                    nearestMeaningfulShooterDist = e.distanceToCaster;
            }

            return count;
        }

        private int CountRangedEnemiesWithLosToCaster(BattlefieldSnapshot snap)
        {
            if (snap == null || snap.enemies == null || caster == null || caster.Map == null)
                return 0;

            int count = 0;
            Map map = caster.Map;

            for (int i = 0; i < snap.enemies.Count; i++)
            {
                EnemyAssessment e = snap.enemies[i];

                if (e == null || e.pawn == null)
                    continue;

                Pawn p = e.pawn;

                if (p.Destroyed || p.Dead || p.Downed || !p.Spawned || p.Map != map)
                    continue;

                if (!e.IsRanged)
                    continue;

                if (!e.hasLineOfSight)
                    continue;

                if (!e.canShootNow)
                    continue;

                count++;
            }

            return count;
        }

        private float GetNearestStandingEnemyDistance(BattlefieldSnapshot snap)
        {
            if (snap == null || snap.enemies == null || caster == null || caster.Map == null)
                return 999f;

            float nearest = 999f;
            Map map = caster.Map;

            for (int i = 0; i < snap.enemies.Count; i++)
            {
                EnemyAssessment e = snap.enemies[i];

                if (e == null || e.pawn == null)
                    continue;

                Pawn p = e.pawn;

                if (p.Destroyed || p.Dead || p.Downed || !p.Spawned || p.Map != map)
                    continue;

                if (e.distanceToCaster < nearest)
                    nearest = e.distanceToCaster;
            }

            return nearest;
        }

        private bool IsCasterBurningNow()
        {
            if (caster == null || !caster.Spawned || caster.Map == null)
                return false;

            if (caster.IsBurning())
                return true;

            List<Thing> things = caster.Position.GetThingList(caster.Map);

            if (things != null)
            {
                for (int i = 0; i < things.Count; i++)
                {
                    Thing t = things[i];

                    if (t == null || t.Destroyed)
                        continue;

                    if (t is Fire)
                        return true;

                    if (t.def != null && t.def.defName == "Fire")
                        return true;
                }
            }

            return false;
        }

        private bool TryExtinguishSelfWithWaterskip()
        {
            if (caster == null || caster.Destroyed || caster.Dead || caster.Downed || !caster.Spawned)
                return false;

            if (caster.Map == null)
                return false;

            if (!IsCasterBurningNow())
                return false;

            if (IsOnSoftCooldown("Waterskip"))
                return false;

            if (!HasEnoughPsyfocusForAbility("Waterskip"))
            {
                if (Prefs.DevMode)
                {
                    Log.Message("[Signal Interceptor] Psycaster Waterskip self-extinguish blocked by psyfocus: "
                                + caster.LabelShort
                                + " | psyfocus=" + GetCurrentPsyfocusFraction().ToString("F2")
                                + " | required=" + GetMinimumPsyfocusForAbility("Waterskip").ToString("F2"));
                }

                return false;
            }

            AbilityDef waterskipDef = GetAbilityDef("Waterskip");

            if (waterskipDef == null)
                return false;

            object ability = GetPawnAbilityObject(waterskipDef);

            if (ability == null)
                return false;

            if (IsAbilityOnCooldown(ability))
                return false;

            bool casted = gc.TryCastPsyAbilityAtCellControlled_Public(
                caster,
                "Waterskip",
                caster.Position,
                PsycasterTuning.StandardPulseRange,
                true);

            if (!casted)
                return false;

            pendingMeleeTargetThingId = -1;
            pendingMeleeUntilTick = -1;
            pendingMeleeReason = null;

            ClearKillContract("Waterskip self extinguish");

            softCooldowns["Waterskip"] = Find.TickManager.TicksGame + Rand.RangeInclusive(300, 480);

            nextActionSelectTick = Find.TickManager.TicksGame + PsycasterTuning.CastWarmupShort + 30;
            nextStanceReevalTick = Find.TickManager.TicksGame + 30;

            Log.Message("[Signal Interceptor] Psycaster emergency Waterskip self-extinguish: "
                        + caster.LabelShort
                        + " | pos=" + caster.Position
                        + " | psyfocus=" + GetCurrentPsyfocusFraction().ToString("F2"));

            return true;
        }

        private bool IsCasterInvisibleNow()
        {
            if (caster == null || caster.health == null || caster.health.hediffSet == null)
                return false;

            HediffDef invisibilityDef = DefDatabase<HediffDef>.GetNamedSilentFail("PsychicInvisibility");

            if (invisibilityDef == null)
                return false;

            return caster.health.hediffSet.HasHediff(invisibilityDef);
        }

    }
}
