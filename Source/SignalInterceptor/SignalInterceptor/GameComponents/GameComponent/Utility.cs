using RimWorld;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace SignalInterceptor
{
    public partial class SignalInterceptorGameComponent
    {
        private bool IsIdleOrWaitJob(Job job)
        {
            if (job == null || job.def == null || job.def.defName == null)
            {
                return true;
            }

            string defName = job.def.defName;

            return defName == "Wait" ||
                   defName == "Wait_Combat" ||
                   defName == "Goto" ||
                   defName.IndexOf("Wander", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private bool IsFleeOrExitJob(Job job)
        {
            if (job == null || job.def == null || job.def.defName == null)
            {
                return false;
            }

            string defName = job.def.defName;

            return defName.IndexOf("Flee", StringComparison.OrdinalIgnoreCase) >= 0
                || defName.IndexOf("Exit", StringComparison.OrdinalIgnoreCase) >= 0
                || defName.IndexOf("Leave", StringComparison.OrdinalIgnoreCase) >= 0
                || defName.IndexOf("GotoMapEdge", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private bool AddHediffToPawnByDefNames(Pawn pawn, params string[] defNames)
        {
            if (pawn == null || defNames == null || defNames.Length == 0)
            {
                return false;
            }

            HediffDef hediffDef = null;

            foreach (string defName in defNames)
            {
                if (defName.NullOrEmpty())
                {
                    continue;
                }

                hediffDef = DefDatabase<HediffDef>.GetNamedSilentFail(defName);
                if (hediffDef != null)
                {
                    break;
                }
            }

            if (hediffDef == null)
            {
                Log.Warning("[SignalInterceptor] Could not find any hediff def from list: " + string.Join(", ", defNames));
                return false;
            }

            if (pawn.health == null || pawn.health.hediffSet == null)
            {
                return false;
            }

            // Не добавляем дубликат, если такой имплант/хеддиф уже есть.
            if (pawn.health.hediffSet.HasHediff(hediffDef))
            {
                return true;
            }

            BodyPartRecord targetPart = FindBestBodyPartForHediff(pawn, hediffDef);

            try
            {
                Hediff hediff = HediffMaker.MakeHediff(hediffDef, pawn, targetPart);
                pawn.health.AddHediff(hediff, targetPart);
                return true;
            }
            catch (Exception ex)
            {
                Log.Warning("[SignalInterceptor] Failed to add hediff " + hediffDef.defName + " to pawn " + pawn.LabelShort + ": " + ex);
                return false;
            }
        }

        private BodyPartRecord FindBestBodyPartForHediff(Pawn pawn, HediffDef hediffDef)
        {
            if (pawn == null ||
                pawn.RaceProps == null ||
                pawn.RaceProps.body == null ||
                pawn.health == null ||
                pawn.health.hediffSet == null)
            {
                return null;
            }

            List<BodyPartRecord> parts = pawn.health.hediffSet.GetNotMissingParts().ToList();

            if (parts.NullOrEmpty())
            {
                return null;
            }

            string hediffDefName = hediffDef != null ? hediffDef.defName : string.Empty;

            bool wantsBrainOrHead =
                hediffDefName.IndexOf("Mechlink", StringComparison.OrdinalIgnoreCase) >= 0 ||
                hediffDefName.IndexOf("Neural", StringComparison.OrdinalIgnoreCase) >= 0 ||
                hediffDefName.IndexOf("LearningAssistant", StringComparison.OrdinalIgnoreCase) >= 0 ||
                hediffDefName.IndexOf("ControlSubLink", StringComparison.OrdinalIgnoreCase) >= 0 ||
                hediffDefName.IndexOf("Bandwidth", StringComparison.OrdinalIgnoreCase) >= 0;

            if (wantsBrainOrHead)
            {
                BodyPartRecord brain = parts.FirstOrDefault(p => p.def != null && p.def.defName == "Brain");
                if (brain != null)
                {
                    return brain;
                }

                BodyPartRecord head = parts.FirstOrDefault(p => p.def != null && p.def.defName == "Head");
                if (head != null)
                {
                    return head;
                }
            }

            // Общий fallback: мозг -> голова -> торс -> любая доступная часть.
            BodyPartRecord fallbackBrain = parts.FirstOrDefault(p => p.def != null && p.def.defName == "Brain");
            if (fallbackBrain != null)
            {
                return fallbackBrain;
            }

            BodyPartRecord fallbackHead = parts.FirstOrDefault(p => p.def != null && p.def.defName == "Head");
            if (fallbackHead != null)
            {
                return fallbackHead;
            }

            BodyPartRecord torso = parts.FirstOrDefault(p => p.def != null && p.def.defName == "Torso");
            if (torso != null)
            {
                return torso;
            }

            return parts.FirstOrDefault();
        }

        private List<string> GetTranslatedStringList(string key, List<string> fallback)
        {
            if (key.CanTranslate())
            {
                string raw = key.Translate().ToString();

                List<string> result = raw
                    .Split(new char[] { '|' }, System.StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.Trim())
                    .Where(s => !string.IsNullOrEmpty(s))
                    .ToList();

                if (result.Count > 0)
                    return result;
            }

            return fallback;
        }
    }
}
