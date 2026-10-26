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
                "时段快捷键（天空 + 环境光 + 雾色 + 平行光）",
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
            OilSkyboxTime.EvaluateFog(period, out Color fog);
            string fogHex = ColorUtility.ToHtmlStringRGB(fog);
            EditorGUILayout.HelpBox(
                $"内部 _Period = {period:0.00}（0 黎明 / 1 白天 / 2 黄昏 / 3 夜晚）\n" +
                $"雾色 = #{fogHex}（夜晚固定 #36363C）· Lighting Fog Density 勿超过 ~0.12（大雾 0.09）\n" +
                "日月方向：Boluo Cozy 轨道（sunDirection=308），不跟随镜头。\n" +
                "拖动 Time Of Day 会同步天空、环境光、雾色与平行光。\n" +
                "Stars 直接改 OilSkybox 材质（或 Lighting → Skybox），本组件不再覆盖。\n" +
                "勾选 Override Colors 后，颜色用 Color Set，时间只改日月方向。",
                MessageType.None);
        }
    }
}
