using UnityEngine;
using UnityEditor;

namespace Game
{
    /// <summary>
    /// 已废弃：旧逻辑用 localY = -meshBottom 抬 Billboard，与 Boluo 约定冲突。
    /// 请改用 Window > Game > Align Tree Prefabs To Boluo。
    /// </summary>
    public class FixAllTreeBillboardY : EditorWindow
    {
        [MenuItem("Window/Game/Fix All Tree Billboard Y")]
        public static void ShowWindow() => GetWindow<FixAllTreeBillboardY>(true, "Fix Tree Billboard Y (Deprecated)");

        void OnGUI()
        {
            minSize = new Vector2(420, 160);
            GUILayout.Space(8);
            EditorGUILayout.HelpBox(
                "此工具已废弃。\n\n" +
                "旧实现会按 meshBottom 抬高 LOD_Billboard.localY，" +
                "与 Boluo（localY=0 + Billboard pivot 烤进 mesh）冲突，是树飘空的次因之一。\n\n" +
                "请使用：Window > Game > Align Tree Prefabs To Boluo",
                MessageType.Warning);
            GUILayout.Space(8);
            if (GUILayout.Button("打开 Align Tree Prefabs To Boluo", GUILayout.Height(36)))
                AlignTreePrefabsToBoluo.MenuAlign();
        }
    }
}
