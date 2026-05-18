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
        private bool TryEmergencyRetreat(BattlefieldSnapshot snap)
        {
            if (snap == null || snap.caster == null || !snap.HasEnemies)
                return false;

            int now = Find.TickManager.TicksGame;

            if (now < nextEmergencyRetreatTick)
                return false;

            bool singleEnemy = snap.enemies != null && snap.enemies.Count == 1;

            int adjacentCount = snap.enemiesAdjacent != null ? snap.enemiesAdjacent.Count : 0;

            int closeMeleeOrAnimals = 0;
            int veryCloseMeleeOrAnimals = 0;

            if (snap.enemies != null)
            {
                for (int i = 0; i < snap.enemies.Count; i++)
                {
                    EnemyAssessment e = snap.enemies[i];

                    if (e == null || e.pawn == null)
                        continue;

                    if (e.pawn.Destroyed || e.pawn.Dead || e.pawn.Downed || !e.pawn.Spawned)
                        continue;

                    bool meleeLike = e.IsAnimal || e.IsMelee || e.role == EnemyRole.Wimp;

                    if (!meleeLike)
                        continue;

                    if (e.distanceToCaster <= 6f)
                        closeMeleeOrAnimals++;

                    if (e.distanceToCaster <= 2.5f)
                        veryCloseMeleeOrAnimals++;
                }
            }

            bool criticalHp = snap.casterHpFraction <= 0.40f;

            bool lowHpUnderFire =
                snap.casterHpFraction <= 0.55f &&
                snap.IsUnderRangedFire &&
                !singleEnemy;

            bool surrounded =
                adjacentCount >= 3 ||
                (adjacentCount >= 2 && snap.casterHpFraction <= 0.70f);

            /*
             * Новый блок:
             * если кастер уже просел и ближник стоит прямо в контакте,
             * разрешаем emergency Skip даже против одного врага.
             */
            bool meleeEmergency =
                (snap.casterHpFraction <= 0.72f && veryCloseMeleeOrAnimals >= 1) ||
                (snap.casterHpFraction <= 0.62f && closeMeleeOrAnimals >= 1) ||
                (snap.casterHpFraction <= 0.85f && closeMeleeOrAnimals >= 2);

            if (!criticalHp && !lowHpUnderFire && !surrounded && !meleeEmergency)
                return false;

            AbilityDef skipDef = GetAbilityDef("Skip");

            if (skipDef == null)
                return false;

            if (!HasEnoughPsyfocusForAbility("Skip"))
            {
                if (Prefs.DevMode)
                {
                    Log.Message("[Signal Interceptor] Psycaster emergency-retreat Skip blocked by psyfocus: "
                                + caster.LabelShort
                                + " | psyfocus=" + GetCurrentPsyfocusFraction().ToString("F2")
                                + " | required=" + GetMinimumPsyfocusForAbility("Skip").ToString("F2"));
                }

                return false;
            }

            object ability = GetPawnAbilityObject(skipDef);

            if (ability == null)
                return false;

            if (IsAbilityOnCooldown(ability))
                return false;

            IntVec3 retreatCell;

            if (!TryFindEmergencyRetreatCell(snap, out retreatCell))
                return false;

            bool casted = gc.TryCastPsyAbilityToDestination_Public(
                caster,
                "Skip",
                caster,
                retreatCell);

            if (!casted)
                return false;

            pendingMeleeTargetThingId = -1;
            pendingMeleeUntilTick = -1;
            pendingMeleeReason = null;

            ClearKillContract("emergency retreat");

            ApplySoftCooldown("Skip");

            /*
             * Немного короче, чем было.
             * Иначе после одного Skip он слишком долго не может повторить отрыв,
             * а быстрый melee-враг снова догоняет.
             */
            nextEmergencyRetreatTick = Find.TickManager.TicksGame + Rand.RangeInclusive(240, 360);
            nextActionSelectTick = Find.TickManager.TicksGame + PsycasterTuning.CastWarmupShort + 90;

            Log.Message("[Signal Interceptor] Psycaster emergency-retreat Skip self to "
                        + retreatCell
                        + " | HP=" + snap.casterHpFraction.ToString("F2")
                        + " | enemies=" + snap.enemies.Count
                        + " | adjacent=" + adjacentCount
                        + " | closeMelee=" + closeMeleeOrAnimals
                        + " | veryCloseMelee=" + veryCloseMeleeOrAnimals);

            return true;
        }

        private bool TryFindEmergencyRetreatCell(BattlefieldSnapshot snap, out IntVec3 result)
        {
            result = IntVec3.Invalid;

            if (snap == null || caster == null || caster.Map == null || snap.topThreat == null || snap.topThreat.pawn == null)
                return false;

            Map map = caster.Map;
            Pawn threat = snap.topThreat.pawn;

            IntVec3 best = IntVec3.Invalid;
            float bestScore = float.MinValue;

            float currentThreatDist = caster.Position.DistanceTo(threat.Position);

            for (int i = 0; i < 220; i++)
            {
                IntVec3 cell;

                if (!CellFinder.TryFindRandomCellNear(
                    caster.Position,
                    map,
                    30,
                    c => c.InBounds(map)
                         && c.Standable(map)
                         && c.GetFirstPawn(map) == null
                         && c.DistanceToEdge(map) >= MapEdgeDangerDistance
                         && caster.CanReach(c, PathEndMode.OnCell, Danger.Deadly),
                    out cell))
                {
                    continue;
                }

                float distFromCaster = cell.DistanceTo(caster.Position);
                float distFromThreat = cell.DistanceTo(threat.Position);

                if (distFromCaster < 8f)
                    continue;

                if (distFromThreat < 14f)
                    continue;

                /*
                 * Не выбираем клетку у края карты.
                 * Это главный фикс против "убежал с сайта".
                 */
                if (IsNearMapEdge(cell, map, MapEdgeDangerDistance))
                    continue;

                float nearestMeleeOrAnimal = 999f;
                float nearestAnyEnemy = 999f;
                float nearestRangedLos = 999f;
                int visibleShooters = 0;

                if (snap.enemies != null)
                {
                    for (int eIndex = 0; eIndex < snap.enemies.Count; eIndex++)
                    {
                        EnemyAssessment e = snap.enemies[eIndex];

                        if (e == null || e.pawn == null)
                            continue;

                        if (e.pawn.Destroyed || e.pawn.Dead || e.pawn.Downed || !e.pawn.Spawned || e.pawn.Map != map)
                            continue;

                        float ed = cell.DistanceTo(e.pawn.Position);

                        if (ed < nearestAnyEnemy)
                            nearestAnyEnemy = ed;

                        if (e.IsAnimal || e.IsMelee || e.role == EnemyRole.Wimp)
                        {
                            if (ed < nearestMeleeOrAnimal)
                                nearestMeleeOrAnimal = ed;
                        }

                        if (e.IsRanged && GenSight.LineOfSight(e.pawn.Position, cell, map))
                        {
                            visibleShooters++;

                            if (ed < nearestRangedLos)
                                nearestRangedLos = ed;
                        }
                    }
                }

                /*
                 * Emergency retreat всё равно не должен прыгать в пасть зверю.
                 */
                if (nearestMeleeOrAnimal < 14f)
                    continue;

                if (nearestAnyEnemy < 10f)
                    continue;

                float score = 0f;

                /*
                 * Основная цель emergency — выжить.
                 */
                score += distFromThreat * 2.2f;

                if (nearestMeleeOrAnimal < 999f)
                    score += nearestMeleeOrAnimal * 3.0f;

                if (nearestAnyEnemy < 999f)
                    score += nearestAnyEnemy * 1.2f;

                score += distFromCaster * 0.25f;

                /*
                 * Не хотим вставать под стрелков.
                 */
                score -= visibleShooters * 24f;

                if (nearestRangedLos < 24f)
                    score -= (24f - nearestRangedLos) * 4.0f;

                /*
                 * В emergency мягкий home-штраф слабее,
                 * но он всё равно не даёт AI постепенно уводить VIP к краю карты.
                 */
                score -= GetSoftHomePenalty(cell, true);

                /*
                 * Edge penalty почти абсолютный.
                 */
                score -= GetMapEdgePenalty(cell, map);

                /*
                 * Не выбираем клетку, которая приближает к topThreat.
                 * Для животного/милишника это особенно важно.
                 */
                if (distFromThreat < currentThreatDist)
                    score -= 60f;

                /*
                 * Если клетка ближе к HomeAnchor, это небольшой плюс.
                 * Не жёсткий возврат, а мягкое предпочтение.
                 */
                if (HomeAnchor.IsValid)
                {
                    float currentHomeDist = caster.Position.DistanceTo(HomeAnchor);
                    float newHomeDist = cell.DistanceTo(HomeAnchor);

                    if (newHomeDist < currentHomeDist)
                        score += 12f;
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    best = cell;
                }
            }

            if (!best.IsValid)
                return false;

            result = best;
            return true;
        }

        private bool TryExecuteDownedPlayerPawn()
        {
            if (caster == null || caster.Destroyed || caster.Dead || caster.Downed || !caster.Spawned)
                return false;

            Map map = caster.Map;

            if (map == null)
                return false;

            int now = Find.TickManager.TicksGame;

            /*
             * Если уже выполняется AttackMelee по упавшей пешке игрока —
             * не перезапускаем job.
             */
            Job curJob = caster.CurJob;

            if (curJob != null &&
                curJob.def == JobDefOf.AttackMelee &&
                curJob.targetA.HasThing)
            {
                Pawn currentTarget = curJob.targetA.Thing as Pawn;

                if (currentTarget != null &&
                    currentTarget.Spawned &&
                    currentTarget.Map == map &&
                    currentTarget.Faction == Faction.OfPlayer &&
                    currentTarget.Downed &&
                    !currentTarget.Dead &&
                    !currentTarget.Destroyed)
                {
                    return true;
                }
            }

            if (now < nextDownedExecutionScanTick)
                return false;

            /*
             * Было 30 — слишком часто.
             */
            nextDownedExecutionScanTick = now + 180;

            if (!IsCasterFreeToAct())
                return false;

            Pawn target = null;
            float bestScore = float.MinValue;

            IReadOnlyList<Pawn> pawns = map.mapPawns.AllPawnsSpawned;

            for (int i = 0; i < pawns.Count; i++)
            {
                Pawn pawn = pawns[i];

                if (pawn == null || pawn.Destroyed || pawn.Dead || !pawn.Spawned)
                    continue;

                if (pawn.Faction != Faction.OfPlayer)
                    continue;

                if (!pawn.Downed)
                    continue;

                if (!pawn.Position.InBounds(map))
                    continue;

                float distance = caster.Position.DistanceTo(pawn.Position);

                if (distance > 28f)
                    continue;

                if (!caster.CanReach(pawn, PathEndMode.Touch, Danger.Deadly))
                    continue;

                float score = 100f - distance;

                if (pawn.health != null && pawn.health.summaryHealth != null)
                    score += (1f - pawn.health.summaryHealth.SummaryHealthPercent) * 25f;

                if (score > bestScore)
                {
                    bestScore = score;
                    target = pawn;
                }
            }

            if (target == null)
                return false;

            float d = caster.Position.DistanceTo(target.Position);

            Log.Message("[Signal Interceptor] Psycaster executing downed pawn: " +
                        target.LabelShort +
                        " | d=" + d.ToString("F1"));

            Job job = JobMaker.MakeJob(JobDefOf.AttackMelee, target);
            job.locomotionUrgency = LocomotionUrgency.Sprint;
            job.expiryInterval = 300;
            job.checkOverrideOnExpire = true;
            job.killIncappedTarget = true;
            job.maxNumMeleeAttacks = 1;

            caster.jobs.StartJob(
                job,
                JobCondition.InterruptForced,
                null,
                resumeCurJobAfterwards: false,
                cancelBusyStances: true
            );

            /*
             * Было 60 — теперь не спамим.
             */
            nextDownedExecutionScanTick = now + 180;

            return true;
        }

        private bool HasOnlyMeleeOrAnimalEnemies(BattlefieldSnapshot snap)
        {
            if (snap == null || snap.enemies == null || snap.enemies.Count == 0)
                return false;

            bool hasStandingEnemy = false;

            for (int i = 0; i < snap.enemies.Count; i++)
            {
                EnemyAssessment e = snap.enemies[i];

                if (e == null || e.pawn == null)
                    continue;

                if (e.pawn.Destroyed || e.pawn.Dead || e.pawn.Downed || !e.pawn.Spawned)
                    continue;

                hasStandingEnemy = true;

                if (e.IsRanged)
                    return false;
            }

            return hasStandingEnemy;
        }

        private bool HasDangerousBleeding()
        {
            HediffComp_PsycasterRestoringMechanisms restore = GetRestoringMechanisms();

            if (restore != null)
                return restore.HasDangerousBleeding();

            if (caster == null || caster.health == null || caster.health.hediffSet == null)
                return false;

            return caster.health.hediffSet.hediffs
                .OfType<Hediff_Injury>()
                .Any(h => h != null && h.Severity > 0f && h.BleedRate > 0.01f);
        }

    }
}
