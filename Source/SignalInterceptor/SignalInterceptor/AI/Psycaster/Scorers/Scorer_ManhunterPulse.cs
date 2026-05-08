using Verse;

namespace SignalInterceptor.AI.Psycaster
{
    /// <summary>
    /// ManhunterPulse — волна агрессии по животным.
    ///
    /// Используется, когда против пси-кастера есть несколько животных.
    /// Особенно полезно:
    /// - если животных 2+;
    /// - если они сгруппированы;
    /// - если пси-кастер в CrowdControl / Engulfed / Survive;
    /// - если рядом также есть человек, которого животные могут начать мешать/давить.
    /// </summary>
    public class Scorer_ManhunterPulse : AbilityScorerBase
    {
        public override string AbilityDefName { get { return "ManhunterPulse"; } }

        protected override float MinPsyfocusFraction
        {
            get { return 0.35f; }
        }

        protected override float StanceMultiplier(PsycasterStance stance)
        {
            switch (stance)
            {
                case PsycasterStance.Opening: return PsycasterTuning.W_Opening_ManhunterPulse;
                case PsycasterStance.Kite: return PsycasterTuning.W_Kite_ManhunterPulse;
                case PsycasterStance.Disengage: return PsycasterTuning.W_Disengage_ManhunterPulse;
                case PsycasterStance.CrowdControl: return PsycasterTuning.W_CrowdControl_ManhunterPulse;
                case PsycasterStance.Hunt: return PsycasterTuning.W_Hunt_ManhunterPulse;
                case PsycasterStance.Engulfed: return PsycasterTuning.W_Engulfed_ManhunterPulse;
                case PsycasterStance.Survive: return PsycasterTuning.W_Survive_ManhunterPulse;
                default: return 0f;
            }
        }

        protected override bool IsContextuallyAvailable(PsycasterBrain brain, BattlefieldSnapshot snap)
        {
            if (snap == null || snap.animals == null)
                return false;

            /*
             * Одно животное не стоит стоимости ManhunterPulse.
             */
            return snap.animals.Count >= 2;
        }

        protected override ScoredAction ScoreInternal(PsycasterBrain brain, BattlefieldSnapshot snap)
        {
            if (snap.animals == null || snap.animals.Count < 2)
                return ScoredAction.None;

            EnemyAssessment bestCenter = null;
            int bestAnimalCluster = 0;
            float bestThreat = 0f;

            /*
             * Ищем животное, вокруг которого больше всего других животных.
             * Это будет центр пульса.
             */
            for (int i = 0; i < snap.animals.Count; i++)
            {
                EnemyAssessment center = snap.animals[i];

                if (center == null || center.pawn == null)
                    continue;

                if (center.distanceToCaster > PsycasterTuning.StandardPulseRange)
                    continue;

                if (!center.hasLineOfSight)
                    continue;

                int animalCluster = 0;
                float threat = 0f;

                for (int j = 0; j < snap.animals.Count; j++)
                {
                    EnemyAssessment other = snap.animals[j];

                    if (other == null || other.pawn == null)
                        continue;

                    float dist = center.pawn.Position.DistanceTo(other.pawn.Position);

                    if (dist <= PsycasterTuning.ClusterRadius + 1.5f)
                    {
                        animalCluster++;
                        threat += other.threatScore;
                    }
                }

                if (animalCluster > bestAnimalCluster ||
                    animalCluster == bestAnimalCluster && threat > bestThreat)
                {
                    bestAnimalCluster = animalCluster;
                    bestThreat = threat;
                    bestCenter = center;
                }
            }

            if (bestCenter == null)
                return ScoredAction.None;

            /*
             * Если в кластере меньше двух животных — не тратим дорогую способность.
             */
            if (bestAnimalCluster < 2)
                return ScoredAction.None;

            float raw = 10f + bestAnimalCluster * 8f + bestThreat / 4f;

            /*
             * Если пси-кастер зажат животными — это очень хороший каст.
             */
            if (brain.CurrentStance == PsycasterStance.Engulfed)
                raw *= 1.45f;

            if (brain.CurrentStance == PsycasterStance.Survive)
                raw *= 1.25f;

            if (brain.CurrentStance == PsycasterStance.CrowdControl)
                raw *= 1.35f;

            /*
             * Если HP уже просел — приоритет выше.
             */
            if (snap.casterHpFraction < 0.75f)
                raw *= 1.25f;

            if (snap.casterHpFraction < 0.55f)
                raw *= 1.35f;

            /*
             * Если животных 3+ — это ровно тот случай, ради которого способность нужна.
             */
            if (bestAnimalCluster >= 3)
                raw *= 1.4f;

            ScoredAction action = new ScoredAction();
            action.abilityDefName = AbilityDefName;
            action.targetType = ScoredActionTargetType.Cell;
            action.targetCell = bestCenter.pawn.Position;
            action.castWarmupTicks = PsycasterTuning.CastWarmupLong;
            action.score = raw;
            action.debugReason = "ManhunterPulse animals=" + bestAnimalCluster
                                 + " center=" + bestCenter.pawn.LabelShort
                                 + " threat=" + bestThreat.ToString("F1")
                                 + " stance=" + brain.CurrentStance;

            return action;
        }
    }
}
