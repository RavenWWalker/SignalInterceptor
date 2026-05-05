using System.Collections.Generic;

namespace SignalInterceptor.AI.Psycaster
{
    /// <summary>
    /// Выбирает стратегическую стансу пси-кастера на основе BattlefieldSnapshot.
    ///
    /// Алгоритм — приоритетная цепочка:
    /// 1. HP &lt; критического → Survive (overrides everything).
    /// 2. Окружили в ближнем бою → Engulfed.
    /// 3. Изолированная слабая цель + невидимка готова → Hunt.
    /// 4. 4+ врагов кластером → CrowdControl.
    /// 5. 2+ ближников рядом → Disengage.
    /// 6. 1+ дальник в LOS → Kite.
    /// 7. Бой ещё не начат → Opening.
    ///
    /// Stateless. Принимает snap и текущую стансу (для гистерезиса), возвращает новую.
    /// </summary>
    public static class StanceSelector
    {
        /// <summary>
        /// Выбрать стансу на основе snapshot. previousStance используется для гистерезиса:
        /// например, чтобы выйти из Survive нужен HP выше SurviveExitHpFraction, а не просто выше Critical.
        /// Это предотвращает «дёргание» между двумя стансами на границе порога.
        /// </summary>
        public static PsycasterStance Select(BattlefieldSnapshot snap, PsycasterStance previousStance)
        {
            if (snap == null || snap.caster == null)
                return PsycasterStance.Opening;

            // ---------- 1. Survive: критический HP ----------
            // Гистерезис: если уже в Survive — выходим только при подъёме HP до SurviveExit.
            if (previousStance == PsycasterStance.Survive)
            {
                if (snap.casterHpFraction < PsycasterTuning.SurviveExitHpFraction)
                    return PsycasterStance.Survive;
            }
            else
            {
                if (snap.casterHpFraction < PsycasterTuning.CriticalHpFraction)
                    return PsycasterStance.Survive;
            }

            // ---------- 2. Engulfed: окружили в ближнем бою, HP пока ОК ----------
            int adjacent = snap.enemiesAdjacent != null ? snap.enemiesAdjacent.Count : 0;
            if (adjacent >= PsycasterTuning.EngulfedMinEnemies)
                return PsycasterStance.Engulfed;

            // ---------- 3. Hunt: есть слабый одиночка, можно подкрасться ----------
            if (HasIsolatedHuntableTarget(snap))
                return PsycasterStance.Hunt;

            // ---------- 4. CrowdControl: большой кластер ----------
            if (snap.largestClusterSize >= PsycasterTuning.CrowdControlMinEnemies)
                return PsycasterStance.CrowdControl;

            // ---------- 5. Disengage: 2+ ближников рядом, но не «engulfed» ----------
            int closeMelee = CountCloseMelee(snap, 6.0f);
            if (closeMelee >= 2)
                return PsycasterStance.Disengage;

            // ---------- 6. Kite: есть дальник с LOS ----------
            if (snap.IsUnderRangedFire || HasRangedThreatInLos(snap))
                return PsycasterStance.Kite;

            // ---------- 7. Opening: бой ещё не активен или враги далеко ----------
            return PsycasterStance.Opening;
        }

        /// <summary>
        /// Есть ли изолированная цель, которая годится для Hunt:
        /// одиночка с низким HP в стороне от своих, до которой можно дойти невидимкой.
        /// </summary>
        private static bool HasIsolatedHuntableTarget(BattlefieldSnapshot snap)
        {
            if (snap.enemies == null)
                return false;

            for (int i = 0; i < snap.enemies.Count; i++)
            {
                EnemyAssessment e = snap.enemies[i];
                if (e == null || e.pawn == null)
                    continue;

                // Низкий HP — кандидат на добивание.
                bool isWeak = e.hpFraction < 0.5f;

                // Одиночка — нет союзников рядом.
                bool isIsolated = e.alliesInClusterRadius == 0;

                // Не слишком далеко.
                bool isReachable = e.distanceToCaster <= 30f;

                // Это не механоид (механоиды слишком толстые для Hunt-комбо).
                bool isVulnerable = !e.IsMechanoid;

                if (isWeak && isIsolated && isReachable && isVulnerable)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Сколько ближников находится в радиусе maxDistance клеток от пси-кастера.
        /// </summary>
        private static int CountCloseMelee(BattlefieldSnapshot snap, float maxDistance)
        {
            if (snap.meleeEnemies == null)
                return 0;

            int count = 0;
            for (int i = 0; i < snap.meleeEnemies.Count; i++)
            {
                EnemyAssessment e = snap.meleeEnemies[i];
                if (e != null && e.distanceToCaster <= maxDistance)
                    count++;
            }
            return count;
        }

        /// <summary>
        /// Есть ли хотя бы один дальник с LOS до пси-кастера в snapshot.
        /// Дополняет IsUnderRangedFire тем, что ловит и невооружённых-сейчас, но потенциально опасных.
        /// </summary>
        private static bool HasRangedThreatInLos(BattlefieldSnapshot snap)
        {
            if (snap.rangedEnemies == null)
                return false;

            for (int i = 0; i < snap.rangedEnemies.Count; i++)
            {
                EnemyAssessment e = snap.rangedEnemies[i];
                if (e == null)
                    continue;

                if (e.hasLineOfSight && e.distanceToCaster <= PsycasterTuning.StandardPulseRange + 2f)
                    return true;
            }

            return false;
        }
    }
}
