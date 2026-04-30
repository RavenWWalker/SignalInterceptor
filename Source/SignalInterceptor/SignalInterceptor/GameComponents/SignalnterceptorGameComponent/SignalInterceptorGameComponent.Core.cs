using RimWorld;
using RimWorld.Planet;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace SignalInterceptor
{
    public partial class SignalInterceptorGameComponent : GameComponent
    {
        private List<VIPSiteData> trackedVIPSites = new List<VIPSiteData>();
        private List<StashSiteData> trackedSites = new List<StashSiteData>();
        private List<PendingSlaveDelivery> pendingSlaveDeliveries = new List<PendingSlaveDelivery>();
        private List<PendingRaid> pendingRaids = new List<PendingRaid>();

        public SignalInterceptorGameComponent(Game game)
        {
            Log.Message("[Signal Interceptor] GameComponent initialized!");
        }

        public bool doppelgangerFightActive = false;
        public int doppelgangerFightStartTick = -1;

    }
}
