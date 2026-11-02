// WaterSurfaceProbe.cs
//
// 运行期水体贴近查询（无编辑器烘焙）。
// 直接读场景 IslandInlandWater 下河 / 瀑物体：可读网格采顶点，否则用 MeshData；
// 再不行就用 Renderer 包围盒采样。靠近水边距离≈真实距离。

using System.Collections.Generic;
using UnityEngine;
using UnityGameFramework.Runtime;

namespace Game
{
    /// <summary>场景内水体（河 / 瀑布）的运行时最近点查询。</summary>
    public sealed class WaterSurfaceProbe
    {
        public enum SurfaceKind
        {
            River,
            Waterfall,
        }

        sealed class Surface
        {
            public string Name;
            public SurfaceKind Kind;
            public Vector3[] Points;
            public Bounds Bounds;
        }

        const int MaxSamplesPerSurface = 128;

        /// <summary>
        /// 离散采样 Voronoi 边界 sticky 偏置（米）。对齐海岸
        /// <see cref="AmbienceZoneCatalog.OceanShoreStickBiasMeters"/>。
        /// </summary>
        public const float ShellStickBiasMeters = 6f;

        /// <summary>
        /// 邻点线段投影搜索半径。河 bounds 采样沿长轴可疏到 ~20m，
        /// 比海岸 8m 网格更宽，否则沿河 shell 仍会在样点之间阶跃。
        /// </summary>
        public const float ShellNeighborSegmentMeters = 24f;

        readonly List<Surface> Surfaces = new List<Surface>(8);
        Vector3[] _flatRiver = System.Array.Empty<Vector3>();
        Vector3[] _flatFall = System.Array.Empty<Vector3>();
        GameObject _sourceRoot;
        bool _built;

        public bool IsReady
        {
            get
            {
                EnsureBuilt();
                return Surfaces.Count > 0;
            }
        }

        public void Clear()
        {
            Surfaces.Clear();
            _flatRiver = System.Array.Empty<Vector3>();
            _flatFall = System.Array.Empty<Vector3>();
            _sourceRoot = null;
            _built = false;
        }

        public void Rebuild()
        {
            var world = IslandWorldService.Instance;
            GameObject root = world != null ? world.InlandWaterInstance : null;

            Surfaces.Clear();
            _flatRiver = System.Array.Empty<Vector3>();
            _flatFall = System.Array.Empty<Vector3>();
            _sourceRoot = root;
            _built = true;

            if (root == null)
                return;

            var children = root.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < children.Length; i++)
            {
                var t = children[i];
                if (t == null || t == root.transform)
                    continue;
                if (t.parent != root.transform)
                    continue;

                var surface = BuildSurface(t);
                if (surface != null)
                    Surfaces.Add(surface);
            }

            RebuildFlatCaches();

            var names = new System.Text.StringBuilder();
            for (int i = 0; i < Surfaces.Count; i++)
            {
                if (i > 0)
                    names.Append(',');
                names.Append(Surfaces[i].Name).Append('=').Append(Surfaces[i].Points.Length);
            }

            Log.Info(
                $"[WaterProbe] rebuilt surfaces={Surfaces.Count} riverPts={_flatRiver.Length} fallPts={_flatFall.Length} [{names}]");
        }

        void RebuildFlatCaches()
        {
            int riverN = 0;
            int fallN = 0;
            for (int i = 0; i < Surfaces.Count; i++)
            {
                var s = Surfaces[i];
                if (s?.Points == null)
                    continue;
                if (s.Kind == SurfaceKind.River)
                    riverN += s.Points.Length;
                else if (s.Kind == SurfaceKind.Waterfall)
                    fallN += s.Points.Length;
            }

            _flatRiver = riverN > 0 ? new Vector3[riverN] : System.Array.Empty<Vector3>();
            _flatFall = fallN > 0 ? new Vector3[fallN] : System.Array.Empty<Vector3>();
            int ri = 0;
            int fi = 0;
            for (int i = 0; i < Surfaces.Count; i++)
            {
                var s = Surfaces[i];
                if (s?.Points == null)
                    continue;
                if (s.Kind == SurfaceKind.River)
                {
                    for (int j = 0; j < s.Points.Length; j++)
                        _flatRiver[ri++] = s.Points[j];
                }
                else if (s.Kind == SurfaceKind.Waterfall)
                {
                    for (int j = 0; j < s.Points.Length; j++)
                        _flatFall[fi++] = s.Points[j];
                }
            }
        }

        void EnsureBuilt()
        {
            var world = IslandWorldService.Instance;
            GameObject root = world != null ? world.InlandWaterInstance : null;
            if (_built && ReferenceEquals(root, _sourceRoot))
                return;
            Rebuild();
        }

        public bool TryNearest(
            SurfaceKind kind, Vector3 listener, float maxDist,
            out Vector3 closest, out float dist)
        {
            if (!TryNearestAny(kind, listener, out closest, out dist))
                return false;
            return dist <= maxDist;
        }

        public bool TryNearestAny(
            SurfaceKind kind, Vector3 listener,
            out Vector3 closest, out float dist)
        {
            int sticky = -1;
            return TryNearestShell(kind, listener, ref sticky, out closest, out dist);
        }

        /// <summary>
        /// 水平 shell：sticky + 邻点线段投影。沿河走时距离不再在离散样点 Voronoi
        /// 边界阶跃（否则 Point/增益曲线会忽大忽小）。
        /// <paramref name="stickyIndex"/> 传入上次命中的扁平下标（无则 -1），成功后写回。
        /// </summary>
        public bool TryNearestShell(
            SurfaceKind kind, Vector3 listener, ref int stickyIndex,
            out Vector3 closest, out float dist)
        {
            EnsureBuilt();
            Vector3[] flat = kind == SurfaceKind.River ? _flatRiver : _flatFall;
            return ResolveShell(flat, listener, ref stickyIndex, out closest, out dist);
        }

        /// <summary>
        /// 对扁平样点做 sticky + XZ 线段投影。供运行时与单测共用。
        /// </summary>
        public static bool ResolveShell(
            Vector3[] flat, Vector3 listener, ref int stickyIndex,
            out Vector3 closest, out float dist,
            float stickBiasMeters = ShellStickBiasMeters,
            float neighborMeters = ShellNeighborSegmentMeters)
        {
            closest = default;
            dist = float.MaxValue;
            if (flat == null || flat.Length == 0)
            {
                stickyIndex = -1;
                return false;
            }

            float bestSq = float.PositiveInfinity;
            int best = -1;
            for (int i = 0; i < flat.Length; i++)
            {
                float sq = HorizontalSq(listener, flat[i]);
                if (sq >= bestSq)
                    continue;
                bestSq = sq;
                best = i;
            }

            if (best < 0)
            {
                stickyIndex = -1;
                return false;
            }

            if ((uint)stickyIndex < (uint)flat.Length)
            {
                float stickySq = HorizontalSq(listener, flat[stickyIndex]);
                float bias = Mathf.Max(0f, stickBiasMeters);
                if (stickySq <= bestSq + bias * bias)
                {
                    best = stickyIndex;
                    bestSq = stickySq;
                }
            }

            stickyIndex = best;
            Vector3 bestPt = flat[best];
            float bestDist = Mathf.Sqrt(bestSq);
            Vector3 bestShore = bestPt;

            float neighborMax = Mathf.Max(0f, neighborMeters);
            float neighborMaxSq = neighborMax * neighborMax;
            for (int i = 0; i < flat.Length; i++)
            {
                if (i == best)
                    continue;
                if (HorizontalSq(bestPt, flat[i]) > neighborMaxSq)
                    continue;
                if (!TryProjectToSegmentXZ(listener, bestPt, flat[i], out Vector3 proj, out float d))
                    continue;
                if (d >= bestDist)
                    continue;
                bestDist = d;
                bestShore = proj;
            }

            closest = bestShore;
            dist = bestDist;
            return true;
        }

        static float HorizontalSq(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x;
            float dz = a.z - b.z;
            return dx * dx + dz * dz;
        }

        static bool TryProjectToSegmentXZ(
            Vector3 listener, Vector3 a, Vector3 b, out Vector3 proj, out float dist)
        {
            float abx = b.x - a.x;
            float abz = b.z - a.z;
            float abLenSq = abx * abx + abz * abz;
            float t = 0f;
            if (abLenSq > 1e-6f)
            {
                float apx = listener.x - a.x;
                float apz = listener.z - a.z;
                t = Mathf.Clamp01((apx * abx + apz * abz) / abLenSq);
            }

            // Y 沿线段插值，发射器高度贴水面而不是听者脚底。
            proj = new Vector3(
                a.x + abx * t,
                a.y + (b.y - a.y) * t,
                a.z + abz * t);
            float dx = listener.x - proj.x;
            float dz = listener.z - proj.z;
            dist = Mathf.Sqrt(dx * dx + dz * dz);
            return true;
        }

        public bool TryNearestRiver(
            Vector3 listener, float maxDist,
            out Vector3 closest, out float dist)
        {
            return TryNearest(SurfaceKind.River, listener, maxDist, out closest, out dist);
        }

        public void GetAllPoints(List<Vector3> dst)
        {
            if (dst == null)
                return;
            EnsureBuilt();
            for (int i = 0; i < Surfaces.Count; i++)
            {
                var s = Surfaces[i];
                if (s == null || s.Points == null)
                    continue;
                for (int j = 0; j < s.Points.Length; j++)
                    dst.Add(s.Points[j]);
            }
        }

        Surface BuildSurface(Transform t)
        {
            if (!TryClassify(t.name, out SurfaceKind kind))
                return null;

            var points = new List<Vector3>(MaxSamplesPerSurface);

            var filters = t.GetComponentsInChildren<MeshFilter>(true);
            for (int i = 0; i < filters.Length; i++)
            {
                var mf = filters[i];
                if (mf == null || mf.sharedMesh == null)
                    continue;

                Matrix4x4 m = mf.transform.localToWorldMatrix;
                Vector3[] verts = ReadVertices(mf.sharedMesh);
                if (verts != null && verts.Length > 0)
                {
                    int stride = Mathf.Max(1, Mathf.CeilToInt(verts.Length / (float)MaxSamplesPerSurface));
                    for (int v = 0; v < verts.Length; v += stride)
                        points.Add(m.MultiplyPoint3x4(verts[v]));
                }
                else
                {
                    // 不可读网格：沿局部包围盒采一圈点，覆盖整条河带。
                    AppendLocalBoundsSamples(points, mf.sharedMesh.bounds, m, MaxSamplesPerSurface);
                }
            }

            var renderers = t.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                var r = renderers[i];
                if (r == null)
                    continue;
                if (r is MeshRenderer)
                {
                    // 已从 MeshFilter 采过；再补世界包围盒中心，防 MeshFilter 缺失。
                    if (filters.Length == 0)
                        AppendWorldBoundsSamples(points, r.bounds, 8);
                    continue;
                }

                // 粒子 / 雾：飞溅中心。
                points.Add(r.bounds.center);
            }

            if (points.Count == 0)
                points.Add(t.position);

            var arr = points.ToArray();
            var bounds = new Bounds(arr[0], Vector3.zero);
            for (int i = 1; i < arr.Length; i++)
                bounds.Encapsulate(arr[i]);
            bounds.Expand(2f);

            return new Surface
            {
                Name = t.name,
                Kind = kind,
                Points = arr,
                Bounds = bounds,
            };
        }

        /// <summary>Waterfall* / River_04 → 瀑；其余 River_* → 河。</summary>
        bool TryClassify(string name, out SurfaceKind kind)
        {
            kind = SurfaceKind.River;
            if (string.IsNullOrEmpty(name))
                return false;

            if (AmbienceZoneCatalog.IsWaterfallPrefab(name))
            {
                kind = SurfaceKind.Waterfall;
                return true;
            }

            if (name.StartsWith("River_", System.StringComparison.Ordinal))
            {
                kind = SurfaceKind.River;
                return true;
            }

            return false;
        }

        /// <summary>
        /// 仅可读网格返回顶点；否则 null，由 <see cref="AppendLocalBoundsSamples"/> 兜底。
        /// （避免引入 Unity.Collections / MeshData API 兼容坑。）
        /// </summary>
        Vector3[] ReadVertices(Mesh mesh)
        {
            if (mesh == null || !mesh.isReadable)
                return null;

            try
            {
                return mesh.vertices;
            }
            catch (System.Exception e)
            {
                Log.Warning($"[WaterProbe] mesh.vertices '{mesh.name}': {e.Message}");
                return null;
            }
        }

        /// <summary>局部包围盒上采点（中心 + 角 + 边中点），覆盖长条河带。</summary>
        void AppendLocalBoundsSamples(
            List<Vector3> dst, Bounds local, Matrix4x4 localToWorld, int budget)
        {
            Vector3 c = local.center;
            Vector3 e = local.extents;
            dst.Add(localToWorld.MultiplyPoint3x4(c));

            // 8 corners
            for (int xi = -1; xi <= 1; xi += 2)
            for (int yi = -1; yi <= 1; yi += 2)
            for (int zi = -1; zi <= 1; zi += 2)
                dst.Add(localToWorld.MultiplyPoint3x4(c + new Vector3(e.x * xi, e.y * yi, e.z * zi)));

            // 沿最长水平轴再插值，保证贴着河岸时最近点不离谱。
            float hx = e.x * 2f;
            float hz = e.z * 2f;
            int steps = Mathf.Clamp(budget / 4, 4, 24);
            if (hx >= hz)
            {
                for (int i = 0; i <= steps; i++)
                {
                    float t = i / (float)steps;
                    float x = Mathf.Lerp(-e.x, e.x, t);
                    dst.Add(localToWorld.MultiplyPoint3x4(c + new Vector3(x, 0f, 0f)));
                    dst.Add(localToWorld.MultiplyPoint3x4(c + new Vector3(x, 0f, e.z)));
                    dst.Add(localToWorld.MultiplyPoint3x4(c + new Vector3(x, 0f, -e.z)));
                }
            }
            else
            {
                for (int i = 0; i <= steps; i++)
                {
                    float t = i / (float)steps;
                    float z = Mathf.Lerp(-e.z, e.z, t);
                    dst.Add(localToWorld.MultiplyPoint3x4(c + new Vector3(0f, 0f, z)));
                    dst.Add(localToWorld.MultiplyPoint3x4(c + new Vector3(e.x, 0f, z)));
                    dst.Add(localToWorld.MultiplyPoint3x4(c + new Vector3(-e.x, 0f, z)));
                }
            }
        }

        void AppendWorldBoundsSamples(List<Vector3> dst, Bounds world, int edgeSteps)
        {
            Vector3 c = world.center;
            Vector3 e = world.extents;
            dst.Add(c);
            for (int xi = -1; xi <= 1; xi += 2)
            for (int yi = -1; yi <= 1; yi += 2)
            for (int zi = -1; zi <= 1; zi += 2)
                dst.Add(c + new Vector3(e.x * xi, e.y * yi, e.z * zi));

            int n = Mathf.Max(2, edgeSteps);
            for (int i = 0; i <= n; i++)
            {
                float t = i / (float)n;
                dst.Add(new Vector3(Mathf.Lerp(world.min.x, world.max.x, t), c.y, world.min.z));
                dst.Add(new Vector3(Mathf.Lerp(world.min.x, world.max.x, t), c.y, world.max.z));
                dst.Add(new Vector3(world.min.x, c.y, Mathf.Lerp(world.min.z, world.max.z, t)));
                dst.Add(new Vector3(world.max.x, c.y, Mathf.Lerp(world.min.z, world.max.z, t)));
            }
        }
    }
}
