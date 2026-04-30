using RimWorld;
using RimWorld.Planet;
using System.Collections.Generic;
using System.Linq;
using Verse;
using Verse.AI.Group;

namespace SignalInterceptor
{
    public class GenStep_ShuttleVIP : GenStep
    {
        public override int SeedPart => 916178483;

        private const int ClearRadius = 22;

        private static readonly IntVec2[] BuildingSizes = new IntVec2[]
        {
            new IntVec2(6, 5),
            new IntVec2(7, 5),
            new IntVec2(8, 5),
            new IntVec2(7, 6),
            new IntVec2(8, 6),
            new IntVec2(9, 6),
            new IntVec2(5, 7),
            new IntVec2(6, 8),
        };

        public override void Generate(Map map, GenStepParams parms)
        {
            Site site = map.Parent as Site;
            if (site == null) return;

            Faction faction = site.Faction;
            float threatPoints = parms.sitePart?.parms?.threatPoints ?? 800f;

            IntVec3 center = FindCampCenter(map);

            ClearArea(map, center, ClearRadius);
            SpawnLandingPad(map, center);
            SpawnShuttle(map, center);

            int buildingCount = Rand.RangeInclusive(2, 5);
            List<BuildingData> buildings = PlaceBuildings(map, center, buildingCount, faction);

            SpawnCampGenerator(map, center, buildings, faction);

            List<Pawn> guards = SpawnGuards(map, center, faction, threatPoints);

            LordJob_DefendPoint lordJob = new LordJob_DefendPoint(center);
            Lord lord = LordMaker.MakeNewLord(faction, lordJob, map);
            foreach (Pawn guard in guards)
            {
                lord.AddPawn(guard);
            }
        }

        // ===================== МАТЕРИАЛЫ =====================

        private ThingDef ChooseBuildingMaterial()
        {
            var candidates = new List<ThingDef>();
            candidates.Add(ThingDefOf.WoodLog);
            candidates.Add(ThingDefOf.Steel);

            string[] blockNames = { "BlocksSandstone", "BlocksGranite", "BlocksLimestone", "BlocksSlate", "BlocksMarble" };
            foreach (string name in blockNames)
            {
                ThingDef block = DefDatabase<ThingDef>.GetNamedSilentFail(name);
                if (block != null) candidates.Add(block);
            }

            return candidates.RandomElement();
        }

        private TerrainDef GetFloorForMaterial(ThingDef stuff)
        {
            if (stuff == ThingDefOf.WoodLog)
                return DefDatabase<TerrainDef>.GetNamedSilentFail("WoodPlankFloor") ?? TerrainDefOf.Concrete;
            if (stuff == ThingDefOf.Steel)
                return DefDatabase<TerrainDef>.GetNamedSilentFail("MetalTile") ?? TerrainDefOf.Concrete;
            return DefDatabase<TerrainDef>.GetNamedSilentFail("FlagstoneSlate") ?? TerrainDefOf.Concrete;
        }

        // ===================== ПОИСК ЦЕНТРА =====================

        private IntVec3 FindCampCenter(Map map)
        {
            for (int attempt = 0; attempt < 300; attempt++)
            {
                IntVec3 candidate = CellFinder.RandomNotEdgeCell(30, map);

                if (!IsDryStandableCell(map, candidate))
                    continue;

                if (candidate.Roofed(map))
                    continue;

                bool areaOk = true;

                for (int dx = -ClearRadius; dx <= ClearRadius && areaOk; dx += 4)
                {
                    for (int dz = -ClearRadius; dz <= ClearRadius && areaOk; dz += 4)
                    {
                        IntVec3 check = candidate + new IntVec3(dx, 0, dz);

                        if (!check.InBounds(map))
                        {
                            areaOk = false;
                            break;
                        }

                        if (check.GetRoof(map) != null)
                        {
                            areaOk = false;
                            break;
                        }

                        if (IsWaterCell(map, check))
                        {
                            areaOk = false;
                            break;
                        }
                    }
                }

                if (!areaOk)
                    continue;

                if (!IsDryArea(map, candidate, ClearRadius, 3))
                    continue;

                return candidate;
            }

            Log.Warning("[Signal Interceptor] Shuttle VIP could not find fully dry camp center. Falling back to map center.");

            if (IsDryStandableCell(map, map.Center))
                return map.Center;

            IntVec3 fallback;
            if (CellFinder.TryFindRandomCell(
                    map,
                    c => c.InBounds(map)
                      && !c.Roofed(map)
                      && IsDryStandableCell(map, c),
                    out fallback))
            {
                return fallback;
            }

            return map.Center;
        }

        private bool IsWaterCell(Map map, IntVec3 cell)
        {
            if (map == null || !cell.InBounds(map))
                return true;

            TerrainDef terrain = cell.GetTerrain(map);

            if (terrain == null)
                return true;

            if (terrain.IsWater)
                return true;

            return false;
        }

        private bool IsDryStandableCell(Map map, IntVec3 cell)
        {
            if (map == null || !cell.InBounds(map))
                return false;

            if (IsWaterCell(map, cell))
                return false;

            if (!cell.Standable(map))
                return false;

            return true;
        }

        private bool IsDryArea(Map map, IntVec3 center, int radius, int step = 3)
        {
            if (map == null || !center.InBounds(map))
                return false;

            for (int dx = -radius; dx <= radius; dx += step)
            {
                for (int dz = -radius; dz <= radius; dz += step)
                {
                    IntVec3 cell = center + new IntVec3(dx, 0, dz);

                    if (!cell.InBounds(map))
                        return false;

                    if (IsWaterCell(map, cell))
                        return false;
                }
            }

            return true;
        }

        private bool IsDryRect(Map map, IntVec3 corner, int width, int height, int padding = 0)
        {
            if (map == null)
                return false;

            for (int dx = -padding; dx < width + padding; dx++)
            {
                for (int dz = -padding; dz < height + padding; dz++)
                {
                    IntVec3 cell = corner + new IntVec3(dx, 0, dz);

                    if (!cell.InBounds(map))
                        return false;

                    if (IsWaterCell(map, cell))
                        return false;
                }
            }

            return true;
        }

        // ===================== РАСЧИСТКА =====================

        private void ClearArea(Map map, IntVec3 center, int radius)
        {
            for (int dx = -radius; dx <= radius; dx++)
            {
                for (int dz = -radius; dz <= radius; dz++)
                {
                    IntVec3 cell = center + new IntVec3(dx, 0, dz);
                    if (!cell.InBounds(map)) continue;

                    List<Thing> toRemove = cell.GetThingList(map)
                        .Where(t => t.def.category == ThingCategory.Plant
                                 || t.def.category == ThingCategory.Filth
                                 || (t.def.building != null && t.def.building.isNaturalRock)
                                 || t.def.mineable
                                 || (t.def.thingCategories != null
                                     && t.def.thingCategories.Any(c => c.defName.Contains("Chunk"))))
                        .ToList();
                    foreach (Thing t in toRemove)
                    {
                        t.Destroy();
                    }

                    if (map.roofGrid.RoofAt(cell) != null)
                    {
                        map.roofGrid.SetRoof(cell, null);
                    }
                }
            }
        }

        // ===================== ПОСАДОЧНАЯ ПЛОЩАДКА =====================

        private void SpawnLandingPad(Map map, IntVec3 center)
        {
            TerrainDef concrete = TerrainDefOf.Concrete;
            for (int dx = -4; dx <= 4; dx++)
            {
                for (int dz = -4; dz <= 4; dz++)
                {
                    IntVec3 cell = center + new IntVec3(dx, 0, dz);
                    if (cell.InBounds(map) && !IsWaterCell(map, cell))
                    {
                        map.terrainGrid.SetTerrain(cell, concrete);
                    }
                }
            }
        }

        private void SpawnShuttle(Map map, IntVec3 center)
        {
            ThingDef shuttleDef = DefDatabase<ThingDef>.GetNamedSilentFail("ShuttleCrashed");
            if (shuttleDef == null)
                shuttleDef = DefDatabase<ThingDef>.GetNamedSilentFail("Shuttle");

            if (shuttleDef != null)
            {
                IntVec3 shuttleCell = center;

                if (!IsDryStandableCell(map, shuttleCell))
                {
                    CellFinder.TryFindRandomCellNear(
                        center,
                        map,
                        8,
                        c => IsDryStandableCell(map, c) && c.GetFirstBuilding(map) == null,
                        out shuttleCell
                    );
                }

                if (shuttleCell.IsValid && IsDryStandableCell(map, shuttleCell))
                {
                    Thing shuttle = ThingMaker.MakeThing(shuttleDef);
                    GenSpawn.Spawn(shuttle, shuttleCell, map);
                }
            }
        }

        // ===================== ДАННЫЕ ЗДАНИЯ =====================

        private struct BuildingData
        {
            public IntVec3 corner;
            public int width;
            public int height;
            public int doorSide;
        }

        // ===================== РАЗМЕЩЕНИЕ ЗДАНИЙ =====================

        private List<BuildingData> PlaceBuildings(Map map, IntVec3 center, int count, Faction faction)
        {
            List<BuildingData> buildings = new List<BuildingData>();

            float startAngle = Rand.Range(0f, 360f);
            float angleStep = 360f / count;
            int distFromCenter = 14;

            for (int i = 0; i < count; i++)
            {
                IntVec2 size = BuildingSizes.RandomElement();
                if (Rand.Bool)
                    size = new IntVec2(size.z, size.x);

                float angle = (startAngle + i * angleStep) * UnityEngine.Mathf.Deg2Rad;
                int offsetX = (int)(distFromCenter * UnityEngine.Mathf.Cos(angle));
                int offsetZ = (int)(distFromCenter * UnityEngine.Mathf.Sin(angle));

                IntVec3 buildingCenter = center + new IntVec3(offsetX, 0, offsetZ);
                IntVec3 corner = buildingCenter + new IntVec3(-size.x / 2, 0, -size.z / 2);

                IntVec3 farCorner = corner + new IntVec3(size.x + 2, 0, size.z + 2);
                if (!corner.InBounds(map) || !farCorner.InBounds(map))
                    continue;
                if (!IsDryRect(map, corner, size.x, size.z, padding: 1))
                    continue;

                int doorSide = GetDoorSide(corner, size.x, size.z, center);

                ThingDef buildingStuff = ChooseBuildingMaterial();

                SpawnSingleBuilding(map, corner, size.x, size.z, faction, doorSide, buildingStuff);
                SpawnOuterSandbags(map, corner, size.x, size.z, center, faction);

                buildings.Add(new BuildingData
                {
                    corner = corner,
                    width = size.x,
                    height = size.z,
                    doorSide = doorSide
                });
            }

            return buildings;
        }

        private int GetDoorSide(IntVec3 corner, int width, int height, IntVec3 campCenter)
        {
            IntVec3 south = corner + new IntVec3(width / 2, 0, 0);
            IntVec3 north = corner + new IntVec3(width / 2, 0, height - 1);
            IntVec3 west = corner + new IntVec3(0, 0, height / 2);
            IntVec3 east = corner + new IntVec3(width - 1, 0, height / 2);

            float dSouth = south.DistanceTo(campCenter);
            float dNorth = north.DistanceTo(campCenter);
            float dWest = west.DistanceTo(campCenter);
            float dEast = east.DistanceTo(campCenter);

            float min = dSouth;
            int side = 0;
            if (dNorth < min) { min = dNorth; side = 1; }
            if (dWest < min) { min = dWest; side = 2; }
            if (dEast < min) { min = dEast; side = 3; }
            return side;
        }

        // ===================== ПОСТРОЙКА ЗДАНИЯ =====================

        private void SpawnSingleBuilding(Map map, IntVec3 corner, int width, int height, Faction faction, int doorSide, ThingDef buildingStuff)
        {
            ThingDef wallDef = ThingDefOf.Wall;
            ThingDef doorDef = DefDatabase<ThingDef>.GetNamedSilentFail("Door");
            TerrainDef floor = GetFloorForMaterial(buildingStuff);

            IntVec3 doorPos;
            switch (doorSide)
            {
                case 1: doorPos = corner + new IntVec3(width / 2, 0, height - 1); break;
                case 2: doorPos = corner + new IntVec3(0, 0, height / 2); break;
                case 3: doorPos = corner + new IntVec3(width - 1, 0, height / 2); break;
                default: doorPos = corner + new IntVec3(width / 2, 0, 0); break;
            }

            for (int dx = 0; dx < width; dx++)
            {
                for (int dz = 0; dz < height; dz++)
                {
                    IntVec3 cell = corner + new IntVec3(dx, 0, dz);
                    if (!cell.InBounds(map)) continue;
                    if (IsWaterCell(map, cell)) continue;

                    List<Thing> blocking = cell.GetThingList(map)
                        .Where(t => t.def.category == ThingCategory.Plant
                                 || t.def.category == ThingCategory.Building
                                 || t.def.category == ThingCategory.Item
                                 || t.def.category == ThingCategory.Filth)
                        .ToList();
                    foreach (Thing t in blocking)
                    {
                        if (!t.Destroyed) t.Destroy();
                    }

                    bool isWall = dx == 0 || dx == width - 1 || dz == 0 || dz == height - 1;
                    bool isDoor = cell == doorPos;

                    if (isDoor && doorDef != null)
                    {
                        Thing door = ThingMaker.MakeThing(doorDef, buildingStuff);
                        door.SetFactionDirect(faction);
                        GenSpawn.Spawn(door, cell, map);
                        map.terrainGrid.SetTerrain(cell, floor);
                    }
                    else if (isWall)
                    {
                        Thing wall = ThingMaker.MakeThing(wallDef, buildingStuff);
                        wall.SetFactionDirect(faction);
                        GenSpawn.Spawn(wall, cell, map);
                    }
                    else
                    {
                        map.terrainGrid.SetTerrain(cell, floor);
                    }
                }
            }

            SpawnInterior(map, corner, width, height, faction, buildingStuff);
        }

        // ===================== ИНТЕРЬЕР =====================

        private void SpawnInterior(Map map, IntVec3 corner, int width, int height, Faction faction, ThingDef buildingStuff)
        {
            IntVec3 tableCenter = corner + new IntVec3(width / 2, 0, height / 2);

            // Стол 2x2
            ThingDef tableDef = DefDatabase<ThingDef>.GetNamedSilentFail("Table2x2c");
            if (tableDef == null)
                tableDef = DefDatabase<ThingDef>.GetNamedSilentFail("Table1x2c");

            if (tableDef != null && tableCenter.InBounds(map) && tableCenter.GetFirstBuilding(map) == null)
            {
                ThingDef tableStuff = tableDef.MadeFromStuff ? buildingStuff : null;
                Thing table = ThingMaker.MakeThing(tableDef, tableStuff);
                table.SetFactionDirect(faction);
                GenSpawn.Spawn(table, tableCenter, map);
            }

            // Стулья вплотную к столу, повёрнуты лицом к столу
            ThingDef chairDef = DefDatabase<ThingDef>.GetNamedSilentFail("DiningChair");
            if (chairDef != null)
            {
                // offset, rotation (стул смотрит В сторону стола)
                var chairPositions = new List<(IntVec3 offset, Rot4 rot)>
                {
                    (new IntVec3(-2, 0, 0), Rot4.East),    // слева — смотрит вправо
                    (new IntVec3(2, 0, 0),  Rot4.West),    // справа — смотрит влево
                    (new IntVec3(0, 0, -2), Rot4.North),   // снизу — смотрит вверх
                    (new IntVec3(0, 0, 2),  Rot4.South),   // сверху — смотрит вниз
                    (new IntVec3(-2, 0, 1), Rot4.East),    // слева-верх
                    (new IntVec3(2, 0, 1),  Rot4.West),    // справа-верх
                };

                int chairCount = Rand.RangeInclusive(2, 4);
                chairPositions.Shuffle();

                int placed = 0;
                foreach (var (offset, rot) in chairPositions)
                {
                    if (placed >= chairCount) break;

                    IntVec3 chairSpot = tableCenter + offset;
                    if (chairSpot.InBounds(map) && chairSpot.Standable(map)
                        && chairSpot.GetFirstBuilding(map) == null
                        && IsInterior(chairSpot, corner, width, height))
                    {
                        ThingDef chairStuff = chairDef.MadeFromStuff ? buildingStuff : null;
                        Thing chair = ThingMaker.MakeThing(chairDef, chairStuff);
                        chair.SetFactionDirect(faction);
                        GenSpawn.Spawn(chair, chairSpot, map, rot);
                        placed++;
                    }
                }
            }

            // Торшер в углу комнаты (всегда напольный — без проблем с ротацией)
            ThingDef lampDef = DefDatabase<ThingDef>.GetNamedSilentFail("StandingLamp");
            if (lampDef != null)
            {
                IntVec3[] lampCandidates = new IntVec3[]
                {
                    corner + new IntVec3(1, 0, 1),
                    corner + new IntVec3(width - 2, 0, 1),
                    corner + new IntVec3(1, 0, height - 2),
                    corner + new IntVec3(width - 2, 0, height - 2),
                };

                foreach (IntVec3 lampSpot in lampCandidates)
                {
                    if (lampSpot.InBounds(map) && lampSpot.Standable(map)
                        && lampSpot.GetFirstBuilding(map) == null)
                    {
                        Thing lamp = ThingMaker.MakeThing(lampDef);
                        lamp.SetFactionDirect(faction);
                        GenSpawn.Spawn(lamp, lampSpot, map);
                        break;
                    }
                }
            }

            // Сух.пайки
            ThingDef mealDef = DefDatabase<ThingDef>.GetNamedSilentFail("MealSurvivalPack");
            if (mealDef != null)
            {
                for (int i = 0; i < Rand.RangeInclusive(2, 4); i++)
                {
                    IntVec3 mealSpot = corner + new IntVec3(
                        Rand.RangeInclusive(1, width - 2), 0,
                        Rand.RangeInclusive(1, height - 2));

                    if (mealSpot.InBounds(map) && mealSpot.Standable(map)
                        && mealSpot.GetFirstItem(map) == null
                        && mealSpot.GetFirstBuilding(map) == null)
                    {
                        Thing meal = ThingMaker.MakeThing(mealDef);
                        meal.stackCount = Rand.RangeInclusive(3, 8);
                        GenSpawn.Spawn(meal, mealSpot, map);
                    }
                }
            }
        }

        private bool IsInterior(IntVec3 cell, IntVec3 corner, int width, int height)
        {
            int dx = cell.x - corner.x;
            int dz = cell.z - corner.z;
            return dx > 0 && dx < width - 1 && dz > 0 && dz < height - 1;
        }

        // ===================== МЕШКИ С ПЕСКОМ — ВНЕШНИЙ КОНТУР =====================

        private void SpawnOuterSandbags(Map map, IntVec3 corner, int width, int height, IntVec3 campCenter, Faction faction)
        {
            ThingDef sandbags = ThingDefOf.Sandbags;
            ThingDef stuff = sandbags.MadeFromStuff ? GenStuff.DefaultStuffFor(sandbags) : null;

            // 4 угла здания с их координатами
            IntVec3 swCorner = corner + new IntVec3(-1, 0, -1);           // юго-запад
            IntVec3 seCorner = corner + new IntVec3(width, 0, -1);        // юго-восток
            IntVec3 nwCorner = corner + new IntVec3(-1, 0, height);       // северо-запад
            IntVec3 neCorner = corner + new IntVec3(width, 0, height);    // северо-восток

            // Для каждого угла — L-баррикада из 5 мешков
            var allClusters = new List<CornerCluster>();

            // Юго-западный угол
            allClusters.Add(new CornerCluster
            {
                cornerPoint = swCorner,
                positions = new IntVec3[]
                {
                    swCorner + new IntVec3(-1, 0, 0),
                    swCorner + new IntVec3(-1, 0, 1),
                    swCorner + new IntVec3(-1, 0, 2),
                    swCorner + new IntVec3(0, 0, -1),
                    swCorner + new IntVec3(1, 0, -1),
                    swCorner + new IntVec3(2, 0, -1),
                    swCorner + new IntVec3(0, 0, 0),
                }
            });

            // Юго-восточный угол
            allClusters.Add(new CornerCluster
            {
                cornerPoint = seCorner,
                positions = new IntVec3[]
                {
                    seCorner + new IntVec3(1, 0, 0),
                    seCorner + new IntVec3(1, 0, 1),
                    seCorner + new IntVec3(1, 0, 2),
                    seCorner + new IntVec3(0, 0, -1),
                    seCorner + new IntVec3(-1, 0, -1),
                    seCorner + new IntVec3(-2, 0, -1),
                    seCorner + new IntVec3(0, 0, 0),
                }
            });

            // Северо-западный угол
            allClusters.Add(new CornerCluster
            {
                cornerPoint = nwCorner,
                positions = new IntVec3[]
                {
                    nwCorner + new IntVec3(-1, 0, 0),
                    nwCorner + new IntVec3(-1, 0, -1),
                    nwCorner + new IntVec3(-1, 0, -2),
                    nwCorner + new IntVec3(0, 0, 1),
                    nwCorner + new IntVec3(1, 0, 1),
                    nwCorner + new IntVec3(2, 0, 1),
                    nwCorner + new IntVec3(0, 0, 0),
                }
            });

            // Северо-восточный угол
            allClusters.Add(new CornerCluster
            {
                cornerPoint = neCorner,
                positions = new IntVec3[]
                {
                    neCorner + new IntVec3(1, 0, 0),
                    neCorner + new IntVec3(1, 0, -1),
                    neCorner + new IntVec3(1, 0, -2),
                    neCorner + new IntVec3(0, 0, 1),
                    neCorner + new IntVec3(-1, 0, 1),
                    neCorner + new IntVec3(-2, 0, 1),
                    neCorner + new IntVec3(0, 0, 0),
                }
            });

            // Сортируем по дальности от центра лагеря — берём 1-2 самых дальних
            allClusters.Sort((a, b) =>
                b.cornerPoint.DistanceTo(campCenter).CompareTo(a.cornerPoint.DistanceTo(campCenter)));

            int clusterCount = Rand.RangeInclusive(1, 2);

            for (int i = 0; i < clusterCount && i < allClusters.Count; i++)
            {
                foreach (IntVec3 spot in allClusters[i].positions)
                {
                    if (spot.InBounds(map) && spot.Standable(map)
                        && spot.GetFirstBuilding(map) == null)
                    {
                        Thing bag = ThingMaker.MakeThing(sandbags, stuff);
                        bag.SetFactionDirect(faction);
                        GenSpawn.Spawn(bag, spot, map);
                    }
                }
            }
        }

        private struct CornerCluster
        {
            public IntVec3 cornerPoint;
            public IntVec3[] positions;
        }

        // ===================== ОДИН ГЕНЕРАТОР НА ЛАГЕРЬ =====================

        private void SpawnCampGenerator(Map map, IntVec3 center, List<BuildingData> buildings, Faction faction)
        {
            ThingDef generatorDef = DefDatabase<ThingDef>.GetNamedSilentFail("WoodFiredGenerator");
            if (generatorDef == null) return;

            ThingDef conduitDef = DefDatabase<ThingDef>.GetNamedSilentFail("PowerConduit");

            IntVec3[] genCandidates = new IntVec3[]
            {
                center + new IntVec3(6, 0, 0),
                center + new IntVec3(-6, 0, 0),
                center + new IntVec3(0, 0, 6),
                center + new IntVec3(0, 0, -6),
                center + new IntVec3(5, 0, 5),
                center + new IntVec3(-5, 0, -5),
            };

            IntVec3 genPos = IntVec3.Invalid;
            foreach (IntVec3 candidate in genCandidates)
            {
                if (candidate.InBounds(map) && candidate.Standable(map)
                    && candidate.GetFirstBuilding(map) == null)
                {
                    genPos = candidate;
                    break;
                }
            }

            if (!genPos.IsValid) return;

            Thing generator = ThingMaker.MakeThing(generatorDef);
            generator.SetFactionDirect(faction);
            GenSpawn.Spawn(generator, genPos, map);

            CompRefuelable fuel = generator.TryGetComp<CompRefuelable>();
            fuel?.Refuel(fuel.Props.fuelCapacity);

            if (conduitDef == null) return;

            foreach (BuildingData building in buildings)
            {
                IntVec3 wallTarget = new IntVec3(
                    UnityEngine.Mathf.Clamp(genPos.x, building.corner.x, building.corner.x + building.width - 1),
                    0,
                    UnityEngine.Mathf.Clamp(genPos.z, building.corner.z, building.corner.z + building.height - 1));

                foreach (IntVec3 cell in CellsBetween(genPos, wallTarget))
                {
                    if (cell.InBounds(map) && !cell.GetThingList(map).Any(t => t.def == conduitDef))
                    {
                        Thing conduit = ThingMaker.MakeThing(conduitDef);
                        conduit.SetFactionDirect(faction);
                        GenSpawn.Spawn(conduit, cell, map);
                    }
                }
            }
        }

        private IEnumerable<IntVec3> CellsBetween(IntVec3 a, IntVec3 b)
        {
            int xDir = (b.x > a.x) ? 1 : (b.x < a.x) ? -1 : 0;
            int zDir = (b.z > a.z) ? 1 : (b.z < a.z) ? -1 : 0;

            IntVec3 current = a;
            while (current.x != b.x)
            {
                yield return current;
                current = new IntVec3(current.x + xDir, 0, current.z);
            }
            while (current.z != b.z)
            {
                yield return current;
                current = new IntVec3(current.x, 0, current.z + zDir);
            }
            yield return b;
        }

        // ===================== ОХРАНА =====================

        private List<Pawn> SpawnGuards(Map map, IntVec3 center, Faction faction, float threatPoints)
        {
            List<Pawn> guards = new List<Pawn>();

            int guardCount;
            if (threatPoints >= 1800f) guardCount = Rand.RangeInclusive(8, 12);
            else if (threatPoints >= 1200f) guardCount = Rand.RangeInclusive(5, 8);
            else guardCount = Rand.RangeInclusive(3, 5);

            PawnKindDef guardKind = faction.def.basicMemberKind ?? PawnKindDefOf.Villager;

            if (faction.def.pawnGroupMakers != null)
            {
                var combatKinds = faction.def.pawnGroupMakers
                    .Where(pgm => pgm.kindDef == PawnGroupKindDefOf.Combat)
                    .SelectMany(pgm => pgm.options)
                    .Select(opt => opt.kind)
                    .Where(k => k != null)
                    .ToList();

                if (combatKinds.Any())
                    guardKind = combatKinds.RandomElement();
            }

            for (int i = 0; i < guardCount; i++)
            {
                PawnGenerationRequest request = new PawnGenerationRequest(
                    kind: guardKind,
                    faction: faction,
                    context: PawnGenerationContext.NonPlayer,
                    mustBeCapableOfViolence: true
                );

                Pawn guard = PawnGenerator.GeneratePawn(request);
                if (guard == null) continue;

                IntVec3 guardSpot;
                CellFinder.TryFindRandomCellNear(center, map, 18,
                    (IntVec3 c) => c.Standable(map) && c.GetFirstPawn(map) == null,
                    out guardSpot);

                GenSpawn.Spawn(guard, guardSpot, map);
                guards.Add(guard);
            }

            return guards;
        }
    }
}
