using Verse;

namespace SignalInterceptor.AI.Psycaster
{
    /// <summary>
    /// Wallraise — поднимает каменную стену в указанной клетке.
    /// Тактика: разорвать LOS дальникам в Disengage/Survive, выиграть время на cooldown'ах.
    /// </summary>
    public class Scorer_Wallraise : AbilityScorerBase
    {
        public override string AbilityDefName { get { return "Wallraise"; } }

        protected override float StanceMultiplier(PsycasterStance stance)
        {
            switch (stance)
            {
                case PsycasterStance.Opening: return PsycasterTuning.W_Opening_Wallraise;
                case PsycasterStance.Kite: return PsycasterTuning.W_Kite_Wallraise;
                case PsycasterStance.Disengage: return PsycasterTuning.W_Disengage_Wallraise;
                case PsycasterStance.CrowdControl: return PsycasterTuning.W_CrowdControl_Wallraise;
                case PsycasterStance.Hunt: return PsycasterTuning.W_Hunt_Wallraise;
                case PsycasterStance.Engulfed: return PsycasterTuning.W_Engulfed_Wallraise;
                case PsycasterStance.Survive: return PsycasterTuning.W_Survive_Wallraise;
                default: return 0f;
            }
        }

        protected override float MinPsyfocusFraction { get { return 0.10f; } }

        protected override ScoredAction ScoreInternal(PsycasterBrain brain, BattlefieldSnapshot snap)
        {
            // В обычной дуэли Wallraise слишком часто становится "мусорным" действием:
            // score маленький, но если другие scorers временно невалидны, он всё равно выбирается.
            // Против одиночного дальника лучше использовать Skip/Beckon/Blind/Vertigo/melee.
            if (snap.enemies != null &&
                snap.enemies.Count == 1 &&
                brain.CurrentStance != PsycasterStance.Survive &&
                snap.casterHpFraction > 0.45f)
            {
                return ScoredAction.None;
            }
            // В дуэли Wallraise не должен перебивать добивание цели.
            // Иначе получается: Skip/Beckon -> melee -> Wallraise -> цель снова уходит.
            if (snap.enemies != null &&
                snap.enemies.Count == 1 &&
                brain.HasActiveKillContract &&
                brain.CurrentStance != PsycasterStance.Survive &&
                snap.casterHpFraction > 0.45f)
            {
                return ScoredAction.None;
            }

            // Ищем самого толстого дальника в LOS — от него и ставим стену.
            EnemyAssessment best = null;
            int rangedInLOS = 0;
            float bestThreat = 0f;

            for (int i = 0; i < snap.enemies.Count; i++)
            {
                EnemyAssessment e = snap.enemies[i];
                if (e == null || e.pawn == null) continue;
                if (e.role != EnemyRole.Ranged && e.role != EnemyRole.Sniper) continue;
                if (e.distanceToCaster > PsycasterTuning.WallraiseMaxRangedDist) continue;
                if (!e.hasLineOfSight) continue;

                rangedInLOS++;
                if (e.threatScore > bestThreat)
                {
                    bestThreat = e.threatScore;
                    best = e;
                }
            }

            if (rangedInLOS < 1 || best == null)
                return ScoredAction.None;

            IntVec3 wallCell;
            if (!brain.GameComp.TryFindWallraiseCell_Public(brain.Caster, best.pawn, brain.Caster.Map, out wallCell))
                return ScoredAction.None;
            if (!wallCell.IsValid)
                return ScoredAction.None;

            float raw = rangedInLOS * 1.2f;
            if (snap.casterHpFraction < 0.5f) raw *= 1.5f;

            ScoredAction action = new ScoredAction();
            action.abilityDefName = AbilityDefName;
            action.targetType = ScoredActionTargetType.Cell;
            action.targetCell = wallCell;
            action.castWarmupTicks = PsycasterTuning.CastWarmupMedium;
            action.score = raw;
            action.debugReason = "Wallraise vs " + rangedInLOS + " ranged "
                                     + "(top=" + best.pawn.LabelShort + ") @" + wallCell;
            return action;
        }
    }
}
