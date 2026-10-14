#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Game.Editor
{
    /// <summary>
    /// 桥柱贴地：对场景内 Qiao / SWS_DemoPier 实例拉长柱脚顶点到 Terrain。
    /// 菜单: Window/Game/Fit Bridge Pillars
    /// 批处理: -executeMethod Game.Editor.FitBridgePillarsMenu.BatchFit
    /// </summary>
    public static class FitBridgePillarsMenu
    {
        const string PrefabPath = "Assets/GameMain/Environment/Prefabs/DaoYu1/Qiao/SWS_DemoPier.prefab";
        const string AllMapItemPath = "Assets/AssetRaw/Configs/AllMapItem.bytes";
        const string TerrainPrefabPath = "Assets/AssetRaw/Island/IslandTerrain.prefab";

        [MenuItem("Window/Game/Fit Bridge Pillars")]
        public static void MenuFitScene()
        {
            int n = FitSceneInstances();
            EditorUtility.DisplayDialog(
                "Fit Bridge Pillars",
                n > 0
                    ? $"已对 {n} 个桥实例拉伸柱脚顶点（Clone mesh，不改 FBX）。\n运行时加载也会自动贴地。"
                    : "场景里没有可处理的桥实例（需已加载 Terrain + Qiao）。",
                "OK");
        }

        [MenuItem("Window/Game/Fit Bridge Pillars (Preview Gaps)")]
        public static void MenuPreviewGaps()
        {
            int n = PreviewMapItemGaps();
            EditorUtility.DisplayDialog(
                "Bridge Pillar Gaps",
                n > 0
                    ? $"已在 Console 打印 {n} 条 Qiao 摆放的柱脚悬空预览。"
                    : "无法预览（缺 AllMapItem / Terrain / Prefab）。",
                "OK");
        }

        /// <summary>供 -executeMethod 批处理。</summary>
        public static void BatchFit()
        {
            int preview = PreviewMapItemGaps();
            int fitted = FitFromMapItemDrySpawn();
            Debug.Log($"[FitBridgePillars] BatchFit preview={preview} fitted={fitted}");
        }

        public static int FitSceneInstances()
        {
            var filters = Object.FindObjectsByType<MeshFilter>(FindObjectsInactive.Exclude);
            int fitted = 0;
            var seen = new HashSet<EntityId>();

            for (int i = 0; i < filters.Length; i++)
            {
                MeshFilter mf = filters[i];
                if (mf == null || mf.sharedMesh == null)
                    continue;

                string meshName = mf.sharedMesh.name;
                if (meshName.IndexOf("Dock", System.StringComparison.OrdinalIgnoreCase) < 0
                    && meshName.IndexOf("Pier", System.StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                // 爬到带 MapItem 下标名或预制体名的实例根
                Transform root = mf.transform;
                while (root.parent != null
                       && root.parent.name != "Qiao"
                       && root.name.IndexOf("SWS_DemoPier", System.StringComparison.OrdinalIgnoreCase) < 0)
                    root = root.parent;

                EntityId id = root.gameObject.GetEntityId();
                if (!seen.Add(id))
                    continue;

                EnvBridgePillarFit.Ensure(root.gameObject);
                fitted++;
            }

            Debug.Log($"[FitBridgePillars] Scene fitted={fitted}");
            return fitted;
        }

        /// <summary>
        /// 临时实例化全部 Qiao 摆放，贴地后立刻销毁（验证逻辑 / 打日志）。
        /// </summary>
        public static int FitFromMapItemDrySpawn()
        {
            EnsureTerrainInScene();

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            if (prefab == null)
            {
                Debug.LogError("[FitBridgePillars] Missing " + PrefabPath);
                return 0;
            }

            if (!TryLoadQiaoItems(out List<MapItemData> qiao))
                return 0;

            int fitted = 0;
            for (int i = 0; i < qiao.Count; i++)
            {
                MapItemData d = qiao[i];
                GameObject instance = Object.Instantiate(prefab);
                instance.hideFlags = HideFlags.HideAndDontSave;
                instance.transform.position = d.Pos;
                instance.transform.localEulerAngles = d.Rotation;
                instance.transform.localScale = d.Scale;

                MeshFilter mf = instance.GetComponentInChildren<MeshFilter>(true);
                Mesh before = mf != null ? mf.sharedMesh : null;
                Bounds bb = before != null ? before.bounds : default;

                EnvBridgePillarFit.Ensure(instance);

                Mesh after = mf != null ? mf.sharedMesh : null;
                if (after != null && after != before)
                {
                    fitted++;
                    Debug.Log(
                        $"[FitBridgePillars] [{d.Index}] {d.ObjName} " +
                        $"boundsY {bb.min.y:F2}..{bb.max.y:F2} → {after.bounds.min.y:F2}..{after.bounds.max.y:F2}");
                }

                Object.DestroyImmediate(instance);
            }

            Debug.Log($"[FitBridgePillars] DrySpawn fitted={fitted}/{qiao.Count}");
            return fitted;
        }

        public static int PreviewMapItemGaps()
        {
            EnsureTerrainInScene();
            Terrain terrain = Terrain.activeTerrain;
            if (terrain == null || terrain.terrainData == null)
            {
                Debug.LogError("[FitBridgePillars] No Terrain");
                return 0;
            }

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            if (prefab == null)
                return 0;

            MeshFilter prefabMf = prefab.GetComponentInChildren<MeshFilter>(true);
            if (prefabMf == null || prefabMf.sharedMesh == null)
                return 0;

            if (!TryLoadQiaoItems(out List<MapItemData> qiao))
                return 0;

            TerrainData td = terrain.terrainData;
            Vector3 origin = terrain.transform.position;
            Vector3 tSize = td.size;
            Mesh mesh = prefabMf.sharedMesh;
            Vector3[] verts = mesh.vertices;
            float yCut = mesh.bounds.min.y
                         + mesh.bounds.size.y * EnvBridgePillarFit.PillarYMaxNormalized;

            // Prefab child local TRS (Prefab_Docks)
            Transform child = prefabMf.transform;
            Matrix4x4 childLocal = Matrix4x4.TRS(
                child.localPosition, child.localRotation, child.localScale);

            int logged = 0;
            float maxGap = 0f;
            for (int i = 0; i < qiao.Count; i++)
            {
                MapItemData d = qiao[i];
                Matrix4x4 root = Matrix4x4.TRS(d.Pos, Quaternion.Euler(d.Rotation), d.Scale);
                Matrix4x4 localToWorld = root * childLocal;

                float gapSum = 0f;
                int pillarN = 0;
                float itemMax = 0f;

                // 世界 XZ 聚类：累加 xz、追踪最低 y
                var cells = new Dictionary<long, Vector3>();
                var counts = new Dictionary<long, int>();
                for (int v = 0; v < verts.Length; v++)
                {
                    if (verts[v].y > yCut)
                        continue;
                    Vector3 w = localToWorld.MultiplyPoint3x4(verts[v]);
                    long key = ((long)Mathf.FloorToInt(w.x / 0.35f) << 32)
                               ^ (uint)Mathf.FloorToInt(w.z / 0.35f);
                    if (!cells.TryGetValue(key, out Vector3 acc))
                    {
                        cells[key] = w;
                        counts[key] = 1;
                    }
                    else
                    {
                        acc.x += w.x;
                        acc.z += w.z;
                        if (w.y < acc.y) acc.y = w.y;
                        cells[key] = acc;
                        counts[key] = counts[key] + 1;
                    }
                }

                foreach (var kv in cells)
                {
                    int n = counts[kv.Key];
                    if (n < 8)
                        continue;
                    Vector3 acc = kv.Value;
                    float bx = acc.x / n;
                    float bz = acc.z / n;
                    float by = acc.y;
                    float nx = (bx - origin.x) / tSize.x;
                    float nz = (bz - origin.z) / tSize.z;
                    if (nx < 0f || nx > 1f || nz < 0f || nz > 1f)
                        continue;
                    float ty = origin.y + td.GetInterpolatedHeight(nx, nz);
                    float gap = by - (ty - EnvBridgePillarFit.BuryMeters);
                    if (gap <= 0.05f)
                        continue;
                    gapSum += gap;
                    pillarN++;
                    if (gap > itemMax) itemMax = gap;
                }

                if (pillarN == 0)
                    continue;

                logged++;
                if (itemMax > maxGap) maxGap = itemMax;
                Debug.Log(
                    $"[FitBridgePillars] gap [{d.Index}] {d.ObjName} " +
                    $"pillars={pillarN} avgGap={gapSum / pillarN:F2} maxGap={itemMax:F2} deckY={d.Pos.y:F2}");
            }

            Debug.Log($"[FitBridgePillars] Preview done items={logged} globalMaxGap={maxGap:F2}");
            return logged;
        }

        static void EnsureTerrainInScene()
        {
            if (Terrain.activeTerrain != null && Terrain.activeTerrain.terrainData != null)
                return;

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(TerrainPrefabPath);
            if (prefab == null)
            {
                Debug.LogError("[FitBridgePillars] Missing " + TerrainPrefabPath);
                return;
            }

            // Reuse existing instance if any
            var existing = Object.FindObjectsByType<Terrain>(FindObjectsInactive.Exclude);
            if (existing != null && existing.Length > 0)
                return;

            GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            instance.name = "IslandTerrain_FitBridgePreview";
            instance.hideFlags = HideFlags.DontSave;
            Debug.Log("[FitBridgePillars] Instantiated IslandTerrain for preview");
        }

        static bool TryLoadQiaoItems(out List<MapItemData> qiao)
        {
            qiao = new List<MapItemData>();
            if (!File.Exists(AllMapItemPath))
            {
                Debug.LogError("[FitBridgePillars] Missing " + AllMapItemPath);
                return false;
            }

            byte[] bytes = File.ReadAllBytes(AllMapItemPath);
            var all = new List<MapItemData>();
            if (!MapItemData.ReadBinaryTo(bytes, all))
                return false;

            for (int i = 0; i < all.Count; i++)
            {
                if (all[i].Parent == "Qiao")
                    qiao.Add(all[i]);
            }

            return qiao.Count > 0;
        }
    }
}
#endif
