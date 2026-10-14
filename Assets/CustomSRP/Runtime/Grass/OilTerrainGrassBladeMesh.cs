using System.Collections.Generic;
using UnityEngine;

namespace CustomSRP
{
    /// <summary>
    /// 12 株型：草卡片 + 小麦 + 花/三叶草（对齐 Boluo TerrainSplatGrass）。
    /// </summary>
    public static class OilTerrainGrassBladeMesh
    {
        public const int VariantCount = 12;
        public const int TopologyCount = VariantCount;
        public const int FirstFlower = 7;
        public const int DefaultSegments = 4;

        public enum Variant : byte
        {
            Billboard = 0,
            Patch = 1,
            Single = 2,
            Thick = 3,
            Thin = 4,
            Shrub = 5,
            Wheat = 6,
            Aster = 7,
            Chamomile = 8,
            Dandelions = 9,
            Speedwell = 10,
            Clover = 11,
        }

        // 花/蒲公英实例缩放=1；高度靠 mesh（比小麦略矮）。小麦 HeightScale=1.18。
        public static readonly float[] HeightScales =
        {
            1f, 0.96f, 1f, 0.90f, 1.06f, 0.72f, 1.18f, 1f, 1f, 1f, 1f, 0.62f,
        };

        public static readonly float[] WidthScales =
        {
            1f, 1f, 0.88f, 1.12f, 0.92f, 1f, 0.72f, 1f, 1f, 1f, 1f, 0.95f,
        };

        public static bool IsFlower(int variant) =>
            variant >= FirstFlower && variant <= (int)Variant.Speedwell;

        public static bool IsClover(int variant) => variant == (int)Variant.Clover;

        public static bool UsesAtlasColor(int variant) => variant >= FirstFlower;

        public static int PickVariant(Vector3 pos)
        {
            int clustered = TryPickClusteredAccent(pos);
            if (clustered >= 0)
                return clustered;

            float singleH = Hash2(pos.x, pos.z, 113f);
            if (singleH < 0.0038f) return (int)Variant.Wheat;
            if (singleH < 0.0052f) return (int)Variant.Aster;
            if (singleH < 0.0064f) return (int)Variant.Chamomile;
            if (singleH < 0.0075f) return (int)Variant.Dandelions;
            if (singleH < 0.018f) return (int)Variant.Clover;

            float h = Hash2(pos.x, pos.z, 1f);
            if (h < 0.34f) return (int)Variant.Patch;
            if (h < 0.54f) return (int)Variant.Thick;
            if (h < 0.72f) return (int)Variant.Thin;
            if (h < 0.85f) return (int)Variant.Single;
            if (h < 0.94f) return (int)Variant.Billboard;
            return (int)Variant.Shrub;
        }

        static int TryPickClusteredAccent(Vector3 pos)
        {
            const float cell = 3.4f;
            float cx = Mathf.Floor(pos.x / cell);
            float cz = Mathf.Floor(pos.z / cell);
            if (Hash2(cx, cz, 17f) >= 0.26f)
                return -1;

            float ox = 0.20f + 0.60f * Hash2(cx, cz, 41f);
            float oz = 0.20f + 0.60f * Hash2(cx, cz, 53f);
            float dx = pos.x - (cx + ox) * cell;
            float dz = pos.z - (cz + oz) * cell;
            float distSq = dx * dx + dz * dz;

            float sizeRoll = Hash2(cx, cz, 71f);
            float radius;
            float fill;
            if (sizeRoll < 0.28f)
            {
                radius = 0.42f;
                fill = 1f;
            }
            else if (sizeRoll < 0.78f)
            {
                radius = 0.70f + 0.35f * Hash2(cx, cz, 83f);
                fill = 0.82f;
            }
            else
            {
                radius = 1.15f + 0.45f * Hash2(cx, cz, 83f);
                fill = 0.70f;
            }

            if (distSq > radius * radius || Hash2(pos.x, pos.z, 101f) >= fill)
                return -1;

            float typeH = Hash2(cx, cz, 89f);
            if (typeH < 0.16f) return (int)Variant.Wheat;
            if (typeH < 0.36f) return (int)Variant.Aster;
            if (typeH < 0.52f) return (int)Variant.Chamomile;
            if (typeH < 0.66f) return (int)Variant.Dandelions;
            return (int)Variant.Clover;
        }

        public static Mesh[] BuildAll(int segments)
        {
            segments = Mathf.Clamp(segments, 1, 8);
            return new[]
            {
                BuildBillboard(segments),
                BuildPatch(segments),
                BuildSingle(segments),
                BuildThick(segments),
                BuildThin(segments),
                BuildShrub(segments),
                BuildWheat(segments),
                // 小麦 mesh 高≈1.28×1.18≈1.5；花茎≈1.05~1.12（略矮），花冠近水平单片。
                BuildFlowerPlant("FlowerAster", 0, 0.40f, 0.40f, 1.08f, segments),
                BuildFlowerPlant("FlowerChamomile", 1, 0.38f, 0.38f, 1.02f, segments),
                BuildFlowerPlant("FlowerDandelions", 2, 0.42f, 0.42f, 1.12f, segments),
                BuildFlowerPlant("FlowerSpeedwell", 3, 0.12f, 1.10f, 0f, segments, horizontalBloom: false),
                BuildClover(segments),
            };
        }

        static Mesh BuildBillboard(int segments)
        {
            var b = new Builder("OilGrassBillboard");
            b.AddCard(0f, 1.05f, 0.92f, Vector3.zero, segments);
            return b.ToMesh();
        }

        static Mesh BuildSingle(int segments)
        {
            var b = new Builder("OilGrassSingle");
            b.AddCard(0.12f, 0.72f, 0.82f, Vector3.zero, segments);
            b.AddCard(1.68f, 0.64f, 0.74f, new Vector3(0.04f, 0f, 0.02f), segments);
            return b.ToMesh();
        }

        static Mesh BuildThin(int segments)
        {
            var b = new Builder("OilGrassThin");
            b.AddCard(0.18f, 0.88f, 0.98f, new Vector3(0.03f, 0f, 0.02f), segments);
            b.AddCard(1.82f, 0.80f, 0.88f, new Vector3(-0.04f, 0f, -0.03f), segments);
            return b.ToMesh();
        }

        static Mesh BuildThick(int segments)
        {
            var b = new Builder("OilGrassThick");
            b.AddCard(0.08f, 1.18f, 0.78f, Vector3.zero, segments);
            b.AddCard(2.16f, 1.08f, 0.86f, new Vector3(0.05f, 0f, 0.04f), segments);
            b.AddCard(4.12f, 1.00f, 0.70f, new Vector3(-0.05f, 0f, 0.04f), segments);
            return b.ToMesh();
        }

        static Mesh BuildPatch(int segments)
        {
            var b = new Builder("OilGrassPatch");
            b.AddCard(0.15f, 1.12f, 0.88f, new Vector3(0.06f, 0f, 0.04f), segments);
            b.AddCard(2.20f, 1.04f, 0.80f, new Vector3(-0.07f, 0f, 0.05f), segments);
            b.AddCard(4.28f, 0.96f, 0.72f, new Vector3(0.02f, 0f, -0.07f), segments);
            return b.ToMesh();
        }

        static Mesh BuildShrub(int segments)
        {
            var b = new Builder("OilGrassShrub");
            for (int i = 0; i < 5; i++)
            {
                float yaw = i * 1.256f + 0.2f;
                float w = 0.72f + (i % 2) * 0.10f;
                float h = 0.48f + (i % 3) * 0.08f;
                var o = new Vector3(Mathf.Cos(yaw) * 0.10f, 0f, Mathf.Sin(yaw) * 0.10f);
                b.AddCard(yaw + 0.3f, w, h, o, segments);
            }

            return b.ToMesh();
        }

        static Mesh BuildWheat(int segments)
        {
            var b = new Builder("OilGrassWheat");
            b.AddCard(0.10f, 0.42f, 1.28f, new Vector3(0.03f, 0f, 0.01f), segments);
            b.AddCard(1.70f, 0.38f, 1.16f, new Vector3(-0.04f, 0f, 0.03f), segments);
            return b.ToMesh();
        }

        static readonly Vector4 StemUvRect = new Vector4(0.018f, 0.008f, 0.155f, 0.175f);
        static readonly Vector4 CloverLeafUv = new Vector4(0.435f, 0.058f, 0.080f, 0.056f);

        static Mesh BuildFlowerPlant(
            string name,
            int srcIndex,
            float bloomW,
            float bloomH,
            float stemH,
            int segments,
            bool horizontalBloom = true)
        {
            Vector4 bloom = OilTerrainGrassAtlas.FlowerSrcRects[srcIndex];
            var b = new Builder(name);
            if (stemH > 0.02f)
            {
                // 单根细茎，避免再叠成十字。
                b.AddCard(0.08f, 0.028f, stemH, Vector3.zero, StemUvRect, 2);
            }

            var head = new Vector3(0f, Mathf.Max(stemH * 0.92f, 0.02f), 0f);
            if (horizontalBloom)
            {
                // 近水平花盘：俯视像花，侧看也不再是草那种 90° 十字片。
                b.AddHorizontalCard(0.15f, bloomW, bloomH, head, bloom, tiltDeg: 18f);
            }
            else
            {
                // 穗状花（Speedwell）仍用单竖直片。
                b.AddCard(0.12f, bloomW, bloomH, head, bloom, Mathf.Max(2, segments - 1));
            }

            return b.ToMesh();
        }

        static Mesh BuildClover(int segments)
        {
            var b = new Builder("OilGrassClover");
            int seg = Mathf.Max(1, segments / 2);
            b.AddCard(0.08f, 0.03f, 0.055f, Vector3.zero, StemUvRect, 1);
            for (int i = 0; i < 3; i++)
            {
                float yaw = i * 2.094395f;
                var o = new Vector3(Mathf.Cos(yaw) * 0.045f, 0.04f, Mathf.Sin(yaw) * 0.045f);
                b.AddCard(yaw, 0.15f, 0.16f, o, CloverLeafUv, seg);
                b.AddCard(yaw + 1.2f, 0.13f, 0.14f, o, CloverLeafUv, seg);
            }

            return b.ToMesh();
        }

        static float Hash2(float x, float z, float salt) =>
            Frac(Mathf.Sin(x * 12.9898f + z * 78.233f + salt * 37.719f) * 43758.5453f);

        static float Frac(float v) => v - Mathf.Floor(v);

        struct Builder
        {
            public string Name;
            readonly List<Vector3> _verts;
            readonly List<Vector2> _uvs;
            readonly List<int> _tris;

            public Builder(string name)
            {
                Name = name;
                _verts = new List<Vector3>(32);
                _uvs = new List<Vector2>(32);
                _tris = new List<int>(48);
            }

            public void AddCard(float yaw, float width, float height, Vector3 offset, int segments = 4)
            {
                AddCard(yaw, width, height, offset, new Vector4(0f, 0f, 1f, 1f), segments);
            }

            public void AddCard(float yaw, float width, float height, Vector3 offset, Vector4 uvRect, int segments = 4)
            {
                segments = Mathf.Clamp(segments, 1, 8);
                float s = Mathf.Sin(yaw);
                float c = Mathf.Cos(yaw);
                var right = new Vector3(c, 0f, -s) * (width * 0.5f);
                int rows = segments + 1;
                int baseV = _verts.Count;
                for (int r = 0; r < rows; r++)
                {
                    float t = r / (float)segments;
                    var p = offset + new Vector3(0f, height * t, 0f);
                    _verts.Add(p - right);
                    _verts.Add(p + right);
                    _uvs.Add(new Vector2(uvRect.x, uvRect.y + t * uvRect.w));
                    _uvs.Add(new Vector2(uvRect.x + uvRect.z, uvRect.y + t * uvRect.w));
                }

                for (int r = 0; r < segments; r++)
                {
                    int i = baseV + r * 2;
                    _tris.Add(i);
                    _tris.Add(i + 2);
                    _tris.Add(i + 1);
                    _tris.Add(i + 1);
                    _tris.Add(i + 2);
                    _tris.Add(i + 3);
                    _tris.Add(i);
                    _tris.Add(i + 3);
                    _tris.Add(i + 2);
                    _tris.Add(i);
                    _tris.Add(i + 1);
                    _tris.Add(i + 3);
                }
            }

            /// <summary>近水平四边形花盘（双面），yaw 微旋 + 轻微抬头便于侧视。</summary>
            public void AddHorizontalCard(
                float yaw,
                float width,
                float depth,
                Vector3 offset,
                Vector4 uvRect,
                float tiltDeg = 16f)
            {
                float s = Mathf.Sin(yaw);
                float c = Mathf.Cos(yaw);
                float halfW = width * 0.5f;
                float halfD = depth * 0.5f;
                float tilt = tiltDeg * Mathf.Deg2Rad;
                float ty = Mathf.Sin(tilt);
                float tz = Mathf.Cos(tilt);

                // 本地：X 右、Z 前略抬头；再绕 Y 旋 yaw。
                var corners = new[]
                {
                    new Vector3(-halfW, 0f, -halfD),
                    new Vector3(halfW, 0f, -halfD),
                    new Vector3(-halfW, 0f, halfD),
                    new Vector3(halfW, 0f, halfD),
                };
                var uvs = new[]
                {
                    new Vector2(uvRect.x, uvRect.y),
                    new Vector2(uvRect.x + uvRect.z, uvRect.y),
                    new Vector2(uvRect.x, uvRect.y + uvRect.w),
                    new Vector2(uvRect.x + uvRect.z, uvRect.y + uvRect.w),
                };

                int baseV = _verts.Count;
                for (int i = 0; i < 4; i++)
                {
                    Vector3 p = corners[i];
                    // 绕 X 抬头少许，再绕 Y。
                    float y = p.y * tz - p.z * ty;
                    float z = p.y * ty + p.z * tz;
                    p.y = y;
                    p.z = z;
                    var world = new Vector3(p.x * c + p.z * s, p.y, -p.x * s + p.z * c) + offset;
                    _verts.Add(world);
                    _uvs.Add(uvs[i]);
                }

                // 双面
                _tris.Add(baseV);
                _tris.Add(baseV + 2);
                _tris.Add(baseV + 1);
                _tris.Add(baseV + 1);
                _tris.Add(baseV + 2);
                _tris.Add(baseV + 3);
                _tris.Add(baseV);
                _tris.Add(baseV + 1);
                _tris.Add(baseV + 2);
                _tris.Add(baseV + 1);
                _tris.Add(baseV + 3);
                _tris.Add(baseV + 2);
            }

            public Mesh ToMesh()
            {
                var mesh = new Mesh { name = Name };
                mesh.SetVertices(_verts);
                mesh.SetUVs(0, _uvs);
                mesh.SetTriangles(_tris, 0, true);
                mesh.RecalculateBounds();
                mesh.RecalculateNormals();
                return mesh;
            }
        }
    }
}
