using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEditor;

namespace Game
{
    /// <summary>
    /// 修复 BillboardMesh 树底部悬空问题。
    /// 
    /// BillboardMesh 的顶点局部坐标通常 center.y > extent.y（树根在原点上方），
    /// 导致放置时树底部悬空约 4 单位。
    /// 
    /// 本工具遍历所有 *BillboardMesh.asset，读取其 localAABB，计算底部偏移
    /// (center.y - extent.y)，然后将所有顶点下移该偏移量，使树根位于 Y=0。
    /// 
    /// 使用：Window > Game > Fix BillboardMesh Ground Offset
    /// </summary>
    public class FixBillboardMeshGroundOffset : EditorWindow
    {
        const string kBillboardMeshFolder = "Assets/GameMain/Environment/Models";
        const string kBillboardMeshPrefabsFolder = "Assets/GameMain/Environment/Prefabs/DaoYu1";

        Vector3 _previewOffset = Vector3.zero;
        bool _previewMode = false;
        Mesh _previewMesh;
        GameObject _previewGO;

        [MenuItem("Window/Game/Fix BillboardMesh Ground Offset")]
        public static void ShowWindow()
        {
            var window = GetWindow<FixBillboardMeshGroundOffset>(true, "Fix BillboardMesh Ground", true);
            window.minSize = new Vector2(400, 300);
        }

        void OnGUI()
        {
            GUILayout.Label("BillboardMesh 底部贴地修复工具", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "BillboardMesh 的顶点局部坐标底部不在 Y=0，导致树放置时悬空。\n" +
                "本工具将所有顶点下移到底部偏移量，使树根贴地。",
                MessageType.Info);

            EditorGUILayout.Space();

            if (GUILayout.Button("扫描并列出需要修复的 BillboardMesh", GUILayout.Height(30)))
            {
                ScanAndListBillboards();
            }

            EditorGUILayout.Space();

            if (GUILayout.Button("批量修复所有 BillboardMesh（原地覆盖）", GUILayout.Height(35)))
            {
                FixAllBillboardMeshes();
            }

            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(
                "修复前建议先备份工程。\n" +
                "修改后需重新导入使用该 Mesh 的 Prefab。",
                MessageType.Warning);
        }

        void ScanAndListBillboards()
        {
            var guids = AssetDatabase.FindAssets("t:Mesh", new[] { kBillboardMeshFolder });
            int needFix = 0;

            foreach (var guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!Path.GetFileName(path).Contains("BillboardMesh") &&
                    !Path.GetFileName(path).Contains("Billboard"))
                    continue;

                var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(path);
                if (mesh == null) continue;

                var bounds = mesh.bounds;
                float bottomY = bounds.center.y - bounds.extents.y;
                if (bottomY < -0.01f)
                {
                    needFix++;
                    float offset = -bottomY;
                    Debug.Log($"[BillboardFix] 需要修复: {path}\n" +
                               $"  localAABB center.y={bounds.center.y:F3}, extent.y={bounds.extents.y:F3}\n" +
                               $"  底部 Y={bottomY:F3}, 需下移 offset={offset:F3}");
                }
            }

            if (needFix == 0)
                Debug.Log("[BillboardFix] 没有发现需要修复的 BillboardMesh");
            else
                Debug.Log($"[BillboardFix] 共 {needFix} 个 BillboardMesh 需要修复");
        }

        void FixAllBillboardMeshes()
        {
            var guids = AssetDatabase.FindAssets("t:Mesh", new[] { kBillboardMeshFolder });
            int fixedCount = 0;
            int skipCount = 0;

            foreach (var guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                string fileName = Path.GetFileName(path);
                if (!fileName.Contains("BillboardMesh") && !fileName.Contains("Billboard"))
                    continue;

                var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(path);
                if (mesh == null) continue;

                var bounds = mesh.bounds;
                float bottomY = bounds.center.y - bounds.extents.y;

                // 底部已在 Y=0 附近（或高于 0），跳过
                if (bottomY >= -0.01f)
                {
                    skipCount++;
                    continue;
                }

                float offset = -bottomY; // 需要下移的量

                // 创建修复后的网格
                var fixedMesh = FixMeshYOffset(mesh, offset);
                if (fixedMesh == null)
                {
                    Debug.LogWarning($"[BillboardFix] 跳过（无法复制）: {path}");
                    continue;
                }

                // 覆盖保存
                string meshDir = Path.GetDirectoryName(path);
                string meshName = Path.GetFileNameWithoutExtension(path);
                string tempPath = Path.Combine(meshDir, "__temp_" + meshName + ".asset");

                // 先写到临时路径
                AssetDatabase.CreateAsset(fixedMesh, tempPath);
                AssetDatabase.SaveAssets();

                // 再移动覆盖原文件
                string backupPath = path + ".bak";
                if (File.Exists(backupPath))
                    File.Delete(backupPath);
                File.Copy(path, backupPath);

                AssetDatabase.DeleteAsset(path);
                AssetDatabase.MoveAsset(tempPath, path);
                AssetDatabase.SaveAssets();

                fixedCount++;
                Debug.Log($"[BillboardFix] 修复完成: {path}\n" +
                          $"  原底部 Y={bottomY:F3}, 下移 offset={offset:F3}");

                // 清理备份
                if (File.Exists(backupPath))
                    File.Delete(backupPath);
            }

            AssetDatabase.Refresh();
            Debug.Log($"[BillboardFix] 完成！修复 {fixedCount} 个，跳过 {skipCount} 个");
            EditorUtility.DisplayDialog(
                "BillboardMesh 修复完成",
                $"修复: {fixedCount} 个\n跳过（已正确或无法处理）: {skipCount} 个",
                "OK");
        }

        /// <summary>
        /// 将网格所有顶点沿 Y 轴下移指定偏移量，并更新 bounds。
        /// </summary>
        static Mesh FixMeshYOffset(Mesh source, float yOffset)
        {
            if (!source.isReadable)
            {
                Debug.LogWarning($"[BillboardFix] Mesh 不可读，跳过: {source.name}");
                return null;
            }

            var mesh = Instantiate(source);
            mesh.name = source.name;

            Vector3[] verts = mesh.vertices;
            for (int i = 0; i < verts.Length; i++)
                verts[i].y -= yOffset;
            mesh.vertices = verts;

            // 重建 bounds（Unity 会自动计算，但显式设置更稳）
            mesh.RecalculateBounds();
            mesh.RecalculateNormals();

            return mesh;
        }
    }
}
