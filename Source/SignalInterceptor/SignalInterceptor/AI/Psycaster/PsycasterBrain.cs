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
        private int nextDownedExecutionScanTick = -1;

        private int nextSoftLeashReturnTick = -1;

        private const int MapEdgeDangerDistance = 10;

        private const float SoftHomeFreeRadius = 45f;
        private const float SoftHomeSoftRadius = 70f;
        private const float SoftHomeHardRadius = 95f;

        private const int SoftLeashReturnCooldownTicks = 240;
        private const int SoftLeashReturnJobExpiryMin = 160;
        private const int SoftLeashReturnJobExpiryMax = 240;

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

            // Пачка 5: мобильность / защита
            scorers.Add(new Scorer_Skip());
            scorers.Add(new Scorer_ChaosSkip());
            scorers.Add(new Scorer_MassChaosSkip());
            scorers.Add(new Scorer_Wallraise());
            scorers.Add(new Scorer_Beckon());
            scorers.Add(new Scorer_Skipshield());
            scorers.Add(new Scorer_Invisibility());
            scorers.Add(new Scorer_Smokepop());

            // Пачка 5.1 — ближний бой
            scorers.Add(new Scorer_MeleeAttack());

            // Пачка 6: будущие ситуативные
            // scorers.Add(new Scorer_Berserk());
            scorers.Add(new Scorer_ManhunterPulse());
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

            /*
             * ВАЖНЕЙШИЙ ФИКС:
             * leash не должен вмешиваться в активный kill-contract.
             *
             * В твоём логе было:
             * - кастер коммитится в Hodge
             * - потом soft leash return immediateDanger=True
             * - потом снова pending/melee
             *
             * Это и создаёт дёрганье.
             */
            if (HasActiveKillContract)
                return false;

            if (pendingMeleeTargetThingId >= 0 && pendingMeleeUntilTick > now)
                return false;

            Map map = caster.Map;

            float homeDist = caster.Position.DistanceTo(HomeAnchor);

            bool nearEdge = IsNearMapEdge(caster.Position, map, MapEdgeDangerDistance + 4);
            bool immediateDanger = HasImmediateLeashDanger(snap);

            /*
             * Если есть непосредственная опасность — leash НЕ имеет права стартовать.
             *
             * Исключение можно было бы делать для совсем края карты,
             * но на практике это снова создаёт stagger.
             * Поэтому при immediateDanger полностью отдаём управление combat/recovery.
             */
            if (immediateDanger)
                return false;

            /*
             * Если он не слишком далеко и не у края карты — leash не нужен.
             */
            if (homeDist < SoftHomeSoftRadius && !nearEdge)
                return false;

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

            /*
             * Cooldown делаем длиннее, чтобы leash не дёргал пешку каждые пару секунд.
             */
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
                abilityDefName == "Skipshield" ||
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
             * НЕ перезапускаем job каждый тик.
             * Просто даём текущей работе продолжаться.
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

            nextDownedExecutionScanTick = now + 30;

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

                /*
                 * Не надо бежать через всю карту ради казни.
                 * Если нужно агрессивнее — можно поднять до 35-45.
                 */
                if (distance > 28f)
                    continue;

                if (!caster.CanReach(pawn, PathEndMode.Touch, Danger.Deadly))
                    continue;

                float score = 100f - distance;

                /*
                 * Почти мёртвых добивать приоритетнее.
                 */
                if (pawn.health != null && pawn.health.summaryHealth != null)
                {
                    score += (1f - pawn.health.summaryHealth.SummaryHealthPercent) * 25f;
                }

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

            /*
             * Выдаём именно AttackMelee.
             * Важно: не Wander, не Goto, не Cast, а нормальную melee-атаку по downed pawn.
             */
            Job job = JobMaker.MakeJob(JobDefOf.AttackMelee, target);
            job.locomotionUrgency = LocomotionUrgency.Sprint;
            job.expiryInterval = 180;
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
             * Небольшая задержка, чтобы не перезапускать приказ сразу же.
             */
            nextDownedExecutionScanTick = now + 60;

            return true;
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

        private bool IsCasterBurningNow()
        {
            if (caster == null || !caster.Spawned || caster.Map == null)
                return false;

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

            if (caster.health != null && caster.health.hediffSet != null)
            {
                List<Hediff> hediffs = caster.health.hediffSet.hediffs;

                for (int i = 0; i < hediffs.Count; i++)
                {
                    Hediff h = hediffs[i];

                    if (h == null || h.def == null || h.def.defName == null)
                        continue;

                    string defName = h.def.defName;

                    if (defName.IndexOf("Burn", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                        defName.IndexOf("Fire", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private bool TryRunRecoveryLogic(BattlefieldSnapshot snap)
        {
            if (caster == null || caster.Destroyed || caster.Dead || caster.Downed)
                return false;

            int now = Find.TickManager.TicksGame;

            if (!recoveryMode && ShouldEnterRecoveryMode(snap))
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
                            + " | canRegen=" + CanRegenerateNow()
                            + " | reason=" + GetRecoveryReasonForLog(snap));
            }

            if (!recoveryMode)
                return false;

            if (IsRecoveredEnough(snap))
            {
                recoveryMode = false;
                recoveryStartedTick = -1;
                nextRecoveryThinkTick = -1;

                Log.Message("[Signal Interceptor] Psycaster recovered and re-engaging: "
                            + caster.LabelShort
                            + " | hp=" + caster.health.summaryHealth.SummaryHealthPercent.ToString("F2")
                            + " | bleeding=" + HasDangerousBleeding()
                            + " | plateau=" + IsRecoveryPlateauLikely());

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

            /*
             * 1) Сначала пробуем recovery-kite Skip.
             * Это спасает от животных/милишников, которые догоняют кастера во время лечения.
             */
            if (snap != null && snap.HasEnemies && TryRecoveryKiteSkip(snap))
                return;

            /*
             * 2) Затем обычный emergency retreat.
             * Он срабатывает на critical HP / surround / ranged pressure.
             */
            if (snap != null && snap.HasEnemies && TryEmergencyRetreat(snap))
                return;

            /*
             * 3) Новый важный блок:
             * если Skip недоступен, но кастер под ranged pressure,
             * он должен прожать Invisibility / Smokepop, а не просто бежать пешком под пулями.
             */
            if (snap != null && snap.HasEnemies && TryRecoveryDefensivePsycast(snap))
                return;

            /*
             * 4) Если уже идёт безопасный Goto — не трогаем.
             */
            if (caster.CurJobDef == JobDefOf.Goto && IsRecoveryPositionStillSafe(snap))
                return;

            /*
             * 5) Если psy-tools не сработали, тогда обычный recovery retreat.
             */
            IntVec3 retreatCell;

            if (snap != null && snap.HasEnemies && TryFindEmergencyRetreatCell(snap, out retreatCell))
            {
                Job job = JobMaker.MakeJob(JobDefOf.Goto, retreatCell);
                job.locomotionUrgency = LocomotionUrgency.Sprint;
                job.expiryInterval = Rand.RangeInclusive(75, 120);

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

        private bool TryRecoveryDefensivePsycast(BattlefieldSnapshot snap)
        {
            if (snap == null || caster == null || caster.Destroyed || caster.Dead || caster.Downed || !caster.Spawned)
                return false;

            if (caster.Map == null || !snap.HasEnemies)
                return false;

            float hp = caster.health != null && caster.health.summaryHealth != null
                ? caster.health.summaryHealth.SummaryHealthPercent
                : 1f;

            int rangedLos = snap.enemiesWithLosToCaster != null
                ? snap.enemiesWithLosToCaster.Count(e => e != null && e.IsRanged && e.canShootNow)
                : 0;

            int enemyCount = snap.enemies != null ? snap.enemies.Count : 0;

            bool singleEnemy = enemyCount <= 1;

            bool underRangedPressure =
                rangedLos >= 1 ||
                snap.totalIncomingDps >= 10f;

            bool severeRangedPressure =
                rangedLos >= 2 ||
                snap.totalIncomingDps >= 35f;

            bool panicRangedPressure =
                rangedLos >= 3 ||
                snap.totalIncomingDps >= 55f;

            bool critical = hp <= 0.45f;
            bool dangerous = hp <= 0.65f;
            bool damaged = hp <= 0.78f;

            /*
             * Если ranged pressure нет — не тратим defensive psycast.
             */
            if (!underRangedPressure)
                return false;

            /*
             * Фикс:
             * один стрелок не должен заставлять кастера на 0.90-1.00 HP
             * спамить Invisibility/Smokepop.
             */
            if (singleEnemy && hp >= 0.80f && !panicRangedPressure)
                return false;

            /*
             * Ещё один фикс:
             * при высоком HP defensive psycast разрешён только при настоящем сильном огне.
             */
            if (hp >= 0.90f && !panicRangedPressure)
                return false;

            /*
             * 1) Invisibility — panic button.
             */
            if ((critical || dangerous || severeRangedPressure) && !IsCasterInvisibleNow())
            {
                if (TryCastRecoverySelfAbility("Invisibility", PsycasterTuning.InvisibilitySoftCooldownMin, PsycasterTuning.InvisibilitySoftCooldownMax))
                {
                    nextRecoveryThinkTick = Find.TickManager.TicksGame + Rand.RangeInclusive(110, 160);
                    nextActionSelectTick = Find.TickManager.TicksGame + PsycasterTuning.CastWarmupMedium + 90;

                    Log.Message("[Signal Interceptor] Psycaster recovery defensive Invisibility"
                                + " | HP=" + hp.ToString("F2")
                                + " | rangedLOS=" + rangedLos
                                + " | incomingDps=" + snap.totalIncomingDps.ToString("F1")
                                + " | enemies=" + enemyCount);

                    return true;
                }
            }

            /*
             * 2) Smokepop.
             * Если уже невидим — smoke почти всегда лишний.
             */
            if (!IsCasterInvisibleNow() && (critical || dangerous || damaged || panicRangedPressure) && rangedLos >= 1)
            {
                if (TryCastRecoverySelfAbility("Smokepop", PsycasterTuning.SmokepopSoftCooldownMin, PsycasterTuning.SmokepopSoftCooldownMax))
                {
                    nextRecoveryThinkTick = Find.TickManager.TicksGame + Rand.RangeInclusive(110, 160);
                    nextActionSelectTick = Find.TickManager.TicksGame + PsycasterTuning.CastWarmupMedium + 90;

                    Log.Message("[Signal Interceptor] Psycaster recovery defensive Smokepop"
                                + " | HP=" + hp.ToString("F2")
                                + " | rangedLOS=" + rangedLos
                                + " | incomingDps=" + snap.totalIncomingDps.ToString("F1")
                                + " | enemies=" + enemyCount);

                    return true;
                }
            }

            return false;
        }

        private bool TryCastRecoverySelfAbility(string abilityDefName, int softCooldownMin, int softCooldownMax)
        {
            if (string.IsNullOrEmpty(abilityDefName))
                return false;

            if (IsOnSoftCooldown(abilityDefName))
                return false;

            if (!HasEnoughPsyfocusForAbility(abilityDefName))
            {
                if (Prefs.DevMode)
                {
                    Log.Message("[Signal Interceptor] Psycaster recovery cast blocked by psyfocus: "
                                + abilityDefName
                                + " | psyfocus=" + GetCurrentPsyfocusFraction().ToString("F2")
                                + " | required=" + GetMinimumPsyfocusForAbility(abilityDefName).ToString("F2"));
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

            ClearKillContract("recovery defensive " + abilityDefName);

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

        private bool TryRecoveryKiteSkip(BattlefieldSnapshot snap)
        {
            if (snap == null || caster == null || caster.Destroyed || caster.Dead || caster.Downed || !caster.Spawned)
                return false;

            if (caster.Map == null || !snap.HasEnemies)
                return false;

            int now = Find.TickManager.TicksGame;

            /*
             * ВАЖНО:
             * Не используем слишком длинный nextEmergencyRetreatTick как жёсткий запрет.
             * В recovery быстрые животные могут догнать кастера раньше,
             * чем старый emergency cooldown истечёт.
             *
             * Но полностью спамить Skip тоже нельзя.
             * Поэтому используем мягкое окно 150 тиков после последнего emergency Skip.
             */
            if (now < nextEmergencyRetreatTick - 210)
                return false;

            float hp = caster.health != null && caster.health.summaryHealth != null
                ? caster.health.summaryHealth.SummaryHealthPercent
                : 1f;

            int closeMeleeOrAnimals = 0;
            int veryCloseMeleeOrAnimals = 0;
            float nearestMeleeOrAnimalDist = 999f;
            Pawn nearestMeleeOrAnimal = null;

            if (snap.enemies != null)
            {
                for (int i = 0; i < snap.enemies.Count; i++)
                {
                    EnemyAssessment e = snap.enemies[i];

                    if (e == null || e.pawn == null)
                        continue;

                    if (e.pawn.Destroyed || e.pawn.Dead || e.pawn.Downed || !e.pawn.Spawned || e.pawn.Map != caster.Map)
                        continue;

                    bool meleeLike = e.IsAnimal || e.IsMelee || e.role == EnemyRole.Wimp;

                    if (!meleeLike)
                        continue;

                    if (e.distanceToCaster < nearestMeleeOrAnimalDist)
                    {
                        nearestMeleeOrAnimalDist = e.distanceToCaster;
                        nearestMeleeOrAnimal = e.pawn;
                    }

                    if (e.distanceToCaster <= 10f)
                        closeMeleeOrAnimals++;

                    if (e.distanceToCaster <= 4f)
                        veryCloseMeleeOrAnimals++;
                }
            }

            /*
             * Условия recovery-skip.
             *
             * Главная цель:
             * не ждать, пока животное ударит.
             * Если оно уже в 8-10 клетках и кастер лечится/кровоточит —
             * лучше потратить Skip и сохранить реген.
             */
            bool shouldSkip =
                (hp <= 0.82f && nearestMeleeOrAnimalDist <= 12f) ||
                (hp <= 0.90f && nearestMeleeOrAnimalDist <= 8f) ||
                (veryCloseMeleeOrAnimals >= 1) ||
                (closeMeleeOrAnimals >= 2);

            if (!shouldSkip)
                return false;

            AbilityDef skipDef = GetAbilityDef("Skip");

            if (skipDef == null)
                return false;

            if (!HasEnoughPsyfocusForAbility("Skip"))
            {
                if (Prefs.DevMode)
                {
                    Log.Message("[Signal Interceptor] Psycaster recovery-kite Skip blocked by psyfocus: "
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

            IntVec3 skipCell;

            if (!TryFindRecoverySkipCell(snap, out skipCell))
                return false;

            bool casted = gc.TryCastPsyAbilityToDestination_Public(
                caster,
                "Skip",
                caster,
                skipCell);

            if (!casted)
                return false;

            pendingMeleeTargetThingId = -1;
            pendingMeleeUntilTick = -1;
            pendingMeleeReason = null;

            ClearKillContract("recovery kite skip");

            ApplySoftCooldown("Skip");

            /*
             * Короткое окно: это именно recovery-kite, а не обычный боевой escape.
             * Если животное снова догнало — через несколько секунд можно повторить.
             */
            nextEmergencyRetreatTick = Find.TickManager.TicksGame + Rand.RangeInclusive(150, 240);
            nextActionSelectTick = Find.TickManager.TicksGame + PsycasterTuning.CastWarmupShort + 75;
            nextRecoveryThinkTick = Find.TickManager.TicksGame + Rand.RangeInclusive(75, 105);

            Log.Message("[Signal Interceptor] Psycaster recovery-kite Skip self to "
                        + skipCell
                        + " | HP=" + hp.ToString("F2")
                        + " | nearestMelee="
                        + (nearestMeleeOrAnimal != null ? nearestMeleeOrAnimal.LabelShort : "null")
                        + " | nearestD=" + nearestMeleeOrAnimalDist.ToString("F1")
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

        private float GetCurrentPsyfocusFraction()
        {
            if (caster == null || caster.psychicEntropy == null)
                return 0f;

            return caster.psychicEntropy.CurrentPsyfocus;
        }

        private bool HasEnoughPsyfocusForAbility(string abilityDefName)
        {
            float current = GetCurrentPsyfocusFraction();
            float required = GetMinimumPsyfocusForAbility(abilityDefName);

            return current >= required;
        }

        private float GetMinimumPsyfocusForAbility(string abilityDefName)
        {
            if (string.IsNullOrEmpty(abilityDefName))
                return 0.10f;

            switch (abilityDefName)
            {
                case "Focus":
                    return 0.05f;

                case "Smokepop":
                    return 0.10f;

                case "Waterskip":
                    return 0.10f;

                case "Stun":
                    return 0.12f;

                case "BlindingPulse":
                    return 0.18f;

                case "VertigoPulse":
                    return 0.18f;

                case "Invisibility":
                    return 0.20f;

                case "Skip":
                    return 0.20f;

                case "ChaosSkip":
                    return 0.20f;

                case "Beckon":
                    return 0.20f;

                case "Wallraise":
                    return 0.20f;

                case "Berserk":
                    return 0.24f;

                case "BerserkPulse":
                    return 0.28f;

                case "MassChaosSkip":
                    return 0.28f;

                case "ManhunterPulse":
                    return 0.30f;

                case "Skipshield":
                    return 0.20f;

                default:
                    return 0.15f;
            }
        }

        private bool IsRecoveryPositionStillSafe(BattlefieldSnapshot snap)
        {
            if (caster == null || caster.Destroyed || caster.Dead || caster.Downed || !caster.Spawned)
                return false;

            Map map = caster.Map;

            if (map == null)
                return false;

            /*
             * Если текущий recovery-Goto ведёт к краю карты — это больше не safe.
             */
            Job curJob = caster.CurJob;

            if (curJob != null && curJob.def == JobDefOf.Goto && curJob.targetA.IsValid)
            {
                IntVec3 targetCell = curJob.targetA.Cell;

                if (targetCell.IsValid)
                {
                    if (IsNearMapEdge(targetCell, map, MapEdgeDangerDistance))
                        return false;

                    /*
                     * Если target слишком далеко от HomeAnchor, не считаем этот Goto безопасным.
                     * Это не жёсткая привязка — просто текущий retreat надо пересчитать.
                     */
                    if (HomeAnchor.IsValid)
                    {
                        float targetHomeDist = targetCell.DistanceTo(HomeAnchor);

                        if (targetHomeDist > SoftHomeHardRadius)
                            return false;
                    }
                }
            }

            /*
             * Если он сам уже слишком близко к краю, текущая позиция небезопасна.
             */
            if (IsNearMapEdge(caster.Position, map, MapEdgeDangerDistance))
                return false;

            if (snap == null || snap.enemies == null || snap.enemies.Count == 0)
                return true;

            for (int i = 0; i < snap.enemies.Count; i++)
            {
                EnemyAssessment e = snap.enemies[i];

                if (e == null || e.pawn == null)
                    continue;

                if (e.pawn.Destroyed || e.pawn.Dead || e.pawn.Downed || !e.pawn.Spawned || e.pawn.Map != map)
                    continue;

                /*
                 * Животные и melee-враги опасны даже без "ranged fire".
                 * Если они снова подошли близко — текущий Goto больше не безопасен,
                 * надо пересчитать отступление.
                 */
                if ((e.IsAnimal || e.IsMelee || e.role == EnemyRole.Wimp) && e.distanceToCaster < 14f)
                    return false;

                /*
                 * Дальник с LOS тоже ломает recovery:
                 * реген не успеет начаться, если кастера продолжают простреливать.
                 */
                if (e.IsRanged && e.hasLineOfSight)
                {
                    float safeRange = e.weaponRange > 0f ? e.weaponRange + 2f : 24f;

                    if (e.distanceToCaster <= safeRange)
                        return false;
                }
            }

            return true;
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

        private bool ShouldEnterRecoveryMode(BattlefieldSnapshot snap)
        {
            if (caster == null || caster.health == null)
                return false;

            float hp = caster.health.summaryHealth.SummaryHealthPercent;

            /*
             * Жёсткий критический порог.
             * Сюда входим всегда.
             */
            if (hp <= 0.45f)
                return true;

            /*
             * Старое правило: если HP просел и есть опасное кровотечение —
             * пора отходить.
             */
            if (hp <= 0.58f && HasDangerousBleeding())
                return true;

            if (snap == null || snap.enemies == null || snap.enemies.Count == 0)
                return false;

            int closeMeleeOrAnimals = 0;
            int veryCloseMeleeOrAnimals = 0;
            bool dangerousCloseAnimal = false;
            bool dangerousCloseHumanMelee = false;

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

                /*
                 * Животное рядом — это реальная угроза, даже если у него threatScore не огромный.
                 * Медведи/пумы/ленивцы регулярно сбивают реген.
                 */
                if (e.IsAnimal && e.distanceToCaster <= 5f && e.threatScore >= 2f)
                    dangerousCloseAnimal = true;

                /*
                 * Wimp/Melee-гуманоид тоже может зацарапать кастера,
                 * особенно если бой затянулся и реген постоянно сбивается.
                 */
                if ((e.IsMelee || e.role == EnemyRole.Wimp) && e.distanceToCaster <= 2.5f)
                    dangerousCloseHumanMelee = true;
            }

            /*
             * Новый важный блок:
             * если кастер уже не фулловый и рядом есть контактная угроза —
             * не ждём 45% HP. Уходим раньше.
             */
            if (hp <= 0.82f && veryCloseMeleeOrAnimals >= 1)
                return true;

            if (hp <= 0.75f && closeMeleeOrAnimals >= 1)
                return true;

            if (hp <= 0.88f && dangerousCloseAnimal)
                return true;

            if (hp <= 0.78f && dangerousCloseHumanMelee)
                return true;

            /*
             * Если рядом сразу несколько ближников/животных —
             * это уже окружение, даже при неплохом HP.
             */
            if (hp <= 0.90f && closeMeleeOrAnimals >= 2)
                return true;

            return false;
        }

        private string GetRecoveryReasonForLog(BattlefieldSnapshot snap)
        {
            if (caster == null || caster.health == null)
                return "unknown";

            float hp = caster.health.summaryHealth.SummaryHealthPercent;

            if (hp <= 0.45f)
                return "criticalHp";

            if (hp <= 0.58f && HasDangerousBleeding())
                return "bleedingLowHp";

            if (snap == null || snap.enemies == null)
                return "unknown";

            int closeMeleeOrAnimals = 0;
            int veryCloseMeleeOrAnimals = 0;

            for (int i = 0; i < snap.enemies.Count; i++)
            {
                EnemyAssessment e = snap.enemies[i];

                if (e == null || e.pawn == null)
                    continue;

                bool meleeLike = e.IsAnimal || e.IsMelee || e.role == EnemyRole.Wimp;

                if (!meleeLike)
                    continue;

                if (e.distanceToCaster <= 6f)
                    closeMeleeOrAnimals++;

                if (e.distanceToCaster <= 2.5f)
                    veryCloseMeleeOrAnimals++;
            }

            if (hp <= 0.82f && veryCloseMeleeOrAnimals >= 1)
                return "veryCloseMeleePressure";

            if (hp <= 0.75f && closeMeleeOrAnimals >= 1)
                return "closeMeleePressure";

            if (hp <= 0.90f && closeMeleeOrAnimals >= 2)
                return "multipleMeleePressure";

            return "unknown";
        }

        private bool IsRecoveredEnough(BattlefieldSnapshot snap)
        {
            if (caster == null || caster.health == null)
                return false;

            float hp = caster.health.summaryHealth.SummaryHealthPercent;

            /*
             * Нормальный выход: почти восстановился.
             */
            if (hp >= 0.90f && !HasDangerousBleeding())
                return true;

            /*
             * Осторожный выход: достаточно восстановился и рядом нет давления.
             */
            if (hp >= 0.82f && !HasDangerousBleeding() && !HasRecoveryThreatPressure(snap))
                return true;

            /*
             * Важный фикс:
             * если конечность отрублена, SummaryHealthPercent может навсегда застрять
             * на 0.55-0.70. В таком случае нельзя требовать 0.82/0.90.
             *
             * Если давно не получал урон, реген доступен, рядом нет угрозы,
             * а HP не растёт до старого порога — считаем это плато и возвращаемся в бой.
             */
            if (IsRecoveryPlateauLikely() && !HasRecoveryThreatPressure(snap))
                return true;

            return false;
        }

        private bool IsRecoveryPlateauLikely()
        {
            if (caster == null || caster.health == null)
                return false;

            float hp = caster.health.summaryHealth.SummaryHealthPercent;

            if (hp < 0.55f)
                return false;

            HediffComp_PsycasterRestoringMechanisms restore = GetRestoringMechanisms();

            int ticksSinceDamage = restore != null ? restore.TicksSinceDamage : -1;

            /*
             * Если реген доступен и очень давно не было урона,
             * но HP всё ещё не выше 0.82, скорее всего это не временная рана,
             * а потерянная конечность / permanent cap.
             */
            if (restore != null &&
                restore.CanRegenerateNow &&
                ticksSinceDamage >= 1800 &&
                hp >= 0.55f)
            {
                return true;
            }

            /*
             * Дополнительный safety:
             * если recovery длится уже очень долго и HP хотя бы не критический,
             * не держим AI в вечном бегстве.
             */
            if (recoveryStartedTick > 0)
            {
                int recoveryDuration = Find.TickManager.TicksGame - recoveryStartedTick;

                if (recoveryDuration >= 2400 && hp >= 0.55f)
                    return true;
            }

            return false;
        }

        private bool HasRecoveryThreatPressure(BattlefieldSnapshot snap)
        {
            if (snap == null || snap.enemies == null || snap.enemies.Count == 0)
                return false;

            int closeMeleeOrAnimals = 0;
            int rangedLos = 0;

            for (int i = 0; i < snap.enemies.Count; i++)
            {
                EnemyAssessment e = snap.enemies[i];

                if (e == null || e.pawn == null)
                    continue;

                if (e.pawn.Destroyed || e.pawn.Dead || e.pawn.Downed || !e.pawn.Spawned)
                    continue;

                bool meleeLike = e.IsAnimal || e.IsMelee || e.role == EnemyRole.Wimp;

                if (meleeLike && e.distanceToCaster <= 10f)
                    closeMeleeOrAnimals++;

                if (e.IsRanged && e.hasLineOfSight && e.canShootNow)
                    rangedLos++;
            }

            if (closeMeleeOrAnimals > 0)
                return true;

            if (rangedLos > 0)
                return true;

            if (snap.totalIncomingDps >= 10f)
                return true;

            return false;
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

        private bool TryFindRecoverySkipCell(BattlefieldSnapshot snap, out IntVec3 result)
        {
            result = IntVec3.Invalid;

            if (snap == null || caster == null || caster.Map == null)
                return false;

            Map map = caster.Map;

            IntVec3 best = IntVec3.Invalid;
            float bestScore = float.MinValue;

            int radius = Mathf.RoundToInt(PsycasterTuning.SkipRangeMax);

            for (int i = 0; i < 220; i++)
            {
                IntVec3 cell;

                bool found = CellFinder.TryFindRandomCellNear(
                    caster.Position,
                    map,
                    radius,
                    c => c.InBounds(map)
                         && c.Standable(map)
                         && c.GetFirstPawn(map) == null
                         && c.DistanceToEdge(map) >= MapEdgeDangerDistance
                         && caster.CanReach(c, PathEndMode.OnCell, Danger.Deadly),
                    out cell);

                if (!found)
                    continue;

                float distFromCaster = cell.DistanceTo(caster.Position);

                /*
                 * Skip ради recovery должен реально отрывать дистанцию.
                 */
                if (distFromCaster < 10f)
                    continue;

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
                 * Recovery Skip должен создать пространство для регена.
                 */
                if (nearestMeleeOrAnimal < 18f)
                    continue;

                if (nearestAnyEnemy < 14f)
                    continue;

                float score = 0f;

                if (nearestMeleeOrAnimal < 999f)
                    score += nearestMeleeOrAnimal * 3.4f;

                if (nearestAnyEnemy < 999f)
                    score += nearestAnyEnemy * 1.5f;

                score += distFromCaster * 0.35f;

                /*
                 * Не прыгать под стрелков.
                 */
                score -= visibleShooters * 28f;

                if (nearestRangedLos < 26f)
                    score -= (26f - nearestRangedLos) * 4.5f;

                /*
                 * Главное отличие от старой логики:
                 * Skip не должен уводить VIP к краю карты.
                 */
                score -= GetMapEdgePenalty(cell, map);

                /*
                 * Мягкий leash: в emergency/recovery Skip можно уйти дальше,
                 * но не бесконечно наружу.
                 */
                score -= GetSoftHomePenalty(cell, true);

                /*
                 * Если клетка хотя бы немного возвращает к HomeAnchor — это плюс.
                 */
                if (HomeAnchor.IsValid)
                {
                    float currentHomeDist = caster.Position.DistanceTo(HomeAnchor);
                    float newHomeDist = cell.DistanceTo(HomeAnchor);

                    if (newHomeDist < currentHomeDist)
                        score += 14f;
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
