using RimWorld;
using RimWorld.Planet;
using Verse;

namespace SignalInterceptor
{
    public class StashSiteData : IExposable
    {
        public Site site;
        public float threatPoints;
        public bool lootSpawned;
        public Faction faction;
        public int tier;

        public void ExposeData()
        {
            Scribe_References.Look(ref site, "site");
            Scribe_Values.Look(ref threatPoints, "threatPoints", 0f);
            Scribe_Values.Look(ref lootSpawned, "lootSpawned", false);
            Scribe_References.Look(ref faction, "faction");
            Scribe_Values.Look(ref tier, "tier", 0);
        }
    }

    public class VIPSiteData : IExposable
    {
        public Site site;
        public float threatPoints;
        public int expireTick = -1;

        public Faction faction;
        public Faction enemyFaction;

        public VIPSubtype subtype;
        public bool vipSpawned;
        public bool rewardGiven;

        public IntVec3 signalCampCenter = IntVec3.Invalid;

        public void ExposeData()
        {
            Scribe_References.Look(ref site, "site");
            Scribe_Values.Look(ref threatPoints, "threatPoints", 0f);
            Scribe_References.Look(ref faction, "faction");
            Scribe_References.Look(ref enemyFaction, "enemyFaction");
            Scribe_Values.Look(ref subtype, "subtype", VIPSubtype.ShuttleVIP);
            Scribe_Values.Look(ref vipSpawned, "vipSpawned", false);
            Scribe_Values.Look(ref rewardGiven, "rewardGiven", false);
            Scribe_Values.Look(ref signalCampCenter, "signalCampCenter", IntVec3.Invalid);
            Scribe_Values.Look(ref expireTick, "expireTick", -1);
        }
    }

    public class PendingSlaveDelivery : IExposable
    {
        public int mapId;
        public int deliveryTick;

        public void ExposeData()
        {
            Scribe_Values.Look(ref mapId, "mapId", 0);
            Scribe_Values.Look(ref deliveryTick, "deliveryTick", 0);
        }
    }

    public class PendingRaid : IExposable
    {
        public int mapId;
        public Faction faction;
        public float points;
        public int fireTick;

        public void ExposeData()
        {
            Scribe_Values.Look(ref mapId, "mapId", 0);
            Scribe_References.Look(ref faction, "faction");
            Scribe_Values.Look(ref points, "points", 0f);
            Scribe_Values.Look(ref fireTick, "fireTick", 0);
        }
    }
}
