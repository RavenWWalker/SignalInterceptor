using System.Collections.Generic;
using Verse;

namespace SignalInterceptor
{
    public partial class SignalInterceptorGameComponent
    {
        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref trackedSites, "trackedSites", LookMode.Deep);
            if (trackedSites == null)
                trackedSites = new List<StashSiteData>();

            Scribe_Collections.Look(ref pendingSlaveDeliveries, "pendingSlaveDeliveries", LookMode.Deep);
            if (pendingSlaveDeliveries == null)
                pendingSlaveDeliveries = new List<PendingSlaveDelivery>();

            Scribe_Collections.Look(ref pendingRaids, "pendingRaids", LookMode.Deep);
            if (pendingRaids == null)
                pendingRaids = new List<PendingRaid>();

            Scribe_Collections.Look(ref trackedVIPSites, "trackedVIPSites", LookMode.Deep);
            if (trackedVIPSites == null)
                trackedVIPSites = new List<VIPSiteData>();
            Scribe_Values.Look(ref doppelgangerFightActive, "doppelgangerFightActive", false);
            Scribe_Values.Look(ref doppelgangerFightStartTick, "doppelgangerFightStartTick", -1);
        }
    }
}
