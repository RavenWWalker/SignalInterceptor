using RimWorld;
using System.Collections.Generic;
using System.Linq;
using Verse;
using Verse.AI;

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

        private void TickDoppelgangerSettlementCleanup()
        {
            if (Find.TickManager.TicksGame % 250 == 0)
            {
                CleanupDoppelgangerSettlements();
            }
        }

        private void TickVIPSites()
        {
            // VIP — каждый тик для мгновенного спавна + проверка победы/ухода с карты
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

                /*
                 * Если это сайт двойников, VIP уже был заспавнен,
                 * но карты больше нет — игрок покинул сайт.
                 * Значит временную фракцию нужно убрать из глобального списка.
                 */
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

                // Проверяем победу — карта загружена, VIP был, врагов не осталось
                if (data.vipSpawned && !data.rewardGiven && data.site.HasMap)
                {
                    Map siteMap = data.site.Map;

                    bool enemiesAlive = siteMap.mapPawns.AllPawnsSpawned
                        .Any(p => p.Faction != null
                               && p.Faction.HostileTo(Faction.OfPlayer)
                               && !p.Dead
                               && !p.Downed);

                    if (!enemiesAlive)
                    {
                        data.rewardGiven = true;

                        if (data.subtype == VIPSubtype.DoppelgangerVIP)
                        {
                            GiveVIPVictoryReward();
                            DeactivateDoppelgangerFaction(data.enemyFaction);
                            data.enemyFaction = null;
                        }

                        if (data.subtype == VIPSubtype.MechanitorSignalVIP)
                        {
                            DeactivateRogueMechanitorFaction(data.enemyFaction);
                            data.enemyFaction = null;
                        }
                    }
                }
            }
        }

        private void TickStashSites()
        {
            // Обработка stash сайтов
            for (int i = trackedSites.Count - 1; i >= 0; i--)
            {
                StashSiteData data = trackedSites[i];

                if (data.site == null || !data.site.Spawned)
                {
                    trackedSites.RemoveAt(i);
                    continue;
                }

                if (!data.lootSpawned && data.site.HasMap)
                {
                    SpawnLoot(data.site.Map, data.threatPoints);
                    data.lootSpawned = true;
                }
            }
        }
    }
}
