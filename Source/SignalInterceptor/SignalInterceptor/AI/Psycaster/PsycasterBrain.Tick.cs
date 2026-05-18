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
        // ============================================================
        // Главный тик
        // ============================================================

        public void Tick()
        {
            if (caster == null || caster.Destroyed || caster.Dead || caster.Downed || !caster.Spawned)
                return;

            if (caster.Map == null)
                return;

            /*
             * Абсолютный приоритет:
             * если горит — тушим сразу, не спорим с leash/recovery/combat.
             */
            if (TryExtinguishSelfWithWaterskip())
                return;

            int now = Find.TickManager.TicksGame;

            /*
             * Если RimWorld/другой AI выдал job на выход с карты,
             * мы не даём VIP реально покинуть сайт.
             *
             * Важно: этот метод НЕ должен жёстко тащить VIP в одну точку.
             * Он только отменяет exit-job и выбирает безопасную внутреннюю клетку.
             */
            if (TryStopLeavingMapJob())
                return;

            if (Find.TickManager.TicksGame % PsycasterTuning.NoFleeRefreshTicks == 0)
            {
                if (IsCasterFreeToAct())
                    gc.ForcePsycasterNoFlee_Public(caster, caster.Map);
            }

            if (!focusBuffApplied && now >= graceUntilTick)
            {
                if (HasEnoughPsyfocusForAbility("Focus") &&
                    gc.TryCastSelfPsyAbility_Public(caster, "Focus"))
                {
                    focusBuffApplied = true;
                    nextActionSelectTick = now + PsycasterTuning.CastWarmupShort + 30;
                    return;
                }

                focusBuffApplied = true;
            }

            if (now < graceUntilTick)
                return;

            BattlefieldSnapshot snap = SnapshotBuilder.Build(this);
            LastSnapshot = snap;

            UpdateKillContractTelemetry(snap);

            /*
             * Сначала recovery.
             *
             * Причина:
             * если VIP ранен и его догоняет животное/милишник,
             * нельзя заставлять его возвращаться к HomeAnchor.
             * Сначала выживание, потом мягкий leash.
             */
            if (TryRunRecoveryLogic(snap))
                return;

            /*
             * Мягкий leash после recovery/pre-engage.
             * Важно: сам TryRunSoftLeashReturn теперь подавляется активным боем.
             */
            if (IsCasterFreeToAct() && TryRunPreEngageInvisibility(snap))
                return;

            /*
             * Мягкий leash после recovery.
             *
             * Важное отличие от старой логики:
             * здесь нет "если дальше MaxHomeDistance — немедленно прервать job и бежать в anchor".
             * Возврат происходит только если:
             * - VIP далеко;
             * - нет немедленной угрозы;
             * - cooldown leash-а прошёл;
             * - он свободен для нового решения.
             */
            if (TryRunSoftLeashReturn(snap))
                return;

            if (!snap.HasEnemies)
            {
                pendingMeleeTargetThingId = -1;
                pendingMeleeUntilTick = -1;
                pendingMeleeReason = null;

                ClearKillContract("no standing enemies");

                if (TryExecuteDownedPlayerPawn())
                    return;

                gc.TryAttackPlayerShuttleOrBuilding_Public(caster, caster.Map);
                return;
            }

            // Emergency escape имеет приоритет над pending-melee.
            // Если он реально почти умер — пусть оторвётся, а не самоубивается в дуэли.
            if (IsCasterFreeToAct() && TryEmergencyRetreat(snap))
                return;

            /*
             * Новый фикс:
             * если остался один враг, особенно один дальник,
             * не даём AI бесконечно "preserving ranged tools" на дистанции 10-15.
             * В стабильном состоянии он должен закончить дуэль.
             */
            if (IsCasterFreeToAct() && TryRunSingleEnemyDuelResolution(snap))
                return;

            if (IsCasterFreeToAct() && TryRunWeakPsycasterForcedAssault(snap))
                return;

            if (TryRunPendingMelee(snap))
                return;

            bool interrupt = triggers.ShouldInterrupt(snap);

            if (interrupt)
            {
                nextStanceReevalTick = now;
                nextActionSelectTick = now;
            }

            if (now >= nextStanceReevalTick)
            {
                PsycasterStance newStance = StanceSelector.Select(snap, currentStance);

                if (newStance != currentStance)
                {
                    Log.Message("[Signal Interceptor] Psycaster stance changed: "
                                + currentStance + " -> " + newStance
                                + " | HP=" + snap.casterHpFraction.ToString("F2")
                                + " | enemies=" + snap.enemies.Count
                                + " | cluster=" + snap.largestClusterSize);
                }

                currentStance = newStance;

                nextStanceReevalTick = now + Rand.RangeInclusive(
                    PsycasterTuning.StanceReevalMinTicks,
                    PsycasterTuning.StanceReevalMaxTicks);
            }

            if (now >= nextActionSelectTick && IsCasterFreeToAct())
            {
                ActionSelectAndExecute(snap);

                nextActionSelectTick = now + Rand.RangeInclusive(
                    PsycasterTuning.ActionSelectMinTicks,
                    PsycasterTuning.ActionSelectMaxTicks);
            }
        }


        /// <summary>
        /// Свободна ли пешка для нового решения. Не дёргаем её, если сейчас кастует/атакует.
        /// </summary>
        private bool IsCasterFreeToAct()
        {
            if (caster.stances == null)
                return true;

            if (caster.stances.stunner != null && caster.stances.stunner.Stunned)
                return false;

            // Если каст в процессе — текущая stance это PawnStance_Warmup.
            // Используем имя типа через рефлексию, чтобы не зависеть от namespace
            // (в разных версиях RimWorld класс лежит то в Verse, то в Verse.AI).
            Stance curStance = caster.stances.curStance;
            if (curStance != null)
            {
                string typeName = curStance.GetType().Name;
                if (typeName == "PawnStance_Warmup")
                    return false;
            }

            return true;
        }

    }
}
