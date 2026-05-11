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
            if (pawn == null || pawn.health == null || pawn.health.hediffSet == null)
                return false;

            HediffDef shieldDef = DefDatabase<HediffDef>.GetNamedSilentFail(PsycasterTreeShieldHediffDefName);
            return shieldDef != null && pawn.health.hediffSet.HasHediff(shieldDef);
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

            Thing tree = GetOrFindPsycasterAnimaTree(data, siteMap);

            if (!IsValidPsycasterAnimaTree(tree, siteMap))
            {
                RemovePsycasterResonanceFromMap(siteMap);
                RemovePsycasterTreeShield(psycaster);
                EndPsycasterResonanceCondition(siteMap);
                return;
            }

            EnsurePsycasterResonanceCondition(siteMap, tree, psycaster);
            ApplyPsycasterResonanceToMap(siteMap, psycaster);
        }

        private Thing GetOrFindPsycasterAnimaTree(VIPSiteData data, Map map)
        {
            if (data == null || map == null)
                return null;

            if (IsValidPsycasterAnimaTree(data.psycasterAnimaTree, map))
                return data.psycasterAnimaTree;

            Plant tree;
            if (TryFindPsycasterAnimaTree(map, out tree))
            {
                data.psycasterAnimaTree = tree;

                if (data.signalCampCenter == IntVec3.Invalid)
                {
                    data.signalCampCenter = tree.Position;
                }

                return tree;
            }

            return null;
        }

        private bool IsValidPsycasterAnimaTree(Thing tree, Map map)
        {
            if (tree == null || tree.Destroyed || !tree.Spawned || tree.Map != map)
                return false;

            if (tree.def == null || tree.def.defName != "Plant_TreeAnima")
                return false;

            return true;
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

            List<Pawn> pawns = map.mapPawns.AllPawnsSpawned;

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

            List<Pawn> pawns = map.mapPawns.AllPawnsSpawned;

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

                List<Pawn> pawns = map.mapPawns.AllPawnsSpawned;

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
