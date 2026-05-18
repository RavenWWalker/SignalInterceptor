using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace SignalInterceptor
{
    public class JobDriver_ScanSignals : JobDriver
    {
        private CompSignalInterceptor Comp
            => TargetThingA?.TryGetComp<CompSignalInterceptor>();

        public override string GetReport()
        {
            if (TargetThingA != null)
                return "SI_JobReportFull".Translate(TargetThingA.LabelShort);
            return "SI_JobReportFull".Translate("");
        }

        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            return pawn.Reserve(job.targetA, job, 1, -1, null, errorOnFailed);
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedNullOrForbidden(TargetIndex.A);
            this.FailOnBurningImmobile(TargetIndex.A);

            this.FailOn(delegate
            {
                CompSignalInterceptor comp = Comp;

                if (comp == null)
                    return true;

                if (!comp.ScanningEnabled)
                    return true;

                CompPowerTrader power =
                    comp.parent.GetComp<CompPowerTrader>();

                if (power != null && !power.PowerOn)
                    return true;

                return false;
            });

            Toil scan = ToilMaker.MakeToil("ScanSignals");

            scan.initAction = delegate
            {
                pawn.rotationTracker.FaceTarget(TargetThingA);
            };

            scan.tickAction = delegate
            {
                pawn.rotationTracker.FaceTarget(TargetThingA);
                pawn.skills?.Learn(SkillDefOf.Intellectual, 0.035f);

                CompSignalInterceptor comp = Comp;
                if (comp != null && comp.ScanningEnabled)
                {
                    comp.OnScanTick(pawn);
                }
            };

            scan.handlingFacing = true;
            scan.defaultCompleteMode = ToilCompleteMode.Delay;

            // БЕРЁМ длительность из CompProperties
            scan.defaultDuration =
                Comp?.Props?.scanDurationTicks ?? 5000;

            scan.activeSkill = () => SkillDefOf.Intellectual;

            yield return scan;
        }
    }
}
