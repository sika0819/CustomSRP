#if UNITY_EDITOR
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Unity.AI.Navigation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;

namespace Game.Editor
{
    /// <summary>
    /// 在 IslandNavBake 场景内验证卡住热点：加载 Surface 上的 NavMeshData，
    /// 对已知窄口/栈桥点做 SamplePosition + 局部宽度测量，并在场景里生成标记。
    /// </summary>
    public static class IslandNavBakeSceneVerify
    {
        const string BakeScenePath = "Assets/GameMain/Scenes/IslandNavBake.unity";
        const string MarkerRootName = "__NavStuckHotspots";
        const string ReportRel = "Temp/IslandNavBakeVerify.txt";
        const string TriggerPath = "Assets/GameMain/Editor/.run-nav-bake-verify";
        const float BodyRadius = 1.1f;
        static float MinSafeWidth => BodyRadius * 2f;

        // 来自 MapItem 石缝 / 栈桥上岸热点（世界坐标）
        static readonly (string label, Vector3 pos)[] Hotspots =
        {
            ("rock_gap_0.89_Rock01", new Vector3(503.9f, 35.6f, 462.3f)),
            ("rock_gap_0.89_Cliff03", new Vector3(284.4f, 52.4f, 328.5f)),
            ("rock_gap_0.97_Rock01", new Vector3(504.5f, 35.6f, 462.9f)),
            ("rock_gap_1.54_Rock01", new Vector3(504.0f, 35.8f, 462.8f)),
            ("rock_gap_1.75_RampCliff", new Vector3(360.7f, 79.7f, 507.6f)),
            ("rock_gap_1.93_Pile06", new Vector3(284.5f, 80.1f, 477.6f)),
            ("rock_gap_2.02_FlatCliff", new Vector3(309.9f, 63.2f, 319.5f)),
            ("rock_gap_2.08_nearPier", new Vector3(233.9f, 101.3f, 658.9f)),
            ("rock_gap_2.31_Pile05", new Vector3(482.3f, 35.4f, 431.2f)),
            ("rock_gap_2.33_Rock03_04", new Vector3(513.8f, 35.4f, 468.5f)),
            ("pier_NW_a", new Vector3(192.9f, 104.8f, 684.6f)),
            ("pier_NW_b", new Vector3(226.5f, 101.0f, 646.7f)),
            ("pier_NW_rockPile", new Vector3(230.6f, 98.6f, 643.5f)),
            ("pier_mid", new Vector3(351.0f, 75.3f, 433.7f)),
            ("pier_mid_rockPile", new Vector3(362.7f, 71.3f, 425.9f)),
            ("pier_mideast", new Vector3(433.2f, 33.0f, 547.8f)),
            ("pier_mideast_cliff", new Vector3(422.4f, 24.2f, 556.8f)),
            ("pier_east", new Vector3(576.5f, 16.6f, 599.2f)),
            ("pier_east_rock", new Vector3(577.2f, 14.5f, 585.0f)),
            ("dock_shore", new Vector3(629.7f, 2.4f, 669.8f)),
            ("dock_rock", new Vector3(617.4f, 6.0f, 670.8f)),
            ("fence_meadow", new Vector3(187.6f, 109.5f, 746.0f)),
        };

        [MenuItem("GameMain/Boluo/Verify IslandNavBake Stuck Hotspots")]
        public static void MenuVerify()
        {
            if (!Verify(out string summary))
            {
                EditorUtility.DisplayDialog("IslandNavBake Verify", summary, "OK");
                return;
            }

            EditorUtility.DisplayDialog("IslandNavBake Verify", summary, "OK");
        }

        [InitializeOnLoadMethod]
        static void WatchTrigger()
        {
            EditorApplication.update -= PollTrigger;
            EditorApplication.update += PollTrigger;
        }

        static double _next;

        static void PollTrigger()
        {
            if (EditorApplication.timeSinceStartup < _next)
                return;
            _next = EditorApplication.timeSinceStartup + 1.0;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                return;
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                return;
            if (!File.Exists(TriggerPath))
                return;
            try { File.Delete(TriggerPath); }
            catch { return; }

            try
            {
                bool ok = Verify(out string summary);
                Debug.Log($"[IslandNavBakeVerify] ok={ok}\n{summary}");
            }
            catch (System.Exception ex)
            {
                Debug.LogError("[IslandNavBakeVerify] threw: " + ex);
            }
        }

        public static bool Verify(out string summary)
        {
            EnsureBakeSceneOpen();

            NavMeshSurface surface = Object.FindAnyObjectByType<NavMeshSurface>();
            if (surface == null)
            {
                summary = "IslandNavBake 里找不到 NavMeshSurface。";
                return false;
            }

            NavMeshData data = surface.navMeshData;
            string dataPath = data != null ? AssetDatabase.GetAssetPath(data) : "(null)";
            if (data == null)
            {
                summary = "NavMeshSurface.navMeshData 为空。";
                return false;
            }

            // 场景与运行时 GUID 可能不一致：同时报告 shipping 路径
            var shipping = AssetDatabase.LoadAssetAtPath<NavMeshData>(
                "Assets/AssetRaw/Island/IslandNavMesh.asset");

            NavMeshDataInstance inst = NavMesh.AddNavMeshData(data);
            try
            {
                var sb = new StringBuilder(8 * 1024);
                sb.AppendLine("IslandNavBake scene stuck-hotspot verify");
                sb.AppendLine($"scene={BakeScenePath}");
                sb.AppendLine($"surfaceData={dataPath}");
                sb.AppendLine($"shippingIslandNavMesh={(shipping != null ? "present" : "MISSING")}");
                sb.AppendLine(
                    $"sameObjectAsShipping={(shipping != null && shipping == data)}");
                sb.AppendLine(
                    $"agentTypeID={surface.agentTypeID} useGeometry={surface.useGeometry} " +
                    $"minRegionArea={surface.minRegionArea} voxel={surface.voxelSize} " +
                    $"buildHeightMesh={surface.buildHeightMesh}");
                sb.AppendLine(
                    $"bakeAgentRadius=0.5 runtimeBodyRadius={BodyRadius} minSafeWidth={MinSafeWidth:F2}");
                sb.AppendLine();

                ClearMarkers();
                var markerRoot = new GameObject(MarkerRootName);
                Undo.RegisterCreatedObjectUndo(markerRoot, "Nav stuck markers");

                int offMesh = 0;
                int choke = 0;
                int narrow = 0;
                int okCount = 0;
                var worst = new List<(float width, string label, Vector3 pos, string verdict)>();

                for (int i = 0; i < Hotspots.Length; i++)
                {
                    string label = Hotspots[i].label;
                    Vector3 probe = Hotspots[i].pos;
                    bool on = NavMesh.SamplePosition(probe, out NavMeshHit hit, 4f, NavMesh.AllAreas);
                    float width = on ? MeasureLocalWidth(hit.position) : -1f;
                    string verdict;
                    Color col;
                    if (!on)
                    {
                        offMesh++;
                        verdict = "OFF_MESH";
                        col = Color.magenta;
                    }
                    else if (width < BodyRadius)
                    {
                        choke++;
                        verdict = "CHOKE";
                        col = Color.red;
                    }
                    else if (width < MinSafeWidth)
                    {
                        narrow++;
                        verdict = "NARROW";
                        col = new Color(1f, 0.55f, 0f);
                    }
                    else
                    {
                        okCount++;
                        verdict = "OK";
                        col = Color.green;
                    }

                    Vector3 markPos = on ? hit.position : probe;
                    worst.Add((width, label, markPos, verdict));
                    sb.AppendFormat(
                        CultureInfo.InvariantCulture,
                        "{0,-8} w={1,6:F2}  ({2,8:F1},{3,6:F1},{4,8:F1})  {5}\n",
                        verdict, width, markPos.x, markPos.y, markPos.z, label);

                    var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                    go.name = $"{verdict}_{width:F2}_{label}";
                    go.transform.SetParent(markerRoot.transform, false);
                    go.transform.position = markPos + Vector3.up * 1.5f;
                    go.transform.localScale = Vector3.one * (verdict == "OK" ? 1.2f : 2.2f);
                    Object.DestroyImmediate(go.GetComponent<Collider>());
                    var mr = go.GetComponent<MeshRenderer>();
                    mr.sharedMaterial = new Material(Shader.Find("Sprites/Default")) { color = col };
                }

                string reportPath = Path.GetFullPath(
                    Path.Combine(Application.dataPath, "..", ReportRel));
                Directory.CreateDirectory(Path.GetDirectoryName(reportPath) ?? "Temp");
                File.WriteAllText(reportPath, sb.ToString());

                // 选中最差几个标记，方便 Scene 里 Frame
                worst.Sort((a, b) =>
                {
                    int Va(string v) => v == "CHOKE" ? 0 : v == "NARROW" ? 1 : v == "OFF_MESH" ? 2 : 3;
                    int c = Va(a.verdict).CompareTo(Va(b.verdict));
                    if (c != 0) return c;
                    return a.width.CompareTo(b.width);
                });
                if (markerRoot.transform.childCount > 0)
                {
                    Selection.activeGameObject = markerRoot.transform.GetChild(0).gameObject;
                    SceneView.lastActiveSceneView?.FrameSelected();
                }

                summary =
                    $"surface={dataPath}\n" +
                    $"CHOKE={choke} NARROW={narrow} OFF_MESH={offMesh} OK={okCount}\n" +
                    $"markers=/{MarkerRootName}  report={reportPath}\n" +
                    $"红=胶囊过不去 橙=窄于2.2m 紫=蓝面外 绿=够宽";
                Debug.Log("[IslandNavBakeVerify]\n" + sb);
                return true;
            }
            finally
            {
                if (inst.valid)
                    inst.Remove();
            }
        }

        static void EnsureBakeSceneOpen()
        {
            Scene active = EditorSceneManager.GetActiveScene();
            if (active.path == BakeScenePath)
                return;
            for (int i = 0; i < EditorSceneManager.sceneCount; i++)
            {
                Scene s = EditorSceneManager.GetSceneAt(i);
                if (s.path == BakeScenePath)
                {
                    EditorSceneManager.SetActiveScene(s);
                    return;
                }
            }

            EditorSceneManager.OpenScene(BakeScenePath, OpenSceneMode.Single);
        }

        static void ClearMarkers()
        {
            var existing = GameObject.Find(MarkerRootName);
            if (existing != null)
                Undo.DestroyObjectImmediate(existing);
        }

        static float MeasureLocalWidth(Vector3 onMesh)
        {
            const int dirs = 8;
            const float rayLen = 5f;
            float best = float.PositiveInfinity;
            for (int i = 0; i < dirs / 2; i++)
            {
                float ang = (Mathf.PI * 2f * i) / dirs;
                Vector3 dir = new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang));
                float a = RayClear(onMesh, dir, rayLen);
                float b = RayClear(onMesh, -dir, rayLen);
                best = Mathf.Min(best, a + b);
            }

            return best;
        }

        static float RayClear(Vector3 origin, Vector3 dir, float rayLen)
        {
            if (NavMesh.Raycast(origin, origin + dir * rayLen, out NavMeshHit hit, NavMesh.AllAreas))
                return hit.distance;
            return rayLen;
        }
    }
}
#endif
