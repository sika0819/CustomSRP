using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEditor;

namespace Game
{
    /// <summary>
    /// 修复所有树 Prefab 中 Billboard 节点 localPosition.y，使 Billboard mesh 底部对齐地面（world Y=0）。
    /// 
    /// billboard 世界底部 = billboard.localY + mesh.localAABB.center.y - mesh.localAABB.extent.y
    /// billboard.localY  = -(mesh.localAABB.center.y - mesh.localAABB.extent.y)
    /// 
    /// 与 FixAllTreeGroundAlignment 不同：只处理 Billboard，不碰 LODGroup/LOD0。
    /// </summary>
    public class FixBillboardY : EditorWindow
    {
        const string kPrefabsFolder = "Assets/GameMain/Environment/Prefabs/DaoYu1";
        static readonly string[] s_TreeFolders = { "Songshu", "Lvshu", "Yeshu", "Guanmu" };

        struct FixInfo
        {
            public string PrefabPath;
            public string BillboardName; // "LOD_Billboard"
            public float OldLocalY;
            public float NewLocalY;
            public float MeshBottom; // world bottom before fix
        }

        List<FixInfo> _diagnostics = new List<FixInfo>();

        [MenuItem("Window/Game/Fix Billboard Y (贴地)")]
        public static void ShowWindow() => GetWindow<FixBillboardY>(true, "Fix Billboard Y");

        void OnGUI()
        {
            minSize = new Vector2(500, 280);
            GUILayout.Space(8);
            GUILayout.Label("Billboard 贴地修复", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Billboard mesh 底部 = billboard.localY + mesh.localAABB.center.y - mesh.localAABB.extent.y\n\n" +
                "修复目标：billboard 世界底部 = 0（与 LOD0 对齐）\n" +
                "newBillboard.localY = -(mesh.localAABB.center.y - mesh.localAABB.extent.y)",
                MessageType.Info);
            GUILayout.Space(10);

            if (GUILayout.Button("1. 扫描并预览", GUILayout.Height(28)))
                ScanAll();

            GUILayout.Space(6);

            if (_diagnostics.Count > 0)
            {
                int broken = 0;
                foreach (var d in _diagnostics)
                    if (Mathf.Abs(d.OldLocalY - d.NewLocalY) > 0.001f) broken++;
                EditorGUILayout.LabelField($"待修复: {broken} / {_diagnostics.Count} 个 Prefab");
            }

            GUILayout.Space(6);

            GUI.backgroundColor = new Color(1f, 0.8f, 0.8f);
            if (GUILayout.Button("2. 批量修复所有树 Prefab", GUILayout.Height(40)))
                FixAll();
            GUI.backgroundColor = Color.white;

            GUILayout.Space(8);
            EditorGUILayout.HelpBox(
                "⚠ 会直接覆盖 Prefab 文件。建议使用版本控制或有备份。\n" +
                "处理 Songshu/Lvshu/Yeshu/Guanmu 目录下的所有树 Prefab。\n" +
                "只修 Billboard，不碰 LODGroup / LOD0。",
                MessageType.Warning);
        }

        void ScanAll()
        {
            _diagnostics.Clear();
            int skipped = 0;

            foreach (var folder in s_TreeFolders)
            {
                string folderPath = Path.Combine(kPrefabsFolder, folder);
                if (!Directory.Exists(folderPath)) continue;

                var files = Directory.GetFiles(folderPath, "*.prefab", SearchOption.AllDirectories);
                foreach (var file in files)
                {
                    var prefab = PrefabUtility.LoadPrefabContents(file);
                    if (prefab == null) { skipped++; continue; }

                    var info = AnalyzePrefab(prefab, folder, file);
                    if (info.HasValue)
                    {
                        _diagnostics.Add(info.Value);
                        bool needsFix = Mathf.Abs(info.Value.OldLocalY - info.Value.NewLocalY) > 0.001f;
                        string status = needsFix ? "❌" : "✓";
                        Debug.Log($"[Scan] {folder}/{prefab.name}: BB.localY {info.Value.OldLocalY:F4} → {info.Value.NewLocalY:F4} "
                            + $"(meshBottom={info.Value.MeshBottom:F4}) [{status}]");
                    }
                    else
                    {
                        skipped++;
                    }

                    PrefabUtility.UnloadPrefabContents(prefab);
                }
            }

            EditorUtility.DisplayDialog("扫描完成",
                $"扫描: {_diagnostics.Count} 个\n" +
                $"跳过: {skipped} 个\n" +
                $"需要修复: {CountBroken()} 个", "OK");
        }

        int CountBroken()
        {
            int broken = 0;
            foreach (var d in _diagnostics)
                if (Mathf.Abs(d.OldLocalY - d.NewLocalY) > 0.001f) broken++;
            return broken;
        }

        System.Nullable<FixInfo> AnalyzePrefab(GameObject prefab, string folder, string filePath)
        {
            var info = new FixInfo();
            info.PrefabPath = filePath;
            info.BillboardName = "LOD_Billboard";

            // Find LOD_Billboard child
            Transform bb = FindChild(prefab.transform, "LOD_Billboard");
            if (bb == null) return null;

            info.OldLocalY = bb.localPosition.y;

            // Get billboard mesh bounds
            var mf = bb.GetComponent<MeshFilter>();
            if (mf == null || mf.sharedMesh == null) return null;

            var bounds = mf.sharedMesh.bounds;
            float meshBottom = bounds.center.y - bounds.extents.y;
            info.MeshBottom = info.OldLocalY + meshBottom; // current world bottom

            // Fix: newLocalY = -meshBottom (so world bottom = 0)
            info.NewLocalY = -meshBottom;

            return info;
        }

        void FixAll()
        {
            if (_diagnostics.Count == 0)
            {
                EditorUtility.DisplayDialog("错误", "请先点击「扫描并预览」", "OK");
                return;
            }

            if (!EditorUtility.DisplayDialog("确认修复",
                "将批量修复所有树 Prefab 的 Billboard localPosition.y。\n\n建议先备份或确认版本控制！",
                "确定修复", "取消"))
                return;

            int fixedCount = 0, skippedCount = 0;

            foreach (var info in _diagnostics)
            {
                bool needsFix = Mathf.Abs(info.OldLocalY - info.NewLocalY) > 0.001f;
                if (!needsFix) { skippedCount++; continue; }

                var prefab = PrefabUtility.LoadPrefabContents(info.PrefabPath);
                if (prefab == null) { skippedCount++; continue; }

                Transform bb = FindChild(prefab.transform, "LOD_Billboard");
                if (bb == null) { PrefabUtility.UnloadPrefabContents(prefab); skippedCount++; continue; }

                Undo.RegisterFullObjectHierarchyUndo(bb, "Fix Billboard Y");
                bb.localPosition = new Vector3(0, info.NewLocalY, 0);

                if (PrefabUtility.SaveAsPrefabAsset(prefab, info.PrefabPath))
                {
                    Debug.Log($"[Fix] {prefab.name}: BB.localY {info.OldLocalY:F4} → {info.NewLocalY:F4} "
                        + $"(meshBottom={info.MeshBottom:F4})");
                    fixedCount++;
                }
                else
                {
                    skippedCount++;
                }

                PrefabUtility.UnloadPrefabContents(prefab);
            }

            AssetDatabase.Refresh();
            _diagnostics.Clear();

            EditorUtility.DisplayDialog("修复完成",
                $"修复: {fixedCount} 个\n" +
                $"跳过: {skippedCount} 个", "OK");
        }

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
