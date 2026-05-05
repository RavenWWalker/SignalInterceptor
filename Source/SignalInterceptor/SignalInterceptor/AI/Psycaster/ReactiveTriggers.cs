using Verse;

namespace SignalInterceptor.AI.Psycaster
{
    /// <summary>
    /// Отслеживает критические события, которые требуют немедленной переоценки решения
    /// (то есть прерывания текущего «доверия предыдущему действию»).
    ///
    /// Принципы:
    /// - Триггеров мало, и каждый тщательно выверен.
    /// - Триггер срабатывает один раз — после срабатывания флаг сбрасывается, чтобы не зацикливаться.
    /// - Триггер вызывает StanceSelector + ActionSelect, но не сам выбирает действие.
    ///
    /// Состояние — поля экземпляра, не статика. Один экземпляр на пси-кастера.
    /// Не сериализуется (как и весь Brain).
    /// </summary>
    public class ReactiveTriggers
    {
        // ============================================================
        // Сохранённое предыдущее состояние для сравнения
        // ============================================================

        private float lastKnownHpFraction = 1f;
        private int lastHpSampleTick = -1;
        private float hpDropAccumulator = 0f;
        private int hpAccumulatorWindowEndTick = -1;

        private int lastKnownAdjacentEnemies = 0;
        private bool wasInLineOfSightOfRanged = false;

        // ============================================================
        // Главный метод: вызывается каждый тик из PsycasterBrain.Tick.
        // Возвращает true, если нужно ПРЯМО СЕЙЧАС переоценить стансу и действие.
        // ============================================================

        /// <summary>
        /// Проверить все триггеры. Вернуть true, если хотя бы один сработал.
        /// При срабатывании внутреннее состояние обновляется так, чтобы
        /// тот же триггер не сработал на следующем тике на тех же данных.
        /// </summary>
        public bool ShouldInterrupt(BattlefieldSnapshot snap)
        {
            if (snap == null || snap.caster == null)
                return false;

            bool triggered = false;

            if (CheckSuddenDamage(snap))
                triggered = true;

            if (CheckCriticalHp(snap))
                triggered = true;

            if (CheckEngulfTransition(snap))
                triggered = true;

            if (CheckRangedLineOfSightAcquired(snap))
                triggered = true;

            // Обновляем сохранённое состояние ПОСЛЕ проверок,
            // чтобы все триггеры этого тика сравнивались с одной и той же базой.
            UpdateBaseline(snap);

            return triggered;
        }

        /// <summary>
        /// Сбросить состояние триггеров. Вызывается при создании Brain
        /// и при существенных изменениях контекста (например, пси-кастер заспавнен заново).
        /// </summary>
        public void Reset()
        {
            lastKnownHpFraction = 1f;
            lastHpSampleTick = -1;
            hpDropAccumulator = 0f;
            hpAccumulatorWindowEndTick = -1;

            lastKnownAdjacentEnemies = 0;
            wasInLineOfSightOfRanged = false;
        }

        // ============================================================
        // Конкретные триггеры
        // ============================================================

        /// <summary>
        /// Триггер «внезапный урон»: если за окно SuddenDamageWindowTicks
        /// потеряно больше SuddenDamageThresholdFraction HP — переоценить.
        /// Использует скользящее окно: при выходе за окно аккумулятор обнуляется.
        /// </summary>
        private bool CheckSuddenDamage(BattlefieldSnapshot snap)
        {
            int tick = snap.currentTick;

            // Первый тик — просто запоминаем, не триггерим.
            if (lastHpSampleTick < 0)
                return false;

            // Если окно истекло — обнуляем аккумулятор и начинаем новое.
            if (tick > hpAccumulatorWindowEndTick)
            {
                hpDropAccumulator = 0f;
                hpAccumulatorWindowEndTick = tick + PsycasterTuning.SuddenDamageWindowTicks;
            }

            float drop = lastKnownHpFraction - snap.casterHpFraction;
            if (drop > 0f)
                hpDropAccumulator += drop;

            if (hpDropAccumulator >= PsycasterTuning.SuddenDamageThresholdFraction)
            {
                // Сбрасываем аккумулятор, чтобы не триггерить повторно.
                hpDropAccumulator = 0f;
                hpAccumulatorWindowEndTick = tick + PsycasterTuning.SuddenDamageWindowTicks;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Триггер «критический HP»: при первом пересечении CriticalHpFraction вниз
        /// — мгновенно переоценить (это вход в Survive).
        /// Дальнейшее падение в той же зоне триггер не вызывает (StanceSelector уже в Survive).
        /// </summary>
        private bool CheckCriticalHp(BattlefieldSnapshot snap)
        {
            bool wasAboveCritical = lastKnownHpFraction >= PsycasterTuning.CriticalHpFraction;
            bool isBelowCritical = snap.casterHpFraction < PsycasterTuning.CriticalHpFraction;

            return wasAboveCritical && isBelowCritical;
        }

        /// <summary>
        /// Триггер «вошли в Engulfed»: на прошлом тике рядом было меньше EngulfedMinEnemies,
        /// а сейчас — достаточно. Это значит, что кто-то добежал в ближний бой.
        /// </summary>
        private bool CheckEngulfTransition(BattlefieldSnapshot snap)
        {
            int currentAdjacent = snap.enemiesAdjacent != null ? snap.enemiesAdjacent.Count : 0;

            bool wasNotEngulfed = lastKnownAdjacentEnemies < PsycasterTuning.EngulfedMinEnemies;
            bool isEngulfedNow = currentAdjacent >= PsycasterTuning.EngulfedMinEnemies;

            return wasNotEngulfed && isEngulfedNow;
        }

        /// <summary>
        /// Триггер «появился дальник с LOS»: только что в LOS появился вооружённый дальник.
        /// Это значит, что мы могли расслабиться, а теперь нужно срочно решить — Skip, Smokepop, Wallraise.
        /// </summary>
        private bool CheckRangedLineOfSightAcquired(BattlefieldSnapshot snap)
        {
            bool isInLosNow = snap.IsUnderRangedFire;
            return !wasInLineOfSightOfRanged && isInLosNow;
        }

        /// <summary>
        /// Обновить «прошлое известное состояние» в конце тика.
        /// </summary>
        private void UpdateBaseline(BattlefieldSnapshot snap)
        {
            lastKnownHpFraction = snap.casterHpFraction;
            lastHpSampleTick = snap.currentTick;
            lastKnownAdjacentEnemies = snap.enemiesAdjacent != null ? snap.enemiesAdjacent.Count : 0;
            wasInLineOfSightOfRanged = snap.IsUnderRangedFire;

            // Инициализация окна аккумулятора при первом запуске.
            if (hpAccumulatorWindowEndTick < 0)
                hpAccumulatorWindowEndTick = snap.currentTick + PsycasterTuning.SuddenDamageWindowTicks;
        }
    }
}
