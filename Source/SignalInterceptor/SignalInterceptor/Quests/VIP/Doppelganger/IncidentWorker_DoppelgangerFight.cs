using RimWorld;
using System.Collections.Generic;
using System.Linq;
using Verse;
using Verse.AI;

namespace SignalInterceptor
{
    public class IncidentWorker_DoppelgangerFight : IncidentWorker
    {
        protected override bool CanFireNowSub(IncidentParms parms)
        {
            return GetDoppelgangersOnMap(parms).Count >= 2;
        }

        protected override bool TryExecuteWorker(IncidentParms parms)
        {
            Map map = parms.target as Map;
            if (map == null)
                return false;

            List<Pawn> dopps = GetDoppelgangersOnMap(map);
            if (dopps.Count < 2)
                return false;

            AssignMurderousTargets(dopps);

            string name = dopps.FirstOrDefault()?.LabelShort ?? "Unknown";

            SignalInterceptorGameComponent comp = Current.Game.GetComponent<SignalInterceptorGameComponent>();
            if (comp != null)
            {
                comp.doppelgangerFightActive = true;
                comp.doppelgangerFightStartTick = Find.TickManager.TicksGame;
            }

            Find.LetterStack.ReceiveLetter(
                "SI_DoppelgangerFight_Title".Translate(),
                "SI_DoppelgangerFight_Text".Translate(name),
                LetterDefOf.ThreatSmall,
                new LookTargets(dopps.Cast<Thing>().ToList())
            );

            Log.Message("[Signal Interceptor] Doppelganger fight triggered. Group: " +
                        name +
                        " | Participants: " + dopps.Count);

            return true;
        }

        public static void AssignMurderousTargets(List<Pawn> dopps)
        {
            if (dopps == null || dopps.Count < 2)
                return;

            dopps = dopps
                .Where(p => p != null && !p.Dead && p.Spawned)
                .ToList();

            if (dopps.Count < 2)
                return;

            dopps.Shuffle();

            MentalStateDef murderDef = DefDatabase<MentalStateDef>.GetNamedSilentFail("MurderousRage");
            if (murderDef == null)
            {
                Log.Error("[Signal Interceptor] MurderousRage MentalStateDef not found.");
                return;
            }

            int lettersBefore = Find.LetterStack.LettersListForReading.Count;

            for (int i = 0; i < dopps.Count; i++)
            {
                Pawn attacker = dopps[i];
                Pawn target = dopps[(i + 1) % dopps.Count];

                if (attacker == null || target == null)
                    continue;

                if (attacker.Dead || target.Dead)
                    continue;

                if (attacker.Downed)
                    continue;

                InterruptPawn(attacker);

                bool started = attacker.mindState?.mentalStateHandler?.TryStartMentalState(
                    murderDef,
                    forced: true
                ) ?? false;

                if (!started)
                    continue;

                if (attacker.mindState?.mentalStateHandler?.CurState is MentalState_MurderousRage rage)
                {
                    rage.target = target;
                }
            }

            RemoveExtraMentalBreakLetters(lettersBefore);
        }

        private static void InterruptPawn(Pawn pawn)
        {
            if (pawn == null)
                return;

            if (pawn.CurJob != null && pawn.jobs != null)
            {
                pawn.jobs.EndCurrentJob(JobCondition.InterruptForced);
            }

            if (pawn.InBed())
            {
                Building_Bed bed = pawn.CurrentBed();
                if (bed != null)
                {
                    RestUtility.KickOutOfBed(pawn, bed);
                }
            }
        }

        private static void RemoveExtraMentalBreakLetters(int lettersBefore)
        {
            List<Letter> currentLetters = Find.LetterStack.LettersListForReading;

            while (currentLetters.Count > lettersBefore)
            {
                Find.LetterStack.RemoveLetter(currentLetters[currentLetters.Count - 1]);
            }
        }

        public static List<Pawn> GetDoppelgangersOnMap(Map map)
        {
            if (map == null)
                return new List<Pawn>();

            HediffDef markDef = DefDatabase<HediffDef>.GetNamedSilentFail("SI_DoppelgangerMark");
            if (markDef == null)
                return new List<Pawn>();

            List<Pawn> allMarked = map.mapPawns.AllPawnsSpawned
                .Where(p => p != null
                         && !p.Dead
                         && p.health?.hediffSet != null
                         && p.health.hediffSet.HasHediff(markDef)
                         && IsPlayerControlledOrHeldByPlayer(p))
                .ToList();

            /*
             * Группируем по короткому имени.
             * Все двойники, которых ты создаёшь, получают одинаковое Name,
             * поэтому это должно стабильно их находить.
             */
            IGrouping<string, Pawn> group = allMarked
                .GroupBy(p => p.Name?.ToStringShort ?? "")
                .Where(g => !string.IsNullOrEmpty(g.Key) && g.Count() >= 2)
                .OrderByDescending(g => g.Count())
                .FirstOrDefault();

            if (group == null)
                return new List<Pawn>();

            return group.ToList();
        }

        private static bool IsPlayerControlledOrHeldByPlayer(Pawn pawn)
        {
            if (pawn == null)
                return false;

            if (pawn.Faction == Faction.OfPlayer)
                return true;

            if (pawn.IsPrisonerOfColony)
                return true;

            if (pawn.HostFaction == Faction.OfPlayer)
                return true;

            return false;
        }

        private List<Pawn> GetDoppelgangersOnMap(IncidentParms parms)
        {
            Map map = parms.target as Map;
            if (map == null)
                return new List<Pawn>();

            return GetDoppelgangersOnMap(map);
        }
    }
}
