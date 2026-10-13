using UnityEngine;
using UnityEditor;
using System.IO;
using System.Collections.Generic;

namespace Game
{
    /// <summary>
    /// 修复所有树 Prefab 的贴地问题（Billboard + LODGroup + LOD0 坐标链）。
    /// 
    /// 坐标链公式：
    ///   Billboard 世界底部 = root.localY + BB.localY + BB.meshBottom
    ///   LOD0 世界底部   = root.localY + LOD0.localY + LODGroup.localReferenceY + LOD0.meshBottom
    /// 
    /// 修复值：
    ///   root.localY → 0
    ///   BB.localY → -(root.oldY + BB.meshBottom)  // 抵消 root 变化后对齐 meshBottom
    ///   LODGroup.localReferenceY → -(LOD0.localY + LOD0.meshBottom)  // 抵消后对齐 meshBottom
    ///   LOD0.localY → 0  // 无偏移
    /// 
    /// 使用：Window > Game > Fix All Tree Ground Alignment
    /// </summary>
    public class FixAllTreeGroundAlignment : EditorWindow
    {
        const string kPrefabsFolder = "Assets/GameMain/Environment/Prefabs/DaoYu1";
        static readonly string[] s_TreeFolders = { "Songshu", "Lvshu", "Yeshu", "Guanmu" };

        struct TreeFixInfo
        {
            public string Path;
            public string Name;
            public float RootY, BbLocalY, BbMeshBottom;
            public float LodRefY, Lod0LocalY, Lod0MeshBottom;
            public float BbWorldBottom, Lod0WorldBottom;
            public float FixRootY, FixBbY, FixLodRefY, FixLod0Y;
        }

        List<TreeFixInfo> _diagnostics = new List<TreeFixInfo>();

        [MenuItem("Window/Game/Fix All Tree Ground Alignment")]
        public static void ShowWindow() => GetWindow<FixAllTreeGroundAlignment>(true, "Fix Tree Ground");

        void OnGUI()
        {
            minSize = new Vector2(560, 300);
            GUILayout.Space(8);
            GUILayout.Label("树 Prefab 完整贴地修复（Billboard + LODGroup + LOD0）", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "坐标链：Billboard世界底部 = rootY + BB.localY + BB.meshBottom\n" +
                "          LOD0世界底部 = rootY + LOD0.localY + LODRef.y + LOD0.meshBottom\n\n" +
                "修复目标：world_bottom = 0（贴地）\n" +
                "修复值：root→0, BB→-(root+BBmeshBot), LODRef→-(LOD0+LOD0meshBot), LOD0→0",
                MessageType.Info);
            GUILayout.Space(10);

            if (GUILayout.Button("1. 扫描并预览", GUILayout.Height(28)))
                ScanAll();

            GUILayout.Space(6);

            if (_diagnostics.Count > 0)
            {
                int broken = 0;
                foreach (var d in _diagnostics)
                    if (Mathf.Abs(d.BbWorldBottom) > 0.05f || Mathf.Abs(d.Lod0WorldBottom) > 0.05f) broken++;
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
                "处理 Songshu/Lvshu/Yeshu/Guanmu 目录下的所有树 Prefab。",
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

                    var info = AnalyzePrefab(prefab, folder);
                    if (info.HasValue)
                    {
                        _diagnostics.Add(info.Value);
                        bool bbBad = Mathf.Abs(info.Value.BbWorldBottom) > 0.05f;
                        bool lodBad = Mathf.Abs(info.Value.Lod0WorldBottom) > 0.05f;
                        string status = (bbBad || lodBad) ? "❌" : "✓";
                        Debug.Log($"[Scan] {folder}/{prefab.name}: " +
                            $"BB.bottom={info.Value.BbWorldBottom:F3} LOD0.bottom={info.Value.Lod0WorldBottom:F3} [{status}]");
                    }
                    else
                    {
                        skipped++;
                    }

                    PrefabUtility.UnloadPrefabContents(prefab);
                }
            }

            int broken = 0;
            foreach (var d in _diagnostics)
                if (Mathf.Abs(d.BbWorldBottom) > 0.05f || Mathf.Abs(d.Lod0WorldBottom) > 0.05f) broken++;

            EditorUtility.DisplayDialog("扫描完成",
                $"扫描: {_diagnostics.Count} 个\n" +
                $"跳过: {skipped} 个\n" +
                $"需要修复: {broken} 个", "OK");
        }

        System.Nullable<TreeFixInfo> AnalyzePrefab(GameObject prefab, string folder)
        {
            var info = new TreeFixInfo();
            info.Path = AssetDatabase.GetAssetPath(prefab);
            info.Name = prefab.name;

            // Root localY
            info.RootY = prefab.transform.localPosition.y;

            // LODGroup
            var lodGroup = prefab.GetComponentInChildren<LODGroup>(true);
            if (lodGroup == null) return null;
            info.LodRefY = lodGroup.localReferencePoint.y;

            // LOD_Billboard
            var bb = FindChild(prefab.transform, "LOD_Billboard");
            if (bb != null)
            {
                info.BbLocalY = bb.localPosition.y;
                var mf = bb.GetComponent<MeshFilter>();
                if (mf != null && mf.sharedMesh != null)
                    info.BbMeshBottom = mf.sharedMesh.bounds.center.y - mf.sharedMesh.bounds.extents.y;
            }

            // LOD0 renderer (MeshRenderer on root object, not Billboard)
            // LODGroup is on a child GO (e.g., "Grand Fir 7")
            // Find LOD0 mesh by getting LOD0 renderer, then its GameObject's MeshFilter
            var lods = lodGroup.GetLODs();
            if (lods == null || lods.Length == 0) return null;

            // Find LOD0 (first LOD level with screenRelativeHeight > 0)
            Renderer lod0Renderer = null;
            for (int i = 0; i < lods[0].renderers.Length; i++)
            {
                if (lods[0].renderers[i] is MeshRenderer mr && mr != null)
                {
                    lod0Renderer = mr;
                    break;
                }
            }

            if (lod0Renderer != null)
            {
                // LOD0 localY is relative to the LODGroup's GameObject
                info.Lod0LocalY = lod0Renderer.transform.localPosition.y;

                var mf = lod0Renderer.GetComponent<MeshFilter>();
                if (mf != null && mf.sharedMesh != null)
                    info.Lod0MeshBottom = mf.sharedMesh.bounds.center.y - mf.sharedMesh.bounds.extents.y;
            }

            // Compute world bottoms
            info.BbWorldBottom = info.RootY + info.BbLocalY + info.BbMeshBottom;
            info.Lod0WorldBottom = info.RootY + info.Lod0LocalY + info.LodRefY + info.Lod0MeshBottom;

            // Fix values
            info.FixRootY = -info.RootY;
            info.FixBbY = -(info.RootY + info.BbMeshBottom);
            info.FixLodRefY = -(info.Lod0LocalY + info.Lod0MeshBottom);
            info.FixLod0Y = -info.Lod0LocalY;

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
                "将批量修复所有树 Prefab 的贴地问题。\n\n建议先备份或确认版本控制！",
                "确定修复", "取消"))
                return;

            int fixedCount = 0, skippedCount = 0;

            foreach (var info in _diagnostics)
            {
                // 检查是否需要修复
                bool bbBad = Mathf.Abs(info.BbWorldBottom) > 0.05f;
                bool lodBad = Mathf.Abs(info.Lod0WorldBottom) > 0.05f;
                if (!bbBad && !lodBad) { skippedCount++; continue; }

                var prefab = PrefabUtility.LoadPrefabContents(info.Path);
                if (prefab == null) { skippedCount++; continue; }

                bool modified = false;

                // 1. Fix root localY
                if (Mathf.Abs(info.FixRootY) > 0.001f)
                {
                    Undo.RegisterFullObjectHierarchyUndo(prefab, "Fix Root Y");
                    prefab.transform.localPosition = new Vector3(0, 0, 0);
                    modified = true;
                }

                // 2. Fix Billboard localY
                var bb = FindChild(prefab.transform, "LOD_Billboard");
                if (bb != null && Mathf.Abs(info.FixBbY) > 0.001f)
                {
                    Undo.RegisterFullObjectHierarchyUndo(prefab, "Fix Billboard Y");
                    bb.localPosition = new Vector3(0, info.FixBbY, 0);
                    modified = true;
                }

                // 3. Fix LODGroup localReferencePoint
                var lodGroup = prefab.GetComponentInChildren<LODGroup>(true);
                if (lodGroup != null && Mathf.Abs(info.FixLodRefY) > 0.001f)
                {
                    Undo.RegisterFullObjectHierarchyUndo(lodGroup, "Fix LOD Reference Point");
                    SerializedObject so = new SerializedObject(lodGroup);
                    var prop = so.FindProperty("m_LocalReferencePoint");
                    if (prop != null)
                    {
                        Vector3 lrp = lodGroup.localReferencePoint;
                        prop.vector3Value = new Vector3(lrp.x, info.FixLodRefY, lrp.z);
                        so.ApplyModifiedPropertiesWithoutUndo();
                    }
                    modified = true;
                }

                // 4. Fix LOD0 renderer localY
                if (lodGroup != null && Mathf.Abs(info.FixLod0Y) > 0.001f)
                {
                    var lods = lodGroup.GetLODs();
                    if (lods != null && lods.Length > 0)
                    {
                        for (int i = 0; i < lods[0].renderers.Length; i++)
                        {
                            if (lods[0].renderers[i] is MeshRenderer mr && mr != null)
                            {
                                Undo.RegisterFullObjectHierarchyUndo(mr.gameObject, "Fix LOD0 Y");
                                mr.transform.localPosition = new Vector3(0, info.FixLod0Y, 0);
                                modified = true;
                                break;
                            }
                        }
                    }
                }

                if (modified)
                {
                    if (PrefabUtility.SaveAsPrefabAsset(prefab, info.Path))
                    {
                        Debug.Log($"[Fix] {info.Name}: root→{info.FixRootY:F3} BB.y→{info.FixBbY:F3} LODRef→{info.FixLodRefY:F3} LOD0→{info.FixLod0Y:F3}");
                        fixedCount++;
                    }
                    else
                    {
                        skippedCount++;
                    }
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
