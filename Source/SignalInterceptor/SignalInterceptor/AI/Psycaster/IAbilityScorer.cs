using Verse;

namespace SignalInterceptor.AI.Psycaster
{
    /// <summary>
    /// Один скорер = одна способность пси-кастера.
    /// Каждый тик action-select PsycasterBrain пробегает по списку всех скореров,
    /// у каждого спрашивает «какой у тебя сейчас лучший вариант и какой score»,
    /// и выбирает максимум.
    ///
    /// Скорер обязан быть stateless: всё состояние читает из BattlefieldSnapshot
    /// и из переданного контекста PsycasterBrain. Это позволяет создавать
    /// все скореры один раз в конструкторе Brain и переиспользовать.
    /// </summary>
    public interface IAbilityScorer
    {
        /// <summary>
        /// DefName способности. Должен совпадать с AbilityDef в RimWorld
        /// (например "BlindingPulse", "Skip", "Stun").
        /// Используется для логов, debug overlay и soft-cooldown трекинга.
        /// </summary>
        string AbilityDefName { get; }

        /// <summary>
        /// Применима ли способность вообще на этом пси-кастере прямо сейчас.
        /// Проверяет: пешка обладает способностью, не на cooldown, есть psyfocus, не на soft-cooldown.
        /// Если возвращает false — Score не вызывается.
        /// </summary>
        bool IsAvailable(PsycasterBrain brain, BattlefieldSnapshot snap);

        /// <summary>
        /// Оценить способность для текущего snapshot и заполнить ScoredAction.
        /// Если способность не имеет хорошей цели в этом тике — вернуть score = 0
        /// (или просто не заполнять action.target — тогда action не выберется).
        ///
        /// Контракт:
        /// - score должен быть >= 0. Отрицательные значения трактуются как 0.
        /// - score уже умножен на множитель текущей стансы (получается из PsycasterTuning).
        /// - target / cellTarget — что именно кастовать.
        ///
        /// Реализация должна быть быстрой (вызывается ~ 60-120 тиков). Никаких pathfinding-ов.
        /// </summary>
        ScoredAction Score(PsycasterBrain brain, BattlefieldSnapshot snap);
    }

    /// <summary>
    /// Результат оценки одной способности скорером.
    /// Используется для сравнения вариантов и для последующего execute.
    /// </summary>
    public class ScoredAction
    {
        /// <summary>DefName способности (для логов и cooldown-трекинга).</summary>
        public string abilityDefName;

        /// <summary>Численная оценка. Чем выше — тем приоритетнее. 0 = «не подходит».</summary>
        public float score;

        /// <summary>Цель-пешка, если способность таргетится в пешку (Stun, Berserk, Skip-врага).</summary>
        public Pawn targetPawn;

        /// <summary>Целевая клетка, если способность таргетится в точку (BlindingPulse, Wallraise, Skip-self).</summary>
        public IntVec3 targetCell = IntVec3.Invalid;

        /// <summary>
        /// Целевая клетка-«destination» для двух-target способностей (Skip: target = враг, destination = куда телепортнуть).
        /// IntVec3.Invalid если не используется.
        /// </summary>
        public IntVec3 destinationCell = IntVec3.Invalid;

        /// <summary>
        /// Тип таргетинга — определяет, какой именно TryCast* метод дёргать в Brain.
        /// </summary>
        public ScoredActionTargetType targetType;

        /// <summary>
        /// Сколько тиков занимает warmup этой способности. После успешного каста
        /// Brain ставит NextActionTick = currentTick + castWarmupTicks + небольшой запас.
        /// </summary>
        public int castWarmupTicks;

        /// <summary>
        /// Опциональный текстовый комментарий для debug overlay: «BerserkPulse: 4 цели в кластере».
        /// Не влияет на логику.
        /// </summary>
        public string debugReason;

        /// <summary>
        /// Пустой ScoredAction со score = 0. Используется как «нет подходящего варианта».
        /// </summary>
        public static ScoredAction None
        {
            get { return new ScoredAction { score = 0f }; }
        }

        public bool IsValid
        {
            get { return score > 0f && !string.IsNullOrEmpty(abilityDefName); }
        }
    }

    /// <summary>
    /// Способ таргетинга способности — определяет, какой execute-метод дёргать в Brain.
    /// </summary>
    public enum ScoredActionTargetType
    {
        /// <summary>На себя. Использует TryCastSelfPsyAbility.</summary>
        Self = 0,

        /// <summary>На вражескую пешку. Использует TryCastPsyAbilityControlled.</summary>
        Pawn = 1,

        /// <summary>В клетку. Использует TryCastPsyAbilityAtCellControlled.</summary>
        Cell = 2,

        /// <summary>Skip-подобный каст: target = пешка, destination = клетка. Использует TryCastPsyAbilityToDestination.</summary>
        PawnToDestination = 3
    }
}
