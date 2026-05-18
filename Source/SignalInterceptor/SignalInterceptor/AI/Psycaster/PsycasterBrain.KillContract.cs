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
        public bool HasActiveKillContract
        {
            get
            {
                return killContractTargetThingId >= 0 &&
                       killContractUntilTick > Find.TickManager.TicksGame;
            }
        }

        public bool IsKillContractTarget(Pawn p)
        {
            if (p == null)
                return false;

            if (!HasActiveKillContract)
                return false;

            return p.thingIDNumber == killContractTargetThingId;
        }

        public bool IsAntiKiteEscapeTarget(Pawn p)
        {
            if (p == null)
                return false;

            if (!IsKillContractTarget(p))
                return false;

            return killContractEscapeUntilTick > Find.TickManager.TicksGame;
        }

        public bool ShouldSuppressDistantStun(Pawn p, float distance, bool singleEnemy)
        {
            if (p == null)
                return false;

            // Главный фикс:
            // если цель в kill-contract, Stun разрешён только как close pin.
            // Иначе jump-pack цель провоцирует цикл:
            // escaped -> far Stun -> chase -> stun expired -> escaped.
            if (IsKillContractTarget(p) && distance > 3.5f)
                return true;

            // В дуэли против дальника Stun дальше 7 клеток почти всегда плохой:
            // кастер не успевает реализовать стан в melee.
            if (singleEnemy && distance > 7f)
                return true;

            return false;
        }

        private void StartKillContract(Pawn target, int durationTicks, string reason)
        {
            if (target == null)
                return;

            int now = Find.TickManager.TicksGame;
            int newUntilTick = now + Mathf.Max(180, durationTicks);
            float distance = caster.Position.DistanceTo(target.Position);

            if (killContractTargetThingId == target.thingIDNumber &&
                killContractUntilTick > now)
            {
                bool reasonChanged = killContractReason != reason;
                bool extended = newUntilTick > killContractUntilTick;

                if (extended)
                    killContractUntilTick = newUntilTick;

                if (reasonChanged)
                    killContractReason = reason;

                killContractLastDistance = distance;

                if (distance <= 3.5f)
                    killContractLastCloseTick = now;

                if (Prefs.DevMode && (reasonChanged || extended))
                {
                    Log.Message("[Signal Interceptor] Psycaster kill-contract refresh: "
                                + target.LabelShort
                                + " | reason=" + (reason ?? "unknown")
                                + " | d=" + distance.ToString("F1")
                                + " | until=" + killContractUntilTick
                                + " | extended=" + extended
                                + " | reasonChanged=" + reasonChanged);
                }

                return;
            }

            killContractTargetThingId = target.thingIDNumber;
            killContractUntilTick = newUntilTick;
            killContractReason = reason;
            killContractLastDistance = distance;
            killContractEscapeUntilTick = -1;

            if (distance <= 3.5f)
                killContractLastCloseTick = now;
            else
                killContractLastCloseTick = -1;

            Log.Message("[Signal Interceptor] Psycaster kill-contract start: "
                        + target.LabelShort
                        + " | reason=" + (reason ?? "unknown")
                        + " | d=" + distance.ToString("F1")
                        + " | until=" + killContractUntilTick);
        }

        private void ClearKillContract(string reason)
        {
            if (killContractTargetThingId >= 0)
            {
                Log.Message("[Signal Interceptor] Psycaster kill-contract clear"
                            + " | reason=" + (reason ?? "unknown")
                            + " | oldTargetId=" + killContractTargetThingId);
            }

            killContractTargetThingId = -1;
            killContractUntilTick = -1;
            killContractReason = null;
            killContractLastDistance = 999f;
            killContractLastCloseTick = -1;
            killContractEscapeUntilTick = -1;
        }

        private EnemyAssessment FindKillContractAssessment(BattlefieldSnapshot snap)
        {
            if (snap == null || snap.enemies == null)
                return null;

            if (!HasActiveKillContract)
                return null;

            for (int i = 0; i < snap.enemies.Count; i++)
            {
                EnemyAssessment e = snap.enemies[i];

                if (e == null || e.pawn == null)
                    continue;

                if (e.pawn.thingIDNumber == killContractTargetThingId)
                    return e;
            }

            return null;
        }

        private void UpdateKillContractTelemetry(BattlefieldSnapshot snap)
        {
            int now = Find.TickManager.TicksGame;

            if (!HasActiveKillContract)
                return;

            EnemyAssessment e = FindKillContractAssessment(snap);

            if (e == null ||
                e.pawn == null ||
                e.pawn.Destroyed ||
                e.pawn.Dead ||
                e.pawn.Downed ||
                !e.pawn.Spawned ||
                e.pawn.Map != caster.Map)
            {
                ClearKillContract("target invalid/downed/dead");
                return;
            }

            float d = e.distanceToCaster;

            if (d <= 3.5f)
                killContractLastCloseTick = now;

            bool wasCloseRecently = killContractLastCloseTick > 0 &&
                                    now - killContractLastCloseTick <= 240;

            bool suddenDistanceBreak = killContractLastDistance < 5f && d >= 10f;

            bool escapedFromMelee = wasCloseRecently && d >= 10f;

            bool alreadyInEscapeWindow = killContractEscapeUntilTick > now;

            if ((escapedFromMelee || suddenDistanceBreak) && !alreadyInEscapeWindow)
            {
                killContractEscapeUntilTick = now + 180;

                Log.Message("[Signal Interceptor] Psycaster anti-kite escape detected: "
                            + e.pawn.LabelShort
                            + " | d=" + d.ToString("F1")
                            + " | lastD=" + killContractLastDistance.ToString("F1")
                            + " | reason=" + (killContractReason ?? "unknown"));
            }
            else if ((escapedFromMelee || suddenDistanceBreak) && alreadyInEscapeWindow)
            {
                killContractEscapeUntilTick = Mathf.Max(killContractEscapeUntilTick, now + 60);
            }


            killContractLastDistance = d;
        }

        public bool ShouldReservePsycastForEscape(string abilityDefName, BattlefieldSnapshot snap)
        {
            if (string.IsNullOrEmpty(abilityDefName) || snap == null)
                return false;

            if (snap.caster == null || snap.caster.Dead || snap.caster.Downed)
                return false;

            int rangedLos = snap.enemiesWithLosToCaster != null
                ? snap.enemiesWithLosToCaster.Count(e => e != null && e.IsRanged && e.canShootNow)
                : 0;

            int enemyCount = snap.enemies != null ? snap.enemies.Count : 0;

            bool lowHp = snap.casterHpFraction <= 0.70f;
            bool damagedButStable = snap.casterHpFraction <= 0.85f;
            bool dangerousHp = snap.casterHpFraction <= 0.55f;
            bool criticalHp = snap.casterHpFraction <= 0.45f;

            bool highEntropy = snap.casterEntropyFraction >= 0.72f;

            bool underRangedPressure =
                rangedLos >= 2 ||
                snap.totalIncomingDps >= 18f;

            bool severeRangedPressure =
                rangedLos >= 3 ||
                snap.totalIncomingDps >= 28f;

            bool manyEnemiesWithRanged =
                enemyCount >= 4 && rangedLos >= 2;

            /*
             * Главный фикс:
             * Skip нельзя тратить на offensive-pick, когда кастер уже под давлением дальников.
             *
             * Emergency/recovery Skip вызывается напрямую через TryEmergencyRetreat /
             * TryRecoveryKiteSkip и этот метод не блокирует.
             *
             * Этот метод блокирует только scorers, то есть offensive Scorer_Skip.
             */
            if (abilityDefName == "Skip")
            {
                if (criticalHp)
                    return true;

                if (dangerousHp && underRangedPressure)
                    return true;

                if (lowHp && severeRangedPressure)
                    return true;

                if (damagedButStable && manyEnemiesWithRanged)
                    return true;

                if (currentStance == PsycasterStance.Kite && damagedButStable && underRangedPressure)
                    return true;

                if (currentStance == PsycasterStance.CrowdControl && damagedButStable && underRangedPressure)
                    return true;

                if (currentStance == PsycasterStance.Survive)
                    return true;
            }

            /*
             * Эти способности сами являются defensive escape tools.
             * Их не резервируем от самих себя.
             */
            bool defensiveAbility =
                abilityDefName == "BulletShield" ||
                abilityDefName == "Invisibility" ||
                abilityDefName == "Smokepop" ||
                abilityDefName == "Wallraise" ||
                abilityDefName == "ChaosSkip" ||
                abilityDefName == "MassChaosSkip";

            if (defensiveAbility)
                return false;

            /*
             * Остальные offensive/control способности можно заблокировать,
             * если ситуация требует держать ресурс под защиту.
             */
            if (dangerousHp && underRangedPressure)
                return true;

            if (lowHp && severeRangedPressure)
                return true;

            if (highEntropy && lowHp && underRangedPressure)
                return true;

            if (currentStance == PsycasterStance.Survive && highEntropy)
                return true;

            return false;
        }

    }
}
