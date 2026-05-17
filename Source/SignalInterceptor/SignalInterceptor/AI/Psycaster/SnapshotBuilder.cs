using System.Collections.Generic;
using RimWorld;
using Verse;

namespace SignalInterceptor.AI.Psycaster
{
    /// <summary>
    /// Собирает BattlefieldSnapshot — полную картину мира для одного тика action-select.
    /// Все классификации врагов и численные оценки делаются здесь, ОДИН РАЗ за тик.
    /// Скореры читают только готовый snapshot, ничего не пересчитывают.
    ///
    /// Stateless. Использует методы PsycasterBrain (через ссылку) для специфичных
    /// для проекта проверок типа IsRangedCombatPawn.
    /// </summary>
    public static class SnapshotBuilder
    {
        /// <summary>
        /// Собрать снапшот для текущего тика. Если данных недостаточно (нет каста, нет карты),
        /// возвращает пустой snapshot с HasEnemies = false.
        /// </summary>
        public static BattlefieldSnapshot Build(PsycasterBrain brain)
        {
            BattlefieldSnapshot snap = new BattlefieldSnapshot();

            if (brain == null || brain.Caster == null || brain.Caster.Map == null)
                return snap;

            Pawn caster = brain.Caster;
            Map map = caster.Map;

            snap.caster = caster;
            snap.map = map;
            snap.currentTick = Find.TickManager.TicksGame;

            FillCasterState(snap, brain);
            FillEnemies(snap, brain);
            FillDerivedScores(snap);

            return snap;
        }

        // ============================================================
        // Заполнение состояния пси-кастера
        // ============================================================

        private static void FillCasterState(BattlefieldSnapshot snap, PsycasterBrain brain)
        {
            Pawn caster = snap.caster;

            // HP fraction.
            if (caster.health != null && caster.health.summaryHealth != null)
                snap.casterHpFraction = caster.health.summaryHealth.SummaryHealthPercent;
            else
                snap.casterHpFraction = 1f;

            // Psyfocus и entropy.
            if (caster.psychicEntropy != null)
            {
                snap.casterPsyfocus = caster.psychicEntropy.CurrentPsyfocus;

                float entropyValue = caster.psychicEntropy.EntropyValue;
                float maxEntropy = caster.psychicEntropy.MaxEntropy;
                snap.casterEntropyFraction = maxEntropy > 0.001f
                    ? entropyValue / maxEntropy
                    : 0f;
            }
            else
            {
                snap.casterPsyfocus = 0f;
                snap.casterEntropyFraction = 0f;
            }

            // Stun.
            snap.casterIsStunned = caster.stances != null
                && caster.stances.stunner != null
                && caster.stances.stunner.Stunned;

            // Invisibility / BulletShield — через хеддиффы.
            HediffDef invisibilityDef = DefDatabase<HediffDef>.GetNamedSilentFail("PsychicInvisibility");
            snap.casterIsInvisible = invisibilityDef != null
                && caster.health != null
                && caster.health.hediffSet != null
                && caster.health.hediffSet.HasHediff(invisibilityDef);

            // BulletShield — это thing на карте, не хеддифф. Проверяем по близости.
            snap.casterHasBulletShield = HasNearbyBulletShield(caster);
        }

        private static bool HasNearbyBulletShield(Pawn caster)
        {
            if (caster == null || caster.Map == null)
                return false;

            // BulletShield создаёт здание / эффект около пешки. Простая эвристика:
            // ищем Thing с defName, содержащим "BulletShield", в радиусе 6 от каста.
            Map map = caster.Map;
            IntVec3 center = caster.Position;

            foreach (Thing t in map.listerThings.AllThings)
            {
                if (t == null || t.def == null || t.def.defName == null)
                    continue;

                if (t.def.defName.IndexOf("BulletShield", System.StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                if (t.Position.DistanceTo(center) <= 6f)
                    return true;
            }

            return false;
        }

        // ============================================================
        // Заполнение списка врагов
        // ============================================================

        private static void FillEnemies(BattlefieldSnapshot snap, PsycasterBrain brain)
        {
            Pawn caster = snap.caster;
            Map map = snap.map;

            // Собираем всех враждебных пешек игрока.
            List<Pawn> hostilePlayerPawns = new List<Pawn>();
            foreach (Pawn p in map.mapPawns.AllPawnsSpawned)
            {
                if (p == null || p.Destroyed || p.Dead || p.Downed)
                    continue;

                if (p.Faction != Faction.OfPlayer)
                    continue;

                if (!p.Position.InBounds(map))
                    continue;

                hostilePlayerPawns.Add(p);
            }

            // Оцениваем каждого.
            foreach (Pawn p in hostilePlayerPawns)
            {
                EnemyAssessment a = AssessEnemy(p, caster, map, brain);
                snap.enemies.Add(a);
            }

            // Сортировка по threatScore (max первым).
            snap.enemies.Sort(CompareThreatDescending);

            // Разбиение на категории.
            for (int i = 0; i < snap.enemies.Count; i++)
            {
                EnemyAssessment a = snap.enemies[i];

                if (a.IsRanged)
                    snap.rangedEnemies.Add(a);

                if (a.IsMelee)
                    snap.meleeEnemies.Add(a);

                if (a.role == EnemyRole.Sniper || a.role == EnemyRole.Heavy)
                    snap.snipers.Add(a);

                if (a.IsAnimal)
                    snap.animals.Add(a);

                if (a.IsMechanoid)
                    snap.mechanoids.Add(a);

                if (a.distanceToCaster <= PsycasterTuning.EngulfedRadius)
                    snap.enemiesAdjacent.Add(a);

                if (a.hasLineOfSight)
                    snap.enemiesWithLosToCaster.Add(a);
            }

            // Кластерный анализ — для CrowdControl стансы.
            ComputeClusters(snap);
        }

        private static int CompareThreatDescending(EnemyAssessment a, EnemyAssessment b)
        {
            if (a == null && b == null) return 0;
            if (a == null) return 1;
            if (b == null) return -1;
            return b.threatScore.CompareTo(a.threatScore);
        }

        // ============================================================
        // Оценка одного врага
        // ============================================================

        private static EnemyAssessment AssessEnemy(Pawn p, Pawn caster, Map map, PsycasterBrain brain)
        {
            EnemyAssessment a = new EnemyAssessment();
            a.pawn = p;
            a.distanceToCaster = p.Position.DistanceTo(caster.Position);
            a.hasLineOfSight = GenSight.LineOfSight(caster.Position, p.Position, map);
            a.hpFraction = (p.health != null && p.health.summaryHealth != null)
                ? p.health.summaryHealth.SummaryHealthPercent
                : 1f;
            a.isStunned = p.stances != null && p.stances.stunner != null && p.stances.stunner.Stunned;
            a.isMindControlled = HasMindControlHediff(p);
            a.isInCover = false;

            ClassifyRoleAndWeapon(p, a, brain);

            a.canShootNow = ComputeCanShootNow(p, a);
            a.estimatedDps = EstimateDps(p, a);
            a.threatScore = ComputeThreat(a);

            return a;
        }

        private static bool HasMindControlHediff(Pawn p)
        {
            if (p == null || p.health == null || p.health.hediffSet == null)
                return false;

            HediffSet set = p.health.hediffSet;

            HediffDef berserk = DefDatabase<HediffDef>.GetNamedSilentFail("PsychicBerserk");
            if (berserk != null && set.HasHediff(berserk)) return true;

            HediffDef vertigo = DefDatabase<HediffDef>.GetNamedSilentFail("PsychicVertigo");
            if (vertigo != null && set.HasHediff(vertigo)) return true;

            HediffDef blind = DefDatabase<HediffDef>.GetNamedSilentFail("PsychicBlindness");
            if (blind != null && set.HasHediff(blind)) return true;

            return false;
        }

        private static void ClassifyRoleAndWeapon(Pawn p, EnemyAssessment a, PsycasterBrain brain)
        {
            // Механоиды.
            if (p.RaceProps != null && p.RaceProps.IsMechanoid)
            {
                a.role = EnemyRole.Mechanoid;
                a.weaponRange = GetWeaponRange(p);
                return;
            }

            // Животные.
            if (p.RaceProps != null && p.RaceProps.Animal)
            {
                a.role = EnemyRole.Animal;
                a.weaponRange = 0f;
                return;
            }

            // Гуманоиды.
            if (p.RaceProps != null && p.RaceProps.Humanlike)
            {
                // Пси-кастер у врага?
                if (IsHostilePsycaster(p))
                {
                    a.role = EnemyRole.Psycaster;
                    a.weaponRange = GetWeaponRange(p);
                    return;
                }

                bool isRanged = brain.IsRangedCombatPawn(p);

                if (!isRanged)
                {
                    // Если оружия вообще нет — Wimp. Если ближнее оружие есть — Melee.
                    if (p.equipment == null || p.equipment.Primary == null)
                        a.role = EnemyRole.Wimp;
                    else
                        a.role = EnemyRole.Melee;

                    a.weaponRange = 0f;
                    return;
                }

                // Дальник. Уточняем подкатегорию.
                float range = GetWeaponRange(p);
                a.weaponRange = range;

                if (IsHeavyWeapon(p))
                    a.role = EnemyRole.Heavy;
                else if (range >= PsycasterTuning.SniperRangeThreshold)
                    a.role = EnemyRole.Sniper;
                else
                    a.role = EnemyRole.Ranged;

                return;
            }

            a.role = EnemyRole.Unknown;
            a.weaponRange = 0f;
        }

        private static bool IsHostilePsycaster(Pawn p)
        {
            if (p == null || p.health == null || p.health.hediffSet == null)
                return false;

            HediffDef psylinkDef = DefDatabase<HediffDef>.GetNamedSilentFail("PsychicAmplifier");
            if (psylinkDef == null)
                return false;

            return p.health.hediffSet.HasHediff(psylinkDef);
        }

        private static float GetWeaponRange(Pawn p)
        {
            if (p == null || p.equipment == null || p.equipment.Primary == null)
                return 0f;

            ThingWithComps weapon = p.equipment.Primary;
            if (weapon.def == null || weapon.def.Verbs == null)
                return 0f;

            float maxRange = 0f;
            for (int i = 0; i < weapon.def.Verbs.Count; i++)
            {
                VerbProperties v = weapon.def.Verbs[i];
                if (v == null || v.IsMeleeAttack)
                    continue;
                if (v.range > maxRange)
                    maxRange = v.range;
            }
            return maxRange;
        }

        private static bool IsHeavyWeapon(Pawn p)
        {
            if (p == null || p.equipment == null || p.equipment.Primary == null)
                return false;

            ThingWithComps weapon = p.equipment.Primary;
            if (weapon.def == null)
                return false;

            // Грубая эвристика: масса оружия > порога ИЛИ verbProps содержит explosion.
            if (weapon.def.BaseMass >= PsycasterTuning.HeavyWeaponMassThreshold)
                return true;

            if (weapon.def.Verbs != null)
            {
                for (int i = 0; i < weapon.def.Verbs.Count; i++)
                {
                    VerbProperties v = weapon.def.Verbs[i];
                    if (v == null) continue;

                    // Прямой триггер взрывного оружия.
                    if (v.defaultProjectile != null
                        && v.defaultProjectile.projectile != null
                        && v.defaultProjectile.projectile.explosionRadius > 0f)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool ComputeCanShootNow(Pawn p, EnemyAssessment a)
        {
            if (a.isStunned) return false;
            if (a.isMindControlled) return false;
            if (!a.IsRanged) return false;
            if (p.equipment == null || p.equipment.Primary == null) return false;
            return true;
        }

        private static float EstimateDps(Pawn p, EnemyAssessment a)
        {
            // Грубая оценка: для дальников — damage * burst / cycleTime.
            // Для ближников — masthead melee verb.
            if (p.equipment != null && p.equipment.Primary != null && p.equipment.Primary.def != null)
            {
                ThingWithComps weapon = p.equipment.Primary;
                if (weapon.def.Verbs != null)
                {
                    float bestDps = 0f;
                    for (int i = 0; i < weapon.def.Verbs.Count; i++)
                    {
                        VerbProperties v = weapon.def.Verbs[i];
                        if (v == null) continue;

                        float damage = 0f;
                        if (v.defaultProjectile != null && v.defaultProjectile.projectile != null)
                            damage = v.defaultProjectile.projectile.GetDamageAmount(weapon, null);
                        else if (v.meleeDamageBaseAmount > 0)
                            damage = v.meleeDamageBaseAmount;

                        int burst = v.burstShotCount > 0 ? v.burstShotCount : 1;
                        float cycleTicks = v.warmupTime * 60f
                            + v.defaultCooldownTime * 60f
                            + burst * v.ticksBetweenBurstShots;
                        if (cycleTicks < 1f) cycleTicks = 60f;

                        float dps = damage * burst / (cycleTicks / 60f);
                        if (dps > bestDps) bestDps = dps;
                    }
                    return bestDps;
                }
            }

            // Безоружный гуманоид — кулаки, ~3 dps.
            if (a.IsHumanlike) return 3f;

            // Животное — берём из tools, грубо.
            if (a.IsAnimal && p.def != null && p.def.tools != null)
            {
                float bestToolDps = 0f;
                for (int i = 0; i < p.def.tools.Count; i++)
                {
                    Tool tool = p.def.tools[i];
                    if (tool == null) continue;
                    float dps = tool.power / System.Math.Max(0.5f, tool.cooldownTime);
                    if (dps > bestToolDps) bestToolDps = dps;
                }
                return bestToolDps;
            }

            return 0f;
        }

        private static float ComputeThreat(EnemyAssessment a)
        {
            float threat = a.estimatedDps;

            // Множитель за роль — снайперы и тяжёлые опаснее на дистанции.
            switch (a.role)
            {
                case EnemyRole.Sniper:
                case EnemyRole.Heavy:
                    threat *= 1.5f;
                    break;
                case EnemyRole.Psycaster:
                    threat *= 1.3f;
                    break;
                case EnemyRole.Mechanoid:
                    threat *= 1.2f;
                    break;
                case EnemyRole.Wimp:
                    threat *= 0.3f;
                    break;
            }

            // Если LOS нет — угроза значительно меньше (не может стрелять прямо сейчас).
            if (!a.hasLineOfSight)
                threat *= 0.4f;

            // Если оглушён или под mind control — почти не угроза.
            if (a.isStunned) threat *= 0.1f;
            if (a.isMindControlled) threat *= 0.3f;

            // Дистанция: ближние ближники опаснее, дальние дальники остаются опасными.
            if (a.IsMelee)
            {
                if (a.distanceToCaster <= 3f) threat *= 2.0f;
                else if (a.distanceToCaster <= 8f) threat *= 1.2f;
                else if (a.distanceToCaster > 20f) threat *= 0.4f;
            }
            else if (a.IsRanged)
            {
                if (a.weaponRange > 0f && a.distanceToCaster > a.weaponRange + 3f)
                    threat *= 0.5f;
            }

            if (threat < 0f) threat = 0f;
            return threat;
        }

        // ============================================================
        // Кластерный анализ
        // ============================================================

        private static void ComputeClusters(BattlefieldSnapshot snap)
        {
            // Простая жадная кластеризация: для каждого врага считаем,
            // сколько других врагов в radius=ClusterRadius. Это и записываем в alliesInClusterRadius.
            // Лучший такой враг — центр крупнейшего кластера.
            int bestSize = 0;
            IntVec3 bestCenter = IntVec3.Invalid;

            for (int i = 0; i < snap.enemies.Count; i++)
            {
                EnemyAssessment a = snap.enemies[i];
                if (a == null || a.pawn == null) continue;

                int count = 0;
                for (int j = 0; j < snap.enemies.Count; j++)
                {
                    if (i == j) continue;
                    EnemyAssessment b = snap.enemies[j];
                    if (b == null || b.pawn == null) continue;

                    if (a.pawn.Position.DistanceTo(b.pawn.Position) <= PsycasterTuning.ClusterRadius)
                        count++;
                }

                a.alliesInClusterRadius = count;

                int clusterSize = count + 1; // включая саму пешку
                if (clusterSize > bestSize)
                {
                    bestSize = clusterSize;
                    bestCenter = a.pawn.Position;
                }
            }

            snap.largestClusterSize = bestSize;
            snap.largestClusterCenter = bestCenter;
        }

        // ============================================================
        // Производные оценки
        // ============================================================

        private static void FillDerivedScores(BattlefieldSnapshot snap)
        {
            float totalDps = 0f;
            for (int i = 0; i < snap.enemiesWithLosToCaster.Count; i++)
            {
                EnemyAssessment e = snap.enemiesWithLosToCaster[i];
                if (e != null && e.canShootNow)
                    totalDps += e.estimatedDps;
            }
            snap.totalIncomingDps = totalDps;

            snap.topThreat = snap.enemies.Count > 0 ? snap.enemies[0] : null;
        }
    }
}
