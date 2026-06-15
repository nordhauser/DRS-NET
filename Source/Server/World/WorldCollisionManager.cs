using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using DungeonRunners.Data;
using DungeonRunners.Managers;
using DungeonRunners.Engine;

namespace DungeonRunners.Core
{
    public sealed class WorldCollisionHit
    {
        public bool Blocked;
        public string ZoneName;
        public string InstanceKey;
        public string TileType;
        public int GridX;
        public int GridY;
        public string ObjectPath;
        public string CollisionObject;
        public float WorldX;
        public float WorldY;
        public float WorldZ;
        public float LocalX;
        public float LocalY;
        public float LocalZ;
        public float Distance;
    }

    public sealed class WorldCollisionManager
    {
        private sealed class CollisionBox
        {
            public string ZoneName;
            public string InstanceKey;
            public string TileType;
            public int GridX;
            public int GridY;
            public float TileOriginX;
            public float TileOriginY;
            public string ObjectPath;
            public string CollisionObject;
            public Vector3 Position;
            public float HeadingDeg;
            public bool HeightOnly;
            public float MinX;
            public float MinY;
            public float MinZ;
            public float MaxX;
            public float MaxY;
            public float MaxZ;
            public HybridCollisionObject Hybrid;
        }

        private sealed class CollisionCache
        {
            public string ZoneName;
            public string InstanceKey;
            public readonly List<CollisionBox> Boxes = new List<CollisionBox>();
            public readonly List<CollisionBox> HeightSurfaces = new List<CollisionBox>();
        }

        private sealed class StaticBounds
        {
            public string CollisionObject;
            public bool HeightOnly;
            public float MinX;
            public float MinY;
            public float MinZ;
            public float MaxX;
            public float MaxY;
            public float MaxZ;
            public HybridCollisionObject Hybrid;
            public string HybridSource;
        }

        private static WorldCollisionManager _instance;
        public static WorldCollisionManager Instance => _instance ??= new WorldCollisionManager();

        private readonly Dictionary<string, CollisionCache> _instanceCaches = new Dictionary<string, CollisionCache>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, GCNode> _authoredTextCache = new Dictionary<string, GCNode>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, StaticBounds> _staticBoundsCache = new Dictionary<string, StaticBounds>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _loggedMissingDocs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _loggedMissingBounds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _loggedUnsupportedBlockerUse = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private bool _selfTestLogged;

        public void ClearInstance(string instanceKey)
        {
            if (string.IsNullOrWhiteSpace(instanceKey))
                return;
            _instanceCaches.Remove(instanceKey);
        }

        public bool HasLineOfFire(string zoneName, string instanceKey, Vector3 start, Vector3 end, float radius, out WorldCollisionHit hit)
        {
            return !TrySegmentHit(zoneName, instanceKey, start, end, radius, out hit);
        }

        public bool TrySegmentHit(string zoneName, string instanceKey, Vector3 start, Vector3 end, float radius, out WorldCollisionHit hit)
        {
            hit = null;
            var cache = GetOrBuildCache(zoneName, instanceKey);
            if (cache == null || cache.Boxes.Count == 0)
                return false;

            float bestT = float.MaxValue;
            CollisionBox bestBox = null;
            Vector3 bestPoint = default;
            for (int i = 0; i < cache.Boxes.Count; i++)
            {
                CollisionBox box = cache.Boxes[i];
                if (box.Hybrid == null)
                    continue;
                if (!HybridSegmentIntersects(box, start, end, radius, out float t))
                    continue;
                if (t < bestT)
                {
                    bestT = t;
                    bestBox = box;
                    bestPoint = Vector3.Lerp(start, end, t);
                }
            }

            if (bestBox == null)
            {
                string key = !string.IsNullOrWhiteSpace(cache.InstanceKey) ? cache.InstanceKey : cache.ZoneName;
                if (_loggedUnsupportedBlockerUse.Add(key ?? "unknown"))
                    Debug.LogError($"[WORLD-COLLISION] projectile COBJ blockers partial instance='{key ?? ""}' zone='{zoneName ?? ""}' staticBlockers={cache.Boxes.Count} heightSurfaces={cache.HeightSurfaces.Count} native=WorldCollisionObject::testCollision+HybridCollisionObject::testCollision reason=missing-or-unloaded-cobj");
                return false;
            }

            hit = CreateHit(bestBox, bestPoint, Vector3.Distance(start, bestPoint));
            return true;
        }

        public bool TryGetTerrainHeight(string zoneName, string instanceKey, float worldX, float worldY, float referenceZ, out float height, out string source)
        {
            height = referenceZ;
            source = null;
            var cache = GetOrBuildCache(zoneName, instanceKey);
            if (cache == null || cache.HeightSurfaces.Count == 0)
                return false;

            CollisionBox best = null;
            float bestHeight = referenceZ;
            float bestDelta = float.MaxValue;
            for (int i = 0; i < cache.HeightSurfaces.Count; i++)
            {
                CollisionBox surface = cache.HeightSurfaces[i];
                if (!TryGetSurfaceHeight(surface, worldX, worldY, out float candidateHeight))
                    continue;

                float delta = Mathf.Abs(candidateHeight - referenceZ);
                if (best == null || delta < bestDelta || (Mathf.Abs(delta - bestDelta) <= 0.001f && candidateHeight > bestHeight))
                {
                    best = surface;
                    bestHeight = candidateHeight;
                    bestDelta = delta;
                }
            }

            if (best == null)
                return false;

            height = bestHeight;
            source = $"{best.ObjectPath}@{best.TileType}[{best.GridX},{best.GridY}]";
            return true;
        }

        public void RunStartupSelfTest()
        {
            if (_selfTestLogged)
                return;
            _selfTestLogged = true;

            bool wall = TryResolveStaticBounds("terrain.elmforest.walls.elmforest_4_straight_2", out StaticBounds wallBounds);
            bool rock = TryResolveStaticBounds("terrain.elmforest.decor.rocks.elmforest_rock_13", out StaticBounds rockBounds);
            bool tree = TryResolveStaticBounds("terrain.elmforest.decor.trees.elmforest_tree_13", out StaticBounds treeBounds);
            bool wallPointHit = wallBounds?.Hybrid != null &&
                wallBounds.Hybrid.TestBoundingBox(-12f, -12f, 2f, -12f, -12f, 2f);
            bool wallPointClear = wallBounds?.Hybrid != null &&
                !wallBounds.Hybrid.TestBoundingBox(-12f, -12f, 7f, -12f, -12f, 7f);
            bool treePointHit = treeBounds?.Hybrid != null &&
                treeBounds.Hybrid.TestBoundingBox(1f, -9f, 20f, 1f, -9f, 20f);
            bool treePointClear = treeBounds?.Hybrid != null &&
                !treeBounds.Hybrid.TestBoundingBox(1f, -9f, 50f, 1f, -9f, 50f);
            int bundledCobjCount = CountBundledCobjAssets();
            var sidecar = NativeSidecarCatalog.Instance;
            int sidecarCobjCount = sidecar.StaticObjectEntries.Count;
            int sidecarPlacementCount = sidecar.CobjPlacements.Count;
            bool aabbProbeBlocked = TrySegmentHitInTile(
                "dungeon00_level01", "selftest", "elmforest_tileset_1e1w_a",
                0f, 800f, 2, 4,
                new Vector3(84f, 961.6f, 10f),
                new Vector3(230f, 970f, 10f),
                10f,
                out WorldCollisionHit blockedHit);
            bool clear = !TrySegmentHitInTile(
                "dungeon00_level01", "selftest", "elmforest_tileset_1e1w_a",
                0f, 800f, 2, 4,
                new Vector3(240f, 960f, 10f),
                new Vector3(260f, 970f, 10f),
                10f,
                out WorldCollisionHit clearHit);

            Debug.LogError($"[WORLD-COLLISION-SELFTEST] tile=elmforest_tileset_1e1w_a wallBounds={wall} rockBounds={rock} treeBounds={tree} wall=({DescribeBounds(wallBounds)}) rock=({DescribeBounds(rockBounds)}) tree=({DescribeBounds(treeBounds)}) hybridWall={wallBounds?.Hybrid != null} hybridRock={rockBounds?.Hybrid != null} hybridTree={treeBounds?.Hybrid != null} bodyOffsetWall=0x{(wallBounds?.Hybrid?.BodyOffset ?? 0):X} bundledCobj={bundledCobjCount} sidecarCobj={sidecarCobjCount} pointWallHit={wallPointHit} pointWallClear={wallPointClear} pointTreeHit={treePointHit} pointTreeClear={treePointClear} probeBlocked={aabbProbeBlocked} probeObj={blockedHit?.ObjectPath ?? "none"} probeCollision={blockedHit?.CollisionObject ?? "none"} clear={clear} clearObj={clearHit?.ObjectPath ?? "none"} runtimeProjectileBlockers=hybrid-cobj-when-bundled source=bundled-GC+Database/cobj native=HybridCollisionObject::readObject+WorldCollisionObject::testCollision");
            Debug.LogError($"[COBJ-PLACEMENT-SELFTEST] rawType4={sidecarCobjCount} bundledCobj={bundledCobjCount} placementRefs={sidecarPlacementCount} transformRows=0 placementStatus={(sidecarPlacementCount > 0 ? "PARTIAL" : "MISSING")} geometry=BLOCKED_NATIVE_PLACEMENT_TRANSFORM native=WorldCollisionManager+StaticWorldEntityManager");
        }

        private static int CountBundledCobjAssets()
        {
            string[] roots = { DungeonRunners.Core.DataPaths.CobjDir };
            for (int i = 0; i < roots.Length; i++)
            {
                string root = roots[i];
                if (Directory.Exists(root))
                    return Directory.GetFiles(root, "*.cobj", SearchOption.TopDirectoryOnly).Length;
            }
            return 0;
        }

        private CollisionCache GetOrBuildCache(string zoneName, string instanceKey)
        {
            string key = !string.IsNullOrWhiteSpace(instanceKey) ? instanceKey : zoneName;
            if (string.IsNullOrWhiteSpace(key))
                return null;
            if (_instanceCaches.TryGetValue(key, out CollisionCache cached))
                return cached;

            var cache = new CollisionCache
            {
                ZoneName = zoneName,
                InstanceKey = key
            };

            if (ZoneSpawnManager.Instance.TryGetProceduralSnapshot(key, out var snapshot) && snapshot?.Cells != null)
            {
                for (int i = 0; i < snapshot.Cells.Count; i++)
                {
                    var cell = snapshot.Cells[i];
                    AddTileCollision(cache, snapshot.ZoneName ?? zoneName, key, cell.TileType, cell.WorldOriginX, cell.WorldOriginY, cell.GridX, cell.GridY);
                }
            }

            _instanceCaches[key] = cache;
            int hybridCount = 0;
            for (int i = 0; i < cache.Boxes.Count; i++)
                if (cache.Boxes[i].Hybrid != null)
                    hybridCount++;
            Debug.LogError($"[WORLD-COLLISION] cache instance='{key}' zone='{zoneName ?? ""}' staticBlockers={cache.Boxes.Count} hybridBlockers={hybridCount} heightSurfaces={cache.HeightSurfaces.Count} projectileBlockers=hybrid-cobj-partial source=ZoneSpawnManager+bundled-GC+Database/cobj");
            return cache;
        }

        private bool TrySegmentHitInTile(string zoneName, string instanceKey, string tileType, float tileOriginX, float tileOriginY, int gridX, int gridY, Vector3 start, Vector3 end, float radius, out WorldCollisionHit hit)
        {
            var cache = new CollisionCache { ZoneName = zoneName, InstanceKey = instanceKey };
            AddTileCollision(cache, zoneName, instanceKey, tileType, tileOriginX, tileOriginY, gridX, gridY);

            hit = null;
            float bestT = float.MaxValue;
            CollisionBox bestBox = null;
            Vector3 bestPoint = default;
            for (int i = 0; i < cache.Boxes.Count; i++)
            {
                if (cache.Boxes[i].Hybrid == null || !HybridSegmentIntersects(cache.Boxes[i], start, end, radius, out float t))
                    continue;
                if (t < bestT)
                {
                    bestT = t;
                    bestBox = cache.Boxes[i];
                    bestPoint = Vector3.Lerp(start, end, t);
                }
            }

            if (bestBox == null)
                return false;
            hit = CreateHit(bestBox, bestPoint, Vector3.Distance(start, bestPoint));
            return true;
        }

        private void AddTileCollision(CollisionCache cache, string zoneName, string instanceKey, string tileType, float tileOriginX, float tileOriginY, int gridX, int gridY)
        {
            if (cache == null || string.IsNullOrWhiteSpace(tileType))
                return;

            GCNode tile = ResolveAuthoredNode(tileType);
            if (tile == null)
            {
                LogMissingDoc(tileType, "tile");
                return;
            }

            GCNode map = tile.GetChild("Map");
            if (map == null)
                return;

            var placements = new List<GCNode>();
            CollectAnonymousPlacements(map, placements);
            for (int i = 0; i < placements.Count; i++)
            {
                GCNode placement = placements[i];
                if (string.IsNullOrWhiteSpace(placement.Extends) || !placement.HasProperty("Position"))
                    continue;
                if (!TryParseVector3(placement.GetString("Position"), out Vector3 localPos))
                    continue;
                if (!TryResolveStaticBounds(placement.Extends, out StaticBounds bounds))
                    continue;

                bool heightOnly = bounds.HeightOnly || IsTerrainHeightSurfacePath(placement.Extends);
                var box = new CollisionBox
                {
                    ZoneName = zoneName,
                    InstanceKey = instanceKey,
                    TileType = tileType,
                    GridX = gridX,
                    GridY = gridY,
                    TileOriginX = tileOriginX,
                    TileOriginY = tileOriginY,
                    ObjectPath = placement.Extends,
                    CollisionObject = bounds.CollisionObject,
                    Position = new Vector3(tileOriginX + localPos.x, tileOriginY + localPos.y, localPos.z),
                    HeadingDeg = placement.GetFloat("Heading", 0f),
                    HeightOnly = heightOnly,
                    MinX = bounds.MinX,
                    MinY = bounds.MinY,
                    MinZ = bounds.MinZ,
                    MaxX = bounds.MaxX,
                    MaxY = bounds.MaxY,
                    MaxZ = bounds.MaxZ,
                    Hybrid = bounds.Hybrid
                };
                if (heightOnly)
                    cache.HeightSurfaces.Add(box);
                else
                    cache.Boxes.Add(box);
            }
        }

        private void CollectAnonymousPlacements(GCNode node, List<GCNode> placements)
        {
            if (node == null)
                return;

            if (node.IsAnonymous && node.HasProperty("Position") && !string.IsNullOrWhiteSpace(node.Extends))
                placements.Add(node);

            for (int i = 0; i < node.AnonymousChildren.Count; i++)
                CollectAnonymousPlacements(node.AnonymousChildren[i], placements);
            foreach (var child in node.Children.Values)
                CollectAnonymousPlacements(child, placements);
        }

        private bool TryResolveStaticBounds(string path, out StaticBounds bounds)
        {
            bounds = null;
            if (string.IsNullOrWhiteSpace(path))
                return false;
            if (_staticBoundsCache.TryGetValue(path, out bounds))
                return bounds != null;

            GCNode node = GCDatabase.Instance.ResolveWithInheritance(path) ?? ResolveAuthoredNode(path);
            GCNode desc = node?.GetChild("Description");
            if (desc == null || !desc.HasProperty("CollisionObject"))
            {
                _staticBoundsCache[path] = null;
                return false;
            }

            string collisionObject = desc.GetString("CollisionObject");
            if (string.IsNullOrWhiteSpace(collisionObject))
            {
                _staticBoundsCache[path] = null;
                return false;
            }

            bounds = new StaticBounds
            {
                CollisionObject = collisionObject,
                HeightOnly = IsTerrainHeightSurfacePath(path),
                MinX = desc.GetFloat("MinX", 0f),
                MinY = desc.GetFloat("MinY", 0f),
                MinZ = desc.GetFloat("MinZ", 0f),
                MaxX = desc.GetFloat("MaxX", 0f),
                MaxY = desc.GetFloat("MaxY", 0f),
                MaxZ = desc.GetFloat("MaxZ", 0f)
            };
            if (HybridCollisionObject.TryLoadFromServerData(collisionObject, out var hybrid, out string hybridSource))
            {
                bounds.Hybrid = hybrid;
                bounds.HybridSource = hybridSource;
                Debug.LogError($"[COBJ] loaded name='{collisionObject}' source='{hybridSource}' bodyOffset=0x{hybrid.BodyOffset:X} walk={hybrid.WalkGridX}x{hybrid.WalkGridY}@{hybrid.WalkCellSize} block={hybrid.BlockGridX}x{hybrid.BlockGridY}x{hybrid.BlockGridZ}@{hybrid.BlockCellSize} buckets={hybrid.NonEmptyBuckets} ranges={hybrid.RangeCount} native=HybridCollisionObject::readObject");
            }
            _staticBoundsCache[path] = bounds;
            return true;
        }

        private static bool IsTerrainHeightSurfacePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;
            string normalized = path.Replace('\\', '.').Replace('/', '.');
            return normalized.IndexOf(".floor.", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private GCNode ResolveAuthoredNode(string nameOrPath)
        {
            if (string.IsNullOrWhiteSpace(nameOrPath))
                return null;
            if (_authoredTextCache.TryGetValue(nameOrPath, out GCNode cached))
                return cached;

            GCNode node = GCDatabase.Instance.ResolveWithInheritance(nameOrPath);
            _authoredTextCache[nameOrPath] = node;
            return node;
        }

        private bool SegmentIntersectsBox(CollisionBox box, Vector3 start, Vector3 end, float radius, out float tHit)
        {
            Vector3 localStart = WorldToObjectLocal(box, start);
            Vector3 localEnd = WorldToObjectLocal(box, end);
            Vector3 delta = localEnd - localStart;

            float minX = box.MinX - radius;
            float minY = box.MinY - radius;
            float minZ = box.MinZ - radius;
            float maxX = box.MaxX + radius;
            float maxY = box.MaxY + radius;
            float maxZ = box.MaxZ + radius;

            float tMin = 0f;
            float tMax = 1f;
            if (!ClipSlab(localStart.x, delta.x, minX, maxX, ref tMin, ref tMax) ||
                !ClipSlab(localStart.y, delta.y, minY, maxY, ref tMin, ref tMax) ||
                !ClipSlab(localStart.z, delta.z, minZ, maxZ, ref tMin, ref tMax))
            {
                tHit = 0f;
                return false;
            }

            tHit = Mathf.Clamp01(tMin);
            return true;
        }

        private static bool ClipSlab(float start, float delta, float min, float max, ref float tMin, ref float tMax)
        {
            if (Mathf.Abs(delta) < 0.00001f)
                return start >= min && start <= max;

            float inv = 1f / delta;
            float t1 = (min - start) * inv;
            float t2 = (max - start) * inv;
            if (t1 > t2)
            {
                float tmp = t1;
                t1 = t2;
                t2 = tmp;
            }
            if (t1 > tMin) tMin = t1;
            if (t2 < tMax) tMax = t2;
            return tMin <= tMax && tMax >= 0f && tMin <= 1f;
        }

        private Vector3 WorldToObjectLocal(CollisionBox box, Vector3 world)
        {
            float dx = world.x - box.Position.x;
            float dy = world.y - box.Position.y;
            float dz = world.z - box.Position.z;
            float rad = box.HeadingDeg * Mathf.Deg2Rad;
            float cos = Mathf.Cos(rad);
            float sin = Mathf.Sin(rad);
            return new Vector3((cos * dx) + (sin * dy), (-sin * dx) + (cos * dy), dz);
        }

        private bool HybridSegmentIntersects(CollisionBox box, Vector3 start, Vector3 end, float radius, out float tHit)
        {
            tHit = 0f;
            if (box?.Hybrid == null)
                return false;

            Vector3 localStart = WorldToObjectLocal(box, start);
            Vector3 localEnd = WorldToObjectLocal(box, end);
            return box.Hybrid.TestSegment(localStart, localEnd, radius, out tHit);
        }

        private bool TryGetSurfaceHeight(CollisionBox box, float worldX, float worldY, out float height)
        {
            height = 0f;
            if (box?.Hybrid == null)
                return false;

            Vector3 local = WorldToObjectLocal(box, new Vector3(worldX, worldY, box.Position.z));
            int localXFixed8 = Mathf.RoundToInt(local.x * 256f);
            int localYFixed8 = Mathf.RoundToInt(local.y * 256f);
            if (!box.Hybrid.GetHeight(localXFixed8, localYFixed8, out int heightFixed8))
                return false;

            height = box.Position.z + heightFixed8 / 256f;
            return true;
        }

        private WorldCollisionHit CreateHit(CollisionBox box, Vector3 worldPoint, float distance)
        {
            return new WorldCollisionHit
            {
                Blocked = true,
                ZoneName = box.ZoneName,
                InstanceKey = box.InstanceKey,
                TileType = box.TileType,
                GridX = box.GridX,
                GridY = box.GridY,
                ObjectPath = box.ObjectPath,
                CollisionObject = box.CollisionObject,
                WorldX = worldPoint.x,
                WorldY = worldPoint.y,
                WorldZ = worldPoint.z,
                LocalX = worldPoint.x - box.TileOriginX,
                LocalY = worldPoint.y - box.TileOriginY,
                LocalZ = worldPoint.z,
                Distance = distance
            };
        }

        private static bool TryParseVector3(string value, out Vector3 result)
        {
            result = default;
            if (string.IsNullOrWhiteSpace(value))
                return false;
            string[] parts = value.Split(',');
            if (parts.Length < 2)
                return false;

            if (!TryParseFloat(parts[0], out float x) || !TryParseFloat(parts[1], out float y))
                return false;
            float z = 0f;
            if (parts.Length >= 3)
                TryParseFloat(parts[2], out z);
            result = new Vector3(x, y, z);
            return true;
        }

        private static bool TryParseFloat(string value, out float result)
        {
            return float.TryParse(value?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out result);
        }

        private void LogMissingDoc(string path, string kind)
        {
            string key = kind + ":" + path;
            if (_loggedMissingDocs.Add(key))
                Debug.LogError($"[WORLD-COLLISION] missing authored {kind} path='{path}' source=GCDatabase");
        }

        private void LogMissingBounds(string path)
        {
            if (_loggedMissingBounds.Add(path))
                Debug.LogError($"[WORLD-COLLISION] missing StaticObjectDesc bounds path='{path}'");
        }

        private static string DescribeBounds(StaticBounds bounds)
        {
            if (bounds == null)
                return "missing";
            return $"{bounds.CollisionObject} [{bounds.MinX:F1},{bounds.MinY:F1},{bounds.MinZ:F1}]-[{bounds.MaxX:F1},{bounds.MaxY:F1},{bounds.MaxZ:F1}]";
        }
    }
}
