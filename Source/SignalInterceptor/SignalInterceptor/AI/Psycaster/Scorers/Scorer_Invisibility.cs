using Verse;

namespace SignalInterceptor.AI.Psycaster
{
    /// <summary>
    /// Invisibility:
    /// - defensive: low HP / ranged pressure / recovery;
    /// - offensive: Hunt against high-value ranged target, чтобы безопасно зайти в melee.
    /// </summary>
    public class Scorer_Invisibility : AbilityScorerBase
    {
        public override string AbilityDefName { get { return "Invisibility"; } }

        protected override float StanceMultiplier(PsycasterStance stance)
        {
            switch (stance)
            {
                case PsycasterStance.Opening: return PsycasterTuning.W_Opening_Invisibility;
                case PsycasterStance.Kite: return PsycasterTuning.W_Kite_Invisibility;
                case PsycasterStance.Disengage: return PsycasterTuning.W_Disengage_Invisibility;
                case PsycasterStance.CrowdControl: return PsycasterTuning.W_CrowdControl_Invisibility;
                case PsycasterStance.Hunt: return PsycasterTuning.W_Hunt_Invisibility;
                case PsycasterStance.Engulfed: return PsycasterTuning.W_Engulfed_Invisibility;
                case PsycasterStance.Survive: return PsycasterTuning.W_Survive_Invisibility;
                default: return 0f;
            }
        }

        protected override float MinPsyfocusFraction { get { return 0.20f; } }

        protected override ScoredAction ScoreInternal(PsycasterBrain brain, BattlefieldSnapshot snap)
        {
            if (snap == null || snap.caster == null)
                return ScoredAction.None;

            if (snap.casterIsInvisible)
                return ScoredAction.None;

            int rangedLos = 0;
            EnemyAssessment bestRanged = null;
            float bestThreat = 0f;

            if (snap.enemies != null)
            {
                for (int i = 0; i < snap.enemies.Count; i++)
                {
                    EnemyAssessment e = snap.enemies[i];

                    if (e == null || e.pawn == null)
                        continue;

                    if (e.IsRanged && e.hasLineOfSight && e.canShootNow)
                    {
                        rangedLos++;

                        if (e.threatScore > bestThreat)
                        {
                            bestThreat = e.threatScore;
                            bestRanged = e;
                        }
                    }
                }
            }

            bool defensive =
                snap.casterHpFraction <= 0.55f ||
                brain.CurrentStance == PsycasterStance.Survive ||
                rangedLos >= 3 ||
                snap.totalIncomingDps >= 24f;

            bool offensive =
                brain.CurrentStance == PsycasterStance.Hunt &&
                bestRanged != null &&
                bestRanged.distanceToCaster <= 18f &&
                bestThreat >= 10f;

            if (!defensive && !offensive)
                return ScoredAction.None;

            float raw = defensive ? 10f : 5f;

            if (defensive)
            {
                raw += (0.65f - snap.casterHpFraction) * 20f;
                raw += rangedLos * 2.0f;
                raw += snap.totalIncomingDps / 5f;
                raw += snap.casterEntropyFraction * 4f;
            }

            if (offensive)
            {
                raw += bestThreat / 3f;
                raw += 4f;
            }

            if (brain.CurrentStance == PsycasterStance.Survive)
                raw += 10f;

            ScoredAction action = new ScoredAction();
            action.abilityDefName = AbilityDefName;
            action.targetType = ScoredActionTargetType.Self;
            action.castWarmupTicks = PsycasterTuning.CastWarmupMedium;
            action.score = raw;
            action.debugReason =
                "Invisibility " +
                (defensive ? "defensive" : "offensive") +
                " hp=" + snap.casterHpFraction.ToString("F2") +
                " entropy=" + snap.casterEntropyFraction.ToString("F2") +
                " rangedLOS=" + rangedLos +
                " incomingDps=" + snap.totalIncomingDps.ToString("F1");

            return action;
        }
    }
}
