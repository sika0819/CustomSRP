using UnityEngine;
using UnityEditor;

namespace Game
{
    /// <summary>
    /// 一键修复树飘空：
    /// 1. Snap Trees To Terrain（按条目下标写回 AllMapItem Y）
    /// 2. Align Tree Prefabs To Boluo（localY + localReferencePoint，不改 mesh）
    /// </summary>
    public class FixAllFloatingTrees : EditorWindow
    {
        [MenuItem("Window/Game/Fix All Floating Trees")]
        public static void ShowWindow()
        {
            var window = GetWindow<FixAllFloatingTrees>(true, "Fix Trees");
            window.minSize = new Vector2(520, 220);
        }

        void OnGUI()
        {
            GUILayout.Space(10);
            GUILayout.Label("一键修复树飘在空中", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "1. Snap Trees To Terrain — 按条目下标把树 Y 写到 IslandTerrain + TreeBuryMeters\n" +
                "2. Align Tree Prefabs To Boluo — localY/LRP 对齐 Boluo（不改 mesh）\n\n" +
                "运行时也会对树做贴地（EnvGroundFit.TrySnapMapItemY）。\n" +
                "略微悬空：进 Play 即可；要固化进 bytes 再跑菜单第 1 步。",
                MessageType.Info);
            GUILayout.Space(10);

            GUI.backgroundColor = new Color(0.8f, 1f, 0.8f);
            if (GUILayout.Button("执行修复", GUILayout.Height(40)))
                Execute();
            GUI.backgroundColor = Color.white;
        }

        [MenuItem("Window/Game/Fix All Floating Trees (No Dialog)")]
        public static void Execute()
        {
            Debug.Log("[FixAllTrees] ========== Start ==========");

            int toFix1 = SnapAllTreesToTerrain.AutoScanAndApply(dryRun: true);
            if (toFix1 > 0)
            {
                int fixed1 = SnapAllTreesToTerrain.AutoScanAndApply(dryRun: false);
                Debug.Log($"[FixAllTrees] Step 1: snapped {fixed1} tree entries by ItemIndex");
            }
            else
            {
                Debug.Log("[FixAllTrees] Step 1: AllMapItem tree Y already on terrain");
            }

            int toFix2 = AlignTreePrefabsToBoluo.AlignAll(dryRun: true);
            if (toFix2 > 0)
            {
                int fixed2 = AlignTreePrefabsToBoluo.AlignAll(dryRun: false);
                Debug.Log($"[FixAllTrees] Step 2: aligned {fixed2} prefabs to Boluo");
            }
            else
            {
                Debug.Log("[FixAllTrees] Step 2: Prefabs already match Boluo transforms");
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[FixAllTrees] ========== Complete ==========");
        }
    }
}
