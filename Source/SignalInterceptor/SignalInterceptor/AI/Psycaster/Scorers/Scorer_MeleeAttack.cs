using Verse;

namespace SignalInterceptor.AI.Psycaster
{
    /// <summary>
    /// «Псевдо-скорер»: не каст, а решение бить ближайшего врага в melee.
    /// Имеет очень высокий score, когда враг в радиусе 1 клетки (т.е. адъяцентен) —
    /// чтобы вынуждать псикастера разменивать урон, а не спамить кастами поверх.
    /// Когда нет адъяцентного врага — score 0.
    /// </summary>
    public class Scorer_MeleeAttack : AbilityScorerBase
    {
        // Используем фиктивное имя — реальной AbilityDef нет.
        // IsAvailable переопределим, чтобы не падал поиск AbilityDef.
        public override string AbilityDefName { get { return "MeleeAttack_Pseudo"; } }

        public override bool IsAvailable(PsycasterBrain brain, BattlefieldSnapshot snap)
        {
            if (brain == null || snap == null || snap.caster == null) return false;
            if (snap.enemies == null || snap.enemies.Count == 0) return false;
            return true;
        }

        protected override float StanceMultiplier(PsycasterStance stance)
        {
            switch (stance)
            {
                case PsycasterStance.Opening: return 0.8f;
                case PsycasterStance.Kite: return 0.6f;  // в Kite не любим melee
                case PsycasterStance.Disengage: return 0.3f;
                case PsycasterStance.CrowdControl: return 0.7f;
                case PsycasterStance.Hunt: return 1.5f;  // в Hunt — главный выбор
                case PsycasterStance.Engulfed: return 0.4f;
                case PsycasterStance.Survive: return 0.2f;
                default: return 0.5f;
            }
        }

        protected override ScoredAction ScoreInternal(PsycasterBrain brain, BattlefieldSnapshot snap)
        {
            EnemyAssessment best = null;
            float bestRaw = 0f;

            for (int i = 0; i < snap.enemies.Count; i++)
            {
                EnemyAssessment e = snap.enemies[i];
                if (e == null || e.pawn == null) continue;
                if (e.pawn.Dead || e.pawn.Downed) continue;

                // Адъяцентный (1.5 = диагональ) — главный приоритет.
                if (e.distanceToCaster <= 1.6f)
                {
                    // Огромная база — бить вплотную почти всегда выгоднее каста.
                    float raw = 6f + (e.threatScore / 10f);
                    // Снайпер вплотную — добить за один удар.
                    if (e.role == EnemyRole.Sniper) raw += 2f;
                    // Низкое HP врага — добить.
                    if (e.hpFraction < 0.4f) raw *= 1.4f;
                    if (raw > bestRaw)
                    {
                        bestRaw = raw;
                        best = e;
                    }
                    continue;
                }

                // В радиусе 3 клеток — тоже годится, но дешевле.
                if (e.distanceToCaster <= 3.5f)
                {
                    float raw = 2.5f + (e.threatScore / 15f);
                    if (e.hpFraction < 0.4f) raw *= 1.3f;
                    if (raw > bestRaw)
                    {
                        bestRaw = raw;
                        best = e;
                    }
                }
            }

            if (best == null)
                return ScoredAction.None;

            ScoredAction action = new ScoredAction();
            action.abilityDefName = AbilityDefName;
            action.targetType = ScoredActionTargetType.Pawn;
            action.targetPawn = best.pawn;
            action.castWarmupTicks = 30; // короткий, чтобы быстро решить снова
            action.score = bestRaw;
            action.debugReason = "Melee " + best.pawn.LabelShort
                                     + " d=" + best.distanceToCaster.ToString("F1");
            return action;
        }
    }
}
