namespace SignalInterceptor.AI.Psycaster
{
    /// <summary>
    /// Все магические числа AI пси-кастера. Собраны в одном месте, чтобы балансить
    /// поведение без правки кода логики. Все значения — const или static readonly,
    /// чтобы JIT их инлайнил.
    ///
    /// Принципы:
    /// - Тики: 60 тиков = 1 игровая секунда.
    /// - Дистанции: в клетках карты. Range 25 ~= размер видимости снайпера.
    /// - HP / psyfocus: в долях [0..1], где 1 = полное.
    /// </summary>
    public static class PsycasterTuning
    {
        // ============================================================
        // Временные горизонты AI
        // ============================================================

        /// <summary>Минимальный интервал между «переоценкой стансы». Пересчёт чаще нельзя.</summary>
        public const int StanceReevalMinTicks = 240;

        /// <summary>Максимальный интервал между «переоценкой стансы».</summary>
        public const int StanceReevalMaxTicks = 500;

        /// <summary>Минимальный интервал между выбором действия (action selection).</summary>
        public const int ActionSelectMinTicks = 60;

        /// <summary>Максимальный интервал между выбором действия.</summary>
        public const int ActionSelectMaxTicks = 120;

        /// <summary>
        /// Задержка перед первым тиком AI после загрузки сейва — «пси-кастер приходит в себя».
        /// Это компенсирует обнуление cooldown-таймеров, которое случается из-за того,
        /// что Brain не сериализуется.
        /// </summary>
        public const int PostLoadGraceTicks = 300;

        // ============================================================
        // Триггеры реактивного прерывания (Reactive Interrupt)
        // ============================================================

        /// <summary>
        /// Доля HP, потеря которой за окно SuddenDamageWindowTicks вызовет переоценку стансы.
        /// 0.15 = 15% MaxHP за раз/окно.
        /// </summary>
        public const float SuddenDamageThresholdFraction = 0.15f;

        /// <summary>Окно, в котором считается «внезапный урон» для триггера.</summary>
        public const int SuddenDamageWindowTicks = 180;

        /// <summary>HP-доля ниже которой принудительно входим в Survive независимо от других условий.</summary>
        public const float CriticalHpFraction = 0.35f;

        /// <summary>HP-доля при которой Survive завершается, если враги отдалились.</summary>
        public const float SurviveExitHpFraction = 0.55f;

        // ============================================================
        // Восприятие: пороги классификации врагов
        // ============================================================

        /// <summary>Дальность оружия, начиная с которой враг считается Sniper.</summary>
        public const float SniperRangeThreshold = 25.0f;

        /// <summary>Дальность оружия, начиная с которой враг считается Heavy (если не пси-кастер).</summary>
        public const float HeavyWeaponMassThreshold = 5.0f;

        /// <summary>Радиус, в котором считаем врагов «вокруг пси-кастера» для Engulfed.</summary>
        public const float EngulfedRadius = 2.2f;

        /// <summary>Минимальное число врагов в EngulfedRadius для входа в Engulfed.</summary>
        public const int EngulfedMinEnemies = 2;

        /// <summary>Радиус кластеризации врагов для AoE (Berserk Pulse, Vertigo Pulse).</summary>
        public const float ClusterRadius = 4.0f;

        /// <summary>Минимальный размер кластера для CrowdControl стансы.</summary>
        public const int CrowdControlMinEnemies = 4;

        // ============================================================
        // Дистанции пси-кастера
        // ============================================================

        /// <summary>Идеальная дистанция кайтинга — по этой дистанции AI центрирует свою позицию.</summary>
        public const float KiteIdealDistance = 22.0f;

        /// <summary>Дистанция, ниже которой Skip-self в Kite срабатывает реактивно.</summary>
        public const float KiteEmergencySkipDistance = 8.0f;

        /// <summary>Стандартный максимум для пульсовых способностей.</summary>
        public const float StandardPulseRange = 24.9f;

        /// <summary>«Безопасный радиус» от себя — пульсы ближе не кастуем, чтобы себя не задеть.</summary>
        public const float SelfDamageSafeRadius = 4.0f;

        // ============================================================
        // Cooldown-периоды между однотипными кастами (мин..макс).
        // Это НЕ cooldown самой способности — это мягкий cap,
        // чтобы AI не спамил один и тот же приём.
        // ============================================================

        public const int BlindingPulseSoftCooldownMin = 900;
        public const int BlindingPulseSoftCooldownMax = 1200;

        public const int VertigoPulseSoftCooldownMin = 900;
        public const int VertigoPulseSoftCooldownMax = 1200;

        public const int BerserkPulseSoftCooldownMin = 1500;
        public const int BerserkPulseSoftCooldownMax = 2100;

        public const int InvisibilitySoftCooldownMin = 900;
        public const int InvisibilitySoftCooldownMax = 1500;

        public const int SmokepopSoftCooldownMin = 900;
        public const int SmokepopSoftCooldownMax = 1400;

        public const int WallraiseSoftCooldownMin = 1200;
        public const int WallraiseSoftCooldownMax = 1800;

        public const int SkipshieldSoftCooldownMin = 600;
        public const int SkipshieldSoftCooldownMax = 1200;

        public const int ManhunterPulseSoftCooldownMin = 1800;
        public const int ManhunterPulseSoftCooldownMax = 3000;

        public const int FocusSoftCooldownMin = 600;
        public const int FocusSoftCooldownMax = 900;

        // ============================================================
        // Веса скоров: множители для каждой способности в каждой стансе.
        // Финальный score = базовый_скор * множитель_стансы.
        // 1.0 = нейтрально, 2.0 = в этой стансе сильно поощряется,
        // 0.0 = в этой стансе не использовать.
        //
        // Индексы: [Stance, Ability] — обращение через статические константы.
        // ============================================================

        // Скоры — Opening
        public const float W_Opening_Focus = 3.0f;
        public const float W_Opening_BlindingPulse = 1.5f;
        public const float W_Opening_Stun = 1.2f;
        public const float W_Opening_Berserk = 0.8f;
        public const float W_Opening_BerserkPulse = 0.6f;
        public const float W_Opening_VertigoPulse = 0.6f;
        public const float W_Opening_Skip = 0.6f;
        public const float W_Opening_ChaosSkip = 0.5f;
        public const float W_Opening_MassChaosSkip = 0.8f;
        public const float W_Opening_ManhunterPulse = 1.0f;
        public const float W_Opening_Smokepop = 0.0f;
        public const float W_Opening_Wallraise = 0.4f;
        public const float W_Opening_Skipshield = 0.3f;
        public const float W_Opening_Invisibility = 0.0f;
        public const float W_Opening_Beckon = 0.5f;

        // Скоры — Kite
        public const float W_Kite_Focus = 0.5f;
        public const float W_Kite_BlindingPulse = 2.5f;
        public const float W_Kite_Stun = 1.8f;
        public const float W_Kite_Berserk = 1.0f;
        public const float W_Kite_BerserkPulse = 1.2f;
        public const float W_Kite_VertigoPulse = 1.5f;
        public const float W_Kite_Skip = 0.8f;
        public const float W_Kite_ChaosSkip = 1.0f;
        public const float W_Kite_MassChaosSkip = 1.0f;
        public const float W_Kite_ManhunterPulse = 1.5f;
        public const float W_Kite_Smokepop = 2.0f;
        public const float W_Kite_Wallraise = 0.4f;
        public const float W_Kite_Skipshield = 0.7f;
        public const float W_Kite_Invisibility = 1.0f;
        public const float W_Kite_Beckon = 0.6f;

        // Скоры — Disengage
        public const float W_Disengage_Focus = 0.0f;
        public const float W_Disengage_BlindingPulse = 1.5f;
        public const float W_Disengage_Stun = 2.0f;
        public const float W_Disengage_Berserk = 1.5f;
        public const float W_Disengage_BerserkPulse = 2.0f;
        public const float W_Disengage_VertigoPulse = 1.2f;
        public const float W_Disengage_Skip = 0.0f;
        public const float W_Disengage_ChaosSkip = 1.4f;
        public const float W_Disengage_MassChaosSkip = 1.2f;
        public const float W_Disengage_ManhunterPulse = 1.0f;
        public const float W_Disengage_Smokepop = 0.8f;
        public const float W_Disengage_Wallraise = 1.4f;
        public const float W_Disengage_Skipshield = 1.2f;
        public const float W_Disengage_Invisibility = 1.5f;
        public const float W_Disengage_Beckon = 0.0f;

        // Скоры — CrowdControl
        public const float W_CrowdControl_Focus = 0.0f;
        public const float W_CrowdControl_BlindingPulse = 1.5f;
        public const float W_CrowdControl_Stun = 0.5f;
        public const float W_CrowdControl_Berserk = 0.8f;
        public const float W_CrowdControl_BerserkPulse = 3.0f;
        public const float W_CrowdControl_VertigoPulse = 2.5f;
        public const float W_CrowdControl_Skip = 0.4f;
        public const float W_CrowdControl_ChaosSkip = 0.6f;
        public const float W_CrowdControl_MassChaosSkip = 1.6f;
        public const float W_CrowdControl_ManhunterPulse = 2.5f;
        public const float W_CrowdControl_Smokepop = 1.0f;
        public const float W_CrowdControl_Wallraise = 0.5f;
        public const float W_CrowdControl_Skipshield = 0.6f;
        public const float W_CrowdControl_Invisibility = 0.5f;
        public const float W_CrowdControl_Beckon = 0.4f;

        // Скоры — Hunt
        public const float W_Hunt_Focus = 0.5f;
        public const float W_Hunt_BlindingPulse = 0.8f;
        public const float W_Hunt_Stun = 3.0f;
        public const float W_Hunt_Berserk = 1.5f;
        public const float W_Hunt_BerserkPulse = 0.5f;
        public const float W_Hunt_VertigoPulse = 0.5f;
        public const float W_Hunt_Skip = 1.6f;
        public const float W_Hunt_ChaosSkip = 0.3f;
        public const float W_Hunt_MassChaosSkip = 0.3f;
        public const float W_Hunt_ManhunterPulse = 0.5f;
        public const float W_Hunt_Smokepop = 0.0f;
        public const float W_Hunt_Wallraise = 0.0f;
        public const float W_Hunt_Skipshield = 0.4f;
        public const float W_Hunt_Invisibility = 3.0f;
        public const float W_Hunt_Beckon = 1.9f;

        // Скоры — Engulfed
        public const float W_Engulfed_Focus = 0.0f;
        public const float W_Engulfed_BlindingPulse = 0.8f;
        public const float W_Engulfed_Stun = 1.0f;
        public const float W_Engulfed_Berserk = 0.5f;
        public const float W_Engulfed_BerserkPulse = 2.0f;
        public const float W_Engulfed_VertigoPulse = 1.0f;
        public const float W_Engulfed_Skip = 0.0f;
        public const float W_Engulfed_ChaosSkip = 1.2f;
        public const float W_Engulfed_MassChaosSkip = 2.0f;
        public const float W_Engulfed_ManhunterPulse = 1.5f;
        public const float W_Engulfed_Smokepop = 1.5f;
        public const float W_Engulfed_Wallraise = 0.6f;
        public const float W_Engulfed_Skipshield = 1.4f;
        public const float W_Engulfed_Invisibility = 2.5f;
        public const float W_Engulfed_Beckon = 0.0f;

        // Скоры — Survive
        public const float W_Survive_Focus = 0.0f;
        public const float W_Survive_BlindingPulse = 0.5f;
        public const float W_Survive_Stun = 0.3f;
        public const float W_Survive_Berserk = 0.0f;
        public const float W_Survive_BerserkPulse = 0.5f;
        public const float W_Survive_VertigoPulse = 0.3f;
        public const float W_Survive_Skip = 0.0f;
        public const float W_Survive_ChaosSkip = 1.5f;
        public const float W_Survive_MassChaosSkip = 1.6f;
        public const float W_Survive_ManhunterPulse = 1.0f;
        public const float W_Survive_Smokepop = 2.5f;
        public const float W_Survive_Wallraise = 1.6f;
        public const float W_Survive_Skipshield = 1.8f;
        public const float W_Survive_Invisibility = 4.0f;
        public const float W_Survive_Beckon = 0.0f;

        // ============================================================
        // Пачка 5: общие параметры мобильности
        // ============================================================

        public const float SkipRangeMax = 24.9f;
        public const float SkipMinTargetDistance = 8f;   // ближе — выгоднее ChaosSkip
        public const float ChaosSkipMaxDistance = 10f;  // используем когда враг подошёл близко
        public const float BeckonMinDistance = 6f;
        public const float BeckonMaxDistance = 24.9f;
        public const float WallraiseMaxRangedDist = 28f;
        public const float SkipshieldHpTriggerHp = 0.45f;
        public const int SkipshieldRangedTrigger = 2;    // 2+ дальника в LOS

        // === Pack 5 weights ===
        public const float W_Skip = 1.6f;
        public const float W_ChaosSkip = 1.2f;
        public const float W_MassChaosSkip = 1.4f;  // умножается ещё на W_CrowdControl_MassChaosSkip внутри scorer'а
        public const float W_Beckon = 1.7f;  // самый большой — это лекарство от deadlock
        public const float W_Wallraise = 1.0f;
        public const float W_Skipshield = 1.1f;

        // === Pack 5 stance/movement tuning ===
        public const float KiteMinDistance = 14f;   // ближе — не отступаем дальше
        public const float HuntApproachDistance = 20f;   // в Hunt подходим на это расстояние и стоим
        public const int NoFleeRefreshTicks = 60;    // как часто звать ForcePsycasterNoFlee
        public const int IdleWaitTicks = 30;    // если все скореры пустые — стоим столько

        // ============================================================
        // Cast warmup в тиках (приблизительно по vanilla).
        // Используется для постановки psycasterNextActionTick после успешного каста.
        // ============================================================

        public const int CastWarmupShort = 30;        // Stun, Skip
        public const int CastWarmupMedium = 60;       // BlindingPulse, VertigoPulse
        public const int CastWarmupLong = 120;        // BerserkPulse, ManhunterPulse, Wallraise

        // === Pack 5.1: soft-CD для способностей, которые забыли в Pack 3 ===
        public const int StunSoftCooldownMin = 240;  // 4 секунды
        public const int StunSoftCooldownMax = 360;  // 6 секунд
        public const int SkipSoftCooldownMin = 300;
        public const int SkipSoftCooldownMax = 480;
        public const int ChaosSkipSoftCooldownMin = 240;
        public const int ChaosSkipSoftCooldownMax = 420;
        public const int MassChaosSkipSoftCooldownMin = 900;  // 15 секунд — дорогая
        public const int MassChaosSkipSoftCooldownMax = 1500;
        public const int BeckonSoftCooldownMin = 600;  // 10 секунд — иначе спамим
        public const int BeckonSoftCooldownMax = 900;
    }
}
