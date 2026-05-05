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

        // Psycaster VIP
        public Pawn psycasterPawn;
        public bool psycasterDelivered;
        public bool psycasterDestabilized;
        public bool psycasterWasShocked;
        public int psycasterNextCastTick = -1;
        public bool psycasterFocusUsed;
        public int psycasterNextDefensiveCastTick = -1;
        public int psycasterNextWallraiseTick = -1;
        public int psycasterNextSmokepopTick = -1;
        public int psycasterMeleeCommitTargetThingId = -1;
        public int psycasterMeleeCommitUntilTick = -1;
        public int psycasterLastInvisibilityTick = -999999;
        public int psycasterComboTargetThingId = -1;
        public int psycasterComboStage = 0;
        public int psycasterComboExpireTick = -1;

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

            Scribe_References.Look(ref psycasterPawn, "psycasterPawn");
            Scribe_Values.Look(ref psycasterDelivered, "psycasterDelivered", false);
            Scribe_Values.Look(ref psycasterDestabilized, "psycasterDestabilized", false);
            Scribe_Values.Look(ref psycasterWasShocked, "psycasterWasShocked", false);
            Scribe_Values.Look(ref psycasterNextCastTick, "psycasterNextCastTick", -1);
            Scribe_Values.Look(ref psycasterFocusUsed, "psycasterFocusUsed", false);
            Scribe_Values.Look(ref psycasterNextDefensiveCastTick, "psycasterNextDefensiveCastTick", -1);
            Scribe_Values.Look(ref psycasterNextWallraiseTick, "psycasterNextWallraiseTick", -1);
            Scribe_Values.Look(ref psycasterNextSmokepopTick, "psycasterNextSmokepopTick", -1);
            Scribe_Values.Look(ref psycasterMeleeCommitTargetThingId, "psycasterMeleeCommitTargetThingId", -1);
            Scribe_Values.Look(ref psycasterMeleeCommitUntilTick, "psycasterMeleeCommitUntilTick", -1);
            Scribe_Values.Look(ref psycasterLastInvisibilityTick, "psycasterLastInvisibilityTick", -999999);
            Scribe_Values.Look(ref psycasterComboTargetThingId, "psycasterComboTargetThingId", -1);
            Scribe_Values.Look(ref psycasterComboStage, "psycasterComboStage", 0);
            Scribe_Values.Look(ref psycasterComboExpireTick, "psycasterComboExpireTick", -1);
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
