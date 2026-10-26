#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Unity.AI.Navigation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;

namespace Game.Editor
{
    /// <summary>
    /// 建 IslandNavBake 场景（地形/海/河 + 影响行走的 MapItem），并烘焙 NavMeshData。
    /// 快捷入口: Window/AI/Navigation 窗口顶部 Bake；菜单 GameMain/Boluo/…
    /// </summary>
    public static class IslandNavBakeSceneBuilder
    {
        const string ScenePath = "Assets/GameMain/Scenes/IslandNavBake.unity";
        const string NavMeshAssetPath = "Assets/AssetRaw/Island/IslandNavMesh.asset";
        const string AllMapItemPath = "Assets/AssetRaw/Configs/AllMapItem.bytes";
        const string RootName = "IslandNavBakeRoot";
        const string MapItemsName = "WalkAffectingMapItems";

        /// <summary>Not Walkable area id（Unity 内置，优先级最高，在蓝面上打洞）。</summary>
        const int NotWalkableArea = 1;

        /// <summary>水面 NotWalkable 与码头甲板间距小于 agentHeight 时会挖穿甲板；在甲板 XZ 下打洞避开。</summary>
        const float WalkDeckHolePadding = 1.25f;
        const float WaterHoleCellSize = 2f;

        [MenuItem("GameMain/Boluo/Build Island Nav Bake Scene")]
        public static void MenuBuildScene()
        {
            int spawned = BuildScene();
            EditorUtility.DisplayDialog(
                "Island Nav Bake Scene",
                $"已写入 {ScenePath}\n影响行走的 MapItem 实例: {spawned}\n\n接着用 GameMain/Boluo/Bake Island NavMesh。",
                "OK");
        }

        [MenuItem("GameMain/Boluo/Bake Island NavMesh")]
        public static void MenuBakeNavMesh()
        {
            if (!BakeNavMesh(out string message))
            {
                EditorUtility.DisplayDialog("Bake Island NavMesh", message, "OK");
                return;
            }

            EditorUtility.DisplayDialog("Bake Island NavMesh", message, "OK");
        }

        [MenuItem("GameMain/Boluo/Rebuild Scene And Bake NavMesh (Water Carve)")]
        public static void MenuRebuildAndBake()
        {
            int n = BuildScene();
            bool ok = BakeNavMesh(out string msg);
            EditorUtility.DisplayDialog(
                "Rebuild + Bake NavMesh",
                ok
                    ? $"walkItems={n}\n海已 Not Walkable（河可走）。\n{msg}"
                    : $"walkItems={n}\n失败: {msg}",
                "OK");
        }

        /// <summary>供 -executeMethod：先建场景再 bake。</summary>
        public static void BatchBuildAndBake()
        {
            int n = BuildScene();
            bool ok = BakeNavMesh(out string msg);
            Debug.Log($"[IslandNavBake] BatchBuildAndBake mapItems={n} bakeOk={ok} {msg}");
        }

        public static int BuildScene()
        {
            string dir = Path.GetDirectoryName(ScenePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var root = new GameObject(RootName);

            GameObject terrain = InstantiatePrefab(
                SceneAssets.IslandTerrainPrefabPath, root.transform, "IslandTerrain");
            GameObject ocean = InstantiatePrefab(
                SceneAssets.IslandOceanPrefabPath, root.transform, "IslandOcean");
            GameObject inland = InstantiatePrefab(
                SceneAssets.IslandInlandWaterPrefabPath, root.transform, "IslandInlandWater");

            // 只挖海：河预期可走（河床 / 河面保持 Walkable）。
            if (ocean != null)
                PrepareWaterForCarve(ocean);

            var mapRoot = new GameObject(MapItemsName);
            mapRoot.transform.SetParent(root.transform, false);
            int spawned = SpawnWalkAffectingMapItems(mapRoot.transform);

            // 码头/桥甲板紧贴海面时，NotWalkable 会按 agentHeight 合并挖穿蓝面。
            PunchWaterUnderWalkDecks(ocean, null, mapRoot.transform);

            EnsureNavMeshSurface(root);

            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.Refresh();
            Debug.Log($"[IslandNavBake] Scene saved path={ScenePath} walkItems={spawned} " +
                      $"terrain={terrain != null} ocean={ocean != null} inland={inland != null}");
            return spawned;
        }

        public static bool BakeNavMesh(out string message)
        {
            message = null;
            Scene scene = SceneManager.GetActiveScene();
            if (!scene.path.Replace('\\', '/').EndsWith("IslandNavBake.unity"))
            {
                if (!File.Exists(ScenePath))
                {
                    message = "缺少烘焙场景。请先运行 Build Island Nav Bake Scene。";
                    return false;
                }

                scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            }

            GameObject root = GameObject.Find(RootName);
            if (root == null)
            {
                message = $"场景里找不到 {RootName}。请重建烘焙场景。";
                return false;
            }

            NavMeshSurface surface = root.GetComponent<NavMeshSurface>();
            if (surface == null)
                surface = EnsureNavMeshSurface(root);

            // Bake 只 BuildNavMesh，不改 Surface / Agent。
            // 水面挖洞 / 码头开孔：缺标记时用 Build Island Nav Bake Scene 重建。

            EditorUtility.DisplayProgressBar("Island NavMesh", "Baking…", 0.4f);
            try
            {
                // 场景若已建过，再补一次海面开孔（幂等：已开孔的 mesh 名带后缀则跳过）。河不挖洞。
                GameObject ocean = GameObject.Find("IslandOcean");
                Transform mapRoot = root.transform.Find(MapItemsName);
                if (mapRoot != null)
                    PunchWaterUnderWalkDecks(ocean, null, mapRoot);

                surface.RemoveData();
                surface.BuildNavMesh();
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            NavMeshData baked = surface.navMeshData;
            if (baked == null)
            {
                message = "Bake 失败：NavMeshSurface.navMeshData 为空。";
                return false;
            }

            if (!SaveNavMeshAsset(surface, baked))
            {
                message = $"无法写入 {NavMeshAssetPath}";
                return false;
            }

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            message =
                $"已烘焙并保存:\n{NavMeshAssetPath}\nAddress(Yoo)=IslandNavMesh\n\n" +
                "请在 Navigation 窗口检视岸线/河岸/悬崖蓝面。";
            Debug.Log($"[IslandNavBake] {message.Replace('\n', ' ')}");
            return true;
        }

        /// <summary>
        /// 仅在缺少 NavMeshSurface 时添加并写默认值；已有组件一律保留用户配置。
        /// </summary>
        static NavMeshSurface EnsureNavMeshSurface(GameObject root)
        {
            NavMeshSurface surface = root.GetComponent<NavMeshSurface>();
            if (surface != null)
                return surface;

            surface = root.AddComponent<NavMeshSurface>();
            surface.collectObjects = CollectObjects.Children;
            // RenderMeshes：海面无 Collider 也能被 Not Walkable 挖洞（PhysicsColliders 采不到水面）。
            surface.useGeometry = NavMeshCollectGeometry.RenderMeshes;
            surface.defaultArea = 0; // Walkable
            surface.layerMask = ~0;
            surface.ignoreNavMeshAgent = true;
            surface.ignoreNavMeshObstacle = true;
            // Humanoid agentTypeID=0；半径/坡度与默认设置接近 CC（细调可在 Navigation 窗口改 Agent）。
            surface.agentTypeID = 0;
            return surface;
        }

        /// <summary>
        /// 海面标 Not Walkable，并补 MeshCollider（便于检视 / 若改回 PhysicsColliders 仍能挖洞）。
        /// 仅用于海；河保持可走，不要对 InlandWater 调用。子节点 MeshFilter 也要单独打标。
        /// </summary>
        static void PrepareWaterForCarve(GameObject go)
        {
            if (go == null)
                return;

            TryRebuildOilOceanMesh(go);

            MarkNotWalkable(go);

            MeshFilter[] filters = go.GetComponentsInChildren<MeshFilter>(true);
            for (int i = 0; i < filters.Length; i++)
            {
                MeshFilter mf = filters[i];
                if (mf == null || mf.sharedMesh == null)
                    continue;
                MarkNotWalkable(mf.gameObject);
                MeshCollider mc = mf.GetComponent<MeshCollider>();
                if (mc == null)
                    mc = mf.gameObject.AddComponent<MeshCollider>();
                mc.sharedMesh = mf.sharedMesh;
                mc.convex = false;
            }
        }

        /// <summary>
        /// 在码头/桥 XZ 范围内把水面 NotWalkable 网格镂空，避免与甲板竖直合并后整段不可走。
        /// </summary>
        static void PunchWaterUnderWalkDecks(GameObject ocean, GameObject inland, Transform mapRoot)
        {
            if (mapRoot == null)
                return;

            var holes = new List<Rect>(32);
            CollectWalkDeckHoleRects(mapRoot, holes);
            if (holes.Count == 0)
                return;

            int punched = 0;
            if (ocean != null)
                punched += PunchWaterMeshes(ocean, holes);
            if (inland != null)
                punched += PunchWaterMeshes(inland, holes);

            if (punched > 0)
                Debug.Log($"[IslandNavBake] Punched water under walk decks holes={holes.Count} meshes={punched}");
        }

        static void CollectWalkDeckHoleRects(Transform mapRoot, List<Rect> holes)
        {
            for (int i = 0; i < mapRoot.childCount; i++)
            {
                Transform child = mapRoot.GetChild(i);
                if (child == null || !IsElevatedWalkDeckName(child.name))
                    continue;

                if (!TryGetRendererBoundsXZ(child, out float minX, out float maxX, out float minZ, out float maxZ))
                    continue;

                float pad = WalkDeckHolePadding;
                holes.Add(Rect.MinMaxRect(minX - pad, minZ - pad, maxX + pad, maxZ + pad));
            }
        }

        static bool IsElevatedWalkDeckName(string instanceName)
        {
            if (string.IsNullOrEmpty(instanceName))
                return false;
            // JianZhu_SM_Env_Dock_* / Qiao_* / *Pier*
            if (instanceName.StartsWith("Qiao_", System.StringComparison.Ordinal))
                return true;
            if (instanceName.IndexOf("Dock", System.StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            if (instanceName.IndexOf("Pier", System.StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            return false;
        }

        static bool TryGetRendererBoundsXZ(
            Transform root, out float minX, out float maxX, out float minZ, out float maxZ)
        {
            minX = minZ = float.PositiveInfinity;
            maxX = maxZ = float.NegativeInfinity;
            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
            bool any = false;
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer r = renderers[i];
                if (r == null)
                    continue;
                Bounds b = r.bounds;
                minX = Mathf.Min(minX, b.min.x);
                maxX = Mathf.Max(maxX, b.max.x);
                minZ = Mathf.Min(minZ, b.min.z);
                maxZ = Mathf.Max(maxZ, b.max.z);
                any = true;
            }

            if (!any)
            {
                Collider[] cols = root.GetComponentsInChildren<Collider>(true);
                for (int i = 0; i < cols.Length; i++)
                {
                    Collider c = cols[i];
                    if (c == null || c.isTrigger)
                        continue;
                    Bounds b = c.bounds;
                    minX = Mathf.Min(minX, b.min.x);
                    maxX = Mathf.Max(maxX, b.max.x);
                    minZ = Mathf.Min(minZ, b.min.z);
                    maxZ = Mathf.Max(maxZ, b.max.z);
                    any = true;
                }
            }

            return any && maxX > minX && maxZ > minZ;
        }

        static int PunchWaterMeshes(GameObject waterRoot, List<Rect> holes)
        {
            int n = 0;
            MeshFilter[] filters = waterRoot.GetComponentsInChildren<MeshFilter>(true);
            for (int i = 0; i < filters.Length; i++)
            {
                MeshFilter mf = filters[i];
                if (mf == null || mf.sharedMesh == null)
                    continue;
                if (mf.sharedMesh.name != null
                    && mf.sharedMesh.name.EndsWith("_NavPunch", System.StringComparison.Ordinal))
                    continue;

                Mesh punched = BuildPunchedWaterMesh(mf, holes);
                if (punched == null)
                    continue;

                mf.sharedMesh = punched;
                MeshCollider mc = mf.GetComponent<MeshCollider>();
                if (mc != null)
                    mc.sharedMesh = punched;
                n++;
            }

            return n;
        }

        /// <summary>
        /// 与码头矩形相交的大三角细分成小格，落在开孔内的格子丢掉。
        /// </summary>
        static Mesh BuildPunchedWaterMesh(MeshFilter mf, List<Rect> holes)
        {
            Mesh src = mf.sharedMesh;
            Transform t = mf.transform;
            Vector3[] verts = src.vertices;
            int[] tris = src.triangles;
            if (verts == null || tris == null || tris.Length < 3)
                return null;

            var outVerts = new List<Vector3>(verts.Length + 64);
            var outTris = new List<int>(tris.Length);
            // 先拷贝未碰到开孔的三角；碰到的改走细分。
            bool anyPunch = false;
            for (int i = 0; i < tris.Length; i += 3)
            {
                Vector3 a = t.TransformPoint(verts[tris[i]]);
                Vector3 b = t.TransformPoint(verts[tris[i + 1]]);
                Vector3 c = t.TransformPoint(verts[tris[i + 2]]);
                if (!TriangleOverlapsAnyHole(a, b, c, holes))
                {
                    int baseIdx = outVerts.Count;
                    outVerts.Add(verts[tris[i]]);
                    outVerts.Add(verts[tris[i + 1]]);
                    outVerts.Add(verts[tris[i + 2]]);
                    outTris.Add(baseIdx);
                    outTris.Add(baseIdx + 1);
                    outTris.Add(baseIdx + 2);
                    continue;
                }

                anyPunch = true;
                AppendSubdividedTriMinusHoles(t, a, b, c, holes, outVerts, outTris);
            }

            if (!anyPunch)
                return null;

            var mesh = new Mesh
            {
                name = src.name + "_NavPunch",
                indexFormat = outVerts.Count > 65535
                    ? UnityEngine.Rendering.IndexFormat.UInt32
                    : UnityEngine.Rendering.IndexFormat.UInt16
            };
            mesh.SetVertices(outVerts);
            mesh.SetTriangles(outTris, 0);
            mesh.RecalculateBounds();
            mesh.RecalculateNormals();
            return mesh;
        }

        static bool TriangleOverlapsAnyHole(Vector3 a, Vector3 b, Vector3 c, List<Rect> holes)
        {
            float minX = Mathf.Min(a.x, Mathf.Min(b.x, c.x));
            float maxX = Mathf.Max(a.x, Mathf.Max(b.x, c.x));
            float minZ = Mathf.Min(a.z, Mathf.Min(b.z, c.z));
            float maxZ = Mathf.Max(a.z, Mathf.Max(b.z, c.z));
            var triBounds = Rect.MinMaxRect(minX, minZ, maxX, maxZ);
            for (int i = 0; i < holes.Count; i++)
            {
                if (triBounds.Overlaps(holes[i]))
                    return true;
            }

            return false;
        }

        static void AppendSubdividedTriMinusHoles(
            Transform localOf,
            Vector3 a, Vector3 b, Vector3 c,
            List<Rect> holes,
            List<Vector3> outVerts,
            List<int> outTris)
        {
            float minX = Mathf.Min(a.x, Mathf.Min(b.x, c.x));
            float maxX = Mathf.Max(a.x, Mathf.Max(b.x, c.x));
            float minZ = Mathf.Min(a.z, Mathf.Min(b.z, c.z));
            float maxZ = Mathf.Max(a.z, Mathf.Max(b.z, c.z));
            float y = (a.y + b.y + c.y) * (1f / 3f);
            // 大海洋三角（~500m）只在开孔附近细刨，其余用粗格，避免网格爆炸。
            const float coarseStep = 40f;
            float fineStep = Mathf.Max(0.5f, WaterHoleCellSize);

            for (float x0 = minX; x0 < maxX - 1e-4f; x0 += coarseStep)
            {
                float x1 = Mathf.Min(x0 + coarseStep, maxX);
                for (float z0 = minZ; z0 < maxZ - 1e-4f; z0 += coarseStep)
                {
                    float z1 = Mathf.Min(z0 + coarseStep, maxZ);
                    float cx = (x0 + x1) * 0.5f;
                    float cz = (z0 + z1) * 0.5f;
                    if (!PointInTriangleXZ(cx, cz, a, b, c))
                        continue;

                    var cell = Rect.MinMaxRect(x0, z0, x1, z1);
                    if (!CellOverlapsAnyHole(cell, holes))
                    {
                        AppendWorldQuad(localOf, x0, x1, z0, z1, y, outVerts, outTris);
                        continue;
                    }

                    for (float fx0 = x0; fx0 < x1 - 1e-4f; fx0 += fineStep)
                    {
                        float fx1 = Mathf.Min(fx0 + fineStep, x1);
                        for (float fz0 = z0; fz0 < z1 - 1e-4f; fz0 += fineStep)
                        {
                            float fz1 = Mathf.Min(fz0 + fineStep, z1);
                            float fcx = (fx0 + fx1) * 0.5f;
                            float fcz = (fz0 + fz1) * 0.5f;
                            if (PointInAnyHole(fcx, fcz, holes))
                                continue;
                            if (!PointInTriangleXZ(fcx, fcz, a, b, c))
                                continue;
                            AppendWorldQuad(localOf, fx0, fx1, fz0, fz1, y, outVerts, outTris);
                        }
                    }
                }
            }
        }

        static bool CellOverlapsAnyHole(Rect cell, List<Rect> holes)
        {
            for (int i = 0; i < holes.Count; i++)
            {
                if (cell.Overlaps(holes[i]))
                    return true;
            }

            return false;
        }

        static void AppendWorldQuad(
            Transform localOf,
            float x0, float x1, float z0, float z1, float y,
            List<Vector3> outVerts,
            List<int> outTris)
        {
            Vector3 p00 = localOf.InverseTransformPoint(new Vector3(x0, y, z0));
            Vector3 p10 = localOf.InverseTransformPoint(new Vector3(x1, y, z0));
            Vector3 p11 = localOf.InverseTransformPoint(new Vector3(x1, y, z1));
            Vector3 p01 = localOf.InverseTransformPoint(new Vector3(x0, y, z1));
            int i0 = outVerts.Count;
            outVerts.Add(p00);
            outVerts.Add(p10);
            outVerts.Add(p11);
            outVerts.Add(p01);
            outTris.Add(i0);
            outTris.Add(i0 + 1);
            outTris.Add(i0 + 2);
            outTris.Add(i0);
            outTris.Add(i0 + 2);
            outTris.Add(i0 + 3);
        }

        static bool PointInAnyHole(float x, float z, List<Rect> holes)
        {
            for (int i = 0; i < holes.Count; i++)
            {
                if (holes[i].Contains(new Vector2(x, z)))
                    return true;
            }

            return false;
        }

        static bool PointInTriangleXZ(float x, float z, Vector3 a, Vector3 b, Vector3 c)
        {
            // 重心坐标（XZ）。
            float x1 = a.x, z1 = a.z;
            float x2 = b.x, z2 = b.z;
            float x3 = c.x, z3 = c.z;
            float denom = (z2 - z3) * (x1 - x3) + (x3 - x2) * (z1 - z3);
            if (Mathf.Abs(denom) < 1e-12f)
                return false;
            float l1 = ((z2 - z3) * (x - x3) + (x3 - x2) * (z - z3)) / denom;
            float l2 = ((z3 - z1) * (x - x3) + (x1 - x3) * (z - z3)) / denom;
            float l3 = 1f - l1 - l2;
            return l1 >= -1e-4f && l2 >= -1e-4f && l3 >= -1e-4f;
        }

        static void MarkNotWalkable(GameObject go)
        {
            if (go == null)
                return;
            NavMeshModifier mod = go.GetComponent<NavMeshModifier>();
            if (mod == null)
                mod = go.AddComponent<NavMeshModifier>();
            mod.overrideArea = true;
            mod.area = NotWalkableArea;
            mod.applyToChildren = true;
            mod.ignoreFromBuild = false;
        }

        /// <summary>
        /// OilOceanSurface 在 Assembly-CSharp，Game.Editor 不能直接引用；反射 Rebuild 保证有 RenderMesh 可挖洞。
        /// </summary>
        static void TryRebuildOilOceanMesh(GameObject go)
        {
            if (go == null)
                return;
            System.Type oceanType = System.Type.GetType("CustomSRP.OilOceanSurface, Assembly-CSharp");
            if (oceanType == null)
                return;
            Component ocean = go.GetComponent(oceanType);
            if (ocean == null)
                ocean = go.GetComponentInChildren(oceanType, true);
            if (ocean == null)
                return;
            MethodInfo rebuild = oceanType.GetMethod(
                "Rebuild", BindingFlags.Instance | BindingFlags.Public);
            if (rebuild == null)
                return;
            try
            {
                rebuild.Invoke(ocean, null);
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning("[IslandNavBake] OilOceanSurface.Rebuild failed: " + ex.Message);
            }
        }

        static GameObject InstantiatePrefab(string assetPath, Transform parent, string instanceName)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (prefab == null)
            {
                Debug.LogError($"[IslandNavBake] Missing prefab {assetPath}");
                return null;
            }

            GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, parent);
            instance.name = instanceName;
            return instance;
        }

        static int SpawnWalkAffectingMapItems(Transform parent)
        {
            if (!File.Exists(AllMapItemPath))
            {
                Debug.LogError("[IslandNavBake] Missing " + AllMapItemPath);
                return 0;
            }

            byte[] bytes = File.ReadAllBytes(AllMapItemPath);
            var items = new List<MapItemData>(2560);
            if (!MapItemData.ReadBinaryTo(bytes, items))
                return 0;

            int spawned = 0;
            int missing = 0;
            var prefabCache = new Dictionary<string, GameObject>(256);

            for (int i = 0; i < items.Count; i++)
            {
                MapItemData d = items[i];
                if (d == null || !d.IsActive)
                    continue;
                if (!ShouldIncludeForNavBake(d.Parent, d.PrefabName))
                    continue;

                string editorPath = SceneAssets.DaoYu1EditorPath(d.Parent, d.PrefabName);
                if (string.IsNullOrEmpty(editorPath))
                {
                    missing++;
                    continue;
                }

                if (!prefabCache.TryGetValue(editorPath, out GameObject prefab))
                {
                    prefab = AssetDatabase.LoadAssetAtPath<GameObject>(editorPath);
                    prefabCache[editorPath] = prefab;
                }

                if (prefab == null)
                {
                    missing++;
                    continue;
                }

                GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, parent);
                instance.name = $"{d.Parent}_{d.PrefabName}_{d.Index}";
                instance.transform.SetPositionAndRotation(d.Pos, Quaternion.Euler(d.Rotation));
                instance.transform.localScale = d.Scale;
                spawned++;
            }

            if (missing > 0)
                Debug.LogWarning($"[IslandNavBake] Missing prefabs skipped={missing}");
            return spawned;
        }

        static bool ShouldIncludeForNavBake(string category, string prefabName)
        {
            if (category == "JianZhu" || category == "Qiao")
                return true;
            // 栏杆/稻草人：运行时有碰撞，若不进烤盘蓝面会穿栏 → 过桥上岸被 CC 卡住。
            if (EnvStreamingConfig.IsStructuralClutter(prefabName))
                return true;
            // 全部石头进烤盘（含 Rock_Pile_*）。只烤 Large/Cliff 时，桥边石堆会蓝面可走、运行时撞死。
            if (category == "ShiTou")
                return true;
            if (category == "Other")
            {
                return EnvStreamingConfig.IsFarFieldLandmark(prefabName)
                       || EnvStreamingConfig.IsRockRampLandmark(prefabName);
            }

            return false;
        }

        static bool SaveNavMeshAsset(NavMeshSurface surface, NavMeshData baked)
        {
            string dir = Path.GetDirectoryName(NavMeshAssetPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            // BuildNavMesh 常把 asset 建在场景旁；统一挪/拷到 AssetRaw/Island。
            string existing = AssetDatabase.GetAssetPath(baked);
            if (!string.IsNullOrEmpty(existing) && existing.Replace('\\', '/') == NavMeshAssetPath)
            {
                surface.navMeshData = baked;
                EditorUtility.SetDirty(surface);
                return true;
            }

            if (File.Exists(NavMeshAssetPath))
                AssetDatabase.DeleteAsset(NavMeshAssetPath);

            if (!string.IsNullOrEmpty(existing))
            {
                string err = AssetDatabase.MoveAsset(existing, NavMeshAssetPath);
                if (!string.IsNullOrEmpty(err))
                {
                    Debug.LogWarning($"[IslandNavBake] MoveAsset failed ({err}); duplicating.");
                    NavMeshData copy = Object.Instantiate(baked);
                    copy.name = "IslandNavMesh";
                    AssetDatabase.CreateAsset(copy, NavMeshAssetPath);
                    surface.navMeshData = copy;
                }
                else
                {
                    surface.navMeshData = AssetDatabase.LoadAssetAtPath<NavMeshData>(NavMeshAssetPath);
                }
            }
            else
            {
                baked.name = "IslandNavMesh";
                AssetDatabase.CreateAsset(baked, NavMeshAssetPath);
                surface.navMeshData = baked;
            }

            EditorUtility.SetDirty(surface);
            return AssetDatabase.LoadAssetAtPath<NavMeshData>(NavMeshAssetPath) != null;
        }
    }
}
#endif
