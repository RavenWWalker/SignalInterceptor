using Verse;

namespace SignalInterceptor
{
    public partial class SignalInterceptorGameComponent
    {
        public override void GameComponentTick()
        {
            TickDoppelgangerSettlementCleanup();

            TickVIPSites();

            if (Find.TickManager.TicksGame % 60 != 0)
                return;

            TickStashSites();
            TickPendingSlaveDeliveries();
            TickPendingRaids();

            TickDoppelgangerFightRetargeting();
            TickDoppelgangerHatred();
            TickDoppelgangerFightIncident();
        }
    }
}
