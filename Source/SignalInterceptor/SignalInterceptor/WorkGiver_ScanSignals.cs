using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;

namespace SignalInterceptor
{
    public class WorkGiver_ScanSignals : WorkGiver_Scanner
    {
        public override ThingRequest PotentialWorkThingRequest
            => ThingRequest.ForDef(ThingDefOf.CommsConsole);

        public override PathEndMode PathEndMode
            => PathEndMode.InteractionCell;

        public override bool HasJobOnThing(Pawn pawn, Thing thing, bool forced = false)
        {
            CompSignalInterceptor comp = thing.TryGetComp<CompSignalInterceptor>();
            if (comp == null)
                return false;

            if (!comp.ScanningEnabled)
                return false;

            if (thing.Faction != Faction.OfPlayer)
                return false;

            CompPowerTrader power = thing.TryGetComp<CompPowerTrader>();
            if (power != null && !power.PowerOn)
                return false;

            if (!pawn.CanReserve(thing, 1, -1, null, forced))
                return false;

            return true;
        }

        public override Job JobOnThing(Pawn pawn, Thing thing, bool forced = false)
        {
            return JobMaker.MakeJob(
                DefDatabase<JobDef>.GetNamed("SignalInterceptor_ScanSignals"),
                thing
            );
        }

        public override string PostProcessedGerund(Job job)
        {
            return "SI_JobGerund".Translate(job.targetA.Thing?.LabelShort ?? "");
        }
    }
}
