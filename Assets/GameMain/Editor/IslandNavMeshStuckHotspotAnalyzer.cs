#if UNITY_EDITOR
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.AI;

// scan-bump 1793026218.573193
namespace Game.Editor
{
    /// <summary>
    /// 扫描烘焙 IslandNavMesh：找相对运行时 BodyRadius(=1.1) 过窄的蓝面、碎岛、边界钳位易抖点。
    /// 菜单 / batchmode：Game.Editor.IslandNavMeshStuckHotspotAnalyzer.AnalyzeBatch
    /// 也可丢触发文件 Assets/GameMain/Editor/.run-nav-stuck-analyze 让已开 Editor 自动跑。
    /// </summary>
    public static class IslandNavMeshStuckHotspotAnalyzer
    {
        const string NavMeshAssetPath = "Assets/AssetRaw/Island/IslandNavMesh.asset";
        static readonly string[] NavMeshFallbacks =
        {
            "Assets/GameMain/Scenes/IslandNavBake/NavMesh-IslandNavBakeRoot 1.asset",
            "Assets/GameMain/Scenes/IslandNavBake/NavMesh-IslandNavBakeRoot.asset",
        };
        const string ReportPath = "Assets/GameMain/Editor/IslandNavMeshStuckHotspots.txt";
        const string TriggerPath = "Assets/GameMain/Editor/.run-nav-stuck-analyze";

        // 全岛网格扫描会卡死 Editor 主线程；自动触发改走 IslandNavBakeSceneVerify（只测热点）。
        [InitializeOnLoadMethod]
        static void WatchTrigger()
        {
            EditorApplication.update -= PollTrigger;
            EditorApplication.update += PollTrigger;
        }

        static double _nextPoll;

        static void PollTrigger()
        {
            if (EditorApplication.timeSinceStartup < _nextPoll)
                return;
            _nextPoll = EditorApplication.timeSinceStartup + 1.5;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                return;
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                return;
            if (!File.Exists(TriggerPath))
                return;

            try { File.Delete(TriggerPath); }
            catch { return; }

            Debug.LogWarning(
                "[IslandNavStuck] full-grid analyze disabled (freezes Editor). " +
                "Use GameMain/Boluo/Verify IslandNavBake Stuck Hotspots instead.");
            try
            {
                IslandNavBakeSceneVerify.Verify(out string summary);
                Debug.Log("[IslandNavStuck] redirected verify:\n" + summary);
            }
            catch (System.Exception ex)
            {
                Debug.LogError("[IslandNavStuck] verify threw: " + ex);
            }
        }

        /// <summary>与 IslandJoystickCamera.BodyRadius 对齐。</summary>
        const float BodyRadius = 1.1f;
        /// <summary>胶囊左右各要这么宽的净空；窄于此易卡死/抖。</summary>
        static float MinSafeWidth => BodyRadius * 2f;
        /// <summary>Bake agentRadius=0.5 时蓝面可能只有 ~1m 宽，但仍对 1.1 胶囊过窄。</summary>
        const float SampleStep = 2.5f;
        const float RayLen = 6f;
        const int DirCount = 16;
        const int MaxHotspots = 80;

        [MenuItem("GameMain/Boluo/Analyze Island NavMesh Stuck Hotspots")]
        public static void AnalyzeMenu()
        {
            if (!Analyze(out string summary))
            {
                EditorUtility.DisplayDialog("NavMesh Stuck Hotspots", summary, "OK");
                return;
            }

            EditorUtility.DisplayDialog("NavMesh Stuck Hotspots", summary, "OK");
            EditorUtility.OpenWithDefaultApp(ReportPath);
        }

        /// <summary>Unity -batchmode -executeMethod Game.Editor.IslandNavMeshStuckHotspotAnalyzer.AnalyzeBatch</summary>
        public static void AnalyzeBatch()
        {
            Analyze(out string summary);
            Debug.Log("[IslandNavStuck] " + summary.Replace('\n', ' '));
        }

        public static bool Analyze(out string summary)
        {
            var data = AssetDatabase.LoadAssetAtPath<NavMeshData>(NavMeshAssetPath);
            string usedPath = NavMeshAssetPath;
            if (data == null)
            {
                foreach (string fb in NavMeshFallbacks)
                {
                    data = AssetDatabase.LoadAssetAtPath<NavMeshData>(fb);
                    if (data == null)
                        continue;
                    usedPath = fb;
                    Debug.LogWarning("[IslandNavStuck] primary missing; using " + fb);
                    break;
                }
            }
            if (data == null)
            {
                summary = $"找不到 {NavMeshAssetPath}";
                return false;
            }
            Debug.Log("[IslandNavStuck] analyzing " + usedPath);

            NavMeshDataInstance instance = NavMesh.AddNavMeshData(data);
            try
            {
                NavMeshTriangulation tri = NavMesh.CalculateTriangulation();
                if (tri.vertices == null || tri.vertices.Length == 0)
                {
                    summary = "NavMesh triangulation 为空。";
                    return false;
                }

                Bounds bounds = ComputeBounds(tri.vertices);
                var narrow = new List<(Vector3 pos, float width, string kind)>(256);
                var islands = new List<(Vector3 pos, float area)>(64);

                // 禁止按 bounds 网格扫：海面 bounds 极大时会卡死 Editor 主线程。
                CollectNarrowFromTriangles(tri, narrow);
                CollectTinyIslands(tri, islands);

                narrow.Sort((a, b) => a.width.CompareTo(b.width));
                if (narrow.Count > MaxHotspots)
                    narrow.RemoveRange(MaxHotspots, narrow.Count - MaxHotspots);

                var sb = new StringBuilder(16 * 1024);
                sb.AppendLine("Island NavMesh stuck / jitter hotspot report");
                sb.AppendLine($"asset={NavMeshAssetPath}");
                sb.AppendLine($"bakeAgentRadius=0.5 (ProjectSettings)  runtimeBodyRadius={BodyRadius}  minSafeWidth={MinSafeWidth:F2}");
                sb.AppendLine($"bounds=({bounds.min.x:F1},{bounds.min.z:F1})-({bounds.max.x:F1},{bounds.max.z:F1})  verts={tri.vertices.Length}  indices={tri.indices.Length}");
                sb.AppendLine();
                sb.AppendLine("## Root cause (code + bake)");
                sb.AppendLine("- Player uses CharacterController + NavMesh.SamplePosition XZ clamp (no NavMeshAgent).");
                sb.AppendLine("- Bake agentRadius 0.5 << BodyRadius 1.1 → corridors that look blue can be too tight for the capsule.");
                sb.AppendLine("- Clamp assigns transform.position while CC stays enabled → PhysX fight = severe jitter at edges/gaps.");
                sb.AppendLine("- SamplePosition fail within maxDist → full horizontal rollback (binary stick).");
                sb.AppendLine();
                sb.AppendLine("## Narrow walkable width (local NavMesh.Raycast clearance < minSafeWidth)");
                sb.AppendLine("# kind  width  x  y  z   ← Scene view: Edit/Frame Selected after creating empty at pos");
                for (int i = 0; i < narrow.Count; i++)
                {
                    var h = narrow[i];
                    sb.AppendFormat(
                        CultureInfo.InvariantCulture,
                        "{0,-10} {1,6:F2}  {2,9:F2} {3,8:F2} {4,9:F2}\n",
                        h.kind, h.width, h.pos.x, h.pos.y, h.pos.z);
                }

                sb.AppendLine();
                sb.AppendLine("## Tiny / isolated regions (triangle-cluster area < 8 m^2) — SamplePosition may fail nearby");
                islands.Sort((a, b) => a.area.CompareTo(b.area));
                int show = Mathf.Min(islands.Count, 40);
                for (int i = 0; i < show; i++)
                {
                    var isl = islands[i];
                    sb.AppendFormat(
                        CultureInfo.InvariantCulture,
                        "island     {0,6:F2}m2  {1,9:F2} {2,8:F2} {3,9:F2}\n",
                        isl.area, isl.pos.x, isl.pos.y, isl.pos.z);
                }

                sb.AppendLine();
                sb.AppendLine("## Where to look first in Scene (Navigation overlay)");
                sb.AppendLine("1. Shore / cliff blue-edge ribbons (clamp pulls every frame).");
                sb.AppendLine("2. Dock / Pier / Qiao decks — water NotWalkable carve + punch holes.");
                sb.AppendLine("3. Rock piles / fences / bridge landings — baked obstacles leave <2.2m gaps.");
                sb.AppendLine("4. River bank exit band (~5m XZ) — clamp suddenly re-enables.");
                sb.AppendLine("5. Any thin blue corridor between ShiTou / JianZhu.");

                File.WriteAllText(ReportPath, sb.ToString());
                string tempReport = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Temp", "IslandNavMeshStuckHotspots.txt"));
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(tempReport) ?? "Temp");
                    File.WriteAllText(tempReport, sb.ToString());
                }
                catch (System.Exception ex)
                {
                    Debug.LogWarning("[IslandNavStuck] Temp report write failed: " + ex.Message);
                }

                AssetDatabase.Refresh();

                summary =
                    $"窄口热点 {narrow.Count}（最窄 { (narrow.Count > 0 ? narrow[0].width.ToString("F2") : "-") }m，安全宽>={MinSafeWidth:F1}）\n" +
                    $"碎岛 {islands.Count}\n\n报告:\n{ReportPath}\n{tempReport}";
                Debug.Log("[IslandNavStuck]\n" + sb);
                return true;
            }
            finally
            {
                if (instance.valid)
                    instance.Remove();
            }
        }

        static Bounds ComputeBounds(Vector3[] verts)
        {
            var b = new Bounds(verts[0], Vector3.zero);
            for (int i = 1; i < verts.Length; i++)
                b.Encapsulate(verts[i]);
            return b;
        }

        static void CollectNarrowFromTriangles(
            NavMeshTriangulation tri, List<(Vector3 pos, float width, string kind)> dst)
        {
            int triCount = tri.indices.Length / 3;
            // 大三角面多时降采样；仍覆盖整块蓝面。
            int stride = Mathf.Max(1, triCount / 8000);
            int sampled = 0;
            for (int t = 0; t < triCount; t += stride)
            {
                int i0 = tri.indices[t * 3];
                int i1 = tri.indices[t * 3 + 1];
                int i2 = tri.indices[t * 3 + 2];
                Vector3 mid = (tri.vertices[i0] + tri.vertices[i1] + tri.vertices[i2]) / 3f;
                if (!NavMesh.SamplePosition(mid, out NavMeshHit hit, 1.5f, NavMesh.AllAreas))
                    continue;
                sampled++;

                float width = MeasureLocalWidth(hit.position);
                if (width >= MinSafeWidth)
                    continue;

                string kind = width < BodyRadius ? "choke" : "narrow";
                bool merged = false;
                for (int i = 0; i < dst.Count; i++)
                {
                    Vector3 d = dst[i].pos - hit.position;
                    if (d.x * d.x + d.z * d.z > SampleStep * SampleStep)
                        continue;
                    if (width < dst[i].width)
                        dst[i] = (hit.position, width, kind);
                    merged = true;
                    break;
                }

                if (!merged)
                    dst.Add((hit.position, width, kind));
            }

            Debug.Log(
                $"[IslandNavStuck] tris={triCount} stride={stride} sampled={sampled} narrowRaw={dst.Count}");
        }

        static float MeasureLocalWidth(Vector3 onMesh)
        {
            // 对每个方向 NavMesh.Raycast，取「对向两臂之和」的最小值 ≈ 局部可走宽度。
            float best = float.PositiveInfinity;
            for (int i = 0; i < DirCount / 2; i++)
            {
                float ang = (Mathf.PI * 2f * i) / DirCount;
                Vector3 dir = new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang));
                float a = RayClearance(onMesh, dir);
                float b = RayClearance(onMesh, -dir);
                float width = a + b;
                if (width < best)
                    best = width;
            }

            return best;
        }

        static float RayClearance(Vector3 origin, Vector3 dir)
        {
            Vector3 end = origin + dir * RayLen;
            if (NavMesh.Raycast(origin, end, out NavMeshHit hit, NavMesh.AllAreas))
                return hit.distance;
            return RayLen;
        }

        static void CollectTinyIslands(NavMeshTriangulation tri, List<(Vector3 pos, float area)> dst)
        {
            int triCount = tri.indices.Length / 3;
            var parent = new int[triCount];
            for (int i = 0; i < triCount; i++)
                parent[i] = i;

            int Find(int a)
            {
                while (parent[a] != a)
                    a = parent[a] = parent[parent[a]];
                return a;
            }

            void Union(int a, int b)
            {
                a = Find(a);
                b = Find(b);
                if (a != b)
                    parent[b] = a;
            }

            // 边 → 三角形邻接
            var edgeMap = new Dictionary<long, int>(tri.indices.Length);
            for (int t = 0; t < triCount; t++)
            {
                int i0 = tri.indices[t * 3];
                int i1 = tri.indices[t * 3 + 1];
                int i2 = tri.indices[t * 3 + 2];
                LinkEdge(edgeMap, i0, i1, t, Union);
                LinkEdge(edgeMap, i1, i2, t, Union);
                LinkEdge(edgeMap, i2, i0, t, Union);
            }

            var areaSum = new Dictionary<int, float>(64);
            var centroidSum = new Dictionary<int, Vector3>(64);
            var centroidW = new Dictionary<int, float>(64);

            for (int t = 0; t < triCount; t++)
            {
                int i0 = tri.indices[t * 3];
                int i1 = tri.indices[t * 3 + 1];
                int i2 = tri.indices[t * 3 + 2];
                Vector3 a = tri.vertices[i0];
                Vector3 b = tri.vertices[i1];
                Vector3 c = tri.vertices[i2];
                float area = Vector3.Cross(b - a, c - a).magnitude * 0.5f;
                int root = Find(t);
                if (!areaSum.ContainsKey(root))
                {
                    areaSum[root] = 0f;
                    centroidSum[root] = Vector3.zero;
                    centroidW[root] = 0f;
                }

                areaSum[root] += area;
                Vector3 mid = (a + b + c) / 3f;
                centroidSum[root] += mid * area;
                centroidW[root] += area;
            }

            const float TinyArea = 8f;
            foreach (var kv in areaSum)
            {
                if (kv.Value >= TinyArea)
                    continue;
                float w = centroidW[kv.Key];
                if (w <= 1e-6f)
                    continue;
                dst.Add((centroidSum[kv.Key] / w, kv.Value));
            }
        }

        static void LinkEdge(
            Dictionary<long, int> edgeMap, int i0, int i1, int triIndex, System.Action<int, int> union)
        {
            if (i0 > i1)
            {
                int tmp = i0;
                i0 = i1;
                i1 = tmp;
            }

            long key = ((long)i0 << 32) | (uint)i1;
            if (edgeMap.TryGetValue(key, out int other))
            {
                union(triIndex, other);
                edgeMap.Remove(key);
            }
            else
            {
                edgeMap[key] = triIndex;
            }
        }
    }
}
#endif

