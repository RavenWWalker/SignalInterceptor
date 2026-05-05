using RimWorld;
using Verse;

namespace SignalInterceptor.AI.Psycaster
{
    /// <summary>
    /// База для скорерров. Реализует IsAvailable через проверку наличия способности,
    /// её cooldown, psyfocus и soft-cooldown в Brain.
    ///
    /// Конкретные скореры переопределяют:
    /// - AbilityDefName
    /// - StanceMultiplier — множитель из PsycasterTuning для текущей стансы
    /// - ScoreInternal — собственно оценка
    ///
    /// IsAvailable делает per-ability-cost базу, дальше дочерние классы добавляют
    /// свои условия через переопределение IsContextuallyAvailable.
    /// </summary>
    public abstract class AbilityScorerBase : IAbilityScorer
    {
        public abstract string AbilityDefName { get; }

        /// <summary>
        /// Множитель из PsycasterTuning для конкретной (стансы * способности).
        /// Дочерний класс возвращает один из W_Stance_Ability констант в зависимости от snap.
        /// </summary>
        protected abstract float StanceMultiplier(PsycasterStance stance);

        /// <summary>
        /// Собственная оценка скорера: «сколько score без учёта стансы».
        /// Возвращает ScoredAction, в котором НЕ умножен StanceMultiplier — Score() сделает это сам.
        /// Может вернуть ScoredAction.None, если подходящей цели нет.
        /// </summary>
        protected abstract ScoredAction ScoreInternal(PsycasterBrain brain, BattlefieldSnapshot snap);

        /// <summary>
        /// Дополнительные ситуативные проверки. Например: BerserkPulse требует AoE-цель,
        /// если её нет — IsAvailable вернёт false ещё до вызова Score.
        /// По умолчанию — возвращает true.
        /// </summary>
        protected virtual bool IsContextuallyAvailable(PsycasterBrain brain, BattlefieldSnapshot snap)
        {
            return true;
        }

        public virtual bool IsAvailable(PsycasterBrain brain, BattlefieldSnapshot snap)
        {
            if (brain == null || snap == null || snap.caster == null)
                return false;

            // Способность есть у пешки?
            AbilityDef def = brain.GetAbilityDef(AbilityDefName);
            if (def == null)
                return false;

            object ability = brain.GetPawnAbilityObject(def);
            if (ability == null)
                return false;

            // На жёстком cooldown?
            if (brain.IsAbilityOnCooldown(ability))
                return false;

            // Soft cooldown в Brain (наш собственный, чтобы не спамить)?
            if (brain.IsOnSoftCooldown(AbilityDefName))
                return false;

            // Хватит ли psyfocus? Используем PsychicEntropy.PsyfocusToHediffsThresholds — проще
            // спросить у самой ability, может ли она быть применена. Но это дорого, оставим
            // на ScoreInternal: если способность не сможет — CanApplyOn вернёт false на каст
            // и мы потеряем тик. Здесь делаем грубую отсечку по psyfocus.
            if (snap.casterPsyfocus < MinPsyfocusFraction)
                return false;

            return IsContextuallyAvailable(brain, snap);
        }

        /// <summary>
        /// Минимальная доля psyfocus, при которой имеет смысл вообще пытаться кастовать.
        /// 0.05 — почти всегда true, но отсекает совсем нулевой фокус.
        /// Отдельные скореры (например Focus) переопределяют до 0.0.
        /// </summary>
        protected virtual float MinPsyfocusFraction
        {
            get { return 0.05f; }
        }

        public ScoredAction Score(PsycasterBrain brain, BattlefieldSnapshot snap)
        {
            if (brain == null || snap == null)
                return ScoredAction.None;

            ScoredAction action = ScoreInternal(brain, snap);

            if (action == null || action.score <= 0f)
                return ScoredAction.None;

            // Применяем множитель текущей стансы.
            float multiplier = StanceMultiplier(brain.CurrentStance);
            action.score *= multiplier;

            // Если множитель стансы 0 — способность не используется в этой стансе вообще.
            if (action.score <= 0f)
                return ScoredAction.None;

            // Подставляем имя способности и warmup, если скорер их не заполнил.
            if (string.IsNullOrEmpty(action.abilityDefName))
                action.abilityDefName = AbilityDefName;

            return action;
        }
    }
}
