using Verse;

namespace SignalInterceptor.AI.Psycaster
{
    /// <summary>
    /// «Псевдо-скорер»: не каст, а решение бить/преследовать врага в melee.
    ///
    /// Правило:
    /// - если враг рядом — бить;
    /// - если враг уже под контролем / недавно был притянут Beckon/Skip — добегать и бить;
    /// - если одиночный стрелок далеко и НЕ контролится — не перебивать Beckon/Stun/Skip.
    /// </summary>
    public class Scorer_MeleeAttack : AbilityScorerBase
    {
        public override string AbilityDefName { get { return "MeleeAttack_Pseudo"; } }

        public override bool IsAvailable(PsycasterBrain brain, BattlefieldSnapshot snap)
        {
            if (brain == null || snap == null || snap.caster == null)
                return false;

            if (snap.enemies == null || snap.enemies.Count == 0)
                return false;

            return true;
        }

        protected override float StanceMultiplier(PsycasterStance stance)
        {
            switch (stance)
            {
                case PsycasterStance.Opening: return 0.8f;
                case PsycasterStance.Kite: return 0.95f;
                case PsycasterStance.Disengage: return 0.45f;
                case PsycasterStance.CrowdControl: return 0.6f;
                case PsycasterStance.Hunt: return 1.4f;
                case PsycasterStance.Engulfed: return 0.35f;
                case PsycasterStance.Survive: return 0.1f;
                default: return 0.8f;
            }
        }

        protected override ScoredAction ScoreInternal(PsycasterBrain brain, BattlefieldSnapshot snap)
        {
            EnemyAssessment best = null;
            float bestRaw = 0f;

            bool singleEnemy = snap.enemies.Count == 1;

            for (int i = 0; i < snap.enemies.Count; i++)
            {
                EnemyAssessment e = snap.enemies[i];

                if (e == null || e.pawn == null)
                    continue;

                if (e.pawn.Destroyed || e.pawn.Dead || e.pawn.Downed || !e.pawn.Spawned)
                    continue;

                bool recentlyMovedByPsycast = brain.WasPawnRecentlyMoved(e.pawn);
                bool controlled = e.isStunned || e.isMindControlled || recentlyMovedByPsycast;

                float raw = 0f;

                // 1. Враг вплотную — бить немедленно.
                if (e.distanceToCaster <= 1.6f)
                {
                    raw = 32f + (e.threatScore / 10f);

                    if (e.role == EnemyRole.Sniper || e.role == EnemyRole.Heavy)
                        raw += 5f;

                    if (e.hpFraction < 0.4f)
                        raw *= 1.35f;
                }
                // 2. Враг почти рядом — добежать/ударить.
                else if (e.distanceToCaster <= 3.5f)
                {
                    raw = 16f + (e.threatScore / 14f);

                    if (controlled)
                        raw *= 1.2f;

                    if (e.hpFraction < 0.4f)
                        raw *= 1.25f;
                }
                // 3. Цель контролится и до неё реально можно добежать.
                // Это окно после Beckon/Skip/Stun.
                else if (controlled && e.distanceToCaster <= 18f && brain.CurrentStance != PsycasterStance.Survive)
                {
                    raw = 9f + (e.threatScore / 22f);

                    if (singleEnemy)
                        raw *= 1.15f;

                    if (e.role == EnemyRole.Sniper || e.role == EnemyRole.Heavy)
                        raw *= 1.15f;

                    if (e.hpFraction < 0.5f)
                        raw *= 1.2f;
                }
                // 4. Ближник рядом, но ещё не вплотную.
                else if (e.IsMelee && e.distanceToCaster <= 7f && brain.CurrentStance != PsycasterStance.Survive)
                {
                    raw = 6f + (e.threatScore / 18f);
                }
                // 5. Одиночный дальник НЕ контролится.
                // ВАЖНО: не даём melee перебивать Beckon/Stun/Skip на дистанции.
                else if (singleEnemy && e.IsRanged && e.distanceToCaster <= 8f && brain.CurrentStance == PsycasterStance.Kite)
                {
                    raw = 4f + (e.threatScore / 30f);
                }

                if (raw > bestRaw)
                {
                    bestRaw = raw;
                    best = e;
                }
            }

            if (best == null || bestRaw <= 0f)
                return ScoredAction.None;

            ScoredAction action = new ScoredAction();
            action.abilityDefName = AbilityDefName;
            action.targetType = ScoredActionTargetType.Pawn;
            action.targetPawn = best.pawn;
            action.castWarmupTicks = 90;
            action.score = bestRaw;
            action.debugReason = "Melee commit " + best.pawn.LabelShort
                                 + " d=" + best.distanceToCaster.ToString("F1")
                                 + " role=" + best.role
                                 + " stunned=" + best.isStunned
                                 + " moved=" + brain.WasPawnRecentlyMoved(best.pawn);

            return action;
        }
    }
}
