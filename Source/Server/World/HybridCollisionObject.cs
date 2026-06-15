using System;
using System.Collections.Generic;
using System.IO;
using DungeonRunners.Engine;

namespace DungeonRunners.Core
{
    public sealed class HybridCollisionObject
    {
        public string Name { get; private set; }
        public int WalkCellSize { get; private set; }
        public int WalkOriginX { get; private set; }
        public int WalkOriginY { get; private set; }
        public int WalkGridX { get; private set; }
        public int WalkGridY { get; private set; }
        public int BlockCellSize { get; private set; }
        public int BlockOriginX { get; private set; }
        public int BlockOriginY { get; private set; }
        public int BlockOriginZ { get; private set; }
        public int BlockGridX { get; private set; }
        public int BlockGridY { get; private set; }
        public int BlockGridZ { get; private set; }
        public int NonEmptyBuckets { get; private set; }
        public int RangeCount { get; private set; }
        public int BodyOffset { get; private set; }

        private short[] _walkHeights = Array.Empty<short>();
        private List<VerticalRange>[] _blockBuckets = Array.Empty<List<VerticalRange>>();

        private struct VerticalRange
        {
            public short LowZ;
            public short HighZ;
        }

        public static bool TryLoadFromServerData(string collisionObjectName, out HybridCollisionObject cobj, out string source)
        {
            cobj = null;
            source = null;
            if (string.IsNullOrWhiteSpace(collisionObjectName))
                return false;

            string fileName = collisionObjectName.Trim();
            string[] roots = { DungeonRunners.Core.DataPaths.CobjDir };

            for (int i = 0; i < roots.Length; i++)
            {
                string path = Path.Combine(roots[i], fileName + ".cobj");
                if (!File.Exists(path))
                    path = Path.Combine(roots[i], fileName + ".cobj.bytes");
                if (!File.Exists(path))
                    continue;

                byte[] bytes = File.ReadAllBytes(path);
                if (TryParse(fileName, bytes, out cobj, out string error))
                {
                    source = path;
                    return true;
                }

                Debug.LogError($"[COBJ] parse-failed name='{fileName}' path='{path}' error='{error}' native=HybridCollisionObject::readObject");
                return false;
            }

            return false;
        }

        public static bool TryParse(string name, byte[] bytes, out HybridCollisionObject cobj, out string error)
        {
            cobj = null;
            error = null;
            if (bytes == null || bytes.Length < 0x1B + 40)
            {
                error = "too-small";
                return false;
            }

            try
            {
                if (!TryResolveReadObjectBodyOffset(bytes, out int bodyOffset, out error))
                    return false;

                var parsed = new HybridCollisionObject { Name = name };
                using var ms = new MemoryStream(bytes, false);
                ms.Position = bodyOffset;
                using var reader = new BinaryReader(ms);
                parsed.BodyOffset = bodyOffset;
                parsed.WalkCellSize = reader.ReadInt32();
                parsed.WalkOriginX = reader.ReadInt32();
                parsed.WalkOriginY = reader.ReadInt32();
                parsed.WalkGridX = reader.ReadInt32();
                parsed.WalkGridY = reader.ReadInt32();
                if (parsed.WalkGridX < 0 || parsed.WalkGridY < 0 || parsed.WalkGridX > 4096 || parsed.WalkGridY > 4096)
                {
                    error = "invalid-walk-grid";
                    return false;
                }

                int walkCount = parsed.WalkGridX * parsed.WalkGridY;
                parsed._walkHeights = new short[walkCount];
                for (int i = 0; i < walkCount; i++)
                    parsed._walkHeights[i] = reader.ReadInt16();

                parsed.BlockCellSize = reader.ReadInt32();
                parsed.BlockOriginX = reader.ReadInt32();
                parsed.BlockOriginY = reader.ReadInt32();
                parsed.BlockOriginZ = reader.ReadInt32();
                parsed.BlockGridX = reader.ReadInt32();
                parsed.BlockGridY = reader.ReadInt32();
                parsed.BlockGridZ = reader.ReadInt32();
                if (parsed.BlockGridX < 0 || parsed.BlockGridY < 0 || parsed.BlockGridZ < 0 ||
                    parsed.BlockGridX > 4096 || parsed.BlockGridY > 4096 || parsed.BlockGridZ > 4096)
                {
                    error = "invalid-block-grid";
                    return false;
                }

                int bucketCount = parsed.BlockGridX * parsed.BlockGridY;
                parsed._blockBuckets = new List<VerticalRange>[bucketCount];
                for (int i = 0; i < bucketCount; i++)
                {
                    ushort count = reader.ReadUInt16();
                    if (count == 0)
                        continue;

                    var ranges = new List<VerticalRange>(count);
                    for (int j = 0; j < count; j++)
                    {
                        var range = new VerticalRange
                        {
                            LowZ = reader.ReadInt16(),
                            HighZ = reader.ReadInt16()
                        };
                        ranges.Add(range);
                        parsed.RangeCount++;
                    }
                    parsed._blockBuckets[i] = ranges;
                    parsed.NonEmptyBuckets++;
                }

                if (reader.BaseStream.Position != reader.BaseStream.Length)
                {
                    error = $"trailing-bytes pos={reader.BaseStream.Position} len={reader.BaseStream.Length}";
                    return false;
                }

                cobj = parsed;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static bool TryResolveReadObjectBodyOffset(byte[] bytes, out int bodyOffset, out string error)
        {
            bodyOffset = 0;
            error = null;
            const int wrapperClassStart = 1;
            const string expectedClass = "HybridCollisionObject";
            if (bytes.Length < 0x1B + 4)
            {
                error = "too-small-wrapper";
                return false;
            }

            int zero = Array.IndexOf(bytes, (byte)0, wrapperClassStart);
            if (zero < 0)
            {
                error = "missing-wrapper-terminator";
                return false;
            }

            string className = System.Text.Encoding.ASCII.GetString(bytes, wrapperClassStart, zero - wrapperClassStart);
            if (!string.Equals(className, expectedClass, StringComparison.Ordinal))
            {
                error = $"unexpected-wrapper-class '{className}'";
                return false;
            }

            bodyOffset = zero + 5;
            if (bodyOffset != 0x1B)
            {
                error = $"unexpected-body-offset 0x{bodyOffset:X}";
                return false;
            }

            return true;
        }

        public bool TestSegment(Vector3 startLocal, Vector3 endLocal, float radius, out float tHit)
        {
            tHit = 0f;
            if (_blockBuckets.Length == 0 || BlockGridX <= 0 || BlockGridY <= 0 || BlockCellSize <= 0)
                return false;

            int startX = ToFixed8(startLocal.x);
            int startY = ToFixed8(startLocal.y);
            int startZ = ToFixed8(startLocal.z);
            int endX = ToFixed8(endLocal.x);
            int endY = ToFixed8(endLocal.y);
            int endZ = ToFixed8(endLocal.z);
            int radiusFixed = Math.Max(0, ToFixed8(radius));

            if (TestBoundingBoxFixed(startX, startY, startZ, radiusFixed))
            {
                tHit = 0f;
                return true;
            }

            int dx = endX - startX;
            int dy = endY - startY;
            int dz = endZ - startZ;
            int maxAbs = Math.Max(Math.Abs(dx), Math.Max(Math.Abs(dy), Math.Abs(dz)));
            int n = (maxAbs >> 8) / 3;
            if (n <= 0)
            {
                if (TestBoundingBoxFixed(endX, endY, endZ, radiusFixed))
                {
                    tHit = 1f;
                    return true;
                }
                return false;
            }

            int denom = (n + 1) << 8;
            int stepX = (int)(((long)dx << 8) / denom);
            int stepY = (int)(((long)dy << 8) / denom);
            int stepZ = (int)(((long)dz << 8) / denom);
            for (int j = 1; j <= n; j++)
            {
                int sampleX = startX + (stepX * j);
                int sampleY = startY + (stepY * j);
                int sampleZ = startZ + (stepZ * j);
                if (!TestBoundingBoxFixed(sampleX, sampleY, sampleZ, radiusFixed))
                    continue;
                tHit = j / (float)(n + 1);
                return true;
            }
            return false;
        }

        private static int ToFixed8(float value)
        {
            return Mathf.RoundToInt(value * 256f);
        }

        private bool TestBoundingBoxFixed(int centerX, int centerY, int centerZ, int radiusFixed)
        {
            float minX = (centerX - radiusFixed) / 256f;
            float minY = (centerY - radiusFixed) / 256f;
            float minZ = (centerZ - radiusFixed) / 256f;
            float maxX = (centerX + radiusFixed) / 256f;
            float maxY = (centerY + radiusFixed) / 256f;
            float maxZ = (centerZ + radiusFixed) / 256f;
            return TestBoundingBox(minX, minY, minZ, maxX, maxY, maxZ);
        }

        public bool TestBoundingBox(float minX, float minY, float minZ, float maxX, float maxY, float maxZ)
        {
            if (_blockBuckets.Length == 0 || BlockCellSize <= 0)
                return false;

            int gx0 = Mathf.FloorToInt((minX - BlockOriginX) / BlockCellSize);
            int gy0 = Mathf.FloorToInt((minY - BlockOriginY) / BlockCellSize);
            int gx1 = Mathf.FloorToInt((maxX - BlockOriginX) / BlockCellSize);
            int gy1 = Mathf.FloorToInt((maxY - BlockOriginY) / BlockCellSize);
            gx0 = Mathf.Clamp(gx0, 0, BlockGridX - 1);
            gy0 = Mathf.Clamp(gy0, 0, BlockGridY - 1);
            gx1 = Mathf.Clamp(gx1, 0, BlockGridX - 1);
            gy1 = Mathf.Clamp(gy1, 0, BlockGridY - 1);
            if (gx1 < gx0 || gy1 < gy0)
                return false;

            for (int gy = gy0; gy <= gy1; gy++)
            {
                for (int gx = gx0; gx <= gx1; gx++)
                {
                    var ranges = _blockBuckets[gy * BlockGridX + gx];
                    if (ranges == null)
                        continue;
                    for (int i = 0; i < ranges.Count; i++)
                    {
                        var range = ranges[i];
                        float low = BlockOriginZ + range.LowZ;
                        float high = BlockOriginZ + range.HighZ;
                        if (maxZ >= low && minZ <= high)
                            return true;
                    }
                }
            }

            return false;
        }

        public bool GetHeight(int xFixed8, int yFixed8, out int heightFixed8)
        {
            heightFixed8 = 0;
            int gridW = WalkGridX;
            int gridH = WalkGridY;
            int cellSize = WalkCellSize;
            if (_walkHeights.Length == 0 || gridW < 2 || gridH < 2 || cellSize <= 0)
                return false;

            int x = xFixed8 - WalkOriginX * 0x100;
            int y = yFixed8 - WalkOriginY * 0x100;
            if (x < 0) x += 0x100;
            if (y < 0) y += 0x100;
            if (cellSize * (gridW - 1) * 0x100 <= x) x -= 0x100;
            if ((gridH - 1) * cellSize * 0x100 <= y) y -= 0x100;
            if ((x >> 8) < 0 || (y >> 8) < 0)
                return false;

            int gx = (x >> 8) / cellSize;
            int gy = (y >> 8) / cellSize;
            if (gx >= gridW - 1 || gy >= gridH - 1)
                return false;

            int idx = gridW * gy + gx;
            if (idx < 0 || idx + gridW + 1 >= _walkHeights.Length)
                return false;

            int s1 = _walkHeights[idx];
            int s2 = _walkHeights[idx + 1];
            int s3 = _walkHeights[idx + gridW];
            int s4 = _walkHeights[idx + gridW + 1];
            const int Sentinel = -0x7fff;
            if (s1 == Sentinel || s2 == Sentinel || s3 == Sentinel || s4 == Sentinel)
                return false;
            if (Math.Abs(s1 - s2) >= 0x33 || Math.Abs(s1 - s3) >= 0x33 || Math.Abs(s1 - s4) >= 0x33)
                return false;

            int cell8 = cellSize * 0x100;
            int fracX = ((x - ((gx * 0x100 * cell8) >> 8)) * 0x100) / cell8;
            int fracY = ((y - ((gy * 0x100 * cell8) >> 8)) * 0x100) / cell8;

            int topRow = ((s3 * 0x100 * (0x100 - fracX)) >> 8) + ((s4 * 0x100 * fracX) >> 8);
            int botRow = ((s1 * 0x100 * (0x100 - fracX)) >> 8) + ((s2 * 0x100 * fracX) >> 8);
            heightFixed8 = ((((topRow * fracY) >> 8) + (((0x100 - fracY) * botRow) >> 8)) * 0x100) / 0xa00;
            return true;
        }
    }
}
