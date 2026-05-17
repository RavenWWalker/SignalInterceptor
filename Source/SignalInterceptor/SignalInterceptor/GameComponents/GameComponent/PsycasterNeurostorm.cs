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
            int brokenPawns = ForceNeurostormMentalBreaks(map);

            ApplyPsycasterNeurostormComa(psycaster);

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

        private void ApplyPsycasterNeurostormComa(Pawn psycaster)
        {
            if (psycaster == null || psycaster.Destroyed || psycaster.Dead)
                return;

            if (psycaster.health == null || psycaster.health.hediffSet == null)
                return;

            HediffDef comaDef = DefDatabase<HediffDef>.GetNamedSilentFail("PsychicComa");

            if (comaDef == null)
            {
                Log.Warning("[Signal Interceptor] Could not apply neurostorm coma: HediffDef PsychicComa not found.");
                return;
            }

            Hediff existing = psycaster.health.hediffSet.GetFirstHediffOfDef(comaDef);

            Hediff coma = existing;

            if (coma == null)
            {
                coma = HediffMaker.MakeHediff(comaDef, psycaster);
                psycaster.health.AddHediff(coma);
            }

            SetHediffDisappearTicks(coma, 300000);

            if (psycaster.jobs != null)
            {
                psycaster.jobs.EndCurrentJob(Verse.AI.JobCondition.InterruptForced, true);
            }

            if (psycaster.mindState != null)
            {
                psycaster.mindState.duty = null;
            }

            Log.Message("[Signal Interceptor] Psycaster neurostorm coma applied. " +
                        "Pawn=" + psycaster.LabelShort +
                        " | DurationTicks=300000");
        }

        private void SetHediffDisappearTicks(Hediff hediff, int ticks)
        {
            if (hediff == null)
                return;

            HediffWithComps hediffWithComps = hediff as HediffWithComps;

            if (hediffWithComps == null || hediffWithComps.comps == null)
                return;

            for (int i = 0; i < hediffWithComps.comps.Count; i++)
            {
                HediffComp_Disappears disappears = hediffWithComps.comps[i] as HediffComp_Disappears;

                if (disappears == null)
                    continue;

                disappears.ticksToDisappear = ticks;
                return;
            }
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

        private int ForceNeurostormMentalBreaks(Map map)
        {
            if (map == null)
                return 0;

            int count = 0;

            List<Pawn> pawns = GetAllNeurostormAffectedPawns(map);

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

        private List<Pawn> GetAllNeurostormAffectedPawns(Map map)
        {
            HashSet<Pawn> result = new HashSet<Pawn>();

            if (map == null)
                return result.ToList();

            IReadOnlyList<Pawn> spawnedPawns = map.mapPawns.AllPawnsSpawned;

            for (int i = 0; i < spawnedPawns.Count; i++)
            {
                Pawn pawn = spawnedPawns[i];

                if (pawn != null)
                {
                    result.Add(pawn);
                }
            }

            List<IThingHolder> visitedHolders = new List<IThingHolder>();

            List<Thing> allThings = map.listerThings.AllThings;

            for (int i = 0; i < allThings.Count; i++)
            {
                Thing thing = allThings[i];

                if (thing == null || thing.Destroyed)
                    continue;

                IThingHolder holder = thing as IThingHolder;

                if (holder != null)
                {
                    CollectHeldPawnsRecursive(holder, result, visitedHolders);
                }
            }

            return result.ToList();
        }

        private void CollectHeldPawnsRecursive(IThingHolder holder, HashSet<Pawn> pawns, List<IThingHolder> visitedHolders)
        {
            if (holder == null || pawns == null || visitedHolders == null)
                return;

            if (visitedHolders.Contains(holder))
                return;

            visitedHolders.Add(holder);

            ThingOwner directlyHeldThings = null;

            try
            {
                directlyHeldThings = holder.GetDirectlyHeldThings();
            }
            catch
            {
                directlyHeldThings = null;
            }

            if (directlyHeldThings != null)
            {
                for (int i = 0; i < directlyHeldThings.Count; i++)
                {
                    Thing heldThing = directlyHeldThings[i];

                    if (heldThing == null || heldThing.Destroyed)
                        continue;

                    Pawn heldPawn = heldThing as Pawn;

                    if (heldPawn != null)
                    {
                        pawns.Add(heldPawn);
                    }

                    IThingHolder childHolder = heldThing as IThingHolder;

                    if (childHolder != null)
                    {
                        CollectHeldPawnsRecursive(childHolder, pawns, visitedHolders);
                    }
                }
            }

            List<IThingHolder> childHolders = new List<IThingHolder>();

            try
            {
                holder.GetChildHolders(childHolders);
            }
            catch
            {
                childHolders.Clear();
            }

            for (int i = 0; i < childHolders.Count; i++)
            {
                CollectHeldPawnsRecursive(childHolders[i], pawns, visitedHolders);
            }
        }

        private bool CanNeurostormBreakPawn(Pawn pawn)
        {
            if (pawn == null || pawn.Destroyed || pawn.Dead || pawn.Downed)
                return false;

            if (pawn.RaceProps == null)
                return false;

            if (!pawn.RaceProps.IsFlesh)
                return false;

            if (pawn.mindState == null)
                return false;

            if (pawn.InMentalState)
                return false;

            return true;
        }

        private bool TryForceRandomMentalBreak(Pawn pawn)
        {
            if (pawn == null || pawn.Destroyed || pawn.Dead || pawn.Downed)
                return false;

            if (pawn.mindState == null || pawn.mindState.mentalStateHandler == null)
                return false;

            string reason = "SI_PsycasterNeurostormMentalBreakReason".Translate();

            if (pawn.RaceProps != null && pawn.RaceProps.Humanlike)
            {
                return TryStartFallbackHumanlikeMentalState(pawn, reason);
            }

            if (pawn.RaceProps != null && pawn.RaceProps.Animal)
            {
                return TryStartFallbackAnimalMentalState(pawn, reason);
            }

            return TryStartFallbackAnimalMentalState(pawn, reason);
        }

        private bool TryStartFallbackHumanlikeMentalState(Pawn pawn, string reason)
        {
            if (pawn == null || pawn.Destroyed || pawn.Dead || pawn.Downed)
                return false;

            if (pawn.mindState == null || pawn.mindState.mentalStateHandler == null)
                return false;

            List<string> stateNames = new List<string>
            {
                "Berserk",
                "PanicFlee",
                "Wander_Psychotic",
                "Wander_Sad",
                "Tantrum",
                "FireStartingSpree"
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

        private bool TryStartFallbackAnimalMentalState(Pawn pawn, string reason)
        {
            if (pawn == null || pawn.Destroyed || pawn.Dead || pawn.Downed)
                return false;

            if (pawn.mindState == null || pawn.mindState.mentalStateHandler == null)
                return false;

            List<string> stateNames = new List<string>
            {
                "Manhunter",
                "ManhunterPermanent",
                "PanicFlee",
                "Wander_Psychotic"
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
            if (pawn == null || pawn.Destroyed || pawn.Dead || pawn.Downed)
                return false;

            if (pawn.mindState == null || pawn.mindState.mentalStateHandler == null)
                return false;

            if (def == null)
                return false;

            if (def.defName == "SocialFighting")
                return false;

            if (def.defName == "MurderousRage")
                return false;

            if (def.defName == "InsultingSpree")
                return false;

            if (def.defName == "CorpseObsession")
                return false;

            object handler = pawn.mindState.mentalStateHandler;

            if (handler == null)
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

                object result = method.Invoke(handler, args);

                if (result is bool boolResult)
                    return boolResult;

                return pawn.InMentalState;
            }
            catch (Exception ex)
            {
                Log.Warning("[Signal Interceptor] Failed to start neurostorm mental state. " +
                            "Pawn=" + pawn.LabelShort +
                            " | MentalState=" + def.defName +
                            " | Exception=" + ex);

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
