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
        private bool TryRunPendingMelee(BattlefieldSnapshot snap)
        {
            if (pendingMeleeTargetThingId < 0)
                return false;

            int now = Find.TickManager.TicksGame;

            if (pendingMeleeUntilTick > 0 && now > pendingMeleeUntilTick)
            {
                pendingMeleeTargetThingId = -1;
                pendingMeleeUntilTick = -1;
                pendingMeleeReason = null;
                pendingMeleeLastLogTargetThingId = -1;
                pendingMeleeLastDistanceZone = null;
                return false;
            }

            if (!IsCasterFreeToAct())
                return true;

            if (snap == null || snap.enemies == null || snap.enemies.Count == 0)
                return true;

            Pawn target = null;
            EnemyAssessment targetAssessment = null;

            for (int i = 0; i < snap.enemies.Count; i++)
            {
                EnemyAssessment e = snap.enemies[i];

                if (e == null || e.pawn == null)
                    continue;

                if (e.pawn.thingIDNumber == pendingMeleeTargetThingId)
                {
                    target = e.pawn;
                    targetAssessment = e;
                    break;
                }
            }

            if (target == null ||
                target.Destroyed ||
                target.Dead ||
                target.Downed ||
                !target.Spawned ||
                target.Map != caster.Map)
            {
                pendingMeleeTargetThingId = -1;
                pendingMeleeUntilTick = -1;
                pendingMeleeReason = null;
                pendingMeleeLastLogTargetThingId = -1;
                pendingMeleeLastDistanceZone = null;
                return false;
            }

            float d = targetAssessment != null
                ? targetAssessment.distanceToCaster
                : caster.Position.DistanceTo(target.Position);

            bool targetControlled = false;

            if (targetAssessment != null)
                targetControlled = targetAssessment.isStunned || targetAssessment.isMindControlled || WasPawnRecentlyMoved(target);
            else
                targetControlled = WasPawnRecentlyMoved(target);

            bool singleEnemy = snap.enemies.Count == 1;
            bool fallback1v1 = pendingMeleeReason == "Fallback1v1";
            bool targetIsRanged = targetAssessment != null && targetAssessment.IsRanged;

            if (fallback1v1 &&
                singleEnemy &&
                targetIsRanged &&
                !targetControlled &&
                d > 8f)
            {
                pendingMeleeTargetThingId = -1;
                pendingMeleeUntilTick = -1;
                pendingMeleeReason = null;
                pendingMeleeLastLogTargetThingId = -1;
                pendingMeleeLastDistanceZone = null;

                nextActionSelectTick = now;

                Log.Message("[Signal Interceptor] Psycaster pending-melee released for ranged duel tools: "
                            + target.LabelShort
                            + " | d=" + d.ToString("F1"));

                return false;
            }

            if (IsAntiKiteEscapeTarget(target) && d > 6f)
            {
                nextActionSelectTick = now;

                Log.Message("[Signal Interceptor] Psycaster pending-melee yielded to anti-kite: "
                            + target.LabelShort
                            + " | reason=" + (pendingMeleeReason ?? "unknown")
                            + " | d=" + d.ToString("F1"));

                return false;
            }

            gc.InterruptBadPsycasterCombatJob_Public(caster, target);

            bool started = gc.TryForcePsycasterMeleeAttack_Public(caster, target);

            if (started)
            {
                nextActionSelectTick = now + 30;

                string distanceZone;

                if (d <= 1.8f)
                    distanceZone = "contact";
                else if (d <= 4f)
                    distanceZone = "close";
                else if (d <= 8f)
                    distanceZone = "medium";
                else if (d <= 14f)
                    distanceZone = "far";
                else
                    distanceZone = "veryFar";

                bool shouldLog =
                    pendingMeleeLastLogTargetThingId != target.thingIDNumber ||
                    pendingMeleeLastDistanceZone != distanceZone;

                if (shouldLog)
                {
                    pendingMeleeLastLogTargetThingId = target.thingIDNumber;
                    pendingMeleeLastDistanceZone = distanceZone;

                    Log.Message("[Signal Interceptor] Psycaster pending-melee: "
                                + target.LabelShort
                                + " | reason=" + (pendingMeleeReason ?? "unknown")
                                + " | zone=" + distanceZone
                                + " | d=" + d.ToString("F1"));
                }

                if (d <= 1.8f)
                {
                    pendingMeleeTargetThingId = -1;
                    pendingMeleeUntilTick = -1;
                    pendingMeleeReason = null;
                    pendingMeleeLastLogTargetThingId = -1;
                    pendingMeleeLastDistanceZone = null;
                }

                return true;
            }

            return true;
        }

        private void QueuePendingMelee(Pawn target, int durationTicks, string reason)
        {
            if (target == null)
                return;

            pendingMeleeTargetThingId = target.thingIDNumber;
            pendingMeleeUntilTick = Find.TickManager.TicksGame + Mathf.Max(60, durationTicks);
            pendingMeleeReason = reason;
            pendingMeleeLastLogTargetThingId = -1;
            pendingMeleeLastDistanceZone = null;

            StartKillContract(target, Mathf.Max(durationTicks, 420), reason);
        }

    }
}
