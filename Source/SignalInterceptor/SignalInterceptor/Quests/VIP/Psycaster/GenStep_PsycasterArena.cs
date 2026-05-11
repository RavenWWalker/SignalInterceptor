using RimWorld;
using System.Collections.Generic;
using Verse;

namespace SignalInterceptor
{
    public class GenStep_PsycasterArena : GenStep
    {
        public override int SeedPart => 74129845;

        private static readonly string[] Pattern_01 =
        {
            ".....G.....",
            "...GGGGG...",
            "..GGGGGGG..",
            ".GGGGGGGGG.",
            ".GGGGGGGGG.",
            "GGGGGTGGGGG",
            ".GGGGGGGGG.",
            ".GGGGGGGGG.",
            "..GGGGGGG..",
            "...GGGGG...",
            ".....G....."
        };

        private static readonly string[] Pattern_02 =
        {
            ".....G.....",
            "..G..G..G..",
            "...GGGGG...",
            "..GGGGGGG..",
            ".GGGGGGGGG.",
            "GGGGGTGGGGG",
            ".GGGGGGGGG.",
            "..GGGGGGG..",
            "...GGGGG...",
            "..G..G..G..",
            ".....G....."
        };

        private static readonly string[] Pattern_03 =
        {
            "...G...G...",
            "..GG...GG..",
            ".GGGG.GGGG.",
            "GGGGGGGGGGG",
            "...GGGGG...",
            "...GGTGG...",
            "...GGGGG...",
            "GGGGGGGGGGG",
            ".GGGG.GGGG.",
            "..GG...GG..",
            "...G...G..."
        };

        private static readonly string[] Pattern_04 =
        {
            "....GGG....",
            "..GGGGGGG..",
            ".GGG...GGG.",
            ".GG.....GG.",
            "GGG..G..GGG",
            "GG...T...GG",
            "GGG..G..GGG",
            ".GG.....GG.",
            ".GGG...GGG.",
            "..GGGGGGG..",
            "....GGG...."
        };

        private static readonly string[] Pattern_05 =
        {
            "G....G....G",
            ".G...G...G.",
            "..G.GGG.G..",
            "...GGGGG...",
            "GGGGGGGGGGG",
            "....GTG....",
            "GGGGGGGGGGG",
            "...GGGGG...",
            "..G.GGG.G..",
            ".G...G...G.",
            "G....G....G"
        };

        private static readonly string[] Pattern_06 =
        {
            ".....G.....",
            "....GGG....",
            "..G.GGG.G..",
            ".GGGGGGGGG.",
            "..GGGGGGG..",
            "GGGGGTGGGGG",
            "..GGGGGGG..",
            ".GGGGGGGGG.",
            "..G.GGG.G..",
            "....GGG....",
            ".....G....."
        };

        private static readonly string[] Pattern_07 =
        {
            "..G.....G..",
            ".GGG...GGG.",
            "GGGGG.GGGGG",
            ".GGGGGGGGG.",
            "...GGGGG...",
            "GGGGGTGGGGG",
            "...GGGGG...",
            ".GGGGGGGGG.",
            "GGGGG.GGGGG",
            ".GGG...GGG.",
            "..G.....G.."
        };

        private static readonly string[][] Patterns =
        {
            Pattern_01,
            Pattern_02,
            Pattern_03,
            Pattern_04,
            Pattern_05,
            Pattern_06,
            Pattern_07
        };

        public override void Generate(Map map, GenStepParams parms)
        {
            if (!ModsConfig.RoyaltyActive)
            {
                return;
            }

            IntVec3 center;
            if (!TryFindArenaCenter(map, out center))
            {
                Log.Warning("[Signal Interceptor] Could not find a suitable center for Psycaster Arena.");
                return;
            }

            GenerateArenaAt(center, map);
        }

        private bool TryFindArenaCenter(Map map, out IntVec3 center)
        {
            return CellFinderLoose.TryFindRandomNotEdgeCellWith(18, c =>
            {
                if (!c.Standable(map))
                    return false;

                foreach (IntVec3 adj in GenRadial.RadialCellsAround(c, 8f, true))
                {
                    if (!adj.InBounds(map))
                        return false;

                    TerrainDef terrain = adj.GetTerrain(map);
                    if (terrain == null || terrain.IsWater || terrain.passability == Traversability.Impassable)
                        return false;
                }

                return true;
            }, map, out center);
        }

        private void GenerateArenaAt(IntVec3 center, Map map)
        {
            ThingDef animaTreeDef = DefDatabase<ThingDef>.GetNamedSilentFail("Plant_TreeAnima");
            ThingDef animaGrassDef = DefDatabase<ThingDef>.GetNamedSilentFail("Plant_GrassAnima");
            TerrainDef soilDef = TerrainDefOf.Soil;

            if (animaTreeDef == null || animaGrassDef == null)
            {
                Log.Error("[Signal Interceptor] Missing Anima Tree or Anima Grass Defs. Royalty is probably inactive or defs changed.");
                return;
            }

            string[] pattern = Patterns.RandomElement();
            List<IntVec3> grassCells = new List<IntVec3>();

            int size = pattern.Length;
            int half = size / 2;

            for (int row = 0; row < size; row++)
            {
                string line = pattern[row];

                for (int col = 0; col < line.Length; col++)
                {
                    char symbol = line[col];

                    if (symbol != 'G' && symbol != 'T')
                        continue;

                    int x = col - half;
                    int z = half - row;

                    IntVec3 cell = center + new IntVec3(x, 0, z);

                    if (!cell.InBounds(map))
                        continue;

                    ClearCell(cell, map);
                    map.terrainGrid.SetTerrain(cell, soilDef);

                    if (symbol == 'T')
                    {
                        Plant tree = GenSpawn.Spawn(animaTreeDef, cell, map) as Plant;
                        if (tree != null)
                        {
                            tree.Growth = 1f;
                            tree.HitPoints = tree.MaxHitPoints;
                        }
                    }
                    else
                    {
                        grassCells.Add(cell);
                    }
                }
            }

            foreach (IntVec3 cell in grassCells)
            {
                if (!cell.InBounds(map))
                    continue;

                if (cell.GetPlant(map) != null)
                    continue;

                Plant grass = GenSpawn.Spawn(animaGrassDef, cell, map) as Plant;
                if (grass != null)
                {
                    grass.Growth = 1f;
                }
            }

            Log.Message("[Signal Interceptor] Psycaster arena generated. Center=" + center + " Pattern=" + System.Array.IndexOf(Patterns, pattern));
        }

        private void ClearCell(IntVec3 c, Map map)
        {
            List<Thing> things = map.thingGrid.ThingsListAt(c);

            for (int i = things.Count - 1; i >= 0; i--)
            {
                Thing thing = things[i];

                if (thing == null || thing.Destroyed)
                    continue;

                if (!thing.def.destroyable)
                    continue;

                if (thing is Plant ||
                    thing.def.category == ThingCategory.Item ||
                    thing.def.category == ThingCategory.Building)
                {
                    thing.Destroy(DestroyMode.Vanish);
                }
            }
        }
    }
}
