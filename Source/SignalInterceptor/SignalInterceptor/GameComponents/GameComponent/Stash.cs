using RimWorld;
using RimWorld.Planet;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;

namespace SignalInterceptor
{
    public partial class SignalInterceptorGameComponent
    {
        public void TrackSite(Site site, float threatPoints, Faction faction = null, int tier = 0)
        {
            trackedSites.Add(new StashSiteData
            {
                site = site,
                threatPoints = threatPoints,
                lootSpawned = false,
                faction = faction,
                tier = (tier > 0) ? tier : GetTier(threatPoints)
            });
            Log.Message("[Signal Interceptor] Now tracking site. Threat: " + threatPoints +
                        " | Faction: " + (faction?.Name ?? "null") +
                        " | Tier: " + tier +
                        " | Total tracked: " + trackedSites.Count);
        }

        public List<StashSiteData> GetActiveStashes()
        {
            return trackedSites
                .Where(s => s.site != null && s.site.Spawned)
                .ToList();
        }

        public void RemoveStash(StashSiteData stash)
        {
            trackedSites.Remove(stash);
        }
        private void SpawnLoot(Map map, float threatPoints)
        {
            int tier = GetTier(threatPoints);
            List<Thing> loot = GenerateLoot(tier);
            IntVec3 lootSpot = FindLootSpot(map);

            foreach (Thing item in loot)
            {
                int totalCount = item.stackCount;
                while (totalCount > 0)
                {
                    int spawnCount = System.Math.Min(totalCount, item.def.stackLimit);
                    totalCount -= spawnCount;

                    Thing spawnItem;
                    if (spawnCount == item.stackCount)
                    {
                        spawnItem = item;
                    }
                    else
                    {
                        spawnItem = ThingMaker.MakeThing(item.def, item.Stuff);
                    }
                    spawnItem.stackCount = spawnCount;

                    IntVec3 spawnSpot = lootSpot;
                    CellFinder.TryFindRandomCellNear(lootSpot, map, 5,
                        (IntVec3 c) => c.Standable(map) && c.GetFirstItem(map) == null,
                        out spawnSpot);
                    GenSpawn.Spawn(spawnItem, spawnSpot, map);
                }
            }
        }

        public int GetTier(float points)
        {
            if (points >= 2200f) return 6;
            if (points >= 1700f) return 5;
            if (points >= 1200f) return 4;
            if (points >= 800f) return 3;
            if (points >= 450f) return 2;
            return 1;
        }

        private void SpawnCampProps(Map map, IntVec3 center)
        {
            ThingDef campfireDef = DefDatabase<ThingDef>.GetNamedSilentFail("Campfire");
            if (campfireDef != null)
            {
                IntVec3 fireSpot;
                if (CellFinder.TryFindRandomCellNear(center, map, 5,
                    (IntVec3 c) => c.Standable(map) && !c.Roofed(map) && c.GetFirstThing(map, campfireDef) == null,
                    out fireSpot))
                {
                    Thing campfire = ThingMaker.MakeThing(campfireDef);
                    GenSpawn.Spawn(campfire, fireSpot, map);

                    CompRefuelable fuel = campfire.TryGetComp<CompRefuelable>();
                    fuel?.Refuel(fuel.Props.fuelCapacity);
                }
            }

            ThingDef mealDef = DefDatabase<ThingDef>.GetNamedSilentFail("MealSurvivalPack");
            if (mealDef != null)
            {
                int packs = Rand.RangeInclusive(3, 6);
                for (int i = 0; i < packs; i++)
                {
                    IntVec3 mealSpot;
                    if (CellFinder.TryFindRandomCellNear(center, map, 4,
                        (IntVec3 c) => c.Standable(map) && c.GetFirstItem(map) == null,
                        out mealSpot))
                    {
                        Thing meal = ThingMaker.MakeThing(mealDef);
                        meal.stackCount = Rand.RangeInclusive(2, 5);
                        GenSpawn.Spawn(meal, mealSpot, map);
                    }
                }
            }
        }

        private IntVec3 FindLootSpot(Map map)
        {
            IntVec3 spot = map.Center;

            List<Thing> buildings = map.listerThings.AllThings
                .Where(t => t.def.building != null && t.Faction != null && t.Faction != Faction.OfPlayer)
                .ToList();

            if (buildings.Any())
            {
                Thing building = buildings.RandomElement();
                for (int i = 0; i < 50; i++)
                {
                    IntVec3 candidate = building.Position + GenRadial.RadialPattern[i];
                    if (candidate.InBounds(map) && candidate.Standable(map) && candidate.GetFirstItem(map) == null)
                    {
                        return candidate;
                    }
                }
            }

            CellFinder.TryFindRandomCellNear(map.Center, map, 15,
                (IntVec3 c) => c.Standable(map) && c.GetFirstItem(map) == null,
                out spot);
            return spot;
        }

        private List<Thing> GenerateLoot(int tier)
        {
            List<Thing> loot = new List<Thing>();

            Thing silver = ThingMaker.MakeThing(ThingDefOf.Silver);
            switch (tier)
            {
                case 1: silver.stackCount = Rand.RangeInclusive(100, 250); break;
                case 2: silver.stackCount = Rand.RangeInclusive(250, 450); break;
                case 3: silver.stackCount = Rand.RangeInclusive(450, 700); break;
                case 4: silver.stackCount = Rand.RangeInclusive(700, 1000); break;
                case 5: silver.stackCount = Rand.RangeInclusive(1000, 1500); break;
                case 6: silver.stackCount = Rand.RangeInclusive(1500, 2500); break;
                default: silver.stackCount = 200; break;
            }
            loot.Add(silver);

            Thing gold = ThingMaker.MakeThing(ThingDefOf.Gold);
            switch (tier)
            {
                case 1: gold.stackCount = Rand.RangeInclusive(3, 8); break;
                case 2: gold.stackCount = Rand.RangeInclusive(8, 18); break;
                case 3: gold.stackCount = Rand.RangeInclusive(18, 35); break;
                case 4: gold.stackCount = Rand.RangeInclusive(35, 55); break;
                case 5: gold.stackCount = Rand.RangeInclusive(55, 80); break;
                case 6: gold.stackCount = Rand.RangeInclusive(80, 120); break;
                default: gold.stackCount = 5; break;
            }
            loot.Add(gold);

            Thing components = ThingMaker.MakeThing(ThingDefOf.ComponentIndustrial);
            switch (tier)
            {
                case 1: components.stackCount = Rand.RangeInclusive(2, 5); break;
                case 2: components.stackCount = Rand.RangeInclusive(5, 10); break;
                case 3: components.stackCount = Rand.RangeInclusive(10, 18); break;
                case 4: components.stackCount = Rand.RangeInclusive(18, 28); break;
                case 5: components.stackCount = Rand.RangeInclusive(28, 40); break;
                case 6: components.stackCount = Rand.RangeInclusive(40, 55); break;
                default: components.stackCount = 3; break;
            }
            loot.Add(components);

            Thing meds = ThingMaker.MakeThing(ThingDefOf.MedicineIndustrial);
            switch (tier)
            {
                case 1: meds.stackCount = Rand.RangeInclusive(2, 5); break;
                case 2: meds.stackCount = Rand.RangeInclusive(5, 10); break;
                case 3: meds.stackCount = Rand.RangeInclusive(10, 18); break;
                case 4: meds.stackCount = Rand.RangeInclusive(18, 25); break;
                case 5: meds.stackCount = Rand.RangeInclusive(20, 25); break;
                case 6: meds.stackCount = Rand.RangeInclusive(25, 25); break;
                default: meds.stackCount = 3; break;
            }
            loot.Add(meds);

            if (tier >= 2)
            {
                Thing plasteel = ThingMaker.MakeThing(ThingDefOf.Plasteel);
                switch (tier)
                {
                    case 2: plasteel.stackCount = Rand.RangeInclusive(10, 20); break;
                    case 3: plasteel.stackCount = Rand.RangeInclusive(20, 40); break;
                    case 4: plasteel.stackCount = Rand.RangeInclusive(40, 65); break;
                    case 5: plasteel.stackCount = Rand.RangeInclusive(65, 90); break;
                    case 6: plasteel.stackCount = Rand.RangeInclusive(90, 130); break;
                    default: plasteel.stackCount = 15; break;
                }
                loot.Add(plasteel);
            }

            if (tier >= 3 && Rand.Chance(0.3f + (tier - 3) * 0.15f))
            {
                Thing advComp = ThingMaker.MakeThing(ThingDefOf.ComponentSpacer);
                switch (tier)
                {
                    case 3: advComp.stackCount = Rand.RangeInclusive(1, 2); break;
                    case 4: advComp.stackCount = Rand.RangeInclusive(2, 4); break;
                    case 5: advComp.stackCount = Rand.RangeInclusive(4, 7); break;
                    case 6: advComp.stackCount = Rand.RangeInclusive(7, 12); break;
                    default: advComp.stackCount = 1; break;
                }
                loot.Add(advComp);
            }

            if (tier >= 4 && Rand.Chance(0.3f + (tier - 4) * 0.15f))
            {
                Thing ultMeds = ThingMaker.MakeThing(ThingDefOf.MedicineUltratech);
                switch (tier)
                {
                    case 4: ultMeds.stackCount = Rand.RangeInclusive(1, 3); break;
                    case 5: ultMeds.stackCount = Rand.RangeInclusive(3, 6); break;
                    case 6: ultMeds.stackCount = Rand.RangeInclusive(6, 10); break;
                    default: ultMeds.stackCount = 1; break;
                }
                loot.Add(ultMeds);
            }

            if (tier >= 4 && Rand.Chance(0.2f + (tier - 4) * 0.15f))
            {
                IEnumerable<ThingDef> neurotrainers = DefDatabase<ThingDef>.AllDefs
                    .Where(d => d.defName.StartsWith("Neurotrainer"));
                if (neurotrainers.Any())
                {
                    loot.Add(ThingMaker.MakeThing(neurotrainers.RandomElement()));
                    if (tier >= 6 && Rand.Chance(0.3f))
                    {
                        loot.Add(ThingMaker.MakeThing(neurotrainers.RandomElement()));
                    }
                }
            }

            if (tier >= 4 && Rand.Chance(0.1f + (tier - 4) * 0.1f))
            {
                ThingDef healSerum = DefDatabase<ThingDef>.GetNamedSilentFail("MechSerumHealer");
                if (healSerum != null)
                {
                    loot.Add(ThingMaker.MakeThing(healSerum));
                }
            }

            if (tier >= 5 && Rand.Chance(0.05f + (tier - 5) * 0.08f))
            {
                ThingDef resSerum = DefDatabase<ThingDef>.GetNamedSilentFail("MechSerumResurrector");
                if (resSerum != null)
                {
                    loot.Add(ThingMaker.MakeThing(resSerum));
                }
            }

            if (tier >= 4 && ModsConfig.IsActive("Ludeon.RimWorld.Odyssey"))
            {
                float weaponChance = 0.1f + (tier - 4) * 0.15f;
                if (Rand.Chance(weaponChance))
                {
                    Thing uniqueWeapon = SignalInterceptorUtility.TryGenerateUniqueWeapon();
                    if (uniqueWeapon != null)
                    {
                        loot.Add(uniqueWeapon);
                    }
                    if (tier >= 6 && Rand.Chance(0.25f))
                    {
                        Thing secondWeapon = SignalInterceptorUtility.TryGenerateUniqueWeapon();
                        if (secondWeapon != null)
                        {
                            loot.Add(secondWeapon);
                        }
                    }
                }
            }

            if (tier >= 6 && Rand.Chance(0.08f))
            {
                loot.Add(ThingMaker.MakeThing(ThingDefOf.AIPersonaCore));
            }

            return loot;
        }

        private void TickStashSites()
        {
            // Обработка stash сайтов
            for (int i = trackedSites.Count - 1; i >= 0; i--)
            {
                StashSiteData data = trackedSites[i];

                if (data.site == null || !data.site.Spawned)
                {
                    trackedSites.RemoveAt(i);
                    continue;
                }

                if (!data.lootSpawned && data.site.HasMap)
                {
                    SpawnLoot(data.site.Map, data.threatPoints);
                    data.lootSpawned = true;
                }
            }
        }
    }
}
