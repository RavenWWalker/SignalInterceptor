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
        private void ActionSelectAndExecute(BattlefieldSnapshot snap)
        {
            ScoredAction best = ScoredAction.None;

            for (int i = 0; i < scorers.Count; i++)
            {
                IAbilityScorer scorer = scorers[i];

                if (scorer == null)
                    continue;

                if (!scorer.IsAvailable(this, snap))
                    continue;

                ScoredAction candidate = scorer.Score(this, snap);

                if (candidate == null || !candidate.IsValid)
                    continue;

                /*
                 * Дополнительный слой проверки psyfocus.
                 *
                 * AbilityScorerBase уже проверяет MinPsyfocusFraction,
                 * но там у разных scorers могут быть мягкие пороги.
                 * Здесь проверяем единый brain-level порог:
                 * Skip=0.20, ManhunterPulse=0.30, BerserkPulse=0.28 и т.д.
                 */
                if (candidate.abilityDefName != "MeleeAttack_Pseudo" &&
                    !HasEnoughPsyfocusForAbility(candidate.abilityDefName))
                {
                    if (Prefs.DevMode)
                    {
                        Log.Message("[Signal Interceptor] Psycaster candidate skipped by psyfocus: "
                                    + candidate.abilityDefName
                                    + " | psyfocus=" + GetCurrentPsyfocusFraction().ToString("F2")
                                    + " | required=" + GetMinimumPsyfocusForAbility(candidate.abilityDefName).ToString("F2")
                                    + " | score=" + candidate.score.ToString("F2")
                                    + " | reason=" + (candidate.debugReason ?? ""));
                    }

                    continue;
                }

                if (candidate.score > best.score)
                    best = candidate;
            }

            if (best.IsValid)
            {
                ExecuteAction(best, snap);
                LastChosenAction = best;
                return;
            }

            FallbackBasicAttack(snap);
        }

        private void ExecuteAction(ScoredAction action, BattlefieldSnapshot snap)
        {
            if (action == null || !action.IsValid)
                return;

            if (action.abilityDefName != "MeleeAttack_Pseudo" &&
                !HasEnoughPsyfocusForAbility(action.abilityDefName))
            {
                if (Prefs.DevMode)
                {
                    Log.Message("[Signal Interceptor] Psycaster action blocked by psyfocus: "
                                + action.abilityDefName
                                + " | psyfocus=" + GetCurrentPsyfocusFraction().ToString("F2")
                                + " | required=" + GetMinimumPsyfocusForAbility(action.abilityDefName).ToString("F2")
                                + " | score=" + action.score.ToString("F2")
                                + " | reason=" + (action.debugReason ?? ""));
                }

                return;
            }

            bool casted = false;

            if (action.abilityDefName == "MeleeAttack_Pseudo" && action.targetPawn != null)
            {
                gc.InterruptBadPsycasterCombatJob_Public(caster, action.targetPawn);
                casted = gc.TryForcePsycasterMeleeAttack_Public(caster, action.targetPawn);

                if (casted)
                {
                    float d = caster.Position.DistanceTo(action.targetPawn.Position);
                    StartKillContract(action.targetPawn, 420, "MeleeAttack");

                    if (d <= 3.5f || WasPawnRecentlyMoved(action.targetPawn))
                        nextActionSelectTick = Find.TickManager.TicksGame + 30;
                    else
                        nextActionSelectTick = Find.TickManager.TicksGame + 60;

                    Log.Message("[Signal Interceptor] Psycaster melee: " + action.targetPawn.LabelShort
                                + " | score=" + action.score.ToString("F2")
                                + " | stance=" + currentStance
                                + " | reason=" + (action.debugReason ?? ""));
                }

                LastChosenAction = action;
                return;
            }

            switch (action.targetType)
            {
                case ScoredActionTargetType.Self:
                    casted = gc.TryCastSelfPsyAbility_Public(caster, action.abilityDefName);
                    break;

                case ScoredActionTargetType.Pawn:
                    if (action.targetPawn != null)
                    {
                        casted = gc.TryCastPsyAbilityControlled_Public(
                            caster,
                            action.abilityDefName,
                            action.targetPawn,
                            PsycasterTuning.StandardPulseRange,
                            true,
                            false);
                    }
                    break;

                case ScoredActionTargetType.Cell:
                    if (action.targetCell.IsValid)
                    {
                        casted = gc.TryCastPsyAbilityAtCellControlled_Public(
                            caster,
                            action.abilityDefName,
                            action.targetCell,
                            PsycasterTuning.StandardPulseRange,
                            true);
                    }
                    break;

                case ScoredActionTargetType.PawnToDestination:
                    if (action.targetPawn != null && action.destinationCell.IsValid)
                    {
                        casted = gc.TryCastPsyAbilityToDestination_Public(
                            caster,
                            action.abilityDefName,
                            action.targetPawn,
                            action.destinationCell);
                    }
                    break;
            }

            if (!casted)
                return;

            string abilityName = action.abilityDefName;

            /*
             * ManhunterPulse — это "создать хаос и оторваться".
             * После него нельзя продолжать старый melee-contract.
             */
            if (abilityName == "ManhunterPulse")
            {
                pendingMeleeTargetThingId = -1;
                pendingMeleeUntilTick = -1;
                pendingMeleeReason = null;

                ClearKillContract("ManhunterPulse disengage");

                recoveryMode = true;
                recoveryStartedTick = Find.TickManager.TicksGame;
                nextRecoveryThinkTick = Find.TickManager.TicksGame;

                currentStance = PsycasterStance.Survive;

                nextEmergencyRetreatTick = Find.TickManager.TicksGame;
                nextActionSelectTick = Find.TickManager.TicksGame + PsycasterTuning.CastWarmupLong + 30;
                nextStanceReevalTick = Find.TickManager.TicksGame + 180;

                ApplySoftCooldown(abilityName);

                Log.Message("[Signal Interceptor] Psycaster action: " + abilityName
                            + " | score=" + action.score.ToString("F2")
                            + " | stance=" + currentStance
                            + " | reason=" + (action.debugReason ?? "")
                            + " | postAction=disengage/recovery");

                LastChosenAction = action;
                return;
            }

            /*
             * ChaosSkip — это panic escape / disruption.
             *
             * ВАЖНО:
             * Не ставим QueuePendingMelee после ChaosSkip.
             * ChaosSkip часто отбрасывает цель далеко. Если после него ставить pending-melee,
             * кастер начинает бежать за медведем через полкарты и ловит лишний урон.
             */
            if (abilityName == "ChaosSkip")
            {
                if (action.targetPawn != null)
                    MarkPawnRecentlyMoved(action.targetPawn, 600);

                pendingMeleeTargetThingId = -1;
                pendingMeleeUntilTick = -1;
                pendingMeleeReason = null;

                ClearKillContract("ChaosSkip disengage");

                ApplySoftCooldown(abilityName);

                int warmup = action.castWarmupTicks > 0
                    ? action.castWarmupTicks
                    : PsycasterTuning.CastWarmupShort;

                nextActionSelectTick = Find.TickManager.TicksGame + warmup + 60;
                nextStanceReevalTick = Find.TickManager.TicksGame + 60;

                Log.Message("[Signal Interceptor] Psycaster action: " + abilityName
                            + " | score=" + action.score.ToString("F2")
                            + " | stance=" + currentStance
                            + " | reason=" + (action.debugReason ?? "")
                            + " | postAction=no-pending-melee");

                LastChosenAction = action;
                return;
            }

            if (action.targetPawn != null)
            {
                float d = caster.Position.DistanceTo(action.targetPawn.Position);

                /*
                 * Beckon и Skip — это контролируемое перемещение цели под melee-добивание.
                 * Для них pending-melee нужен.
                 *
                 * ChaosSkip отсюда убран специально.
                 */
                if (abilityName == "Beckon" || abilityName == "Skip")
                {
                    MarkPawnRecentlyMoved(action.targetPawn, 600);
                    QueuePendingMelee(action.targetPawn, 480, abilityName);
                    StartKillContract(action.targetPawn, 900, abilityName);
                }

                if (abilityName == "Stun")
                {
                    MarkPawnRecentlyMoved(action.targetPawn, 180);

                    // Stun считается melee-pin.
                    QueuePendingMelee(action.targetPawn, 240, "Stun");
                    StartKillContract(action.targetPawn, d <= 3.5f ? 600 : 300, "Stun");
                }
            }

            ApplySoftCooldown(abilityName);

            int finalWarmup = action.castWarmupTicks > 0
                ? action.castWarmupTicks
                : PsycasterTuning.CastWarmupMedium;

            int extraDelay = 30;

            if (abilityName == "Stun")
            {
                extraDelay = 0;
            }
            else if ((abilityName == "Beckon" || abilityName == "Skip") &&
                     action.targetPawn != null)
            {
                extraDelay = 5;
            }

            nextActionSelectTick = Find.TickManager.TicksGame + finalWarmup + extraDelay;

            Log.Message("[Signal Interceptor] Psycaster action: " + abilityName
                        + " | score=" + action.score.ToString("F2")
                        + " | stance=" + currentStance
                        + " | reason=" + (action.debugReason ?? ""));

            LastChosenAction = action;
        }

        private void FallbackBasicAttack(BattlefieldSnapshot snap)
        {
            EnemyAssessment top = snap.topThreat;

            if (top == null || top.pawn == null)
            {
                gc.TryForcePsycasterAttackNearestPlayerPawn_Public(caster, caster.Map);
                return;
            }

            bool singleEnemy = snap.enemies != null && snap.enemies.Count == 1;
            bool targetRecentlyControlled = WasPawnRecentlyMoved(top.pawn);
            bool targetControlled = top.isStunned || top.isMindControlled || targetRecentlyControlled;

            // 1v1 против дальника:
            // НЕ включаем pending-melee с 20-30 клеток.
            // Это должно дать шанс action selection выбрать Skip/Beckon/Blind/Vertigo.
            if (singleEnemy &&
                top.IsRanged &&
                currentStance != PsycasterStance.Survive &&
                top.distanceToCaster > 8f &&
                !targetControlled)
            {
                IntVec3 approachCell = ComputeApproachCell(caster.Position, top.pawn.Position, 12f);

                if (approachCell.IsValid &&
                    approachCell.InBounds(caster.Map) &&
                    approachCell.Standable(caster.Map))
                {
                    Job job = JobMaker.MakeJob(JobDefOf.Goto, approachCell);
                    job.locomotionUrgency = LocomotionUrgency.Jog;
                    caster.jobs.StartJob(job, JobCondition.InterruptForced);

                    nextActionSelectTick = Find.TickManager.TicksGame + 45;

                    Log.Message("[Signal Interceptor] Psycaster duel-approach, preserving ranged tools: "
                                + top.pawn.LabelShort
                                + " | d=" + top.distanceToCaster.ToString("F1")
                                + " | role=" + top.role
                                + " | stance=" + currentStance);
                }

                return;
            }

            // Если цель уже близко или контролится — добиваем.
            if (singleEnemy && currentStance != PsycasterStance.Survive && top.distanceToCaster <= 30f)
            {
                QueuePendingMelee(top.pawn, 240, "Fallback1v1");

                gc.InterruptBadPsycasterCombatJob_Public(caster, top.pawn);
                gc.TryForcePsycasterMeleeAttack_Public(caster, top.pawn);

                Log.Message("[Signal Interceptor] Psycaster melee-fallback 1v1: "
                            + top.pawn.LabelShort
                            + " | d=" + top.distanceToCaster.ToString("F1")
                            + " | role=" + top.role
                            + " | controlled=" + targetControlled
                            + " | stance=" + currentStance);

                return;
            }

            IntVec3 anchor = (snap.largestClusterSize > 0 && snap.largestClusterCenter.IsValid)
                ? snap.largestClusterCenter
                : top.pawn.Position;

            float curDist = caster.Position.DistanceTo(anchor);
            float ideal = PsycasterTuning.KiteIdealDistance;
            float minD = PsycasterTuning.KiteMinDistance;

            if (currentStance == PsycasterStance.Hunt && top.distanceToCaster <= 8f)
            {
                QueuePendingMelee(top.pawn, 240, "HuntFallback");

                gc.InterruptBadPsycasterCombatJob_Public(caster, top.pawn);
                gc.TryForcePsycasterMeleeAttack_Public(caster, top.pawn);
                return;
            }

            if (curDist >= minD && curDist <= ideal + 2f)
            {
                gc.InterruptBadPsycasterCombatJob_Public(caster, top.pawn);

                Log.Message("[Signal Interceptor] Psycaster idle-hold dist=" + curDist.ToString("F1")
                            + " window=" + minD + "-" + ideal
                            + " stance=" + currentStance);

                return;
            }

            if (curDist > ideal + 2f)
            {
                IntVec3 approachCell = ComputeApproachCell(caster.Position, anchor, ideal);

                if (approachCell.IsValid &&
                    approachCell.InBounds(caster.Map) &&
                    approachCell.Standable(caster.Map))
                {
                    Job job = JobMaker.MakeJob(JobDefOf.Goto, approachCell);
                    job.locomotionUrgency = LocomotionUrgency.Jog;
                    caster.jobs.StartJob(job, JobCondition.InterruptForced);

                    Log.Message("[Signal Interceptor] Psycaster idle-approach " + approachCell
                                + " dist " + curDist.ToString("F1") + "->" + ideal.ToString("F0"));
                }

                return;
            }

            if (curDist < minD)
            {
                IntVec3 retreatCell = ComputeRetreatCell(caster.Position, anchor, 4);

                if (retreatCell.IsValid &&
                    retreatCell.InBounds(caster.Map) &&
                    retreatCell.Standable(caster.Map))
                {
                    Job job = JobMaker.MakeJob(JobDefOf.Goto, retreatCell);
                    job.locomotionUrgency = LocomotionUrgency.Sprint;
                    caster.jobs.StartJob(job, JobCondition.InterruptForced);

                    Log.Message("[Signal Interceptor] Psycaster idle-retreat " + retreatCell);
                }
            }
        }

        private IntVec3 ComputeApproachCell(IntVec3 from, IntVec3 to, float idealDist)
        {
            int dx = to.x - from.x;
            int dz = to.z - from.z;
            float len = Mathf.Sqrt(dx * dx + dz * dz);
            if (len <= 0.01f) return from;

            float move = len - idealDist;
            if (move <= 0f) return from;

            float nx = dx / len;
            float nz = dz / len;
            int cx = from.x + Mathf.RoundToInt(nx * move);
            int cz = from.z + Mathf.RoundToInt(nz * move);
            return new IntVec3(cx, from.y, cz);
        }

        private IntVec3 ComputeRetreatCell(IntVec3 from, IntVec3 anchor, int steps)
        {
            int dx = from.x - anchor.x;
            int dz = from.z - anchor.z;
            float len = Mathf.Sqrt(dx * dx + dz * dz);
            if (len <= 0.01f)
            {
                // Стоим прямо на якоре — отойдём на восток.
                return new IntVec3(from.x + steps, from.y, from.z);
            }
            float nx = dx / len;
            float nz = dz / len;
            int cx = from.x + Mathf.RoundToInt(nx * steps);
            int cz = from.z + Mathf.RoundToInt(nz * steps);
            return new IntVec3(cx, from.y, cz);
        }

    }
}
