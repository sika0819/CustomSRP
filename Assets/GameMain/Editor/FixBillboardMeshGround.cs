using UnityEngine;
using UnityEditor;

namespace Game
{
    /// <summary>已废弃：会抬高 Billboard localY，导致树底悬空。</summary>
    public class FixTreeBillboardPositions : EditorWindow
    {
        [MenuItem("Window/Game/Fix Tree Billboard Positions (Deprecated)")]
        public static void ShowWindow() =>
            GetWindow<FixTreeBillboardPositions>(true, "Fix Tree Billboard (Deprecated)");

        void OnGUI()
        {
            EditorGUILayout.HelpBox(
                "已废弃。请用 Window > Game > Align Tree Prefabs To Boluo。",
                MessageType.Warning);
            if (GUILayout.Button("打开 Align Tree Prefabs To Boluo", GUILayout.Height(36)))
                AlignTreePrefabsToBoluo.MenuAlign();
        }
    }
}
