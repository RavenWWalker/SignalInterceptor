using RimWorld;
using RimWorld.Planet;
using System.Collections.Generic;
using System.Linq;
using Verse;

namespace SignalInterceptor
{
    public partial class SignalInterceptorGameComponent
    {
        private const string PsycasterResonanceHediffDefName = "SI_PsycasterSiteResonance";
        private const string PsycasterTreeShieldHediffDefName = "SI_PsycasterTreeShield";
        private const string PsycasterResonanceConditionDefName = "SI_PsycasterResonance";

        public static bool HasPsycasterTreeShield(Pawn pawn)
        {
            if (pawn == null || pawn.Destroyed || pawn.Dead || pawn.health == null || pawn.health.hediffSet == null)
                return false;

            HediffDef shieldDef = DefDatabase<HediffDef>.GetNamedSilentFail(PsycasterTreeShieldHediffDefName);

            if (shieldDef == null)
                return false;

            if (!pawn.health.hediffSet.HasHediff(shieldDef))
                return false;

            SignalInterceptorGameComponent comp = Current.Game?.GetComponent<SignalInterceptorGameComponent>();

            if (comp == null)
                return false;

            VIPSiteData data = comp.trackedVIPSites
                .FirstOrDefault(d =>
                    d != null
                    && !d.rewardGiven
                    && d.subtype == VIPSubtype.PsycasterVIP
                    && d.psycasterPawn == pawn);

            if (data == null)
            {
                comp.RemovePsycasterTreeShield(pawn);
                return false;
            }

            Map map = data.site != null && data.site.HasMap
                ? data.site.Map
                : pawn.Map;

            if (!IsLinkedPsycasterAnimaTree(data, data.psycasterAnimaTree, map))
            {
                comp.RemovePsycasterTreeShield(pawn);
                return false;
            }

            return true;
        }

        private void TickPsycasterSiteResonance(VIPSiteData data)
        {
            if (data == null || data.subtype != VIPSubtype.PsycasterVIP)
                return;

            Pawn psycaster = data.psycasterPawn;

            Map siteMap = data.site != null && data.site.HasMap
                ? data.site.Map
                : null;

            if (siteMap == null)
            {
                RemovePsycasterResonanceFromAllMaps(psycaster);
                EndPsycasterResonanceConditionFromAllMaps();
                return;
            }

            if (psycaster == null || psycaster.Destroyed || psycaster.Dead || !psycaster.Spawned || psycaster.Map != siteMap)
            {
                RemovePsycasterResonanceFromMap(siteMap);
                EndPsycasterResonanceCondition(siteMap);
                return;
            }

            /*
             * Старые сейвы/текущие сейвы могли не иметь psycasterAnimaTreeLinked,
             * но если уже есть shield/resonance/condition/центр лагеря — считаем,
             * что дерево было связано.
             */
            if (!data.psycasterAnimaTreeLinked && HasAnyPsycasterResonanceState(data, psycaster, siteMap))
            {
                data.psycasterAnimaTreeLinked = true;
            }

            /*
             * Если дерево уже было связано — больше НЕ ищем любое другое дерево на карте.
             * Проверяем только сохранённое linked tree.
             */
            if (data.psycasterAnimaTreeLinked)
            {
                if (!IsLinkedPsycasterAnimaTree(data, data.psycasterAnimaTree, siteMap))
                {
                    SendPsycasterTreeDestroyedLetter(data, psycaster);

                    RemovePsycasterResonanceFromMap(siteMap);
                    RemovePsycasterTreeShield(psycaster);
                    EndPsycasterResonanceCondition(siteMap);

                    Log.Message("[Signal Interceptor] Psycaster linked anima tree is gone. Resonance disabled. " +
                                "Pawn=" + psycaster.LabelShort +
                                " | Site=" + (data.site?.LabelCap.ToString() ?? "null") +
                                " | Anchor=" + data.signalCampCenter +
                                " | TreeRef=" + (data.psycasterAnimaTree != null ? data.psycasterAnimaTree.ToString() : "null"));

                    return;
                }

                EnsurePsycasterResonanceCondition(siteMap, data.psycasterAnimaTree, psycaster);
                ApplyPsycasterResonanceToMap(siteMap, psycaster);
                return;
            }

            /*
             * Первый тик после генерации/старый сейв без явной привязки.
             * Ищем только дерево рядом с signalCampCenter.
             */
            Plant foundTree;

            if (!TryFindLinkedPsycasterAnimaTree(data, siteMap, out foundTree))
            {
                RemovePsycasterResonanceFromMap(siteMap);
                RemovePsycasterTreeShield(psycaster);
                EndPsycasterResonanceCondition(siteMap);
                return;
            }

            data.psycasterAnimaTree = foundTree;
            data.psycasterAnimaTreeLinked = true;

            if (data.signalCampCenter == IntVec3.Invalid)
            {
                data.signalCampCenter = foundTree.Position;
            }

            EnsurePsycasterResonanceCondition(siteMap, foundTree, psycaster);
            ApplyPsycasterResonanceToMap(siteMap, psycaster);
        }

        private bool HasAnyPsycasterResonanceState(VIPSiteData data, Pawn psycaster, Map map)
        {
            if (data == null)
                return false;

            if (data.psycasterAnimaTree != null)
                return true;

            if (data.signalCampCenter.IsValid)
                return true;

            if (psycaster != null && HasPsycasterTreeShield(psycaster))
                return true;

            if (map != null)
            {
                GameConditionDef conditionDef = DefDatabase<GameConditionDef>.GetNamedSilentFail(PsycasterResonanceConditionDefName);

                if (conditionDef != null &&
                    map.gameConditionManager.ActiveConditions.Any(c => c != null && c.def == conditionDef))
                {
                    return true;
                }

                HediffDef resonanceDef = DefDatabase<HediffDef>.GetNamedSilentFail(PsycasterResonanceHediffDefName);

                if (resonanceDef != null)
                {
                    IReadOnlyList<Pawn> pawns = map.mapPawns.AllPawnsSpawned;

                    for (int i = 0; i < pawns.Count; i++)
                    {
                        Pawn pawn = pawns[i];

                        if (pawn?.health?.hediffSet != null && pawn.health.hediffSet.HasHediff(resonanceDef))
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        private void SendPsycasterTreeDestroyedLetter(VIPSiteData data, Pawn psycaster)
        {
            if (data == null)
                return;

            if (data.psycasterTreeDestroyedLetterSent)
                return;

            data.psycasterTreeDestroyedLetterSent = true;

            string psycasterName = psycaster != null
                ? psycaster.LabelShort
                : "SI_PsycasterUnknown".Translate().ToString();

            LookTargets lookTargets = LookTargets.Invalid;

            if (psycaster != null && !psycaster.Destroyed)
            {
                lookTargets = new LookTargets(psycaster);
            }
            else if (data.site != null)
            {
                lookTargets = new LookTargets(data.site);
            }

            Find.LetterStack.ReceiveLetter(
                "SI_PsycasterTreeDestroyedTitle".Translate(),
                "SI_PsycasterTreeDestroyedText".Translate(psycasterName),
                LetterDefOf.NeutralEvent,
                lookTargets
            );

            Log.Message("[Signal Interceptor] Psycaster anima tree destroyed letter sent. " +
                        "Psycaster=" + psycasterName +
                        " | Site=" + (data.site?.LabelCap.ToString() ?? "null") +
                        " | Anchor=" + data.signalCampCenter);
        }


        private Thing GetOrFindPsycasterAnimaTree(VIPSiteData data, Map map)
        {
            if (data == null || map == null)
                return null;

            if (IsLinkedPsycasterAnimaTree(data, data.psycasterAnimaTree, map))
                return data.psycasterAnimaTree;

            /*
             * Если связь уже была установлена — не ищем новое дерево.
             */
            if (data.psycasterAnimaTreeLinked)
                return null;

            Plant tree;

            if (TryFindLinkedPsycasterAnimaTree(data, map, out tree))
            {
                data.psycasterAnimaTree = tree;
                data.psycasterAnimaTreeLinked = true;

                if (data.signalCampCenter == IntVec3.Invalid)
                {
                    data.signalCampCenter = tree.Position;
                }

                return tree;
            }

            return null;
        }

        private static bool IsValidPsycasterAnimaTree(Thing tree, Map map)
        {
            if (tree == null || tree.Destroyed || !tree.Spawned || tree.Map != map)
                return false;

            if (tree.def == null || tree.def.defName != "Plant_TreeAnima")
                return false;

            return true;
        }

        private static bool IsLinkedPsycasterAnimaTree(VIPSiteData data, Thing tree, Map map)
        {
            if (data == null)
                return false;

            if (!IsValidPsycasterAnimaTree(tree, map))
                return false;

            if (data.signalCampCenter.IsValid)
            {
                float distance = tree.Position.DistanceTo(data.signalCampCenter);

                if (distance > 14f)
                    return false;
            }

            return true;
        }

        private bool TryFindLinkedPsycasterAnimaTree(VIPSiteData data, Map map, out Plant tree)
        {
            tree = null;

            if (data == null || map == null)
                return false;

            ThingDef animaTreeDef = DefDatabase<ThingDef>.GetNamedSilentFail("Plant_TreeAnima");

            if (animaTreeDef == null)
                return false;

            List<Plant> candidates = map.listerThings.ThingsOfDef(animaTreeDef)
                .OfType<Plant>()
                .Where(p => IsLinkedPsycasterAnimaTree(data, p, map))
                .ToList();

            if (candidates.Count == 0)
                return false;

            if (data.signalCampCenter.IsValid)
            {
                tree = candidates
                    .OrderBy(p => p.Position.DistanceTo(data.signalCampCenter))
                    .FirstOrDefault();

                return tree != null;
            }

            tree = candidates.RandomElementWithFallback(null);
            return tree != null;
        }

        private bool TryFindPsycasterAnimaTree(Map map, out Plant tree)
        {
            tree = null;

            if (map == null)
                return false;

            ThingDef animaTreeDef = DefDatabase<ThingDef>.GetNamedSilentFail("Plant_TreeAnima");
            ThingDef animaGrassDef = DefDatabase<ThingDef>.GetNamedSilentFail("Plant_GrassAnima");

            if (animaTreeDef == null)
                return false;

            List<Plant> candidates = map.listerThings.ThingsOfDef(animaTreeDef)
                .OfType<Plant>()
                .Where(p => p != null && !p.Destroyed && p.Spawned && p.Map == map)
                .ToList();

            if (candidates.Count == 0)
                return false;

            if (animaGrassDef == null)
            {
                tree = candidates.RandomElement();
                return true;
            }

            tree = candidates
                .OrderByDescending(p => CountNearbyThingsOfDef(map, p.Position, animaGrassDef, 8f))
                .FirstOrDefault();

            return tree != null;
        }

        private int CountNearbyThingsOfDef(Map map, IntVec3 center, ThingDef def, float radius)
        {
            if (map == null || def == null || !center.IsValid)
                return 0;

            int count = 0;

            foreach (IntVec3 cell in GenRadial.RadialCellsAround(center, radius, true))
            {
                if (!cell.InBounds(map))
                    continue;

                List<Thing> things = map.thingGrid.ThingsListAt(cell);

                for (int i = 0; i < things.Count; i++)
                {
                    if (things[i] != null && things[i].def == def)
                    {
                        count++;
                    }
                }
            }

            return count;
        }

        private bool TryFindPsycasterSpawnSpotNearTree(Map map, IntVec3 treePosition, out IntVec3 spot)
        {
            spot = IntVec3.Invalid;

            if (map == null || !treePosition.IsValid)
                return false;

            for (int radius = 2; radius <= 7; radius++)
            {
                if (CellFinder.TryFindRandomCellNear(
                    treePosition,
                    map,
                    radius,
                    c => c.InBounds(map)
                         && c.Standable(map)
                         && !c.Roofed(map)
                         && c.GetFirstPawn(map) == null
                         && c.GetPlant(map) == null,
                    out spot))
                {
                    return true;
                }
            }

            return CellFinder.TryFindRandomCellNear(
                treePosition,
                map,
                12,
                c => c.InBounds(map)
                     && c.Standable(map)
                     && !c.Roofed(map)
                     && c.GetFirstPawn(map) == null,
                out spot);
        }

        private void ApplyPsycasterResonanceToMap(Map map, Pawn psycaster)
        {
            if (map == null)
                return;

            HediffDef resonanceDef = DefDatabase<HediffDef>.GetNamedSilentFail(PsycasterResonanceHediffDefName);
            HediffDef shieldDef = DefDatabase<HediffDef>.GetNamedSilentFail(PsycasterTreeShieldHediffDefName);

            if (resonanceDef == null || shieldDef == null)
            {
                Log.Warning("[Signal Interceptor] Psycaster resonance hediff defs are missing.");
                return;
            }

            IReadOnlyList<Pawn> pawns = map.mapPawns.AllPawnsSpawned;

            for (int i = 0; i < pawns.Count; i++)
            {
                Pawn pawn = pawns[i];

                if (!IsLivingResonanceTarget(pawn))
                    continue;

                if (!pawn.health.hediffSet.HasHediff(resonanceDef))
                {
                    pawn.health.AddHediff(resonanceDef);
                }
            }

            if (psycaster != null &&
                IsLivingResonanceTarget(psycaster) &&
                psycaster.Spawned &&
                psycaster.Map == map &&
                !psycaster.health.hediffSet.HasHediff(shieldDef))
            {
                psycaster.health.AddHediff(shieldDef);
            }
        }

        private bool IsLivingResonanceTarget(Pawn pawn)
        {
            if (pawn == null || pawn.Destroyed || pawn.Dead)
                return false;

            if (pawn.health == null || pawn.health.hediffSet == null)
                return false;

            if (pawn.RaceProps == null)
                return false;

            return pawn.RaceProps.IsFlesh;
        }

        private void RemovePsycasterResonanceFromMap(Map map)
        {
            if (map == null)
                return;

            HediffDef resonanceDef = DefDatabase<HediffDef>.GetNamedSilentFail(PsycasterResonanceHediffDefName);
            HediffDef shieldDef = DefDatabase<HediffDef>.GetNamedSilentFail(PsycasterTreeShieldHediffDefName);

            if (resonanceDef == null && shieldDef == null)
                return;

            IReadOnlyList<Pawn> pawns = map.mapPawns.AllPawnsSpawned;

            for (int i = 0; i < pawns.Count; i++)
            {
                Pawn pawn = pawns[i];

                if (pawn == null || pawn.health == null || pawn.health.hediffSet == null)
                    continue;

                if (resonanceDef != null)
                {
                    RemoveHediffIfPresent(pawn, resonanceDef);
                }

                if (shieldDef != null)
                {
                    RemoveHediffIfPresent(pawn, shieldDef);
                }
            }
        }

        private void RemovePsycasterResonanceFromAllMaps(Pawn psycaster)
        {
            HediffDef resonanceDef = DefDatabase<HediffDef>.GetNamedSilentFail(PsycasterResonanceHediffDefName);
            HediffDef shieldDef = DefDatabase<HediffDef>.GetNamedSilentFail(PsycasterTreeShieldHediffDefName);

            foreach (Map map in Find.Maps)
            {
                if (map == null)
                    continue;

                IReadOnlyList<Pawn> pawns = map.mapPawns.AllPawnsSpawned;

                for (int i = 0; i < pawns.Count; i++)
                {
                    Pawn pawn = pawns[i];

                    if (pawn == null || pawn.health == null || pawn.health.hediffSet == null)
                        continue;

                    if (resonanceDef != null)
                    {
                        RemoveHediffIfPresent(pawn, resonanceDef);
                    }

                    if (shieldDef != null)
                    {
                        RemoveHediffIfPresent(pawn, shieldDef);
                    }
                }
            }

            if (psycaster != null && psycaster.health != null && psycaster.health.hediffSet != null)
            {
                if (resonanceDef != null)
                {
                    RemoveHediffIfPresent(psycaster, resonanceDef);
                }

                if (shieldDef != null)
                {
                    RemoveHediffIfPresent(psycaster, shieldDef);
                }
            }
        }

        private void RemovePsycasterTreeShield(Pawn psycaster)
        {
            if (psycaster == null || psycaster.health == null || psycaster.health.hediffSet == null)
                return;

            HediffDef shieldDef = DefDatabase<HediffDef>.GetNamedSilentFail(PsycasterTreeShieldHediffDefName);

            if (shieldDef != null)
            {
                RemoveHediffIfPresent(psycaster, shieldDef);
            }
        }

        private void RemoveHediffIfPresent(Pawn pawn, HediffDef hediffDef)
        {
            if (pawn == null || hediffDef == null || pawn.health == null || pawn.health.hediffSet == null)
                return;

            Hediff hediff = pawn.health.hediffSet.GetFirstHediffOfDef(hediffDef);

            if (hediff != null)
            {
                pawn.health.RemoveHediff(hediff);
            }
        }

        private void EnsurePsycasterResonanceCondition(Map map, Thing tree, Pawn psycaster)
        {
            if (map == null)
                return;

            GameConditionDef def = DefDatabase<GameConditionDef>.GetNamedSilentFail(PsycasterResonanceConditionDefName);

            if (def == null)
                return;

            GameCondition existing = map.gameConditionManager.ActiveConditions
                .FirstOrDefault(c => c != null && c.def == def);

            if (existing is GameCondition_PsycasterResonance existingResonance)
            {
                existingResonance.tree = tree;
                existingResonance.psycaster = psycaster;
                return;
            }

            GameCondition condition = GameConditionMaker.MakeCondition(def, 99999999);

            if (condition is GameCondition_PsycasterResonance resonance)
            {
                resonance.tree = tree;
                resonance.psycaster = psycaster;
            }

            map.gameConditionManager.RegisterCondition(condition);
        }

        private void EndPsycasterResonanceCondition(Map map)
        {
            if (map == null)
                return;

            GameConditionDef def = DefDatabase<GameConditionDef>.GetNamedSilentFail(PsycasterResonanceConditionDefName);

            if (def == null)
                return;

            List<GameCondition> conditions = map.gameConditionManager.ActiveConditions
                .Where(c => c != null && c.def == def)
                .ToList();

            for (int i = 0; i < conditions.Count; i++)
            {
                conditions[i].End();
            }
        }

        private void EndPsycasterResonanceConditionFromAllMaps()
        {
            foreach (Map map in Find.Maps)
            {
                EndPsycasterResonanceCondition(map);
            }
        }
    }
}
