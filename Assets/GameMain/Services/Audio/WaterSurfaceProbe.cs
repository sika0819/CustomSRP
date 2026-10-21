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
    public static class WaterSurfaceProbe
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

        static readonly List<Surface> Surfaces = new List<Surface>(8);
        static GameObject _sourceRoot;
        static bool _built;

        public static bool IsReady
        {
            get
            {
                EnsureBuilt();
                return Surfaces.Count > 0;
            }
        }

        public static void Clear()
        {
            Surfaces.Clear();
            _sourceRoot = null;
            _built = false;
        }

        public static void Rebuild()
        {
            var world = IslandWorldService.Instance;
            GameObject root = world != null ? world.InlandWaterInstance : null;

            Surfaces.Clear();
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

            var names = new System.Text.StringBuilder();
            for (int i = 0; i < Surfaces.Count; i++)
            {
                if (i > 0)
                    names.Append(',');
                names.Append(Surfaces[i].Name).Append('=').Append(Surfaces[i].Points.Length);
            }

            Log.Info($"[WaterProbe] rebuilt surfaces={Surfaces.Count} [{names}]");
        }

        static void EnsureBuilt()
        {
            var world = IslandWorldService.Instance;
            GameObject root = world != null ? world.InlandWaterInstance : null;
            if (_built && ReferenceEquals(root, _sourceRoot))
                return;
            Rebuild();
        }

        public static bool TryNearest(
            SurfaceKind kind, Vector3 listener, float maxDist,
            out Vector3 closest, out float dist)
        {
            if (!TryNearestAny(kind, listener, out closest, out dist))
                return false;
            return dist <= maxDist;
        }

        public static bool TryNearestAny(
            SurfaceKind kind, Vector3 listener,
            out Vector3 closest, out float dist)
        {
            closest = default;
            dist = float.MaxValue;
            EnsureBuilt();

            bool any = false;
            for (int i = 0; i < Surfaces.Count; i++)
            {
                var s = Surfaces[i];
                if (s == null || s.Kind != kind || s.Points == null || s.Points.Length == 0)
                    continue;

                if (DistanceToBounds(s.Bounds, listener) >= dist)
                    continue;

                float d = DistanceToSurface(s, listener, out Vector3 point);
                if (d < dist)
                {
                    dist = d;
                    closest = point;
                    any = true;
                }
            }

            return any;
        }

        public static bool TryNearestRiver(
            Vector3 listener, float maxDist,
            out Vector3 closest, out float dist)
        {
            return TryNearest(SurfaceKind.River, listener, maxDist, out closest, out dist);
        }

        public static void GetAllPoints(List<Vector3> dst)
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

        static float DistanceToBounds(Bounds b, Vector3 p)
        {
            float dx = Mathf.Max(b.min.x - p.x, 0f) + Mathf.Max(p.x - b.max.x, 0f);
            float dy = Mathf.Max(b.min.y - p.y, 0f) + Mathf.Max(p.y - b.max.y, 0f);
            float dz = Mathf.Max(b.min.z - p.z, 0f) + Mathf.Max(p.z - b.max.z, 0f);
            return Mathf.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        static float DistanceToSurface(Surface s, Vector3 listener, out Vector3 closest)
        {
            closest = s.Points[0];
            float bestSq = float.MaxValue;
            for (int i = 0; i < s.Points.Length; i++)
            {
                float dx = s.Points[i].x - listener.x;
                float dy = s.Points[i].y - listener.y;
                float dz = s.Points[i].z - listener.z;
                float sq = dx * dx + dy * dy + dz * dz;
                if (sq >= bestSq)
                    continue;
                bestSq = sq;
                closest = s.Points[i];
            }
            return Mathf.Sqrt(bestSq);
        }

        static Surface BuildSurface(Transform t)
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
        static bool TryClassify(string name, out SurfaceKind kind)
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
        static Vector3[] ReadVertices(Mesh mesh)
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
        static void AppendLocalBoundsSamples(
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

        static void AppendWorldBoundsSamples(List<Vector3> dst, Bounds world, int edgeSteps)
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
