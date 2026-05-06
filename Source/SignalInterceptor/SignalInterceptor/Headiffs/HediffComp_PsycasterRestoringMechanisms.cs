using System.Linq;
using RimWorld;
using Verse;

namespace SignalInterceptor
{
    public class HediffComp_PsycasterRestoringMechanisms : HediffComp
    {
        private int lastDamageTakenTick = -999999;
        private int nextHealTick;

        public HediffCompProperties_PsycasterRestoringMechanisms Props
        {
            get { return (HediffCompProperties_PsycasterRestoringMechanisms)props; }
        }

        public Pawn Pawn
        {
            get { return parent != null ? parent.pawn : null; }
        }

        public int LastDamageTakenTick
        {
            get { return lastDamageTakenTick; }
        }

        public int TicksSinceDamage
        {
            get { return Find.TickManager.TicksGame - lastDamageTakenTick; }
        }

        public bool RecentlyDamaged
        {
            get { return TicksSinceDamage < Props.noDamageDelayTicks; }
        }

        public bool CanRegenerateNow
        {
            get
            {
                Pawn pawn = Pawn;

                if (pawn == null || pawn.Destroyed || pawn.Dead)
                    return false;

                if (Props.onlyWhenNotRecentlyDamaged && RecentlyDamaged)
                    return false;

                return true;
            }
        }

        public void Notify_Damaged()
        {
            lastDamageTakenTick = Find.TickManager.TicksGame;
        }

        public override void CompPostTick(ref float severityAdjustment)
        {
            base.CompPostTick(ref severityAdjustment);

            Pawn pawn = Pawn;

            if (pawn == null || pawn.Destroyed || pawn.Dead)
                return;

            int tick = Find.TickManager.TicksGame;

            if (tick < nextHealTick)
                return;

            nextHealTick = tick + Props.intervalTicks;

            if (!CanRegenerateNow)
                return;

            if (!NeedsBodyRepair(pawn))
                return;

            bool healed = HealBleedingFirst(pawn);

            if (!healed)
                healed = HealAnyInjury(pawn);

            ReduceBloodLoss(pawn);

            if (healed && Prefs.DevMode &&
                (pawn.health.summaryHealth.SummaryHealthPercent < 0.99f || HasDangerousBleeding()))
            {
                Log.Message(
                    "[Signal Interceptor] Restoring Mechanisms tick: " +
                    pawn.LabelShort +
                    " | hp=" + pawn.health.summaryHealth.SummaryHealthPercent.ToString("F2") +
                    " | ticksSinceDamage=" + TicksSinceDamage);
            }
        }

        private bool NeedsBodyRepair(Pawn pawn)
        {
            if (pawn == null || pawn.health == null || pawn.health.hediffSet == null)
                return false;

            if (pawn.health.hediffSet.GetFirstHediffOfDef(HediffDefOf.BloodLoss) != null)
                return true;

            return pawn.health.hediffSet.hediffs
                .OfType<Hediff_Injury>()
                .Any(h => h != null && h.Severity > 0.05f);
        }

        private bool HealBleedingFirst(Pawn pawn)
        {
            Hediff_Injury injury = pawn.health.hediffSet.hediffs
                .OfType<Hediff_Injury>()
                .Where(h => h != null && h.Severity > 0f && h.BleedRate > 0.001f)
                .OrderByDescending(h => h.BleedRate)
                .ThenByDescending(h => h.Severity)
                .FirstOrDefault();

            if (injury == null)
                return false;

            injury.Heal(Props.healAmount * Props.bleedingHealMultiplier);
            return true;
        }

        private bool HealAnyInjury(Pawn pawn)
        {
            Hediff_Injury injury = pawn.health.hediffSet.hediffs
                .OfType<Hediff_Injury>()
                .Where(h => h != null && h.Severity > 0f)
                .OrderByDescending(h => h.Severity)
                .FirstOrDefault();

            if (injury == null)
                return false;

            injury.Heal(Props.healAmount);
            return true;
        }

        private void ReduceBloodLoss(Pawn pawn)
        {
            Hediff bloodLoss = pawn.health.hediffSet.GetFirstHediffOfDef(HediffDefOf.BloodLoss);

            if (bloodLoss == null)
                return;

            bloodLoss.Severity -= Props.bloodLossReduction;

            if (bloodLoss.Severity <= 0.001f)
            {
                pawn.health.RemoveHediff(bloodLoss);
            }
        }

        public bool HasDangerousBleeding()
        {
            Pawn pawn = Pawn;

            if (pawn == null || pawn.Destroyed || pawn.Dead)
                return false;

            return pawn.health.hediffSet.hediffs
                .OfType<Hediff_Injury>()
                .Any(h => h != null && h.Severity > 0f && h.BleedRate > 0.01f);
        }

        public override void CompExposeData()
        {
            base.CompExposeData();

            Scribe_Values.Look(ref lastDamageTakenTick, "lastDamageTakenTick", -999999);
            Scribe_Values.Look(ref nextHealTick, "nextHealTick", 0);
        }
    }
}
