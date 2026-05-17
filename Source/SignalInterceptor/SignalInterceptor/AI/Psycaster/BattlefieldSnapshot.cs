using System.Collections.Generic;
using Verse;

namespace SignalInterceptor.AI.Psycaster
{
    /// <summary>
    /// Снимок поля боя на момент текущего action-select тика.
    /// Собирается один раз в начале action-select итерации, передаётся всем скорерам.
    /// Не сохраняется между тиками — это исключительно входные данные для решения.
    ///
    /// Поля публичные намеренно: это POCO, не объект с инкапсуляцией.
    /// </summary>
    public class BattlefieldSnapshot
    {
        // ============================================================
        // Контекст
        // ============================================================

        /// <summary>Сам пси-кастер.</summary>
        public Pawn caster;

        /// <summary>Карта, на которой идёт бой.</summary>
        public Map map;

        /// <summary>Текущий тик игры. Снимается один раз для согласованности.</summary>
        public int currentTick;

        // ============================================================
        // Состояние пси-кастера
        // ============================================================

        /// <summary>Доля HP пси-кастера [0..1].</summary>
        public float casterHpFraction;

        /// <summary>Текущий psyfocus [0..1]. 0 = пустой, 1 = полный.</summary>
        public float casterPsyfocus;

        /// <summary>Текущая нейронная heat [0..1] относительно PsychicEntropyMax.</summary>
        public float casterEntropyFraction;

        /// <summary>Стан, оглушение или прерванный каст у самого пси-кастера.</summary>
        public bool casterIsStunned;

        /// <summary>Сейчас под действием Invisibility.</summary>
        public bool casterIsInvisible;

        /// <summary>Сейчас под действием BulletShield (или аналогичного барьера).</summary>
        public bool casterHasBulletShield;

        // ============================================================
        // Враги
        // ============================================================

        /// <summary>Все враждебные пешки на карте, отсортированные по threatScore (max первым).</summary>
        public List<EnemyAssessment> enemies = new List<EnemyAssessment>();

        /// <summary>Только дальники (Ranged + Sniper + Heavy), отсортированы по threatScore.</summary>
        public List<EnemyAssessment> rangedEnemies = new List<EnemyAssessment>();

        /// <summary>Только ближники (Melee).</summary>
        public List<EnemyAssessment> meleeEnemies = new List<EnemyAssessment>();

        /// <summary>Снайперы и тяжёлые (range >= SniperRangeThreshold) — приоритетные цели.</summary>
        public List<EnemyAssessment> snipers = new List<EnemyAssessment>();

        /// <summary>Враждебные животные.</summary>
        public List<EnemyAssessment> animals = new List<EnemyAssessment>();

        /// <summary>Враждебные механоиды.</summary>
        public List<EnemyAssessment> mechanoids = new List<EnemyAssessment>();

        /// <summary>Враги в EngulfedRadius от пси-кастера.</summary>
        public List<EnemyAssessment> enemiesAdjacent = new List<EnemyAssessment>();

        /// <summary>Враги с прямым LOS до пси-кастера прямо сейчас.</summary>
        public List<EnemyAssessment> enemiesWithLosToCaster = new List<EnemyAssessment>();

        // ============================================================
        // Производные оценки
        // ============================================================

        /// <summary>Суммарный DPS всех видимых врагов с LOS.</summary>
        public float totalIncomingDps;

        /// <summary>Самый опасный враг прямо сейчас (топ enemies). Может быть null.</summary>
        public EnemyAssessment topThreat;

        /// <summary>
        /// Размер крупнейшего кластера врагов в радиусе ClusterRadius друг от друга.
        /// Используется StanceSelector-ом для входа в CrowdControl.
        /// </summary>
        public int largestClusterSize;

        /// <summary>
        /// Центр крупнейшего кластера. IntVec3.Invalid если кластера нет.
        /// </summary>
        public IntVec3 largestClusterCenter = IntVec3.Invalid;

        // ============================================================
        // Утилиты
        // ============================================================

        /// <summary>Есть ли вообще враги для атаки.</summary>
        public bool HasEnemies
        {
            get { return enemies != null && enemies.Count > 0; }
        }

        /// <summary>Пси-кастер сейчас находится под прямым огнём (1+ дальников с LOS).</summary>
        public bool IsUnderRangedFire
        {
            get
            {
                if (enemiesWithLosToCaster == null)
                    return false;

                for (int i = 0; i < enemiesWithLosToCaster.Count; i++)
                {
                    EnemyAssessment e = enemiesWithLosToCaster[i];
                    if (e != null && e.IsRanged && e.canShootNow)
                        return true;
                }

                return false;
            }
        }

        /// <summary>Пси-кастер сейчас в ближнем бою (1+ ближников рядом).</summary>
        public bool IsInMeleeRange
        {
            get
            {
                if (enemiesAdjacent == null)
                    return false;

                for (int i = 0; i < enemiesAdjacent.Count; i++)
                {
                    EnemyAssessment e = enemiesAdjacent[i];
                    if (e != null)
                        return true;
                }

                return false;
            }
        }
    }
}
