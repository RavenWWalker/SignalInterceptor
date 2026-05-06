using RimWorld;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;
using Verse.AI;

namespace SignalInterceptor.AI.Psycaster
{
    /// <summary>
    /// Главный «мозг» пси-кастера. Один экземпляр на пси-кастер-VIP.
    /// Не сериализуется — пересоздаётся при загрузке сейва, с компенсацией PostLoadGraceTicks.
    ///
    /// Жизненный цикл:
    /// - VIP.Psycaster.cs создаёт Brain в SpawnPsycasterVIP (или ленивая инициализация).
    /// - Каждый игровой тик вызывается Brain.Tick().
    /// - Brain сам решает, нужно ли пересчитать стансу / выбрать действие, или подождать.
    ///
    /// Архитектура трёх временных горизонтов:
    /// - StanceReevalMin/Max тиков — переоценка стансы.
    /// - ActionSelectMin/Max тиков — выбор действия (если пешка свободна).
    /// - ReactiveTriggers — мгновенное прерывание по триггеру.
    /// </summary>
    public class PsycasterBrain
    {
        // ============================================================
        // Контекст
        // ============================================================

        private readonly SignalInterceptorGameComponent gc;
        private readonly Pawn caster;

        public Pawn Caster { get { return caster; } }
        public SignalInterceptorGameComponent GameComp { get { return gc; } }

        // ============================================================
        // Состояние
        // ============================================================

        private PsycasterStance currentStance = PsycasterStance.Opening;
        public PsycasterStance CurrentStance { get { return currentStance; } }

        private int nextStanceReevalTick = -1;
        private int nextActionSelectTick = -1;
        private int graceUntilTick = -1;
        private bool focusBuffApplied = false;

        private readonly ReactiveTriggers triggers = new ReactiveTriggers();
        private readonly Dictionary<string, int> softCooldowns = new Dictionary<string, int>();
        private readonly Dictionary<int, int> recentlyMovedPawns = new Dictionary<int, int>();
        private int pendingMeleeTargetThingId = -1;
        private int pendingMeleeUntilTick = -1;
        private string pendingMeleeReason = null;
        private int killContractTargetThingId = -1;
        private int killContractUntilTick = -1;
        private string killContractReason = null;
        private float killContractLastDistance = 999f;
        private int killContractLastCloseTick = -1;
        private int killContractEscapeUntilTick = -1;
        private bool recoveryMode = false;
        private int recoveryStartedTick = -1;
        private int nextRecoveryThinkTick = -1;
        private int nextEmergencyRetreatTick = -1;

        private readonly List<IAbilityScorer> scorers = new List<IAbilityScorer>();

        /// <summary>
        /// Последняя выбранная и применённая action — для debug overlay.
        /// </summary>
        public ScoredAction LastChosenAction { get; private set; }

        /// <summary>
        /// Снапшот, использованный при последнем action-select — для debug overlay.
        /// </summary>
        public BattlefieldSnapshot LastSnapshot { get; private set; }

        // ============================================================
        // Конструктор и регистрация скорерров
        // ============================================================

        public PsycasterBrain(SignalInterceptorGameComponent gc, Pawn caster)
        {
            this.gc = gc;
            this.caster = caster;

            int now = Find.TickManager.TicksGame;
            graceUntilTick = now + PsycasterTuning.PostLoadGraceTicks;
            nextStanceReevalTick = now + PsycasterTuning.PostLoadGraceTicks;
            nextActionSelectTick = now + PsycasterTuning.PostLoadGraceTicks;

            triggers.Reset();

            // Регистрация скорерров. Сейчас пусто — добавим в Пачках 4-6.
            RegisterScorers();
        }

        private void RegisterScorers()
        {
            // Пачка 4: контроль
            scorers.Add(new Scorer_Stun());
            scorers.Add(new Scorer_BerserkPulse());
            scorers.Add(new Scorer_BlindingPulse());
            scorers.Add(new Scorer_VertigoPulse());

            // Пачка 5: мобильность
            scorers.Add(new Scorer_Skip());
            scorers.Add(new Scorer_ChaosSkip());
            scorers.Add(new Scorer_MassChaosSkip());
            scorers.Add(new Scorer_Wallraise());
            scorers.Add(new Scorer_Beckon());
            // scorers.Add(new Scorer_Smokepop());
            scorers.Add(new Scorer_Skipshield());
            // Пачка 5.1 — ближний бой
            scorers.Add(new Scorer_MeleeAttack());

            // Пачка 6: ситуативные
            // scorers.Add(new Scorer_Berserk());
            // scorers.Add(new Scorer_ManhunterPulse());
            // scorers.Add(new Scorer_Invisibility());
            // scorers.Add(new Scorer_Beckon());
            // scorers.Add(new Scorer_Focus());
        }

        // ============================================================
        // Главный тик
        // ============================================================

        public void Tick()
        {
            if (caster == null || caster.Destroyed || caster.Dead || caster.Downed || !caster.Spawned)
                return;

            if (caster.Map == null)
                return;

            int now = Find.TickManager.TicksGame;

            if (HomeAnchor.IsValid)
            {
                float homeDist = caster.Position.DistanceTo(HomeAnchor);

                // ВАЖНО:
                // если активен kill-contract, anchor не должен ломать добивание цели.
                // Старое MaxHomeDistance=85 слишком часто рвало бой на 86-88 клетках.
                float allowedHomeDistance = HasActiveKillContract ? 130f : MaxHomeDistance;

                if (homeDist > allowedHomeDistance)
                {
                    Job goHome = JobMaker.MakeJob(JobDefOf.Goto, HomeAnchor);
                    goHome.locomotionUrgency = LocomotionUrgency.Sprint;
                    caster.jobs.StartJob(goHome, JobCondition.InterruptForced);

                    nextActionSelectTick = Find.TickManager.TicksGame + 90;

                    Log.Message("[Signal Interceptor] Psycaster too far from anchor (d="
                                + homeDist.ToString("F1") + "), returning home " + HomeAnchor
                                + " | allowed=" + allowedHomeDistance.ToString("F0")
                                + " | contract=" + HasActiveKillContract);

                    return;
                }
            }

            if (Find.TickManager.TicksGame % PsycasterTuning.NoFleeRefreshTicks == 0)
            {
                if (IsCasterFreeToAct())
                    gc.ForcePsycasterNoFlee_Public(caster, caster.Map);
            }

            if (!focusBuffApplied && now >= graceUntilTick)
            {
                if (gc.TryCastSelfPsyAbility_Public(caster, "Focus"))
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

            if (TryRunRecoveryLogic(snap))
                return;

            if (!snap.HasEnemies)
            {
                pendingMeleeTargetThingId = -1;
                pendingMeleeUntilTick = -1;
                pendingMeleeReason = null;

                ClearKillContract("no enemies");

                gc.TryAttackPlayerShuttleOrBuilding_Public(caster, caster.Map);
                return;
            }

            // Emergency escape имеет приоритет над pending-melee.
            // Если он реально почти умер — пусть оторвётся, а не самоубивается в дуэли.
            if (IsCasterFreeToAct() && TryEmergencyRetreat(snap))
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

        // ============================================================
        // Выбор действия (action-select)
        // ============================================================

        private bool TryRunRecoveryLogic(BattlefieldSnapshot snap)
        {
            if (caster == null || caster.Destroyed || caster.Dead || caster.Downed)
                return false;

            int now = Find.TickManager.TicksGame;

            if (!recoveryMode && ShouldEnterRecoveryMode())
            {
                recoveryMode = true;
                recoveryStartedTick = now;
                nextRecoveryThinkTick = now;

                pendingMeleeTargetThingId = -1;
                pendingMeleeUntilTick = -1;
                pendingMeleeReason = null;

                ClearKillContract("enter recovery");

                currentStance = PsycasterStance.Survive;
                nextStanceReevalTick = now + Rand.RangeInclusive(180, 300);
                nextActionSelectTick = now;

                Log.Message("[Signal Interceptor] Psycaster entering recovery mode: "
                            + caster.LabelShort
                            + " | hp=" + caster.health.summaryHealth.SummaryHealthPercent.ToString("F2")
                            + " | bleeding=" + HasDangerousBleeding()
                            + " | canRegen=" + CanRegenerateNow());
            }

            if (!recoveryMode)
                return false;

            if (IsRecoveredEnough())
            {
                recoveryMode = false;
                recoveryStartedTick = -1;
                nextRecoveryThinkTick = -1;

                Log.Message("[Signal Interceptor] Psycaster recovered and re-engaging: "
                            + caster.LabelShort
                            + " | hp=" + caster.health.summaryHealth.SummaryHealthPercent.ToString("F2")
                            + " | bleeding=" + HasDangerousBleeding());

                nextActionSelectTick = now + Rand.RangeInclusive(30, 60);
                nextStanceReevalTick = now + Rand.RangeInclusive(30, 60);

                return false;
            }

            if (now < nextRecoveryThinkTick)
                return true;

            nextRecoveryThinkTick = now + Rand.RangeInclusive(90, 150);

            RunRecoveryMovement(snap);

            return true;
        }

        private void RunRecoveryMovement(BattlefieldSnapshot snap)
        {
            if (!IsCasterFreeToAct())
                return;

            // В recovery режиме пси-кастер имеет право сначала прожать emergency Skip.
            // Иначе он может умереть от огня до того, как 15 секунд без урона вообще начнутся.
            if (snap != null && snap.HasEnemies && TryEmergencyRetreat(snap))
                return;

            if (caster.CurJobDef == JobDefOf.Goto)
                return;

            IntVec3 retreatCell;

            if (snap != null && snap.HasEnemies && TryFindEmergencyRetreatCell(snap, out retreatCell))
            {
                Job job = JobMaker.MakeJob(JobDefOf.Goto, retreatCell);
                job.locomotionUrgency = LocomotionUrgency.Sprint;
                job.expiryInterval = Rand.RangeInclusive(120, 180);

                caster.jobs.StartJob(job, JobCondition.InterruptForced);

                HediffComp_PsycasterRestoringMechanisms restore = GetRestoringMechanisms();

                Log.Message("[Signal Interceptor] Psycaster recovery retreat: "
                            + caster.LabelShort
                            + " -> " + retreatCell
                            + " | hp=" + caster.health.summaryHealth.SummaryHealthPercent.ToString("F2")
                            + " | canRegen=" + (restore != null && restore.CanRegenerateNow)
                            + " | ticksSinceDamage=" + (restore != null ? restore.TicksSinceDamage : -1));

                return;
            }

            HediffComp_PsycasterRestoringMechanisms comp = GetRestoringMechanisms();

            if (Prefs.DevMode)
            {
                Log.Message("[Signal Interceptor] Psycaster recovery holding: "
                            + caster.LabelShort
                            + " | hp=" + caster.health.summaryHealth.SummaryHealthPercent.ToString("F2")
                            + " | canRegen=" + (comp != null && comp.CanRegenerateNow)
                            + " | ticksSinceDamage=" + (comp != null ? comp.TicksSinceDamage : -1));
            }
        }

        private HediffComp_PsycasterRestoringMechanisms GetRestoringMechanisms()
        {
            return PsycasterRecoveryUtility.GetRestoringComp(caster);
        }

        private bool CanRegenerateNow()
        {
            HediffComp_PsycasterRestoringMechanisms restore = GetRestoringMechanisms();

            return restore != null && restore.CanRegenerateNow;
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

        private bool ShouldEnterRecoveryMode()
        {
            if (caster == null || caster.health == null)
                return false;

            float hp = caster.health.summaryHealth.SummaryHealthPercent;

            if (hp <= 0.35f)
                return true;

            if (hp <= 0.45f && HasDangerousBleeding())
                return true;

            return false;
        }

        private bool IsRecoveredEnough()
        {
            if (caster == null || caster.health == null)
                return false;

            float hp = caster.health.summaryHealth.SummaryHealthPercent;

            if (hp < 0.58f)
                return false;

            if (HasDangerousBleeding())
                return false;

            return true;
        }

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

            // ВАЖНО:
            // Fallback1v1 больше не имеет права держать long melee-pursuit с 20-30 клеток.
            // Иначе он блокирует action selection, и AI не выбирает Skip/Beckon/Pulse.
            if (fallback1v1 &&
                singleEnemy &&
                targetIsRanged &&
                !targetControlled &&
                d > 8f)
            {
                pendingMeleeTargetThingId = -1;
                pendingMeleeUntilTick = -1;
                pendingMeleeReason = null;

                nextActionSelectTick = now;

                Log.Message("[Signal Interceptor] Psycaster pending-melee released for ranged duel tools: "
                            + target.LabelShort
                            + " | d=" + d.ToString("F1"));

                return false;
            }

            // Если цель была в melee-contract и резко сбежала, не продолжаем тупо бежать пешком.
            // Отдаём управление action selection, чтобы сработал Skip/Beckon/Blind/Vertigo.
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

                Log.Message("[Signal Interceptor] Psycaster pending-melee: "
                            + target.LabelShort
                            + " | reason=" + (pendingMeleeReason ?? "unknown")
                            + " | d=" + d.ToString("F1"));

                if (d <= 1.8f)
                {
                    pendingMeleeTargetThingId = -1;
                    pendingMeleeUntilTick = -1;
                    pendingMeleeReason = null;
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

            StartKillContract(target, Mathf.Max(durationTicks, 420), reason);
        }

        private bool TryEmergencyRetreat(BattlefieldSnapshot snap)
        {
            if (snap == null || snap.caster == null || !snap.HasEnemies)
                return false;

            int now = Find.TickManager.TicksGame;

            if (now < nextEmergencyRetreatTick)
                return false;

            // В 1v1 не паникуем слишком рано. Иначе он будет ломать нормальную дуэль.
            bool singleEnemy = snap.enemies != null && snap.enemies.Count == 1;

            int adjacentCount = snap.enemiesAdjacent != null ? snap.enemiesAdjacent.Count : 0;

            bool criticalHp = snap.casterHpFraction <= 0.30f;
            bool lowHpUnderFire = snap.casterHpFraction <= 0.45f && snap.IsUnderRangedFire && !singleEnemy;

            bool surrounded =
                adjacentCount >= 3 ||
                (adjacentCount >= 2 && snap.casterHpFraction <= 0.60f);

            if (!criticalHp && !lowHpUnderFire && !surrounded)
                return false;

            if (!criticalHp && !lowHpUnderFire && !surrounded)
                return false;

            AbilityDef skipDef = GetAbilityDef("Skip");

            if (skipDef == null)
                return false;

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

            ApplySoftCooldown("Skip");

            nextEmergencyRetreatTick = Find.TickManager.TicksGame + Rand.RangeInclusive(360, 540);
            nextActionSelectTick = Find.TickManager.TicksGame + PsycasterTuning.CastWarmupShort + 90;

            Log.Message("[Signal Interceptor] Psycaster emergency-retreat Skip self to "
                        + retreatCell
                        + " | HP=" + snap.casterHpFraction.ToString("F2")
                        + " | enemies=" + snap.enemies.Count
                        + " | adjacent=" + (snap.enemiesAdjacent != null ? snap.enemiesAdjacent.Count : 0));

            return true;
        }

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

            killContractTargetThingId = target.thingIDNumber;
            killContractUntilTick = now + Mathf.Max(180, durationTicks);
            killContractReason = reason;
            killContractLastDistance = caster.Position.DistanceTo(target.Position);

            if (killContractLastDistance <= 3.5f)
                killContractLastCloseTick = now;

            Log.Message("[Signal Interceptor] Psycaster kill-contract start: "
                        + target.LabelShort
                        + " | reason=" + (reason ?? "unknown")
                        + " | d=" + killContractLastDistance.ToString("F1")
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

        private bool TryFindEmergencyRetreatCell(BattlefieldSnapshot snap, out IntVec3 result)
        {
            result = IntVec3.Invalid;

            if (snap == null || caster == null || caster.Map == null || snap.topThreat == null || snap.topThreat.pawn == null)
                return false;

            Map map = caster.Map;
            Pawn threat = snap.topThreat.pawn;

            IntVec3 best = IntVec3.Invalid;
            float bestScore = float.MinValue;

            for (int i = 0; i < 80; i++)
            {
                IntVec3 cell;

                if (!CellFinder.TryFindRandomCellNear(
                    caster.Position,
                    map,
                    22,
                    c => c.InBounds(map)
                         && c.Standable(map)
                         && c.GetFirstPawn(map) == null
                         && caster.CanReach(c, PathEndMode.OnCell, Danger.Deadly),
                    out cell))
                {
                    continue;
                }

                float distFromThreat = cell.DistanceTo(threat.Position);
                float distFromCaster = cell.DistanceTo(caster.Position);

                if (distFromCaster < 8f)
                    continue;

                if (distFromThreat < 12f)
                    continue;

                float score = 0f;

                score += distFromThreat * 2.0f;
                score += distFromCaster * 0.25f;

                int visibleShooters = 0;

                if (snap.rangedEnemies != null)
                {
                    for (int r = 0; r < snap.rangedEnemies.Count; r++)
                    {
                        EnemyAssessment e = snap.rangedEnemies[r];

                        if (e == null || e.pawn == null || !e.pawn.Spawned || e.pawn.Map != map)
                            continue;

                        if (GenSight.LineOfSight(e.pawn.Position, cell, map))
                            visibleShooters++;
                    }
                }

                score -= visibleShooters * 18f;

                if (HomeAnchor.IsValid)
                {
                    float homeDist = cell.DistanceTo(HomeAnchor);

                    if (homeDist > MaxHomeDistance)
                        score -= 100f;
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

        private void ActionSelectAndExecute(BattlefieldSnapshot snap)
        {
            // Пробежаться по скорерам, выбрать максимум.
            ScoredAction best = ScoredAction.None;

            for (int i = 0; i < scorers.Count; i++)
            {
                IAbilityScorer scorer = scorers[i];
                if (scorer == null) continue;

                if (!scorer.IsAvailable(this, snap))
                    continue;

                ScoredAction candidate = scorer.Score(this, snap);
                if (candidate == null || !candidate.IsValid) continue;

                if (candidate.score > best.score)
                    best = candidate;
            }

            if (best.IsValid)
            {
                ExecuteAction(best, snap);
                LastChosenAction = best;
                return;
            }

            // FALLBACK на время Пачки 3: скорерров ещё нет, поэтому просто
            // дёрнем самый банальный путь — атаковать ближайшего цели в melee.
            // Когда подключим Пачки 4-6 — этот fallback станет почти недостижимым.
            FallbackBasicAttack(snap);
        }

        private void ExecuteAction(ScoredAction action, BattlefieldSnapshot snap)
        {
            if (action == null || !action.IsValid)
                return;

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

            if (casted)
            {
                if (action.targetPawn != null)
                {
                    string n = action.abilityDefName;
                    float d = caster.Position.DistanceTo(action.targetPawn.Position);

                    if (n == "Beckon" || n == "Skip" || n == "ChaosSkip")
                    {
                        MarkPawnRecentlyMoved(action.targetPawn, 600);
                        QueuePendingMelee(action.targetPawn, 480, n);
                        StartKillContract(action.targetPawn, 900, n);
                    }

                    if (n == "Stun")
                    {
                        MarkPawnRecentlyMoved(action.targetPawn, 180);

                        // Stun теперь считается melee-pin, а не способом догонять цель с 15 клеток.
                        // Контракт стартует/обновляется, но StunScorer ниже запретит дальний Stun.
                        QueuePendingMelee(action.targetPawn, 240, "Stun");
                        StartKillContract(action.targetPawn, d <= 3.5f ? 600 : 300, "Stun");
                    }
                }

                ApplySoftCooldown(action.abilityDefName);

                int warmup = action.castWarmupTicks > 0
                    ? action.castWarmupTicks
                    : PsycasterTuning.CastWarmupMedium;

                int extraDelay = 30;

                if (action.abilityDefName == "Stun")
                {
                    extraDelay = 0;
                }
                else if ((action.abilityDefName == "Beckon" || action.abilityDefName == "Skip") &&
                         action.targetPawn != null)
                {
                    extraDelay = 5;
                }

                nextActionSelectTick = Find.TickManager.TicksGame + warmup + extraDelay;

                Log.Message("[Signal Interceptor] Psycaster action: " + action.abilityDefName
                            + " | score=" + action.score.ToString("F2")
                            + " | stance=" + currentStance
                            + " | reason=" + (action.debugReason ?? ""));
            }
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

        // Целочисленные хелперы — без UnityEngine.Vector3, чтобы не тянуть using.
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

        // ============================================================
        // Soft-cooldown
        // ============================================================

        public bool IsOnSoftCooldown(string abilityDefName)
        {
            if (string.IsNullOrEmpty(abilityDefName)) return false;
            int until;
            if (!softCooldowns.TryGetValue(abilityDefName, out until)) return false;
            return Find.TickManager.TicksGame < until;
        }

        public void MarkPawnRecentlyMoved(Pawn p, int holdTicks)
        {
            if (p == null) return;
            recentlyMovedPawns[p.thingIDNumber] = Find.TickManager.TicksGame + holdTicks;
        }

        public bool WasPawnRecentlyMoved(Pawn p)
        {
            if (p == null) return false;
            int until;
            if (!recentlyMovedPawns.TryGetValue(p.thingIDNumber, out until)) return false;
            return Find.TickManager.TicksGame < until;
        }

        public void ApplySoftCooldown(string abilityDefName)
        {
            if (string.IsNullOrEmpty(abilityDefName)) return;

            int min = 0;
            int max = 0;
            switch (abilityDefName)
            {
                case "Stun":
                    min = PsycasterTuning.StunSoftCooldownMin;
                    max = PsycasterTuning.StunSoftCooldownMax;
                    break;
                case "Skip":
                    min = PsycasterTuning.SkipSoftCooldownMin;
                    max = PsycasterTuning.SkipSoftCooldownMax;
                    break;
                case "ChaosSkip":
                    min = PsycasterTuning.ChaosSkipSoftCooldownMin;
                    max = PsycasterTuning.ChaosSkipSoftCooldownMax;
                    break;
                case "MassChaosSkip":
                    min = PsycasterTuning.MassChaosSkipSoftCooldownMin;
                    max = PsycasterTuning.MassChaosSkipSoftCooldownMax;
                    break;
                case "Beckon":
                    min = PsycasterTuning.BeckonSoftCooldownMin;
                    max = PsycasterTuning.BeckonSoftCooldownMax;
                    break;
                case "BlindingPulse":
                    min = PsycasterTuning.BlindingPulseSoftCooldownMin;
                    max = PsycasterTuning.BlindingPulseSoftCooldownMax;
                    break;
                case "VertigoPulse":
                    min = PsycasterTuning.VertigoPulseSoftCooldownMin;
                    max = PsycasterTuning.VertigoPulseSoftCooldownMax;
                    break;
                case "BerserkPulse":
                    min = PsycasterTuning.BerserkPulseSoftCooldownMin;
                    max = PsycasterTuning.BerserkPulseSoftCooldownMax;
                    break;
                case "Invisibility":
                    min = PsycasterTuning.InvisibilitySoftCooldownMin;
                    max = PsycasterTuning.InvisibilitySoftCooldownMax;
                    break;
                case "Smokepop":
                    min = PsycasterTuning.SmokepopSoftCooldownMin;
                    max = PsycasterTuning.SmokepopSoftCooldownMax;
                    break;
                case "Wallraise":
                    min = PsycasterTuning.WallraiseSoftCooldownMin;
                    max = PsycasterTuning.WallraiseSoftCooldownMax;
                    break;
                case "Skipshield":
                    min = PsycasterTuning.SkipshieldSoftCooldownMin;
                    max = PsycasterTuning.SkipshieldSoftCooldownMax;
                    break;
                case "ManhunterPulse":
                    min = PsycasterTuning.ManhunterPulseSoftCooldownMin;
                    max = PsycasterTuning.ManhunterPulseSoftCooldownMax;
                    break;
                case "Focus":
                    min = PsycasterTuning.FocusSoftCooldownMin;
                    max = PsycasterTuning.FocusSoftCooldownMax;
                    break;
                default:
                    return; // Способность без soft-cooldown.
            }

            int until = Find.TickManager.TicksGame + Rand.RangeInclusive(min, max);
            softCooldowns[abilityDefName] = until;
        }

        // ============================================================
        // Прокси к хелперам SignalInterceptorGameComponent.
        // Скореры дёргают эти методы, не публичные у GameComp.
        // ============================================================

        public AbilityDef GetAbilityDef(string defName)
        {
            return gc.FindAbilityDefByPossibleName_Public(defName);
        }

        public object GetPawnAbilityObject(AbilityDef def)
        {
            return gc.GetPawnAbility_Public(caster, def);
        }

        public bool IsAbilityOnCooldown(object ability)
        {
            return gc.IsAbilityOnCooldown_Public(ability);
        }

        public bool IsRangedCombatPawn(Pawn p)
        {
            return gc.IsRangedCombatPawn_Public(p);
        }

        public IntVec3 HomeAnchor = IntVec3.Invalid;
        public const float MaxHomeDistance = 120f;
    }
}
