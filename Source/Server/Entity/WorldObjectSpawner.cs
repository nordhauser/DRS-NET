using System;
using System.Collections.Generic;
using DungeonRunners.Combat;
using DungeonRunners.Engine;

namespace DungeonRunners.Managers
{
    // ═══════════════════════════════════════════════════════════════════════════
    // WORLD OBJECT SPAWNER — Barrels + Treasure Chests in dungeon zones
    //
    // Barrels: Spawned as one-hit creatures via CombatManager.SpawnMonster().
    //          When killed, ProcessMonsterKill fires → LootManager.GenerateDestroyableLoot().
    //
    // Chests:  Spawned as NCI entities using SAME packet pattern as checkpoints.
    //          When clicked, HandleChestActivation fires → LootManager.GenerateChestLoot().
    //          Uses same 0x01 create + 0x02 init + 0x35 activation as portals/checkpoints.
    //
    // Binary: World::attachObjects spawns StaticObjectGeneratorTable (barrels)
    //         + WorldEntityGeneratorTable (chests/NCIs) per zone tile.
    // ═══════════════════════════════════════════════════════════════════════════

    public static class WorldObjectSpawner
    {
        private const string NativeWorldObjectRngContract = "GCObjectGeneratorTable<WorldEntity>::GenerateObjectFromTable@0x00568150 Random::generate@0x0044B1F0";

        // Authored PKG barrel GC types. Keep synthetic world.objects.* as legacy fallback only.
        private static readonly string[] BarrelTypes =
        {
            "terrain.misc.interactives.Breakiable_Barrel_01",
            "terrain.misc.interactives.Breakiable_Barrel_02",
            "terrain.misc.interactives.Breakiable_Barrel_03",
        };
        private const string CrateType = "world.objects.crate.breakable";

        // ── Chest GC types (NCI entities, match original binary GC paths) ──
        private static readonly string[] SmallChestTypes =
        {
            "terrain.interactives.loot.Chest_Sm_01",
            "terrain.interactives.loot.Chest_Sm_02",
            "terrain.interactives.loot.Chest_Sm_03",
        };
        private static readonly string[] MediumChestTypes =
        {
            "terrain.interactives.loot.Chest_Md_01",
            "terrain.interactives.loot.Chest_Md_02",
            "terrain.interactives.loot.Chest_Md_03",
        };
        private const string LargeChestType = "terrain.interactives.loot.Chest_Lg_01";

        // ── Placement offsets within maze tiles ──
        private static readonly float[][] ObjectOffsets =
        {
            new[] { -40f, -30f }, new[] {  35f, -25f },
            new[] { -30f,  40f }, new[] {  45f,  35f },
            new[] { -50f,   0f }, new[] {   0f, -50f },
            new[] {  25f,  50f }, new[] { -45f,  20f },
        };

        // ═══════════════════════════════════════════════════════════════
        // BARREL SPAWNS — returned as DungeonSpawnData for SpawnMonster
        // ═══════════════════════════════════════════════════════════════

        public static List<DatabaseLoader.DungeonSpawnData> GenerateBarrels(
            string zoneName, uint seed = 0xBEEFBEEF)
        {
            var spawns = new List<DatabaseLoader.DungeonSpawnData>();
            if (!DungeonMazeSpawner.IsProceduralZone(zoneName)) return spawns;

            var cells = GetMazeCells(zoneName, seed);

            int cellIndex = 0;
            foreach (var cell in cells)
            {
                if (cell.isEntry)
                {
                    cellIndex++;
                    continue;
                }

                string cellOwner = $"{zoneName}:cell{cellIndex}";

                // 40% of cells get 1-2 barrels
                if (NextWorldObjectChance(40, "WorldObjectSpawner.barrel.cellChance", cellOwner))
                {
                    int count = NextWorldObjectIntInclusive(1, 2, "WorldObjectSpawner.barrel.count", cellOwner);
                    for (int i = 0; i < count; i++)
                    {
                        string barrelOwner = $"{cellOwner}:barrel{i}";
                        var off = ObjectOffsets[NextWorldObjectIntInclusive(0, ObjectOffsets.Length - 1, "WorldObjectSpawner.barrel.offset", barrelOwner)];
                        float px = cell.cx + off[0] + NextWorldObjectFloat(-5f, 5f, "WorldObjectSpawner.barrel.jitterX", barrelOwner);
                        float py = cell.cy + off[1] + NextWorldObjectFloat(-5f, 5f, "WorldObjectSpawner.barrel.jitterY", barrelOwner);
                        spawns.Add(new DatabaseLoader.DungeonSpawnData
                        {
                            zoneName = zoneName,
                            gcType = BarrelTypes[NextWorldObjectIntInclusive(0, BarrelTypes.Length - 1, "WorldObjectSpawner.barrel.type", barrelOwner)],
                            posX = px,
                            posY = py,
                            posZ = Core.PathMapManager.Instance.GetHeight(zoneName, px, py, 10f),
                            heading = NextWorldObjectIntInclusive(0, 359, "WorldObjectSpawner.barrel.heading", barrelOwner)
                        });
                    }
                }

                // 15% of cells get a crate
                if (NextWorldObjectChance(15, "WorldObjectSpawner.crate.cellChance", cellOwner))
                {
                    var off = ObjectOffsets[NextWorldObjectIntInclusive(0, ObjectOffsets.Length - 1, "WorldObjectSpawner.crate.offset", cellOwner)];
                    float px = cell.cx + off[0];
                    float py = cell.cy + off[1];
                    spawns.Add(new DatabaseLoader.DungeonSpawnData
                    {
                        zoneName = zoneName,
                        gcType = CrateType,
                        posX = px,
                        posY = py,
                        posZ = Core.PathMapManager.Instance.GetHeight(zoneName, px, py, 10f),
                        heading = NextWorldObjectIntInclusive(0, 359, "WorldObjectSpawner.crate.heading", cellOwner)
                    });
                }

                cellIndex++;
            }

            Debug.LogError($"[WorldObjects] {zoneName}: {spawns.Count} barrels/crates");
            return spawns;
        }

        // ═══════════════════════════════════════════════════════════════
        // CHEST SPAWNS — returned as ChestSpawnData for NCI entity packets
        // Spawned using SAME pattern as checkpoints (0x01 + 0x02)
        // ═══════════════════════════════════════════════════════════════

        public static List<ChestSpawnData> GenerateChests(
            string zoneName, uint seed = 0xBEEFBEEF)
        {
            var chests = new List<ChestSpawnData>();
            if (!DungeonMazeSpawner.IsProceduralZone(zoneName)) return chests;

            var cells = GetMazeCells(zoneName, seed);

            int cellIndex = 0;
            foreach (var cell in cells)
            {
                if (cell.isEntry) { cellIndex++; continue; }

                string cellOwner = $"{zoneName}:cell{cellIndex}";

                // Small chest: ~20% of cells (1 per cell max)
                if (NextWorldObjectChance(20, "WorldObjectSpawner.chest.smallChance", cellOwner))
                {
                    var off = ObjectOffsets[(cellIndex + 3) % ObjectOffsets.Length];
                    float px = cell.cx + off[0];
                    float py = cell.cy + off[1];
                    SnapAndGetHeight(zoneName, ref px, ref py, out float pz);
                    chests.Add(new ChestSpawnData
                    {
                        GCType = SmallChestTypes[NextWorldObjectIntInclusive(0, SmallChestTypes.Length - 1, "WorldObjectSpawner.chest.smallType", cellOwner)],
                        Label = "Treasure Chest",
                        PosX = px,
                        PosY = py,
                        PosZ = pz,
                        Heading = NextWorldObjectIntInclusive(0, 359, "WorldObjectSpawner.chest.smallHeading", cellOwner),
                        ItemGenerator = "TreasureChestSmallIG",
                        ItemCount = 1,
                        ItemGenerator2 = "DefaultIG",
                        ItemCount2 = 2,
                    });
                }

                // Medium chest: ~5% of cells (rarer, better loot)
                if (NextWorldObjectChance(5, "WorldObjectSpawner.chest.mediumChance", cellOwner))
                {
                    var off = ObjectOffsets[(cellIndex + 5) % ObjectOffsets.Length];
                    float px = cell.cx + off[0];
                    float py = cell.cy + off[1];
                    SnapAndGetHeight(zoneName, ref px, ref py, out float pz);
                    chests.Add(new ChestSpawnData
                    {
                        GCType = MediumChestTypes[NextWorldObjectIntInclusive(0, MediumChestTypes.Length - 1, "WorldObjectSpawner.chest.mediumType", cellOwner)],
                        Label = "Large Treasure Chest",
                        PosX = px,
                        PosY = py,
                        PosZ = pz,
                        Heading = NextWorldObjectIntInclusive(0, 359, "WorldObjectSpawner.chest.mediumHeading", cellOwner),
                        ItemGenerator = "TreasureChestMediumIG",
                        ItemCount = 2,
                        ItemGenerator2 = "DefaultIG",
                        ItemCount2 = 2,
                    });
                }

                cellIndex++;
            }

            // One large chest per zone (boss-quality, always in last cell)
            if (cells.Count > 2)
            {
                var lastCell = cells[cells.Count - 1];
                float lx = lastCell.cx;
                float ly = lastCell.cy + 30f;
                SnapAndGetHeight(zoneName, ref lx, ref ly, out float lz);
                chests.Add(new ChestSpawnData
                {
                    GCType = LargeChestType,
                    Label = "Grand Treasure Chest",
                    PosX = lx,
                    PosY = ly,
                    PosZ = lz,
                    Heading = 0,
                    ItemGenerator = "TreasureChestLargeIG",
                    ItemCount = 1,
                    ItemGenerator2 = "DefaultIG",
                    ItemCount2 = 4,
                });
            }

            Debug.LogError($"[WorldObjects] {zoneName}: {chests.Count} treasure chests");
            return chests;
        }

        /// <summary>
        /// Snap position to nearest walkable pathmap node and get correct Z height.
        /// Same approach as DungeonMazeSpawner.FindWalkableSpot for mobs.
        /// </summary>
        private static void SnapAndGetHeight(string zoneName, ref float x, ref float y, out float z)
        {
            z = Core.PathMapManager.Instance.GetHeight(zoneName, x, y, 50f) + 3f;
        }

        // ═══════════════════════════════════════════════════════════════
        // HELPERS
        // ═══════════════════════════════════════════════════════════════

        private static uint NextWorldObjectRaw(string phase, string owner)
        {
            uint raw = NativeRandomStreams.GenerateGlobalStatic(phase, owner);
            Debug.LogError($"[WORLDOBJECT-RNG-NATIVE] stream=globalStatic phase={phase} raw=0x{raw:X8} owner='{owner}' native={NativeWorldObjectRngContract}");
            return raw;
        }

        private static int NextWorldObjectIntInclusive(int minInclusive, int maxInclusive, string phase, string owner)
        {
            if (maxInclusive <= minInclusive)
                return minInclusive;
            uint value = NativeRandomStreams.GenerateGlobalStaticRangeInclusive((uint)minInclusive, (uint)maxInclusive, phase, owner);
            Debug.LogError($"[WORLDOBJECT-RNG-NATIVE] stream=globalStatic phase={phase} value={value} range=[{minInclusive}..{maxInclusive}] owner='{owner}' native={NativeWorldObjectRngContract}");
            return (int)value;
        }

        private static bool NextWorldObjectChance(int percent, string phase, string owner)
        {
            int clamped = Math.Max(0, Math.Min(100, percent));
            int roll = NextWorldObjectIntInclusive(0, 99, phase, owner);
            bool result = roll < clamped;
            Debug.LogError($"[WORLDOBJECT-RNG-NATIVE] stream=globalStatic phase={phase}.result roll={roll} percent={clamped} result={result} owner='{owner}' native={NativeWorldObjectRngContract}");
            return result;
        }

        private static float NextWorldObjectFloat(float minInclusive, float maxExclusive, string phase, string owner)
        {
            if (maxExclusive <= minInclusive)
                return minInclusive;
            uint raw = NextWorldObjectRaw(phase, owner);
            float unit = (raw & 0x00FFFFFF) / 16777216f;
            float value = minInclusive + unit * (maxExclusive - minInclusive);
            Debug.LogError($"[WORLDOBJECT-RNG-NATIVE] stream=globalStatic phase={phase} value={value:F3} range=[{minInclusive:F3}..{maxExclusive:F3}) owner='{owner}' native={NativeWorldObjectRngContract}");
            return value;
        }

        public static bool IsDestroyableObject(string gcType)
        {
            if (string.IsNullOrEmpty(gcType)) return false;
            if (gcType.StartsWith("world.objects.barrel", StringComparison.OrdinalIgnoreCase) ||
                gcType.StartsWith("world.objects.crate", StringComparison.OrdinalIgnoreCase))
                return true;
            for (int i = 0; i < BarrelTypes.Length; i++)
            {
                if (string.Equals(gcType, BarrelTypes[i], StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private struct CellPos { public float cx, cy; public bool isEntry; }

        private static List<CellPos> GetMazeCells(string zoneName, uint seed)
        {
            if (!DungeonMazeSpawner.TryGetMazeDimensions(zoneName,
                out int width, out int height, out int entryX, out int entryY,
                out int randomness, out int sparseness, out int deadEndRemoval))
                return new List<CellPos>();

            var maze = new MazeGenerator(width, height, seed,
                randomness, sparseness, deadEndRemoval);
            var cells = maze.Generate();
            var result = new List<CellPos>();
            foreach (var c in cells)
                result.Add(new CellPos
                {
                    cx = c.WorldCenterX,
                    cy = c.WorldCenterY,
                    isEntry = (c.GridX == entryX && c.GridY == entryY)
                });
            return result;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // CHEST SPAWN DATA — position + loot info for NCI entity spawning
    // ═══════════════════════════════════════════════════════════════════════════

    public class ChestSpawnData
    {
        public string GCType;          // terrain.interactives.loot.Chest_Sm_01
        public string Label;           // Display name
        public float PosX, PosY, PosZ;
        public float Heading;
        public string ItemGenerator;   // TreasureChestIG, TreasureChestSmallIG
        public int ItemCount;          // How many items to generate
        public string ItemGenerator2;
        public int ItemCount2;
        public string ItemGenerator3;
        public int ItemCount3;
        public string ItemGenerator4;
        public int ItemCount4;
        public string ItemGenerator5;
        public int ItemCount5;

        public IEnumerable<(string Generator, int Count, int Slot)> GetNativeChestGenerators(string fallbackGenerator, int fallbackCount)
        {
            string gen1 = !string.IsNullOrWhiteSpace(ItemGenerator) ? ItemGenerator : fallbackGenerator;
            int count1 = ItemCount > 0 ? ItemCount : fallbackCount;
            if (!string.IsNullOrWhiteSpace(gen1) && count1 > 0) yield return (gen1, count1, 1);
            if (!string.IsNullOrWhiteSpace(ItemGenerator2) && ItemCount2 > 0) yield return (ItemGenerator2, ItemCount2, 2);
            if (!string.IsNullOrWhiteSpace(ItemGenerator3) && ItemCount3 > 0) yield return (ItemGenerator3, ItemCount3, 3);
            if (!string.IsNullOrWhiteSpace(ItemGenerator4) && ItemCount4 > 0) yield return (ItemGenerator4, ItemCount4, 4);
            if (!string.IsNullOrWhiteSpace(ItemGenerator5) && ItemCount5 > 0) yield return (ItemGenerator5, ItemCount5, 5);
        }
    }
}
