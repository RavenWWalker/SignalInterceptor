using RimWorld;
using RimWorld.Planet;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Verse;

namespace SignalInterceptor
{
    public partial class SignalInterceptorGameComponent
    {
        public void NotifyPsycasterShockLanceUsed(Pawn target, Pawn user)
        {
            if (target == null || target.Destroyed || target.Dead)
                return;

            VIPSiteData data = FindActivePsycasterVIPData(target);

            if (data == null)
                return;

            if (data.psycasterNeurostormTriggered)
                return;

            if (HasPsycasterTreeShield(target))
                return;

            Map map = target.Map;

            if (map == null && data.site != null && data.site.HasMap)
                map = data.site.Map;

            if (map == null)
                return;

            data.psycasterNeurostormTriggered = true;

            TriggerPsycasterNeurostorm(data, map, target, user);
        }

        private VIPSiteData FindActivePsycasterVIPData(Pawn psycaster)
        {
            if (psycaster == null)
                return null;

            for (int i = 0; i < trackedVIPSites.Count; i++)
            {
                VIPSiteData data = trackedVIPSites[i];

                if (data == null)
                    continue;

                if (data.subtype != VIPSubtype.PsycasterVIP)
                    continue;

                if (data.rewardGiven)
                    continue;

                if (data.psycasterPawn == psycaster)
                    return data;
            }

            return null;
        }

        private void TriggerPsycasterNeurostorm(VIPSiteData data, Map map, Pawn psycaster, Pawn user)
        {
            int affectedFactions = DamageRelationsWithAllFactions();

            int brokenPawns = ForceNeurostormMentalBreaks(map, psycaster);

            Find.LetterStack.ReceiveLetter(
                "SI_PsycasterNeurostormTitle".Translate(),
                "SI_PsycasterNeurostormText".Translate(
                    psycaster != null ? psycaster.LabelShort : "unknown",
                    affectedFactions.ToString(),
                    brokenPawns.ToString()
                ),
                LetterDefOf.ThreatBig,
                psycaster != null ? new LookTargets(psycaster) : null
            );

            Log.Warning("[Signal Interceptor] Psycaster neurostorm triggered. " +
                        "Psycaster=" + (psycaster != null ? psycaster.LabelShort : "null") +
                        " | User=" + (user != null ? user.LabelShort : "null") +
                        " | FactionsAffected=" + affectedFactions +
                        " | PawnsBroken=" + brokenPawns);
        }

        private int DamageRelationsWithAllFactions()
        {
            int affected = 0;

            List<Faction> factions = Find.FactionManager.AllFactions
                .Where(f => f != null)
                .Where(f => !f.IsPlayer)
                .Where(f => !f.defeated)
                .Where(f => !f.Hidden)
                .Where(f => f.def != null)
                .Where(f => f.def.humanlikeFaction)
                .ToList();

            for (int i = 0; i < factions.Count; i++)
            {
                Faction faction = factions[i];

                int goodwillLoss = -Rand.RangeInclusive(35, 50);

                try
                {
                    faction.TryAffectGoodwillWith(
                        Faction.OfPlayer,
                        goodwillLoss,
                        canSendMessage: false,
                        canSendHostilityLetter: true
                    );

                    affected++;
                }
                catch (Exception ex)
                {
                    Log.Warning("[Signal Interceptor] Failed to apply neurostorm goodwill penalty to faction " +
                                faction.Name +
                                ": " + ex);
                }
            }

            return affected;
        }

        private int ForceNeurostormMentalBreaks(Map map, Pawn psycaster)
        {
            if (map == null)
                return 0;

            int count = 0;

            IReadOnlyList<Pawn> pawns = map.mapPawns.AllPawnsSpawned;

            for (int i = 0; i < pawns.Count; i++)
            {
                Pawn pawn = pawns[i];

                if (!CanNeurostormBreakPawn(pawn))
                    continue;

                if (TryForceRandomMentalBreak(pawn))
                {
                    count++;
                }
            }

            return count;
        }

        private bool CanNeurostormBreakPawn(Pawn pawn)
        {
            if (pawn == null || pawn.Destroyed || pawn.Dead || pawn.Downed)
                return false;

            if (!pawn.Spawned || pawn.Map == null)
                return false;

            if (pawn.RaceProps == null || !pawn.RaceProps.IsFlesh)
                return false;

            if (pawn.mindState == null)
                return false;

            if (pawn.InMentalState)
                return false;

            return true;
        }

        private bool TryForceRandomMentalBreak(Pawn pawn)
        {
            if (pawn == null || pawn.mindState == null)
                return false;

            string reason = "SI_PsycasterNeurostormMentalBreakReason".Translate();

            if (TryDoRandomMentalBreakByReflection(pawn, reason))
                return true;

            return TryStartFallbackMentalState(pawn, reason);
        }

        private bool TryDoRandomMentalBreakByReflection(Pawn pawn, string reason)
        {
            object mentalBreaker = pawn.mindState.mentalBreaker;

            if (mentalBreaker == null)
                return false;

            MethodInfo method = mentalBreaker.GetType()
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(m => m.Name == "TryDoRandomMentalBreak")
                .OrderByDescending(m => m.GetParameters().Length)
                .FirstOrDefault();

            if (method == null)
                return false;

            try
            {
                object[] args = BuildReflectionArgs(method, reason);

                object result = method.Invoke(mentalBreaker, args);

                if (result is bool boolResult)
                    return boolResult;

                return true;
            }
            catch
            {
                return false;
            }
        }

        private bool TryStartFallbackMentalState(Pawn pawn, string reason)
        {
            if (pawn == null || pawn.mindState == null || pawn.mindState.mentalStateHandler == null)
                return false;

            List<string> stateNames = new List<string>
            {
                "Berserk",
                "PanicFlee",
                "Wander_Psychotic",
                "Wander_Sad",
                "Tantrum",
                "FireStartingSpree",
                "InsultingSpree"
            };

            stateNames.Shuffle();

            for (int i = 0; i < stateNames.Count; i++)
            {
                MentalStateDef def = DefDatabase<MentalStateDef>.GetNamedSilentFail(stateNames[i]);

                if (def == null)
                    continue;

                if (TryStartMentalStateByReflection(pawn, def, reason))
                    return true;
            }

            return false;
        }

        private bool TryStartMentalStateByReflection(Pawn pawn, MentalStateDef def, string reason)
        {
            object handler = pawn.mindState.mentalStateHandler;

            if (handler == null || def == null)
                return false;

            MethodInfo method = handler.GetType()
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(m => m.Name == "TryStartMentalState")
                .Where(m =>
                {
                    ParameterInfo[] ps = m.GetParameters();
                    return ps.Length > 0 && ps[0].ParameterType == typeof(MentalStateDef);
                })
                .OrderByDescending(m => m.GetParameters().Length)
                .FirstOrDefault();

            if (method == null)
                return false;

            try
            {
                ParameterInfo[] ps = method.GetParameters();
                object[] args = new object[ps.Length];

                for (int i = 0; i < ps.Length; i++)
                {
                    Type type = ps[i].ParameterType;

                    if (i == 0)
                    {
                        args[i] = def;
                    }
                    else if (type == typeof(string))
                    {
                        args[i] = reason;
                    }
                    else if (type == typeof(bool))
                    {
                        args[i] = true;
                    }
                    else if (type.IsValueType)
                    {
                        args[i] = Activator.CreateInstance(type);
                    }
                    else
                    {
                        args[i] = null;
                    }
                }

                object result = method.Invoke(handler, args);

                if (result is bool boolResult)
                    return boolResult;

                return pawn.InMentalState;
            }
            catch
            {
                return false;
            }
        }

        private object[] BuildReflectionArgs(MethodInfo method, string reason)
        {
            ParameterInfo[] ps = method.GetParameters();
            object[] args = new object[ps.Length];

            for (int i = 0; i < ps.Length; i++)
            {
                Type type = ps[i].ParameterType;

                if (type == typeof(string))
                {
                    args[i] = reason;
                }
                else if (type == typeof(bool))
                {
                    args[i] = false;
                }
                else if (type.IsValueType)
                {
                    args[i] = Activator.CreateInstance(type);
                }
                else
                {
                    args[i] = null;
                }
            }

            return args;
        }
    }
}
