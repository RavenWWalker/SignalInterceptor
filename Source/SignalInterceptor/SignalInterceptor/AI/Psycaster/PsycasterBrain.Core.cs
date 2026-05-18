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
    public partial class PsycasterBrain
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
        private int pendingMeleeLastLogTargetThingId = -1;
        private string pendingMeleeLastDistanceZone = null;
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
        private int nextSoftLeashSuppressedLogTick = -1;

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
            scorers.Add(new Scorer_BulletShield());
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
        // Хелперы для подмодулей (см. partial-файлы).
        // ============================================================

        public IntVec3 HomeAnchor = IntVec3.Invalid;
        public const float MaxHomeDistance = 120f;
    }
}
