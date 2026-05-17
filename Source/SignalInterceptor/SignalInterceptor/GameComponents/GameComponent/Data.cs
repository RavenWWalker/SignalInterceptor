using RimWorld;
using RimWorld.Planet;
using SignalInterceptor.AI.Psycaster;
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

        // Psycaster VIP — состояние квеста (НЕ AI). Сохраняется в сейв.
        public Pawn psycasterPawn;
        public bool psycasterDelivered;
        public bool psycasterDestabilized;
        public bool psycasterWasShocked;
        public Thing psycasterAnimaTree;
        public bool psycasterAnimaTreeLinked;
        public bool psycasterNeurostormTriggered;
        public bool psycasterTreeDestroyedLetterSent;

        /// <summary>
        /// Мозг пси-кастера. Не сериализуется — после загрузки сейва пересоздаётся
        /// в SignalInterceptorGameComponent при первом вызове TickPsycasterCombatAI.
        /// </summary>
        [Unsaved(false)]
        public PsycasterBrain psycasterBrain;

        // ============================================================
        // ВРЕМЕННЫЕ ПОЛЯ для совместимости со старым AI-кодом.
        // Старый код в VIP.Psycaster.cs (TryRunPsycasterRangedGroupMode и др.) ещё ссылается
        // на эти поля. Мы их физически больше не используем — новый AI работает через PsycasterBrain.
        // Все эти поля помечены [Unsaved] и будут полностью удалены вместе со старым кодом
        // в финальной пачке cleanup.
        // ============================================================

        [Unsaved(false)] public int psycasterNextCastTick = -1;
        [Unsaved(false)] public bool psycasterFocusUsed;
        [Unsaved(false)] public int psycasterNextDefensiveCastTick = -1;
        [Unsaved(false)] public int psycasterNextWallraiseTick = -1;
        [Unsaved(false)] public int psycasterNextSmokepopTick = -1;
        [Unsaved(false)] public int psycasterMeleeCommitTargetThingId = -1;
        [Unsaved(false)] public int psycasterMeleeCommitUntilTick = -1;
        [Unsaved(false)] public int psycasterLastInvisibilityTick = -999999;
        [Unsaved(false)] public int psycasterComboTargetThingId = -1;
        [Unsaved(false)] public int psycasterComboStage = 0;
        [Unsaved(false)] public int psycasterComboExpireTick = -1;
        [Unsaved(false)] public int psycasterMode = 0;
        [Unsaved(false)] public int psycasterModeUntilTick = -1;
        [Unsaved(false)] public int psycasterModeTargetThingId = -1;
        [Unsaved(false)] public int psycasterNextThinkTick = -1;
        [Unsaved(false)] public int psycasterLastBlindingPulseTick = -999999;
        [Unsaved(false)] public int psycasterLastVertigoPulseTick = -999999;
        [Unsaved(false)] public int psycasterLastBerserkPulseTick = -999999;

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
            Scribe_References.Look(ref psycasterAnimaTree, "psycasterAnimaTree");
            Scribe_Values.Look(ref psycasterAnimaTreeLinked, "psycasterAnimaTreeLinked", false);
            Scribe_Values.Look(ref psycasterNeurostormTriggered, "psycasterNeurostormTriggered", false);
            Scribe_Values.Look(ref psycasterTreeDestroyedLetterSent, "psycasterTreeDestroyedLetterSent", false);
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
