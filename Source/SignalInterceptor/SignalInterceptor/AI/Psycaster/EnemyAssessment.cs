using Verse;

namespace SignalInterceptor.AI.Psycaster
{
    /// <summary>
    /// Оценка одного враждебного юнита с точки зрения пси-кастера.
    /// Создаётся один раз при сборке BattlefieldSnapshot, не пересчитывается в течение тика.
    /// Все скореры способностей читают именно эту структуру, а не саму Pawn.
    /// </summary>
    public class EnemyAssessment
    {
        /// <summary>Сама пешка. Хранится без Scribe, нужна для каста и движения.</summary>
        public Pawn pawn;

        /// <summary>Тактическая роль (см. EnemyRole).</summary>
        public EnemyRole role;

        /// <summary>Расстояние до пси-кастера в клетках на момент сборки snapshot.</summary>
        public float distanceToCaster;

        /// <summary>Есть ли LOS от пси-кастера до этой пешки.</summary>
        public bool hasLineOfSight;

        /// <summary>Может ли пешка стрелять прямо сейчас (есть оружие, не оглушена, не паникует).</summary>
        public bool canShootNow;

        /// <summary>Эффективная дальность оружия в клетках. 0 если безоружен.</summary>
        public float weaponRange;

        /// <summary>
        /// Грубая оценка DPS пешки в моменте. Считается из верба основного оружия:
        /// damage * burst / (warmup + cooldown) * частота попаданий.
        /// Используется как «вес угрозы» при выборе цели для контроля.
        /// </summary>
        public float estimatedDps;

        /// <summary>Доля текущего HP от максимального [0..1]. 0.3 = «почти даун».</summary>
        public float hpFraction;

        /// <summary>Оглушена ли пешка прямо сейчас.</summary>
        public bool isStunned;

        /// <summary>Уже под Berserk / Manhunter / Vertigo (бьёт своих или дезориентирована).</summary>
        public bool isMindControlled;

        /// <summary>В укрытии (есть covered cell между ним и пси-кастером).</summary>
        public bool isInCover;

        /// <summary>
        /// Сколько других вражеских пешек находится в радиусе ClusterRadius от этой.
        /// Используется для оценки качества AoE по этой цели.
        /// </summary>
        public int alliesInClusterRadius;

        /// <summary>
        /// Итоговая «угроза» этой пешки для пси-кастера прямо сейчас.
        /// Комбинация estimatedDps, distance, hasLineOfSight и роли.
        /// Чем выше — тем приоритетнее контроль.
        /// </summary>
        public float threatScore;

        // ============================================================
        // Удобные предикаты — обёртки для читаемости в скорерах.
        // ============================================================

        public bool IsHumanlike
        {
            get { return pawn != null && pawn.RaceProps != null && pawn.RaceProps.Humanlike; }
        }

        public bool IsAnimal
        {
            get { return role == EnemyRole.Animal; }
        }

        public bool IsMechanoid
        {
            get { return role == EnemyRole.Mechanoid; }
        }

        public bool IsRanged
        {
            get { return role == EnemyRole.Ranged || role == EnemyRole.Sniper || role == EnemyRole.Heavy; }
        }

        public bool IsMelee
        {
            get { return role == EnemyRole.Melee; }
        }

        /// <summary>
        /// Восприимчив ли к псионическим mind-контрол способностям (Berserk, BerserkPulse, BlindingPulse, VertigoPulse).
        /// Механоиды и животные — нет (механоиды иммунны, животные не реагируют на Berserk на разум).
        /// На самом деле BlindingPulse/VertigoPulse работают и на животных — это уточняем в каждом скорере.
        /// </summary>
        public bool IsSusceptibleToMindControl
        {
            get { return IsHumanlike; }
        }
    }
}
