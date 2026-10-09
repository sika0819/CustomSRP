using UnityEditor;
using UnityEngine;

namespace CustomSRP.Editor
{
    [CustomEditor(typeof(OilSkyboxTime))]
    public class OilSkyboxTimeEditor : UnityEditor.Editor
    {
        static readonly (string Label, float Hours)[] Periods =
        {
            ("黎明 06:00", 6f),
            ("白天 12:00", 12f),
            ("黄昏 18:00", 18f),
            ("夜晚 00:00", 0f),
        };

        public override void OnInspectorGUI()
        {
            var sky = (OilSkyboxTime)target;

            EditorGUI.BeginChangeCheck();
            DrawDefaultInspector();
            if (EditorGUI.EndChangeCheck())
            {
                sky.ApplyEditorPreview();
                EditorUtility.SetDirty(sky);
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField(
                "时段快捷键（天空 + 环境光 + 平行光）",
                EditorStyles.miniLabel);

            EditorGUILayout.BeginHorizontal();
            for (int i = 0; i < Periods.Length; i++)
            {
                if (!GUILayout.Button(Periods[i].Label))
                {
                    continue;
                }

                Undo.RecordObject(sky, "Oil Sky Time Of Day");
                if (sky.sun != null)
                {
                    Undo.RecordObject(sky.sun, "Oil Sky Sun");
                    Undo.RecordObject(sky.sun.transform, "Oil Sky Sun Rotation");
                }

                sky.ApplyEditorHours(Periods[i].Hours);
                EditorUtility.SetDirty(sky);
                // ExitGUI after scheduling deferred sun — avoid IMGUI re-entry on same GO.
                GUIUtility.ExitGUI();
            }

            EditorGUILayout.EndHorizontal();

            float period = sky.CurrentPeriod();
            EditorGUILayout.HelpBox(
                $"内部 _Period = {period:0.00}（0 黎明 / 1 白天 / 2 黄昏 / 3 夜晚）\n" +
                "拖动 Time Of Day 会同步天空、环境光与平行光方向/颜色/强度。",
                MessageType.None);
        }
    }
}
