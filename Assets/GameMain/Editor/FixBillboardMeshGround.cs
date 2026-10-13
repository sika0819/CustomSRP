using UnityEngine;
using UnityEditor;
using System.IO;
using System.Collections.Generic;

namespace Game
{
    /// <summary>
    /// 修复 BillboardMesh 树底部悬空问题。
    /// 
    /// BillboardMesh 的 mesh.bounds.bottom ≈ 4.2，导致树根不在 Y=0。
    /// 本工具直接通过 Unity API 修改 Prefab 中 Billboard 节点的 localPosition.y，
    /// 使树根贴地。
    /// 
    /// 使用：Window > Game > Fix Tree Billboard Positions
    /// </summary>
    public class FixTreeBillboardPositions : EditorWindow
    {
        const string kPrefabsFolder = "Assets/GameMain/Environment/Prefabs/DaoYu1";
        const string kMeshesFolder = "Assets/GameMain/Environment/Models";

        // mesh GUID -> 底部偏移量
        Dictionary<string, float> _offsetCache = new Dictionary<string, float>();

        [MenuItem("Window/Game/Fix Tree Billboard Positions")]
        public static void ShowWindow() => GetWindow<FixTreeBillboardPositions>(true, "Fix Tree Billboard");

        void OnGUI()
        {
            minSize = new Vector2(460, 260);
            GUILayout.Space(8);
            GUILayout.Label("BillboardMesh 树 Prefab 贴地修复", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "BillboardMesh 的 mesh.bounds.bottom ≈ 4.2，导致树底悬空。\n\n" +
                "本工具修改 Prefab 中 LOD_Billboard 节点的 localPosition.y，\n" +
                "使 Billboard mesh 底部对齐树根（Y=0）。",
                MessageType.Info);
            GUILayout.Space(10);

            if (GUILayout.Button("1. 扫描并预览", GUILayout.Height(28)))
                ScanPrefabs();

            GUILayout.Space(6);

            GUI.backgroundColor = new Color(1f, 0.8f, 0.8f);
            if (GUILayout.Button("2. 批量修复所有树 Prefab", GUILayout.Height(40)))
                FixAllPrefabs();
            GUI.backgroundColor = Color.white;

            GUILayout.Space(8);
            EditorGUILayout.HelpBox(
                "⚠ 会直接覆盖 Prefab 文件。建议使用版本控制或有备份。\n" +
                "处理 Songshu / Lvshu / Yeshu / Guanmu 目录下的 Prefab。",
                MessageType.Warning);
        }

        void ScanPrefabs()
        {
            // 构建 mesh -> 偏移量 缓存
            _offsetCache.Clear();
            var meshGuids = AssetDatabase.FindAssets("t:Mesh", new[] { kMeshesFolder });
            foreach (var guid in meshGuids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                string name = Path.GetFileName(path);
                if (!name.Contains("BillboardMesh") && !name.Contains("Billboard"))
                    continue;
                var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(path);
                if (mesh == null) continue;
                var b = mesh.bounds;
                float bottom = b.center.y - b.extents.y;
                if (bottom < 0.01f)
                    _offsetCache[guid] = -bottom; // 存储需要下移的量
            }

            // 扫描 Prefab
            var treeFolders = new[] { "Songshu", "Lvshu", "Yeshu", "Guanmu" };
            int needFix = 0;

            foreach (var folder in treeFolders)
            {
                string folderPath = Path.Combine(kPrefabsFolder, folder);
                if (!Directory.Exists(folderPath)) continue;

                var files = Directory.GetFiles(folderPath, "*.prefab", SearchOption.AllDirectories);
                foreach (var file in files)
                {
                    var prefab = PrefabUtility.LoadPrefabContents(file);
                    if (prefab == null) continue;

                    var lodBB = FindChild(prefab.transform, "LOD_Billboard");
                    if (lodBB == null) { PrefabUtility.UnloadPrefabContents(prefab); continue; }

                    var meshFilter = lodBB.GetComponent<MeshFilter>();
                    if (meshFilter == null || meshFilter.sharedMesh == null)
                    { PrefabUtility.UnloadPrefabContents(prefab); continue; }

                    string guid = AssetDatabase.AssetPathToGUID(
                        AssetDatabase.GetAssetPath(meshFilter.sharedMesh));

                    if (_offsetCache.TryGetValue(guid, out float offset) && offset > 0.01f)
                    {
                        float currentY = lodBB.localPosition.y;
                        float targetY = currentY - offset;
                        needFix++;
                        Debug.Log($"[Scan] {folder}/{prefab.name}: LOD_Billboard.y {currentY:F2} → {targetY:F2} (offset={offset:F2})");
                    }

                    PrefabUtility.UnloadPrefabContents(prefab);
                }
            }

            EditorUtility.DisplayDialog("扫描完成", $"需要修复: {needFix} 个 Prefab", "OK");
        }

        void FixAllPrefabs()
        {
            if (!EditorUtility.DisplayDialog("确认修复",
                "将批量修改所有树 Prefab 中 Billboard 节点的 localPosition.y，修复底部悬空。\n\n建议先备份！",
                "确定修复", "取消"))
                return;

            // 构建缓存
            _offsetCache.Clear();
            var meshGuids = AssetDatabase.FindAssets("t:Mesh", new[] { kMeshesFolder });
            foreach (var guid in meshGuids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                string name = Path.GetFileName(path);
                if (!name.Contains("BillboardMesh") && !name.Contains("Billboard"))
                    continue;
                var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(path);
                if (mesh == null) continue;
                var b = mesh.bounds;
                float bottom = b.center.y - b.extents.y;
                if (bottom < 0.01f)
                    _offsetCache[guid] = -bottom;
            }

            var treeFolders = new[] { "Songshu", "Lvshu", "Yeshu", "Guanmu" };
            int fixedCount = 0;
            int skipCount = 0;

            foreach (var folder in treeFolders)
            {
                string folderPath = Path.Combine(kPrefabsFolder, folder);
                if (!Directory.Exists(folderPath)) continue;

                var files = Directory.GetFiles(folderPath, "*.prefab", SearchOption.AllDirectories);
                foreach (var file in files)
                {
                    var prefab = PrefabUtility.LoadPrefabContents(file);
                    if (prefab == null) { skipCount++; continue; }

                    var lodBB = FindChild(prefab.transform, "LOD_Billboard");
                    if (lodBB == null) { PrefabUtility.UnloadPrefabContents(prefab); skipCount++; continue; }

                    var meshFilter = lodBB.GetComponent<MeshFilter>();
                    if (meshFilter == null || meshFilter.sharedMesh == null)
                    { PrefabUtility.UnloadPrefabContents(prefab); skipCount++; continue; }

                    string guid = AssetDatabase.AssetPathToGUID(
                        AssetDatabase.GetAssetPath(meshFilter.sharedMesh));

                    if (!_offsetCache.TryGetValue(guid, out float offset) || offset <= 0.01f)
                    { PrefabUtility.UnloadPrefabContents(prefab); skipCount++; continue; }

                    float currentY = lodBB.localPosition.y;
                    float targetY = currentY - offset;
                    lodBB.localPosition = new Vector3(0, targetY, 0);

                    PrefabUtility.SaveAsPrefabAsset(prefab, file);
                    PrefabUtility.UnloadPrefabContents(prefab);

                    fixedCount++;
                    Debug.Log($"[Fix] {folder}/{Path.GetFileNameWithoutExtension(file)}: y {currentY:F2} → {targetY:F2}");
                }
            }

            AssetDatabase.Refresh();
            EditorUtility.DisplayDialog("修复完成",
                $"修复: {fixedCount} 个\n跳过: {skipCount} 个", "OK");
            Debug.Log($"[Fix] 完成: 修复 {fixedCount}, 跳过 {skipCount}");
        }

        /// <summary>
        /// 按名称查找子节点（递归）。
        /// </summary>
        static Transform FindChild(Transform parent, string name)
        {
            if (parent == null) return null;
            if (parent.name == name) return parent;
            foreach (Transform child in parent)
            {
                var result = FindChild(child, name);
                if (result != null) return result;
            }
            return null;
        }
    }
}
