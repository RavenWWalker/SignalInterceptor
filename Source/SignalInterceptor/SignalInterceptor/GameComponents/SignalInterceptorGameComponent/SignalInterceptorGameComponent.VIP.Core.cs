using RimWorld;
using RimWorld.Planet;
using System.Linq;
using Verse;

namespace SignalInterceptor
{
    public partial class SignalInterceptorGameComponent
    {
        public void TrackVIPSite(Site site, float threatPoints, Faction faction, VIPSubtype subtype, int timeoutTicks)
        {
            trackedVIPSites.Add(new VIPSiteData
            {
                site = site,
                threatPoints = threatPoints,
                faction = faction,
                enemyFaction = null,
                subtype = subtype,
                vipSpawned = false,
                rewardGiven = false,
                expireTick = timeoutTicks > 0 ? Find.TickManager.TicksGame + timeoutTicks : -1
            });

            Log.Message("[Signal Interceptor] Tracking VIP site. " +
                        "Subtype: " + subtype +
                        " | Context faction: " + (faction?.Name ?? "null") +
                        " | Site faction: " + (site?.Faction?.Name ?? "null") +
                        " | Enemy faction: null" +
                        " | Threat: " + threatPoints +
                        " | Expire tick: " + (timeoutTicks > 0 ? (Find.TickManager.TicksGame + timeoutTicks).ToString() : "handled by quest timeout"));
        }

        public void GiveVIPVictoryReward()
        {
            Faction rewardFaction = Find.FactionManager.AllFactions
                .Where(f => !f.IsPlayer && !f.defeated && !f.Hidden && !f.HostileTo(Faction.OfPlayer))
                .RandomElementWithFallback(null);

            if (rewardFaction != null)
            {
                int goodwillBonus = Rand.RangeInclusive(10, 25);
                int before = rewardFaction.GoodwillWith(Faction.OfPlayer);
                rewardFaction.TryAffectGoodwillWith(Faction.OfPlayer, goodwillBonus,
                    canSendMessage: false, canSendHostilityLetter: false);
                int after = rewardFaction.GoodwillWith(Faction.OfPlayer);
                int actualDelta = after - before;

                Find.LetterStack.ReceiveLetter(
                    "SI_VIP_SuccessTitle".Translate(),
                    "SI_VIP_SuccessText".Translate(rewardFaction.Name, actualDelta.ToString()),
                    LetterDefOf.PositiveEvent);
            }
        }

        private void SpawnVIP(VIPSiteData data)
        {
            Map map = data.site.Map;

            switch (data.subtype)
            {
                case VIPSubtype.ShuttleVIP:
                    SpawnShuttleVIP(map, data);
                    break;

                case VIPSubtype.PsycasterVIP:
                    SpawnPsycasterVIP(map, data);
                    break;

                case VIPSubtype.MechanitorSignalVIP:
                    SpawnMechanitorSignalVIP(map, data);
                    break;

                case VIPSubtype.PilgrimVIP:
                    SpawnShuttleVIP(map, data); // TODO: подтип 4
                    break;

                case VIPSubtype.DoppelgangerVIP:
                    SpawnDoppelgangerVIP(map, data);
                    break;
            }

            Log.Message("[Signal Interceptor] VIP spawned. Subtype: " + data.subtype);
        }

        private int GetVIPTier(float points)
        {
            if (points >= 4500f) return 9;
            if (points >= 3600f) return 8;
            if (points >= 2800f) return 7;
            if (points >= 2200f) return 6;
            if (points >= 1700f) return 5;
            if (points >= 1200f) return 4;
            if (points >= 800f) return 3;
            if (points >= 450f) return 2;
            return 1;
        }

        private bool HasActiveHostileThreats(Map map, Faction expectedEnemyFaction = null)
        {
            if (map == null)
            {
                return false;
            }

            return map.mapPawns.AllPawnsSpawned
                .Any(p =>
                    p != null
                    && !p.Dead
                    && !p.Downed
                    && p.Faction != null
                    && p.Faction.HostileTo(Faction.OfPlayer)
                    && (expectedEnemyFaction == null || p.Faction == expectedEnemyFaction));
        }

        private void CompleteVIPQuest(VIPSiteData data)
        {
            if (data == null || data.rewardGiven)
            {
                return;
            }

            data.rewardGiven = true;

            switch (data.subtype)
            {
                case VIPSubtype.DoppelgangerVIP:
                    Find.LetterStack.ReceiveLetter(
                        "SI_VIP_CompletedTitle".Translate(),
                        "SI_VIP_CompletedText".Translate(),
                        LetterDefOf.PositiveEvent
                    );

                    GiveVIPVictoryReward();

                    DeactivateDoppelgangerFaction(data.enemyFaction);
                    data.enemyFaction = null;
                    break;

                case VIPSubtype.MechanitorSignalVIP:
                    Find.LetterStack.ReceiveLetter(
                        "SI_VIP_CompletedTitle".Translate(),
                        "SI_MechanitorSignal_CompletedText".Translate(),
                        LetterDefOf.PositiveEvent
                    );

                    DeactivateRogueMechanitorFaction(data.enemyFaction);
                    data.enemyFaction = null;
                    break;

                default:
                    Find.LetterStack.ReceiveLetter(
                        "SI_VIP_CompletedTitle".Translate(),
                        "SI_VIP_CompletedText".Translate(),
                        LetterDefOf.PositiveEvent
                    );
                    break;
            }

            Log.Message("[Signal Interceptor] VIP quest completed. Subtype=" + data.subtype);
        }

        private void FailVIPQuest(VIPSiteData data, string reasonKey)
        {
            if (data == null || data.rewardGiven)
            {
                return;
            }

            data.rewardGiven = true;

            string title = "SI_VIP_FailedTitle".Translate();

            string text;

            if (!reasonKey.NullOrEmpty() && reasonKey.CanTranslate())
            {
                text = reasonKey.Translate().ToString();
            }
            else
            {
                text = "SI_VIP_FailedText".Translate().ToString();
            }

            Find.LetterStack.ReceiveLetter(
                title,
                text,
                LetterDefOf.NegativeEvent
            );

            if (data.subtype == VIPSubtype.DoppelgangerVIP)
            {
                DeactivateDoppelgangerFaction(data.enemyFaction);
                data.enemyFaction = null;
            }

            if (data.subtype == VIPSubtype.MechanitorSignalVIP)
            {
                DeactivateRogueMechanitorFaction(data.enemyFaction);
                data.enemyFaction = null;
            }

            Log.Message("[Signal Interceptor] VIP quest failed. Subtype=" +
                        data.subtype +
                        " | Reason=" + reasonKey);
        }

        public void TrySpawnVIPOnMapGenerated(Map map)
        {
            for (int i = trackedVIPSites.Count - 1; i >= 0; i--)
            {
                VIPSiteData data = trackedVIPSites[i];

                if (data.site == null || !data.site.Spawned)
                    continue;

                if (data.vipSpawned)
                    continue;

                if (data.site.Map != map)
                    continue;

                try
                {
                    SpawnVIP(data);
                    data.vipSpawned = true;

                    Log.Message("[Signal Interceptor] VIP spawned during map generation for subtype: " + data.subtype);
                }
                catch (System.Exception ex)
                {
                    Log.Error("[Signal Interceptor] Exception while spawning VIP during map generation. " +
                              "Subtype: " + data.subtype +
                              " | Site: " + (data.site?.LabelCap ?? "null") +
                              " | Exception: " + ex);

                    if (data.subtype == VIPSubtype.DoppelgangerVIP)
                    {
                        DeactivateDoppelgangerFaction(data.enemyFaction);
                        data.enemyFaction = null;
                    }

                    /*
                     * Ставим true, чтобы ошибка не повторялась каждый тик бесконечно.
                     * Иначе будет тот самый спам Duplicate stacktrace / Random state stack.
                     */
                    data.vipSpawned = true;
                    data.rewardGiven = true;
                }

                break;
            }
        }

        private void TickVIPSites()
        {
            for (int i = trackedVIPSites.Count - 1; i >= 0; i--)
            {
                VIPSiteData data = trackedVIPSites[i];

                if (data == null)
                {
                    trackedVIPSites.RemoveAt(i);
                    continue;
                }

                if (!data.rewardGiven && data.expireTick > 0 && Find.TickManager.TicksGame >= data.expireTick)
                {
                    if (data.site != null && data.site.HasMap)
                    {
                        data.expireTick = -1;

                        Log.Message("[Signal Interceptor] VIP site timer expired, but map is active. Timeout disabled. Subtype=" + data.subtype);

                        continue;
                    }

                    FailVIPQuest(data, "SI_VIP_FailedExpiredText");

                    if (data.site != null && data.site.Spawned)
                    {
                        Find.WorldObjects.Remove(data.site);
                    }

                    if (data.subtype == VIPSubtype.DoppelgangerVIP)
                    {
                        DeactivateDoppelgangerFaction(data.enemyFaction);
                        data.enemyFaction = null;
                    }

                    if (data.subtype == VIPSubtype.MechanitorSignalVIP)
                    {
                        DeactivateRogueMechanitorFaction(data.enemyFaction);
                        data.enemyFaction = null;
                    }

                    trackedVIPSites.RemoveAt(i);
                    continue;
                }

                if (data.site == null || !data.site.Spawned)
                {
                    if (data.subtype == VIPSubtype.PsycasterVIP)
                    {
                        if (data.psycasterPawn != null
                            && !data.psycasterPawn.Destroyed
                            && !data.psycasterPawn.Dead
                            && !data.rewardGiven)
                        {
                            TickPsycasterVIP(data);
                            continue;
                        }
                    }

                    if (!data.rewardGiven)
                    {
                        FailVIPQuest(data, "SI_VIP_FailedExpiredText");
                    }

                    if (data.subtype == VIPSubtype.DoppelgangerVIP)
                    {
                        DeactivateDoppelgangerFaction(data.enemyFaction);
                        data.enemyFaction = null;
                    }

                    if (data.subtype == VIPSubtype.MechanitorSignalVIP)
                    {
                        DeactivateRogueMechanitorFaction(data.enemyFaction);
                        data.enemyFaction = null;
                    }

                    trackedVIPSites.RemoveAt(i);
                    continue;
                }

                if (data.subtype == VIPSubtype.DoppelgangerVIP
                    && data.vipSpawned
                    && data.enemyFaction != null
                    && !data.site.HasMap)
                {
                    DeactivateDoppelgangerFaction(data.enemyFaction);
                    data.enemyFaction = null;

                    Log.Message("[Signal Interceptor] Doppelganger map left. Temporary faction deactivated.");
                }

                if (data.subtype == VIPSubtype.MechanitorSignalVIP
                    && data.vipSpawned
                    && data.enemyFaction != null
                    && data.site != null
                    && !data.site.HasMap
                    && !data.rewardGiven)
                {
                    FailVIPQuest(data, "SI_MechanitorSignal_FailedFledText");

                    DeactivateRogueMechanitorFaction(data.enemyFaction);
                    data.enemyFaction = null;

                    Log.Message("[Signal Interceptor] Mechanitor signal map left. Quest failed.");
                }

                if (!data.vipSpawned && data.site.HasMap)
                {
                    SpawnVIP(data);
                    data.vipSpawned = true;
                }

                if (data.subtype == VIPSubtype.MechanitorSignalVIP
                    && data.vipSpawned
                    && data.site != null
                    && data.site.HasMap
                    && data.enemyFaction != null
                    && Find.TickManager.TicksGame % 30 == 0)
                {
                    EnforceMechanitorSignalCombat(data);
                }

                if (data.subtype == VIPSubtype.PsycasterVIP
                    && data.vipSpawned
                    && !data.rewardGiven
                    && Find.TickManager.TicksGame % 15 == 0)
                {
                    TickPsycasterVIP(data);
                }

                if (data.vipSpawned && !data.rewardGiven && data.site.HasMap)
                {
                    Map siteMap = data.site.Map;

                    if (data.subtype == VIPSubtype.PsycasterVIP)
                    {
                        if (data.psycasterPawn == null || data.psycasterPawn.Destroyed || data.psycasterPawn.Dead)
                        {
                            FailVIPQuest(data, "SI_VIP_FailedText");
                            continue;
                        }

                        continue;
                    }

                    bool enemiesAlive = siteMap.mapPawns.AllPawnsSpawned
                        .Any(p => p.Faction != null
                               && p.Faction.HostileTo(Faction.OfPlayer)
                               && !p.Dead
                               && !p.Downed);

                    if (!enemiesAlive)
                    {
                        CompleteVIPQuest(data);
                    }
                }
            }
        }

        private void TickPsycasterVIP(VIPSiteData data)
        {
            if (data == null || data.subtype != VIPSubtype.PsycasterVIP)
                return;

            Pawn psycaster = data.psycasterPawn;

            if (psycaster == null || psycaster.Destroyed)
            {
                FailVIPQuest(data, "SI_VIP_FailedText");
                return;
            }

            if (psycaster.Dead)
            {
                FailVIPQuest(data, "SI_VIP_FailedText");
                return;
            }

            if (IsPsycasterDeliveredToPlayerSettlement(psycaster))
            {
                data.psycasterDelivered = true;
                CompleteVIPQuest(data);
                return;
            }

            if (!psycaster.Spawned)
            {
                if (!psycaster.IsPrisonerOfColony)
                {
                    FailVIPQuest(data, "SI_VIP_FailedText");
                }

                return;
            }

            if (psycaster.Spawned && psycaster.Map != null && psycaster.Map == data.site?.Map)
            {
                TickPsycasterCombatAI(data, psycaster, psycaster.Map);
            }
        }

        private bool IsPsycasterDeliveredToPlayerSettlement(Pawn psycaster)
        {
            if (psycaster == null || psycaster.Dead || psycaster.Destroyed)
                return false;

            if (!psycaster.Spawned || psycaster.Map == null)
                return false;

            if (!psycaster.IsPrisonerOfColony)
                return false;

            return psycaster.Map.IsPlayerHome;
        }

    }
}
