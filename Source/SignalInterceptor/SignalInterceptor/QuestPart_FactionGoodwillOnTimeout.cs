using RimWorld;
using RimWorld.Planet;
using Verse;

namespace SignalInterceptor
{
    public class QuestPart_FactionGoodwillOnTimeout : QuestPart
    {
        public Faction faction;
        public int goodwillBonus;
        public Site site;
        public bool isFriendly;

        private bool siteWasEntered = false;
        private bool stashWasReported = false;

        public void MarkAsReported()
        {
            stashWasReported = true;
        }

        public override void Notify_QuestSignalReceived(Signal signal)
        {
            base.Notify_QuestSignalReceived(signal);

            if (site != null && site.HasMap)
            {
                siteWasEntered = true;
            }
        }

        public override void Cleanup()
        {
            base.Cleanup();

            if (site != null && site.HasMap)
            {
                siteWasEntered = true;
            }

            // Не давать бонус если тайник был сдан другой фракции
            if (stashWasReported)
                return;

            if (quest.State != QuestState.EndedSuccess && !siteWasEntered && isFriendly && faction != null)
            {
                faction.TryAffectGoodwillWith(Faction.OfPlayer, goodwillBonus, canSendMessage: true, canSendHostilityLetter: false);

                Find.LetterStack.ReceiveLetter(
                    "SI_LetterSecretKept".Translate(),
                    "SI_LetterSecretKeptText".Translate(faction.Name, goodwillBonus),
                    LetterDefOf.PositiveEvent
                );
            }
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_References.Look(ref faction, "faction");
            Scribe_Values.Look(ref goodwillBonus, "goodwillBonus", 0);
            Scribe_References.Look(ref site, "site");
            Scribe_Values.Look(ref isFriendly, "isFriendly", false);
            Scribe_Values.Look(ref siteWasEntered, "siteWasEntered", false);
            Scribe_Values.Look(ref stashWasReported, "stashWasReported", false);
        }
    }
}
