using System.Collections.Generic;
using UnityEngine;

namespace Game
{
    /// <summary>
    /// Qiao（SWS_DemoPier）桥柱贴地：按柱子聚类拉伸 mesh 底部顶点到 Terrain。
    /// 桥面/栏杆顶点不动；只改柱身 Y。每实例 Clone 一份 mesh，不改共享 FBX。
    /// </summary>
    public static class EnvBridgePillarFit
    {
        /// <summary>柱脚相对地表再下沉的米数（埋进地形）。</summary>
        public const float BuryMeters = 0.2f;

        /// <summary>
        /// mesh 本地 Y 归一化上限：低于此分位视为柱身。
        /// 甲板约在 0.75+；柱顶约在 0.70，取 0.73 包住整根柱、避开甲板。
        /// </summary>
        public const float PillarYMaxNormalized = 0.73f;

        /// <summary>世界 XZ 聚类格子（米）。</summary>
        public const float ClusterCellMeters = 0.35f;

        const int MinClusterVerts = 8;
        const float MinPillarHeightLocal = 0.8f;
        const float ReachEpsilon = 0.05f;

        static readonly List<Vector3> Verts = new List<Vector3>(8192);
        static readonly List<int> ClusterIds = new List<int>(8192);
        static readonly Dictionary<long, int> CellToCluster = new Dictionary<long, int>(64);
        static readonly List<Cluster> Clusters = new List<Cluster>(32);

        struct Cluster
        {
            public float SumX, SumZ;
            public float MinY, MaxY;
            public int Count;
        }

        public static void Ensure(GameObject go, string category)
        {
            if (go == null || !EnvStreamingConfig.IsBridgeCategory(category))
                return;
            Ensure(go);
        }

        public static void Ensure(GameObject go)
        {
            if (go == null)
                return;

            MeshFilter[] filters = go.GetComponentsInChildren<MeshFilter>(true);
            for (int i = 0; i < filters.Length; i++)
            {
                MeshFilter mf = filters[i];
                if (mf == null || mf.sharedMesh == null)
                    continue;
                if (mf.GetComponent<EnvBridgePillarFitMeshOwner>() != null)
                    continue;

                TryFitFilter(mf);
            }
        }

        static bool TryFitFilter(MeshFilter mf)
        {
            Mesh source = mf.sharedMesh;
            if (source == null || !source.isReadable || source.vertexCount < MinClusterVerts)
                return false;

            Terrain terrain = ResolveTerrain();
            if (terrain == null || terrain.terrainData == null)
                return false;

            TerrainData td = terrain.terrainData;
            Vector3 origin = terrain.transform.position;
            Vector3 size = td.size;
            if (size.x <= 0.0001f || size.z <= 0.0001f)
                return false;

            source.GetVertices(Verts);
            int vertCount = Verts.Count;
            if (vertCount == 0)
                return false;

            Bounds b = source.bounds;
            float yCut = b.min.y + b.size.y * PillarYMaxNormalized;

            CellToCluster.Clear();
            Clusters.Clear();
            ClusterIds.Clear();
            if (ClusterIds.Capacity < vertCount)
                ClusterIds.Capacity = vertCount;

            Transform tr = mf.transform;
            bool anyPillar = false;

            for (int i = 0; i < vertCount; i++)
            {
                Vector3 lv = Verts[i];
                if (lv.y > yCut)
                {
                    ClusterIds.Add(-1);
                    continue;
                }

                Vector3 world = tr.TransformPoint(lv);
                int cx = Mathf.FloorToInt(world.x / ClusterCellMeters);
                int cz = Mathf.FloorToInt(world.z / ClusterCellMeters);
                long key = ((long)cx << 32) ^ (uint)cz;

                if (!CellToCluster.TryGetValue(key, out int ci))
                {
                    ci = Clusters.Count;
                    CellToCluster[key] = ci;
                    Clusters.Add(new Cluster
                    {
                        SumX = lv.x,
                        SumZ = lv.z,
                        MinY = lv.y,
                        MaxY = lv.y,
                        Count = 1
                    });
                }
                else
                {
                    Cluster c = Clusters[ci];
                    c.SumX += lv.x;
                    c.SumZ += lv.z;
                    if (lv.y < c.MinY) c.MinY = lv.y;
                    if (lv.y > c.MaxY) c.MaxY = lv.y;
                    c.Count++;
                    Clusters[ci] = c;
                }

                ClusterIds.Add(ci);
                anyPillar = true;
            }

            if (!anyPillar || Clusters.Count == 0)
                return false;

            bool changed = false;
            for (int ci = 0; ci < Clusters.Count; ci++)
            {
                Cluster c = Clusters[ci];
                if (c.Count < MinClusterVerts)
                    continue;
                float height = c.MaxY - c.MinY;
                if (height < MinPillarHeightLocal)
                    continue;

                float avgX = c.SumX / c.Count;
                float avgZ = c.SumZ / c.Count;
                Vector3 bottomLocal = new Vector3(avgX, c.MinY, avgZ);
                Vector3 attachLocal = new Vector3(avgX, c.MaxY, avgZ);
                Vector3 bottomWorld = tr.TransformPoint(bottomLocal);
                Vector3 attachWorld = tr.TransformPoint(attachLocal);

                float nx = (bottomWorld.x - origin.x) / size.x;
                float nz = (bottomWorld.z - origin.z) / size.z;
                if (nx < 0f || nx > 1f || nz < 0f || nz > 1f)
                    continue;

                float terrainY = origin.y + td.GetInterpolatedHeight(nx, nz);
                float targetBottomY = terrainY - BuryMeters;
                float gap = bottomWorld.y - targetBottomY;
                if (gap <= ReachEpsilon)
                    continue;

                Vector3 pillarAxis = attachWorld - bottomWorld;
                float axisLen = pillarAxis.magnitude;
                if (axisLen < 1e-4f)
                    continue;

                Vector3 up = pillarAxis / axisLen;
                Vector3 newBottomWorld = bottomWorld - up * gap;
                Vector3 newBottomLocal = tr.InverseTransformPoint(newBottomWorld);

                float attachY = c.MaxY;
                float oldBottomY = c.MinY;
                float denom = attachY - oldBottomY;
                if (Mathf.Abs(denom) < 1e-5f)
                    continue;

                float newBottomY = newBottomLocal.y;
                for (int i = 0; i < vertCount; i++)
                {
                    if (ClusterIds[i] != ci)
                        continue;

                    Vector3 v = Verts[i];
                    float alpha = (attachY - v.y) / denom;
                    if (alpha < 0f) alpha = 0f;
                    else if (alpha > 1f) alpha = 1f;
                    v.y = attachY + (newBottomY - attachY) * alpha;
                    Verts[i] = v;
                    changed = true;
                }
            }

            if (!changed)
                return false;

            Mesh fitted = Object.Instantiate(source);
            fitted.name = source.name + "_PillarFit";
            fitted.SetVertices(Verts);
            fitted.RecalculateBounds();
            mf.sharedMesh = fitted;

            EnvBridgePillarFitMeshOwner owner =
                mf.gameObject.GetComponent<EnvBridgePillarFitMeshOwner>();
            if (owner == null)
                owner = mf.gameObject.AddComponent<EnvBridgePillarFitMeshOwner>();
            owner.Set(fitted);
            return true;
        }

        static Terrain ResolveTerrain()
        {
            Terrain terrain = Terrain.activeTerrain;
            if (terrain != null && terrain.terrainData != null)
                return terrain;

            var all = Object.FindObjectsByType<Terrain>(FindObjectsInactive.Exclude);
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] != null && all[i].terrainData != null)
                    return all[i];
            }

            return null;
        }
    }
}
