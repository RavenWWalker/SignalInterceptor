using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace SignalInterceptor
{
    public class JobDriver_ScanSignals : JobDriver
    {
        private const int SessionDurationTicks = 2500;

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

            // Проверяем сканирование перед началом и во время работы
            this.FailOn(delegate
            {
                CompSignalInterceptor comp = Comp;
                return comp == null || !comp.ScanningEnabled;
            });

            // Шаг 1: Идём к консоли
            yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.InteractionCell);

            // Шаг 2: Сканируем один сеанс
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
            scan.defaultDuration = SessionDurationTicks;
            scan.activeSkill = () => SkillDefOf.Intellectual;
            yield return scan;
        }
    }
}
