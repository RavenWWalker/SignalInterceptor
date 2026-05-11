using Verse;
using RimWorld;

namespace SignalInterceptor.AI.Psycaster
{
    /// <summary>
    /// Skipshield.
    /// Приоритет:
    /// 1) Мили-кастеры ставят на себя, чтобы безопасно вытягивать противников в ближний бой;
    /// 2) закрыть опасных дальников куполом, чтобы они не стреляли по кастеру;
    /// 3) если HP низкое / кастер под огнём — поставить купол на себя.
    /// </summary>
    public class Scorer_Skipshield : AbilityScorerBase
    {
        public override string AbilityDefName { get { return "Skipshield"; } }

        protected override float StanceMultiplier(PsycasterStance stance)
        {
            switch (stance)
            {
                case PsycasterStance.Opening: return PsycasterTuning.W_Opening_Skipshield;
                case PsycasterStance.Kite: return PsycasterTuning.W_Kite_Skipshield;
                case PsycasterStance.Disengage: return PsycasterTuning.W_Disengage_Skipshield;
                case PsycasterStance.CrowdControl: return PsycasterTuning.W_CrowdControl_Skipshield;
                case PsycasterStance.Hunt: return PsycasterTuning.W_Hunt_Skipshield;
                case PsycasterStance.Engulfed: return PsycasterTuning.W_Engulfed_Skipshield;
                case PsycasterStance.Survive: return PsycasterTuning.W_Survive_Skipshield;
                default: return 0f;
            }
        }

        protected override float MinPsyfocusFraction { get { return 0.25f; } }

        protected override ScoredAction ScoreInternal(PsycasterBrain brain, BattlefieldSnapshot snap)
        {
            if (snap == null || snap.caster == null || snap.enemies == null)
                return ScoredAction.None;

            if (snap.casterHasSkipshield)
                return ScoredAction.None;

            int rangedLos = 0;
            EnemyAssessment bestRanged = null;
            float bestThreat = 0f;

            for (int i = 0; i < snap.enemies.Count; i++)
            {
                EnemyAssessment e = snap.enemies[i];

                if (e == null || e.pawn == null)
                    continue;

                if (!e.IsRanged)
                    continue;

                if (!e.hasLineOfSight)
                    continue;

                if (e.distanceToCaster > PsycasterTuning.StandardPulseRange)
                    continue;

                rangedLos++;

                if (e.threatScore > bestThreat)
                {
                    bestThreat = e.threatScore;
                    bestRanged = e;
                }
            }

            // Проверяем, является ли псикастер бойцом ближнего боя
            bool isMeleeCaster = snap.caster.equipment?.Primary != null && snap.caster.equipment.Primary.def.IsMeleeWeapon;

            bool hpTrigger = snap.casterHpFraction <= 0.55f;
            bool fireTrigger = rangedLos >= 2;
            bool severeFire = rangedLos >= 3 || snap.totalIncomingDps >= 24f;
            bool entropyDanger = snap.casterEntropyFraction >= 0.70f && snap.casterHpFraction <= 0.65f;

            // Новый триггер: милишнику выгодно ставить щит, даже если стрелков немного, 
            // чтобы подготовить "арену" для Skip.
            bool meleeArenaTrigger = isMeleeCaster && rangedLos >= 1 && snap.casterEntropyFraction < 0.60f;

            if (!hpTrigger && !fireTrigger && !severeFire && !entropyDanger && !meleeArenaTrigger)
                return ScoredAction.None;

            IntVec3 targetCell = snap.caster.Position;
            string mode = "self";

            // Если кастер не милишник, и есть опасный дальник на дистанции — первично купол на него/их позицию.
            // Милишнику же всегда выгоднее ставить купол на СЕБЯ, чтобы вытягивать туда врагов.
            if (!isMeleeCaster && bestRanged != null &&
                bestRanged.distanceToCaster >= 6f &&
                GenSight.LineOfSight(snap.caster.Position, bestRanged.pawn.Position, snap.map))
            {
                targetCell = bestRanged.pawn.Position;
                mode = "ranged";
            }
            else if (isMeleeCaster && meleeArenaTrigger)
            {
                mode = "melee_arena";
            }

            float raw = 6f;

            raw += rangedLos * 2.2f;
            raw += snap.totalIncomingDps / 5f;

            if (hpTrigger)
                raw += (0.60f - snap.casterHpFraction) * 18f;

            if (severeFire)
                raw += 8f;

            if (entropyDanger)
                raw += 5f;

            if (brain.CurrentStance == PsycasterStance.Survive)
                raw += 10f;

            // Накидываем жирный бонус за подготовку Арены, если псикастер милишник
            if (mode == "melee_arena")
                raw += 15f;

            ScoredAction action = new ScoredAction();
            action.abilityDefName = AbilityDefName;
            action.targetType = ScoredActionTargetType.Cell;
            action.targetCell = targetCell;
            action.castWarmupTicks = PsycasterTuning.CastWarmupMedium;
            action.score = raw;
            action.debugReason =
                "Skipshield " + mode +
                " hp=" + snap.casterHpFraction.ToString("F2") +
                " entropy=" + snap.casterEntropyFraction.ToString("F2") +
                " rangedLOS=" + rangedLos +
                " incomingDps=" + snap.totalIncomingDps.ToString("F1");

            return action;
        }
    }
}
