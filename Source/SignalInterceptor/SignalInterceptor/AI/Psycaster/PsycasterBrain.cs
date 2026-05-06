using RimWorld;
using System.Collections.Generic;
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

                if (homeDist > MaxHomeDistance)
                {
                    Job goHome = JobMaker.MakeJob(JobDefOf.Goto, HomeAnchor);
                    goHome.locomotionUrgency = LocomotionUrgency.Sprint;
                    caster.jobs.StartJob(goHome, JobCondition.InterruptForced);

                    nextActionSelectTick = Find.TickManager.TicksGame + 90;

                    Log.Message("[Signal Interceptor] Psycaster too far from anchor (d="
                                + homeDist.ToString("F1") + "), returning home " + HomeAnchor);

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

            if (!snap.HasEnemies)
            {
                pendingMeleeTargetThingId = -1;
                pendingMeleeUntilTick = -1;
                pendingMeleeReason = null;

                gc.TryAttackPlayerShuttleOrBuilding_Public(caster, caster.Map);
                return;
            }

            // Самый важный блок:
            // pending melee после Stun/Skip/Beckon исполняется ДО stance/action логики.
            // Это убирает тупой провис после Stun.
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

            for (int i = 0; i < snap.enemies.Count; i++)
            {
                EnemyAssessment e = snap.enemies[i];

                if (e == null || e.pawn == null)
                    continue;

                if (e.pawn.thingIDNumber == pendingMeleeTargetThingId)
                {
                    target = e.pawn;
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

            gc.InterruptBadPsycasterCombatJob_Public(caster, target);

            bool started = gc.TryForcePsycasterMeleeAttack_Public(caster, target);

            if (started)
            {
                nextActionSelectTick = now + 30;

                Log.Message("[Signal Interceptor] Psycaster pending-melee: "
                            + target.LabelShort
                            + " | reason=" + (pendingMeleeReason ?? "unknown")
                            + " | d=" + caster.Position.DistanceTo(target.Position).ToString("F1"));

                // Не очищаем pending сразу, если цель ещё не рядом.
                // Пусть несколько быстрых тиков подряд удерживают AttackMelee,
                // пока RimWorld job нормально зацепит движущуюся цель.
                if (caster.Position.DistanceTo(target.Position) <= 1.8f)
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

                    if (n == "Beckon" || n == "Skip" || n == "ChaosSkip")
                    {
                        MarkPawnRecentlyMoved(action.targetPawn, 600);
                        QueuePendingMelee(action.targetPawn, 360, n);
                    }

                    if (n == "Stun")
                    {
                        MarkPawnRecentlyMoved(action.targetPawn, 240);

                        // Главный фикс:
                        // после Stun не ждём обычный action-select.
                        // Как только warmup закончится, Tick() мгновенно выдаст AttackMelee.
                        QueuePendingMelee(action.targetPawn, 300, "Stun");
                    }
                }

                ApplySoftCooldown(action.abilityDefName);

                int warmup = action.castWarmupTicks > 0
                    ? action.castWarmupTicks
                    : PsycasterTuning.CastWarmupMedium;

                int extraDelay = 30;

                if (action.abilityDefName == "Stun")
                {
                    // Да, фактически убираем пост-задержку.
                    // Warmup самого Stun уже учтён. После него pending-melee заберёт управление.
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

            // В 1v1 fallback никогда не должен отступать от уже выбранной цели.
            // Если скореры ничего умного не нашли — бей/преследуй.
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
        public const float MaxHomeDistance = 85f;
    }
}
