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
        private bool TryRunSoftLeashReturn(BattlefieldSnapshot snap)
        {
            if (caster == null || caster.Destroyed || caster.Dead || caster.Downed || !caster.Spawned || caster.Map == null)
                return false;

            if (!HomeAnchor.IsValid)
                return false;

            int now = Find.TickManager.TicksGame;

            if (now < nextSoftLeashReturnTick)
                return false;

            if (!IsCasterFreeToAct())
                return false;

            if (HasActiveKillContract)
                return false;

            if (pendingMeleeTargetThingId >= 0 && pendingMeleeUntilTick > now)
                return false;

            Map map = caster.Map;

            float homeDist = caster.Position.DistanceTo(HomeAnchor);
            bool nearEdge = IsNearMapEdge(caster.Position, map, MapEdgeDangerDistance + 4);

            /*
             * Сначала дешёвый early-out.
             * Если он не далеко от HomeAnchor и не у края карты — leash вообще не нужен.
             * Это убирает лишнюю боевую проверку и лишние dev-логи.
             */
            if (homeDist < SoftHomeSoftRadius && !nearEdge)
                return false;

            bool immediateDanger = HasImmediateLeashDanger(snap);

            /*
             * Если есть непосредственная опасность — leash не имеет права стартовать.
             */
            if (immediateDanger)
                return false;

            /*
             * Подавляем leash в активном бою.
             * Важно: это проверяется только после homeDist/nearEdge,
             * чтобы не логировать "suppressed by combat" каждый тик, когда leash вообще не нужен.
             */
            if (snap != null && snap.HasEnemies)
            {
                float nearestEnemyDist = GetNearestStandingEnemyDistance(snap);
                int visibleRanged = CountRangedEnemiesWithLosToCaster(snap);

                bool combatRelevant =
                    nearestEnemyDist <= 65f ||
                    visibleRanged > 0 ||
                    snap.totalIncomingDps > 0f ||
                    currentStance == PsycasterStance.Opening ||
                    currentStance == PsycasterStance.CrowdControl ||
                    currentStance == PsycasterStance.Hunt ||
                    currentStance == PsycasterStance.Kite ||
                    currentStance == PsycasterStance.Survive;

                /*
                 * В бою leash разрешён только если он реально у края карты
                 * или экстремально далеко от HomeAnchor.
                 */
                if (combatRelevant && !nearEdge && homeDist < SoftHomeHardRadius + 20f)
                {
                    if (Prefs.DevMode && now >= nextSoftLeashSuppressedLogTick)
                    {
                        nextSoftLeashSuppressedLogTick = now + 300;

                        Log.Message("[Signal Interceptor] Psycaster soft leash suppressed by combat: "
                                    + caster.LabelShort
                                    + " | homeDist=" + homeDist.ToString("F1")
                                    + " | nearestEnemy=" + nearestEnemyDist.ToString("F1")
                                    + " | visibleRanged=" + visibleRanged
                                    + " | incomingDps=" + snap.totalIncomingDps.ToString("F1")
                                    + " | stance=" + currentStance);
                    }

                    return false;
                }
            }

            /*
             * Если текущий Goto уже ведёт в нормальную внутреннюю клетку,
             * не прерываем его.
             */
            Job curJob = caster.CurJob;

            if (curJob != null && curJob.def == JobDefOf.Goto && curJob.targetA.IsValid)
            {
                IntVec3 targetCell = curJob.targetA.Cell;

                if (targetCell.IsValid &&
                    targetCell.InBounds(map) &&
                    !IsNearMapEdge(targetCell, map, MapEdgeDangerDistance) &&
                    (!HomeAnchor.IsValid || targetCell.DistanceTo(HomeAnchor) < homeDist))
                {
                    return true;
                }
            }

            IntVec3 returnCell;

            if (!TryFindSoftLeashReturnCell(snap, out returnCell))
                return false;

            Job job = JobMaker.MakeJob(JobDefOf.Goto, returnCell);
            job.locomotionUrgency = LocomotionUrgency.Sprint;
            job.expiryInterval = Rand.RangeInclusive(SoftLeashReturnJobExpiryMin, SoftLeashReturnJobExpiryMax);

            caster.jobs.StartJob(job, JobCondition.InterruptForced);

            nextSoftLeashReturnTick = now + Mathf.Max(SoftLeashReturnCooldownTicks, 420);
            nextActionSelectTick = now + 150;
            nextStanceReevalTick = now + 150;

            Log.Message("[Signal Interceptor] Psycaster soft leash return: "
                        + caster.LabelShort
                        + " -> " + returnCell
                        + " | homeDist=" + homeDist.ToString("F1")
                        + " | nearEdge=" + nearEdge
                        + " | immediateDanger=" + immediateDanger
                        + " | anchor=" + HomeAnchor);

            return true;
        }

        private bool TryFindSoftLeashReturnCell(BattlefieldSnapshot snap, out IntVec3 result)
        {
            result = IntVec3.Invalid;

            if (caster == null || caster.Map == null)
                return false;

            Map map = caster.Map;

            IntVec3 center = HomeAnchor.IsValid ? HomeAnchor : caster.Position;

            IntVec3 best = IntVec3.Invalid;
            float bestScore = float.MinValue;

            /*
             * Ищем не строго HomeAnchor, а хорошую внутреннюю клетку.
             * Это снижает риск stagger-а и не заставляет VIP возвращаться в одну точку.
             */
            for (int i = 0; i < 180; i++)
            {
                IntVec3 cell;

                bool found = CellFinder.TryFindRandomCellNear(
                    center,
                    map,
                    32,
                    c => c.InBounds(map)
                         && c.Standable(map)
                         && c.GetFirstPawn(map) == null
                         && c.DistanceToEdge(map) >= MapEdgeDangerDistance
                         && caster.CanReach(c, PathEndMode.OnCell, Danger.Deadly),
                    out cell);

                if (!found)
                    continue;

                float distFromCaster = cell.DistanceTo(caster.Position);

                if (distFromCaster < 6f)
                    continue;

                float nearestAnyEnemy = 999f;
                float nearestMeleeOrAnimal = 999f;
                float nearestRangedLos = 999f;
                int visibleShooters = 0;

                if (snap != null && snap.enemies != null)
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
                 * Не возвращаем его в пасть милишнику/животному.
                 */
                if (nearestMeleeOrAnimal < 10f)
                    continue;

                if (nearestAnyEnemy < 8f)
                    continue;

                float score = 0f;

                /*
                 * Базовая цель leash-а — двигаться внутрь карты и ближе к HomeAnchor.
                 */
                if (HomeAnchor.IsValid)
                {
                    float homeDist = cell.DistanceTo(HomeAnchor);
                    score -= homeDist * 1.8f;
                }

                score += cell.DistanceToEdge(map) * 1.5f;

                /*
                 * Но всё ещё учитываем угрозы.
                 */
                if (nearestMeleeOrAnimal < 999f)
                    score += nearestMeleeOrAnimal * 2.4f;

                if (nearestAnyEnemy < 999f)
                    score += nearestAnyEnemy * 0.8f;

                score -= visibleShooters * 20f;

                if (nearestRangedLos < 24f)
                    score -= (24f - nearestRangedLos) * 3.5f;

                /*
                 * Мягко штрафуем клетки, которые всё ещё слишком далеко от домашней области.
                 */
                score -= GetSoftHomePenalty(cell, false);

                /*
                 * Жёстко штрафуем edge.
                 */
                score -= GetMapEdgePenalty(cell, map);

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

        private bool TryStopLeavingMapJob()
        {
            if (caster == null || caster.Destroyed || caster.Dead || caster.Downed || !caster.Spawned || caster.Map == null)
                return false;

            Job curJob = caster.CurJob;

            if (curJob == null || curJob.def == null)
                return false;

            string defName = curJob.def.defName;

            bool leaving =
                defName == "ExitMap" ||
                defName == "ExitMapBest" ||
                defName == "ExitMapNearDutyTarget" ||
                defName == "GotoMapEdge" ||
                defName.IndexOf("ExitMap", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                defName.IndexOf("LeaveMap", System.StringComparison.OrdinalIgnoreCase) >= 0;

            if (!leaving)
                return false;

            Map map = caster.Map;

            caster.jobs.EndCurrentJob(JobCondition.InterruptForced, true, true);

            BattlefieldSnapshot snap = SnapshotBuilder.Build(this);
            LastSnapshot = snap;

            IntVec3 safeCell;

            /*
             * Важный фикс:
             * если он попытался выйти с карты, НЕ заставляем его идти строго в HomeAnchor.
             * Иначе будет stagger: edge -> anchor -> threat -> edge -> anchor.
             *
             * Вместо этого ищем безопасную внутреннюю клетку:
             * - не у края;
             * - по возможности ближе к HomeAnchor;
             * - не в пасти у животного/милишника;
             * - не под прямым LOS стрелков.
             */
            if (TryFindSoftLeashReturnCell(snap, out safeCell))
            {
                Job goSafe = JobMaker.MakeJob(JobDefOf.Goto, safeCell);
                goSafe.locomotionUrgency = LocomotionUrgency.Sprint;
                goSafe.expiryInterval = Rand.RangeInclusive(SoftLeashReturnJobExpiryMin, SoftLeashReturnJobExpiryMax);

                caster.jobs.StartJob(goSafe, JobCondition.InterruptForced);

                nextSoftLeashReturnTick = Find.TickManager.TicksGame + SoftLeashReturnCooldownTicks;
                nextActionSelectTick = Find.TickManager.TicksGame + 90;
                nextStanceReevalTick = Find.TickManager.TicksGame + 90;

                Log.Message("[Signal Interceptor] Psycaster tried to leave map; redirected to safe inner cell. "
                            + "Pawn=" + caster.LabelShort
                            + " | OldJob=" + defName
                            + " | Cell=" + safeCell
                            + " | HomeAnchor=" + HomeAnchor);

                return true;
            }

            /*
             * Fallback:
             * если нормальную клетку найти не удалось, идём в HomeAnchor,
             * но только если он валиден и не находится у края карты.
             */
            if (HomeAnchor.IsValid && !IsNearMapEdge(HomeAnchor, map, MapEdgeDangerDistance))
            {
                Job goHome = JobMaker.MakeJob(JobDefOf.Goto, HomeAnchor);
                goHome.locomotionUrgency = LocomotionUrgency.Sprint;
                goHome.expiryInterval = Rand.RangeInclusive(SoftLeashReturnJobExpiryMin, SoftLeashReturnJobExpiryMax);

                caster.jobs.StartJob(goHome, JobCondition.InterruptForced);

                nextSoftLeashReturnTick = Find.TickManager.TicksGame + SoftLeashReturnCooldownTicks;
                nextActionSelectTick = Find.TickManager.TicksGame + 90;
                nextStanceReevalTick = Find.TickManager.TicksGame + 90;

                Log.Message("[Signal Interceptor] Psycaster tried to leave map; returning to anchor fallback. "
                            + "Pawn=" + caster.LabelShort
                            + " | OldJob=" + defName
                            + " | Anchor=" + HomeAnchor);

                return true;
            }

            /*
             * Последний fallback:
             * job выхода отменён, но нового job нет.
             * Это всё равно лучше, чем дать VIP уйти с карты.
             */
            nextSoftLeashReturnTick = Find.TickManager.TicksGame + SoftLeashReturnCooldownTicks;
            nextActionSelectTick = Find.TickManager.TicksGame + 60;

            Log.Message("[Signal Interceptor] Psycaster tried to leave map; exit job cancelled without safe fallback. "
                        + "Pawn=" + caster.LabelShort
                        + " | OldJob=" + defName);

            return true;
        }

        private bool HasImmediateLeashDanger(BattlefieldSnapshot snap)
        {
            if (snap == null || snap.enemies == null || snap.enemies.Count == 0)
                return false;

            for (int i = 0; i < snap.enemies.Count; i++)
            {
                EnemyAssessment e = snap.enemies[i];

                if (e == null || e.pawn == null)
                    continue;

                if (e.pawn.Destroyed || e.pawn.Dead || e.pawn.Downed || !e.pawn.Spawned)
                    continue;

                /*
                 * Ближник/животное рядом — нельзя включать leash-return.
                 */
                if ((e.IsAnimal || e.IsMelee || e.role == EnemyRole.Wimp) && e.distanceToCaster <= 12f)
                    return true;

                /*
                 * Стрелок с LOS рядом — тоже нельзя заставлять идти домой.
                 */
                if (e.IsRanged && e.hasLineOfSight)
                {
                    float safeRange = e.weaponRange > 0f ? e.weaponRange + 2f : 24f;

                    if (e.distanceToCaster <= safeRange)
                        return true;
                }
            }

            return false;
        }

        private float GetSoftHomePenalty(IntVec3 cell, bool emergency)
        {
            if (!HomeAnchor.IsValid || !cell.IsValid)
                return 0f;

            float dist = cell.DistanceTo(HomeAnchor);

            if (dist <= SoftHomeFreeRadius)
                return 0f;

            if (dist <= SoftHomeSoftRadius)
            {
                float over = dist - SoftHomeFreeRadius;
                return emergency ? over * 0.35f : over * 0.9f;
            }

            if (dist <= SoftHomeHardRadius)
            {
                float over = dist - SoftHomeSoftRadius;
                return emergency ? 12f + over * 0.9f : 24f + over * 1.8f;
            }

            /*
             * Очень далеко от домашней зоны.
             * В emergency не запрещаем полностью, но делаем сильно нежелательным.
             */
            float hardOver = dist - SoftHomeHardRadius;

            return emergency ? 70f + hardOver * 1.2f : 160f + hardOver * 3.0f;
        }

        private bool IsNearMapEdge(IntVec3 cell, Map map, int edgeDistance)
        {
            if (map == null || !cell.IsValid || !cell.InBounds(map))
                return true;

            return cell.x < edgeDistance
                   || cell.z < edgeDistance
                   || cell.x >= map.Size.x - edgeDistance
                   || cell.z >= map.Size.z - edgeDistance;
        }

        private float GetMapEdgePenalty(IntVec3 cell, Map map)
        {
            if (map == null || !cell.IsValid || !cell.InBounds(map))
                return 10000f;

            int edgeDist = cell.DistanceToEdge(map);

            if (edgeDist < MapEdgeDangerDistance)
                return 10000f;

            /*
             * В пределах 10-18 клеток от края — не абсолютный запрет,
             * но сильный штраф, чтобы AI предпочитал внутренние клетки.
             */
            if (edgeDist < MapEdgeDangerDistance + 8)
                return (MapEdgeDangerDistance + 8 - edgeDist) * 35f;

            return 0f;
        }

    }
}
