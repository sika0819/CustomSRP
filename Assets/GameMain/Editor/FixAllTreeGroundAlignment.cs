using UnityEngine;
using UnityEditor;
using System.IO;
using System.Collections.Generic;

namespace Game
{
    /// <summary>
    /// 对齐 Boluo 树 Prefab 约定（不改 mesh）：
    ///   - root / LOD_* / LOD_Billboard 的 localPosition.y → 0（若 Boluo 同名节点非 0 则保留需走 AlignTreePrefabsToBoluo）
    ///   - LODGroup.localReferencePoint → 渲染器包围盒中心（相对 root）
    ///
    /// 禁止再用 localY = -meshBottom（会把树抬离 Boluo 埋干约定）。
    /// 优先使用：Window > Game > Align Tree Prefabs To Boluo（逐字段抄 Boluo）。
    /// 本工具作离线兜底：全 0 + 重算 LRP。
    /// </summary>
    public class FixAllTreeGroundAlignment : EditorWindow
    {
        const string kPrefabsFolder = "Assets/GameMain/Environment/Prefabs/DaoYu1";
        static readonly string[] s_TreeFolders = { "Songshu", "Lvshu", "Yeshu", "Guanmu" };

        [MenuItem("Window/Game/Fix All Tree Ground Alignment")]
        public static void ShowWindow() => GetWindow<FixAllTreeGroundAlignment>(true, "Fix Tree Ground");

        public static int AutoScanAndFix(bool dryRun)
        {
            int need = 0, fixedCount = 0;
            foreach (var folder in s_TreeFolders)
            {
                string folderPath = Path.Combine(kPrefabsFolder, folder);
                if (!Directory.Exists(folderPath)) continue;
                foreach (var file in Directory.GetFiles(folderPath, "*.prefab", SearchOption.AllDirectories))
                {
                    string assetPath = file.Replace('\\', '/');
                    if (NeedsFix(assetPath))
                    {
                        need++;
                        if (!dryRun && ApplyBoluoConvention(assetPath))
                            fixedCount++;
                    }
                }
            }

            return dryRun ? need : fixedCount;
        }

        void OnGUI()
        {
            minSize = new Vector2(520, 220);
            GUILayout.Space(8);
            GUILayout.Label("树 Prefab → Boluo 约定（localY=0 + LRP=包围盒中心）", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "对齐 Boluo：子节点 localY=0，LODGroup.localReferencePoint=渲染包围盒中心。\n" +
                "不改 mesh。棕榈等 Boluo 本身 rootY≠0 的，请用 Align Tree Prefabs To Boluo。",
                MessageType.Info);
            GUILayout.Space(8);
            if (GUILayout.Button("扫描需要修复的数量", GUILayout.Height(28)))
            {
                int n = AutoScanAndFix(dryRun: true);
                EditorUtility.DisplayDialog("扫描", $"需要修复: {n}", "OK");
            }

            GUILayout.Space(6);
            GUI.backgroundColor = new Color(1f, 0.8f, 0.8f);
            if (GUILayout.Button("批量应用 Boluo 约定", GUILayout.Height(40)))
            {
                int n = AutoScanAndFix(dryRun: false);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
                EditorUtility.DisplayDialog("完成", $"已修复 {n} 个 Prefab", "OK");
            }

            GUI.backgroundColor = Color.white;
        }

        static bool NeedsFix(string assetPath)
        {
            var root = PrefabUtility.LoadPrefabContents(assetPath);
            if (root == null) return false;
            try
            {
                var lod = root.GetComponentInChildren<LODGroup>(true);
                if (lod == null) return false;

                foreach (var t in root.GetComponentsInChildren<Transform>(true))
                {
                    // 棕榈类 Boluo 有大幅负 rootY，本兜底工具不强制；只盯明显正偏移
                    if (t.localPosition.y > 0.05f)
                        return true;
                }

                // LRP.y 若被压到接近 0 / 负值，而树明显高于地面，视为坏
                if (lod.localReferencePoint.y < 0.5f)
                    return true;

                return false;
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        static bool ApplyBoluoConvention(string assetPath)
        {
            var root = PrefabUtility.LoadPrefabContents(assetPath);
            if (root == null) return false;
            bool dirty = false;
            try
            {
                foreach (var t in root.GetComponentsInChildren<Transform>(true))
                {
                    // 兜底：只清正 localY（负 Y 可能是 Boluo 棕榈埋点，留给 AlignTreePrefabsToBoluo）
                    if (t.localPosition.y <= 0.05f) continue;
                    var lp = t.localPosition;
                    t.localPosition = new Vector3(lp.x, 0f, lp.z);
                    dirty = true;
                }

                var lod = root.GetComponentInChildren<LODGroup>(true);
                if (lod != null)
                {
                    Vector3 center = ComputeLocalBoundsCenter(root);
                    if ((lod.localReferencePoint - center).sqrMagnitude > 1e-6f)
                    {
                        var so = new SerializedObject(lod);
                        var prop = so.FindProperty("m_LocalReferencePoint");
                        if (prop != null)
                        {
                            prop.vector3Value = center;
                            so.ApplyModifiedPropertiesWithoutUndo();
                            dirty = true;
                        }
                    }
                }

                if (dirty)
                    PrefabUtility.SaveAsPrefabAsset(root, assetPath);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }

            return dirty;
        }

        static Vector3 ComputeLocalBoundsCenter(GameObject root)
        {
            var renderers = root.GetComponentsInChildren<Renderer>(true);
            bool any = false;
            var bounds = new Bounds();
            for (int i = 0; i < renderers.Length; i++)
            {
                var r = renderers[i];
                if (r == null || !r.enabled) continue;
                if (!any)
                {
                    bounds = r.bounds;
                    any = true;
                }
                else bounds.Encapsulate(r.bounds);
            }

            if (!any) return Vector3.zero;
            return root.transform.InverseTransformPoint(bounds.center);
        }
    }
}
