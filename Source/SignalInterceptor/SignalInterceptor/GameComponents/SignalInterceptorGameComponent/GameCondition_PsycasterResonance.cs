using RimWorld;
using Verse;

namespace SignalInterceptor
{
    public class GameCondition_PsycasterResonance : GameCondition
    {
        public Thing tree;
        public Pawn psycaster;

        public override void GameConditionTick()
        {
            base.GameConditionTick();

            if (Find.TickManager.TicksGame % 60 != 0)
                return;

            if (tree == null || tree.Destroyed || !tree.Spawned)
            {
                End();
                return;
            }

            if (psycaster == null || psycaster.Destroyed || psycaster.Dead || !psycaster.Spawned)
            {
                End();
                return;
            }

            if (tree.Map == null || psycaster.Map == null || tree.Map != psycaster.Map)
            {
                End();
            }
        }

        public override void ExposeData()
        {
            base.ExposeData();

            Scribe_References.Look(ref tree, "tree");
            Scribe_References.Look(ref psycaster, "psycaster");
        }
    }
}
