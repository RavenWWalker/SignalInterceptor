using RimWorld;
using System.Collections.Generic;
using System.Linq;
using Verse;

namespace SignalInterceptor
{
    public partial class SignalInterceptorGameComponent
    {
        public void ScheduleSlaveDelivery(Map map, int delayTicks)
        {
            pendingSlaveDeliveries.Add(new PendingSlaveDelivery
            {
                mapId = map.uniqueID,
                deliveryTick = Find.TickManager.TicksGame + delayTicks
            });
        }

        public void ScheduleCounterIntelRaid(Map map, Faction faction, float points, int delayTicks)
        {
            pendingRaids.Add(new PendingRaid
            {
                mapId = map.uniqueID,
                faction = faction,
                points = points,
                fireTick = Find.TickManager.TicksGame + delayTicks
            });
            Log.Message("[Signal Interceptor] Counter-intel raid scheduled. Faction: " + faction.Name +
                        " | Points: " + points + " | Fire tick: " + (Find.TickManager.TicksGame + delayTicks));
        }

        private void DeliverSlave(PendingSlaveDelivery delivery)
        {
            Map map = Find.Maps.FirstOrDefault(m => m.uniqueID == delivery.mapId);
            if (map == null) return;

            PawnGenerationRequest request = new PawnGenerationRequest(
                kind: PawnKindDefOf.Slave,
                faction: null,
                context: PawnGenerationContext.NonPlayer,
                forceGenerateNewPawn: true,
                canGeneratePawnRelations: false,
                allowFood: false
            );

            Pawn slave = PawnGenerator.GeneratePawn(request);
            if (slave == null) return;

            slave.guest?.SetGuestStatus(Faction.OfPlayer, GuestStatus.Slave);

            IntVec3 edgeCell;
            if (!CellFinder.TryFindRandomEdgeCellWith(
                    (IntVec3 c) => map.reachability.CanReachColony(c),
                    map, CellFinder.EdgeRoadChance_Neutral, out edgeCell))
            {
                edgeCell = CellFinder.RandomEdgeCell(map);
            }

            GenSpawn.Spawn(slave, edgeCell, map);

            Find.LetterStack.ReceiveLetter(
                "SI_LetterSlaveArrivedTitle".Translate(),
                "SI_LetterSlaveArrivedText".Translate(slave.LabelShort),
                LetterDefOf.PositiveEvent,
                new LookTargets(slave)
            );
        }

        private void ExecuteRaid(PendingRaid raid)
        {
            Map map = Find.Maps.FirstOrDefault(m => m.uniqueID == raid.mapId);
            if (map == null) return;
            if (raid.faction == null) return;
            if (raid.faction.defeated) return;

            IncidentParms parms = new IncidentParms();
            parms.target = map;
            parms.faction = raid.faction;
            parms.points = raid.points;
            parms.forced = true;

            bool success = IncidentDefOf.RaidEnemy.Worker.TryExecute(parms);

            if (!success)
            {
                if (!raid.faction.HostileTo(Faction.OfPlayer))
                {
                    raid.faction.TryAffectGoodwillWith(Faction.OfPlayer, -200, canSendMessage: false, canSendHostilityLetter: false);
                }

                IncidentParms parms2 = new IncidentParms();
                parms2.target = map;
                parms2.faction = raid.faction;
                parms2.points = raid.points * 2f;
                parms2.forced = true;

                IncidentDefOf.RaidEnemy.Worker.TryExecute(parms2);
            }
        }

        private void TickPendingSlaveDeliveries()
        {
            // Обработка отложенных доставок рабов
            for (int i = pendingSlaveDeliveries.Count - 1; i >= 0; i--)
            {
                PendingSlaveDelivery delivery = pendingSlaveDeliveries[i];

                if (Find.TickManager.TicksGame >= delivery.deliveryTick)
                {
                    DeliverSlave(delivery);
                    pendingSlaveDeliveries.RemoveAt(i);
                }
            }
        }

        private void TickPendingRaids()
        {
            // Обработка отложенных рейдов контрразведки
            for (int i = pendingRaids.Count - 1; i >= 0; i--)
            {
                PendingRaid raid = pendingRaids[i];

                if (Find.TickManager.TicksGame >= raid.fireTick)
                {
                    ExecuteRaid(raid);
                    pendingRaids.RemoveAt(i);
                }
            }
        }
    }
}
