using UnityEngine;
using UnityEditor;
using System.IO;
using System.Collections.Generic;

namespace Game
{
    /// <summary>
    /// 批量修复 BillboardMesh 树 Prefab 中 LOD_Billboard 节点的 localPosition.y
    /// 使 Billboard 底部对齐树根（世界 Y=0）。
    /// 
    /// BillboardMesh 的 localAABB.bottom 通常 ≈ -4.2，需要将 LOD_Billboard.y 
    /// 设置为 -localAABB.bottom 使其贴地。
    /// 
    /// 使用：Window > Game > Fix All Tree Billboard Y
    /// </summary>
    public class FixAllTreeBillboardY : EditorWindow
    {
        // mesh GUID -> localAABB bottom (负数表示需要上移)
        static readonly Dictionary<string, float> s_MeshBottomOffset = new Dictionary<string, float>();
        
        static readonly string[] s_TreeFolders = { "Songshu", "Lvshu", "Yeshu", "Guanmu" };
        static readonly string s_PrefabFolder = "Assets/GameMain/Environment/Prefabs/DaoYu1";
        static readonly string s_MeshFolder = "Assets/GameMain/Environment/Models";

        [MenuItem("Window/Game/Fix All Tree Billboard Y")]
        public static void ShowWindow() => GetWindow<FixAllTreeBillboardY>(true, "Fix Tree Billboard Y");

        void OnGUI()
        {
            minSize = new Vector2(420, 220);
            GUILayout.Space(8);
            GUILayout.Label("批量修复所有树 Billboard 贴地", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "遍历所有 Songshu/Lvshu/Yeshu/Guanmu Prefab，\n" +
                "根据其 BillboardMesh 的 localAABB.bottom 计算偏移量，\n" +
                "自动修正 LOD_Billboard 节点的 localPosition.y。",
                MessageType.Info);
            GUILayout.Space(10);

            if (GUILayout.Button("扫描并预览修复", GUILayout.Height(30)))
                ScanAndPreview();

            GUILayout.Space(6);

            GUI.backgroundColor = new Color(1f, 0.8f, 0.8f);
            if (GUILayout.Button("执行批量修复", GUILayout.Height(40)))
                FixAll();
            GUI.backgroundColor = Color.white;

            GUILayout.Space(8);
            EditorGUILayout.HelpBox(
                "⚠ 会直接覆盖 Prefab 文件。修复后需重新加载场景。",
                MessageType.Warning);
        }

        static void ScanAndPreview()
        {
            BuildMeshCache();
            
            int count = 0;
            foreach (var folder in s_TreeFolders)
            {
                var folderPath = Path.Combine(s_PrefabFolder, folder);
                if (!Directory.Exists(folderPath)) continue;

                foreach (var file in Directory.GetFiles(folderPath, "*.prefab", SearchOption.AllDirectories))
                {
                    var yaml = File.ReadAllText(file);
                    var (currentY, targetY) = GetBillboardYOffset(yaml);
                    if (targetY != currentY && Mathf.Abs(targetY - currentY) > 0.01f)
                    {
                        count++;
                        string name = Path.GetFileNameWithoutExtension(file);
                        Debug.Log($"[Preview] {folder}/{name}: LOD_Billboard.y {currentY:F3} → {targetY:F3}");
                    }
                }
            }
            
            Debug.Log($"[Scan] 共 {count} 个 Prefab 需要修复");
            EditorUtility.DisplayDialog("扫描完成", $"需要修复: {count} 个 Prefab", "OK");
        }

        static void FixAll()
        {
            if (!EditorUtility.DisplayDialog("确认修复",
                "将批量修改所有树 Prefab 中 Billboard 的 localPosition.y。\n\n建议先备份！",
                "确定修复", "取消"))
                return;

            BuildMeshCache();

            int fixedCount = 0;
            int skipCount = 0;

            foreach (var folder in s_TreeFolders)
            {
                var folderPath = Path.Combine(s_PrefabFolder, folder);
                if (!Directory.Exists(folderPath)) continue;

                foreach (var file in Directory.GetFiles(folderPath, "*.prefab", SearchOption.AllDirectories))
                {
                    var yaml = File.ReadAllText(file);
                    var (currentY, targetY) = GetBillboardYOffset(yaml);

                    if (Mathf.Abs(targetY - currentY) < 0.01f)
                    {
                        skipCount++;
                        continue;
                    }

                    // 替换 LOD_Billboard 的 m_LocalPosition.y
                    yaml = SetBillboardLocalY(yaml, currentY, targetY);
                    File.WriteAllText(file, yaml);
                    fixedCount++;
                }
            }

            AssetDatabase.Refresh();
            EditorUtility.DisplayDialog("修复完成",
                $"修复: {fixedCount} 个\n跳过: {skipCount} 个", "OK");
            Debug.Log($"[Fix] 完成: 修复 {fixedCount}, 跳过 {skipCount}");
        }

        /// <summary>
        /// 构建 mesh GUID -> localAABB.bottom 的缓存。
        /// bottom < 0 表示需要下移 LOD_Billboard。
        /// </summary>
        static void BuildMeshCache()
        {
            s_MeshBottomOffset.Clear();
            
            foreach (var file in Directory.GetFiles(s_MeshFolder, "*.asset", SearchOption.AllDirectories))
            {
                string name = Path.GetFileName(file);
                if (!name.Contains("BillboardMesh") && !name.Contains("Billboard"))
                    continue;

                var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(file);
                if (mesh == null) continue;

                string guid = AssetDatabase.AssetPathToGUID(file);
                var b = mesh.bounds;
                float bottom = b.center.y - b.extents.y;
                s_MeshBottomOffset[guid] = bottom;
            }
        }

        /// <summary>
        /// 从 Prefab YAML 中提取 BillboardMesh GUID 和当前 LOD_Billboard.localPosition.y，
        /// 返回 (currentY, targetY)。
        /// </summary>
        static (float currentY, float targetY) GetBillboardYOffset(string yaml)
        {
            // 1. 找到 LOD_Billboard GameObject
            int lodIdx = yaml.IndexOf("m_Name: LOD_Billboard");
            if (lodIdx < 0) return (0, 0);

            // 2. 找到其引用的 mesh (m_Mesh: {fileID: 4300000, guid: xxxx)
            int meshStart = yaml.LastIndexOf("m_Mesh:", lodIdx);
            if (meshStart < 0) return (0, 0);

            int guidStart = yaml.IndexOf("guid: ", meshStart);
            if (guidStart < 0) return (0, 0);
            guidStart += 6;
            int guidEnd = yaml.IndexOf(",", guidStart);
            if (guidEnd < 0) guidEnd = yaml.IndexOf("}", guidStart);
            string meshGuid = yaml.Substring(guidStart, guidEnd - guidStart).Trim();

            // 3. 找到对应的 Transform (向上找 !u!4 &)
            int transformStart = yaml.LastIndexOf("--- !u!4 &", lodIdx);
            if (transformStart < 0) return (0, 0);

            // 4. 提取 m_LocalPosition.y
            int yIdx = yaml.IndexOf("m_LocalPosition:", transformStart);
            if (yIdx < 0 || yIdx > transformStart + 1000) return (0, 0);

            int yValStart = yaml.IndexOf("y: ", yIdx);
            if (yValStart < 0) return (0, 0);
            yValStart += 3;
            int yValEnd = yaml.IndexOf(",", yValStart);
            if (yValEnd < 0) yValEnd = yaml.IndexOf("}", yValStart);

            string yStr = yaml.Substring(yValStart, yValEnd - yValStart).Trim();
            if (!float.TryParse(yStr, out float currentY)) currentY = 0f;

            // 5. 计算目标 Y
            float targetY = currentY;
            if (s_MeshBottomOffset.TryGetValue(meshGuid, out float bottom) && bottom < -0.01f)
            {
                targetY = currentY - bottom; // 下移 bottom (bottom 是负数)
            }

            return (currentY, targetY);
        }

        /// <summary>
        /// 修改 YAML 中 LOD_Billboard 的 m_LocalPosition.y。
        /// </summary>
        static string SetBillboardLocalY(string yaml, float oldY, float newY)
        {
            // 找到 m_Name: LOD_Billboard
            int lodIdx = yaml.IndexOf("m_Name: LOD_Billboard");
            if (lodIdx < 0) return yaml;

            // 向上找对应的 Transform
            int transformStart = yaml.LastIndexOf("--- !u!4 &", lodIdx);
            if (transformStart < 0) return yaml;

            // 找 m_LocalPosition.y
            int yIdx = yaml.IndexOf("m_LocalPosition:", transformStart);
            if (yIdx < 0 || yIdx > transformStart + 1000) return yaml;

            int yValStart = yaml.IndexOf("y: ", yIdx);
            if (yValStart < 0) return yaml;
            yValStart += 3;
            int yValEnd = yaml.IndexOf(",", yValStart);
            if (yValEnd < 0) yValEnd = yaml.IndexOf("}", yValStart);

            string oldYStr = yaml.Substring(yValStart, yValEnd - yValStart).Trim();
            string newYStr = newY.ToString("F6");

            yaml = yaml.Replace($"y: {oldYStr}", $"y: {newYStr}");
            return yaml;
        }
    }
}
