using RimWorld;
using Verse;

namespace SignalInterceptor
{
    public class ThoughtWorker_TheOriginal : ThoughtWorker
    {
        protected override ThoughtState CurrentStateInternal(Pawn p)
        {
            if (p == null)
                return ThoughtState.Inactive;

            if (p.story?.traits == null)
                return ThoughtState.Inactive;

            TraitDef originalTrait = DefDatabase<TraitDef>.GetNamedSilentFail("SI_TheOriginal");
            if (originalTrait == null)
                return ThoughtState.Inactive;

            if (!p.story.traits.HasTrait(originalTrait))
                return ThoughtState.Inactive;

            return ThoughtState.ActiveAtStage(0);
        }
    }
}
