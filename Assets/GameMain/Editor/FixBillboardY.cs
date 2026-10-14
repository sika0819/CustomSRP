using UnityEngine;
using UnityEditor;

namespace Game
{
    /// <summary>已废弃：会抬高 LOD_Billboard.localY，与 Boluo 约定冲突。</summary>
    public class FixBillboardY : EditorWindow
    {
        [MenuItem("Window/Game/Fix Billboard Y (Deprecated)")]
        public static void ShowWindow() => GetWindow<FixBillboardY>(true, "Fix Billboard Y (Deprecated)");

        void OnGUI()
        {
            EditorGUILayout.HelpBox(
                "已废弃。请用 Window > Game > Align Tree Prefabs To Boluo（localY=0，不改 mesh）。",
                MessageType.Warning);
            if (GUILayout.Button("打开 Align Tree Prefabs To Boluo", GUILayout.Height(36)))
                AlignTreePrefabsToBoluo.MenuAlign();
        }
    }
}
