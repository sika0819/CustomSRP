using UnityEngine;
using UnityEditor;

namespace Game
{
    /// <summary>已废弃：会改 Billboard localY / mesh 偏移，与 Boluo 分叉。</summary>
    public class FixBillboardMeshGroundOffset : EditorWindow
    {
        [MenuItem("Window/Game/Fix Billboard Mesh Ground Offset (Deprecated)")]
        public static void ShowWindow() =>
            GetWindow<FixBillboardMeshGroundOffset>(true, "Fix BB Mesh Ground (Deprecated)");

        void OnGUI()
        {
            EditorGUILayout.HelpBox(
                "已废弃。请用 Window > Game > Align Tree Prefabs To Boluo（不改 mesh）。",
                MessageType.Warning);
            if (GUILayout.Button("打开 Align Tree Prefabs To Boluo", GUILayout.Height(36)))
                AlignTreePrefabsToBoluo.MenuAlign();
        }
    }
}
