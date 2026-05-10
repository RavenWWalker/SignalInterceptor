using System.Linq;
using RimWorld;
using Verse;

namespace SignalInterceptor
{
    public class HediffComp_PsycasterRestoringMechanisms : HediffComp
    {
        private int lastDamageTakenTick = -999999;
        private int nextHealTick;
        private int nextBleedingSealLogTick = -1;

        public HediffCompProperties_PsycasterRestoringMechanisms Props
        {
            get { return (HediffCompProperties_PsycasterRestoringMechanisms)props; }
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

            /*
             * ВАЖНЫЙ ФИКС:
             * Перед обычным лечением удаляем кровоточащие Hediff_Injury,
             * которые остались на уже отсутствующих частях тела.
             *
             * Это не лечит missing part и не возвращает конечность.
             * Это только "запечатывает" фантомное кровотечение после ампутации.
             */
            bool sealedMissingPartBleeding = SealBleedingOnMissingParts(pawn);

            bool needsRepair = NeedsBodyRepair(pawn);

            if (!needsRepair && !sealedMissingPartBleeding)
                return;

            bool healed = false;

            if (needsRepair)
            {
                healed = HealBleedingFirst(pawn);

                if (!healed)
                    healed = HealAnyInjury(pawn);

                ReduceBloodLoss(pawn);
            }

            if ((healed || sealedMissingPartBleeding) && Prefs.DevMode &&
                (pawn.health.summaryHealth.SummaryHealthPercent < 0.99f || HasDangerousBleeding()))
            {
                Log.Message(
                    "[Signal Interceptor] Restoring Mechanisms tick: " +
                    pawn.LabelShort +
                    " | hp=" + pawn.health.summaryHealth.SummaryHealthPercent.ToString("F2") +
                    " | ticksSinceDamage=" + TicksSinceDamage +
                    " | sealedMissingPartBleeding=" + sealedMissingPartBleeding);
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
                .Any(h =>
                    h != null &&
                    h.Severity > 0f &&
                    (
                        h.Severity > 0.05f ||
                        h.BleedRate > 0.001f ||
                        IsOnMissingPartOrMissingAncestor(pawn, h.Part)
                    ));
        }

        private bool HealBleedingFirst(Pawn pawn)
        {
            if (pawn == null || pawn.health == null || pawn.health.hediffSet == null)
                return false;

            Hediff_Injury injury = pawn.health.hediffSet.hediffs
                .OfType<Hediff_Injury>()
                .Where(h => h != null && h.Severity > 0f && h.BleedRate > 0.001f)
                .OrderByDescending(h => IsOnMissingPartOrMissingAncestor(pawn, h.Part))
                .ThenByDescending(h => h.BleedRate)
                .ThenByDescending(h => h.Severity)
                .FirstOrDefault();

            if (injury == null)
                return false;

            /*
             * Если рана находится на уже отсутствующей части тела —
             * это фантомное кровотечение после ампутации.
             * Его надо удалить, а не пытаться лечить численно.
             */
            if (IsOnMissingPartOrMissingAncestor(pawn, injury.Part))
            {
                RemovePhantomBleedingInjury(pawn, injury, "HealBleedingFirst");
                return true;
            }

            injury.Heal(Props.healAmount * Props.bleedingHealMultiplier);
            return true;
        }

        private bool HealAnyInjury(Pawn pawn)
        {
            if (pawn == null || pawn.health == null || pawn.health.hediffSet == null)
                return false;

            Hediff_Injury injury = pawn.health.hediffSet.hediffs
                .OfType<Hediff_Injury>()
                .Where(h => h != null && h.Severity > 0f)
                .OrderByDescending(h => IsOnMissingPartOrMissingAncestor(pawn, h.Part))
                .ThenByDescending(h => h.Severity)
                .FirstOrDefault();

            if (injury == null)
                return false;

            if (IsOnMissingPartOrMissingAncestor(pawn, injury.Part))
            {
                RemovePhantomBleedingInjury(pawn, injury, "HealAnyInjury");
                return true;
            }

            injury.Heal(Props.healAmount);
            return true;
        }

        private bool SealBleedingOnMissingParts(Pawn pawn)
        {
            if (pawn == null || pawn.health == null || pawn.health.hediffSet == null)
                return false;

            bool removedAny = false;

            /*
             * ToList() обязателен, потому что мы можем удалять hediff'ы
             * из исходной коллекции во время прохода.
             */
            var injuries = pawn.health.hediffSet.hediffs
                .OfType<Hediff_Injury>()
                .Where(h =>
                    h != null &&
                    h.Severity > 0f &&
                    h.BleedRate > 0.001f &&
                    IsOnMissingPartOrMissingAncestor(pawn, h.Part))
                .OrderByDescending(h => h.BleedRate)
                .ThenByDescending(h => h.Severity)
                .ToList();

            if (injuries.Count <= 0)
                return false;

            /*
             * За один interval можно убрать несколько фантомных кровотечений.
             * Это не обычное лечение, а cleanup невалидных bleeding injuries.
             */
            for (int i = 0; i < injuries.Count; i++)
            {
                Hediff_Injury injury = injuries[i];

                if (injury == null)
                    continue;

                /*
                 * Дополнительная защита:
                 * если hediff уже был удалён чем-то другим между ToList() и этим местом,
                 * не пытаемся удалить повторно.
                 */
                if (!pawn.health.hediffSet.hediffs.Contains(injury))
                    continue;

                RemovePhantomBleedingInjury(pawn, injury, "SealBleedingOnMissingParts");
                removedAny = true;
            }

            return removedAny;
        }

        private void RemovePhantomBleedingInjury(Pawn pawn, Hediff_Injury injury, string source)
        {
            if (pawn == null || pawn.health == null || pawn.health.hediffSet == null || injury == null)
                return;

            /*
             * Hediff не имеет Destroyed.
             * Проверяем, что injury всё ещё находится в hediffSet.
             */
            if (!pawn.health.hediffSet.hediffs.Contains(injury))
                return;

            float bleedRate = injury.BleedRate;
            float severity = injury.Severity;
            string partLabel = injury.Part != null ? injury.Part.Label : "null";
            string injuryLabel = injury.Label;

            pawn.health.RemoveHediff(injury);

            int now = Find.TickManager.TicksGame;

            if (Prefs.DevMode && now >= nextBleedingSealLogTick)
            {
                nextBleedingSealLogTick = now + 120;

                Log.Message(
                    "[Signal Interceptor] Restoring Mechanisms sealed phantom bleeding: " +
                    pawn.LabelShort +
                    " | source=" + source +
                    " | injury=" + injuryLabel +
                    " | part=" + partLabel +
                    " | severity=" + severity.ToString("F2") +
                    " | bleedRate=" + bleedRate.ToString("F4") +
                    " | ticksSinceDamage=" + TicksSinceDamage);
            }
        }

        private bool IsOnMissingPartOrMissingAncestor(Pawn pawn, BodyPartRecord part)
        {
            if (pawn == null || pawn.health == null || pawn.health.hediffSet == null)
                return false;

            if (part == null)
                return false;

            BodyPartRecord cur = part;

            while (cur != null)
            {
                if (pawn.health.hediffSet.PartIsMissing(cur))
                    return true;

                cur = cur.parent;
            }

            return false;
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
            Pawn pawn = parent != null ? parent.pawn : null;

            if (pawn == null || pawn.Destroyed || pawn.Dead)
                return false;

            if (pawn.health == null || pawn.health.hediffSet == null)
                return false;

            return pawn.health.hediffSet.hediffs
                .OfType<Hediff_Injury>()
                .Any(h =>
                    h != null &&
                    h.Severity > 0f &&
                    h.BleedRate > 0.01f);
        }

        public override void CompExposeData()
        {
            base.CompExposeData();

            Scribe_Values.Look(ref lastDamageTakenTick, "lastDamageTakenTick", -999999);
            Scribe_Values.Look(ref nextHealTick, "nextHealTick", 0);
            Scribe_Values.Look(ref nextBleedingSealLogTick, "nextBleedingSealLogTick", -1);
        }
    }
}
