using RimWorld;
using System.Collections.Generic;
using Verse;

namespace SignalInterceptor
{
    public class GenStep_PsycasterArena : GenStep
    {
        public override int SeedPart => 74129845; // Уникальный сид для GenStep

        public override void Generate(Map map, GenStepParams parms)
        {
            // 1. Ищем подходящее место для островка.
            // Нам нужен центр квадрата 9x9, где нет непроходимых скал и воды.
            IntVec3 center;
            if (!TryFindArenaCenter(map, out center))
            {
                Log.Warning("[Signal Interceptor] Could not find a suitable center for Psycaster Arena.");
                return;
            }

            // 2. Рисуем островок (меняем террейн на почву) и сажаем дерево с травой.
            GenerateArenaAt(center, map);
        }

        private bool TryFindArenaCenter(Map map, out IntVec3 center)
        {
            return CellFinderLoose.TryFindRandomNotEdgeCellWith(15, c =>
            {
                if (!c.Standable(map)) return false;

                // Проверяем, что вокруг в радиусе 6 клеток нет воды и непроходимых скал
                foreach (IntVec3 adj in GenRadial.RadialCellsAround(c, 6f, true))
                {
                    if (!adj.InBounds(map)) return false;
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
            TerrainDef soilDef = TerrainDefOf.Soil; // Или TerrainDefOf.MossyTerrain для атмосферы

            if (animaTreeDef == null || animaGrassDef == null)
            {
                Log.Error("[Signal Interceptor] Missing Anima Tree or Grass Defs. Is Royalty active?");
                return;
            }

            // Шаблон твоего островка 9x9
            // 0 = не трогаем (Х)
            // 1 = земля + трава (Д)
            // 2 = центр (О)
            int[,] pattern = new int[9, 9]
            {
                { 0, 0, 0, 1, 1, 1, 0, 0, 0 },
                { 0, 1, 1, 1, 1, 1, 1, 1, 0 },
                { 0, 1, 1, 1, 1, 1, 1, 1, 0 },
                { 1, 1, 1, 1, 1, 1, 1, 1, 1 },
                { 1, 1, 1, 1, 2, 1, 1, 1, 1 },
                { 1, 1, 1, 1, 1, 1, 1, 1, 1 },
                { 0, 1, 1, 1, 1, 1, 1, 1, 0 },
                { 0, 1, 1, 1, 1, 1, 1, 1, 0 },
                { 0, 0, 0, 1, 1, 1, 0, 0, 0 }
            };

            List<IntVec3> grassCells = new List<IntVec3>();

            // Проходимся по сетке 9x9 (от -4 до +4 от центра)
            for (int x = -4; x <= 4; x++)
            {
                for (int z = -4; z <= 4; z++)
                {
                    int gridX = x + 4;
                    int gridZ = z + 4; // Z идет снизу вверх

                    int cellType = pattern[8 - gridZ, gridX]; // Переворачиваем Z для визуального соответствия массиву

                    if (cellType == 0) continue; // Это 'Х'

                    IntVec3 c = center + new IntVec3(x, 0, z);
                    if (!c.InBounds(map)) continue;

                    // Очищаем клетку от мусора, камней и старых растений
                    ClearCell(c, map);

                    // Стелим почву, если это не так
                    map.terrainGrid.SetTerrain(c, soilDef);

                    if (cellType == 2)
                    {
                        // Сажаем Дерево (О)
                        Plant tree = (Plant)GenSpawn.Spawn(animaTreeDef, c, map);
                        tree.Growth = 1f; // Делаем его полностью выросшим

                        // Добавляем к дереву наш кастомный Comp для излучения Пси-поля
                        // (Его мы напишем в Шаге 2)
                    }
                    else if (cellType == 1)
                    {
                        // Запоминаем клетки для травы (Д)
                        grassCells.Add(c);
                    }
                }
            }

            // Сажаем траву (можно добавить рандомизации, чтобы не было прям монолитно 100% заполнено, 
            // но по условию сажаем узором).
            foreach (IntVec3 c in grassCells)
            {
                // Для красоты можно сделать так, чтобы по углам трава была чуть реже, но пока сажаем везде:
                Plant grass = (Plant)GenSpawn.Spawn(animaGrassDef, c, map);
                grass.Growth = 1f;
            }
        }

        private void ClearCell(IntVec3 c, Map map)
        {
            List<Thing> things = map.thingGrid.ThingsListAt(c);
            for (int i = things.Count - 1; i >= 0; i--)
            {
                Thing t = things[i];
                if (t.def.destroyable && (t is Plant || t.def.category == ThingCategory.Item || t.def.category == ThingCategory.Building))
                {
                    t.Destroy(DestroyMode.Vanish);
                }
            }
        }
    }
}
