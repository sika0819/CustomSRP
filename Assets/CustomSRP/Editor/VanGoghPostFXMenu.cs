#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace CustomSRP.Editor
{
    public static class VanGoghPostFXMenu
    {
        const string SettingsPath = "Assets/CustomSRP/Settings/PostFXSettings.asset";

        [MenuItem("CustomSRP/油画滤镜/应用梵高调色")]
        public static void ApplyActive()
        {
            var pipeline = GraphicsSettings.currentRenderPipeline as CustomRenderPipelineAsset;
            PostFXSettings settings = pipeline != null
                ? pipeline.Settings.postFXSettings
                : AssetDatabase.LoadAssetAtPath<PostFXSettings>(SettingsPath);
            if (settings == null)
            {
                Debug.LogError("[VanGogh] 找不到 PostFXSettings。");
                return;
            }

            Apply(settings);
            Debug.Log($"[VanGogh] 已写入 {AssetDatabase.GetAssetPath(settings)}");
        }

        public static void Apply(PostFXSettings settings)
        {
            var so = new SerializedObject(settings);

            SetBloom(so.FindProperty("bloom"));
            SetColor(so.FindProperty("colorAdjustments"));
            SetWhiteBalance(so.FindProperty("whiteBalance"));
            SetSplit(so.FindProperty("splitToning"));
            SetSmh(so.FindProperty("shadowsMidtonesHighlights"));
            SetTone(so.FindProperty("toneMapping"));

            SerializedProperty canvas = so.FindProperty("canvas");
            canvas.FindPropertyRelative("enabled").boolValue = true;
            canvas.FindPropertyRelative("grain").floatValue = 0.028f;
            canvas.FindPropertyRelative("weave").floatValue = 0.022f;

            SerializedProperty oil = so.FindProperty("oilFilter");
            oil.FindPropertyRelative("enabled").boolValue = true;
            oil.FindPropertyRelative("strength").floatValue = 1.15f;
            oil.FindPropertyRelative("interiorFloor").floatValue = 0.12f;
            oil.FindPropertyRelative("radiusScale").floatValue = 1.08f;

            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssets();
        }

        static void SetBloom(SerializedProperty bloom)
        {
            bloom.FindPropertyRelative("enabled").boolValue = true;
            bloom.FindPropertyRelative("threshold").floatValue = 1f;
            bloom.FindPropertyRelative("intensity").floatValue = 0.3f;
            bloom.FindPropertyRelative("scatter").floatValue = 0.85f;
            bloom.FindPropertyRelative("fadeFireflies").boolValue = true;
            bloom.FindPropertyRelative("mode").enumValueIndex =
                (int)PostFXSettings.BloomSettings.Mode.Scattering;
        }

        static void SetColor(SerializedProperty color)
        {
            color.FindPropertyRelative("enabled").boolValue = true;
            color.FindPropertyRelative("postExposure").floatValue = 0.05f;
            color.FindPropertyRelative("contrast").floatValue = 10f;
            color.FindPropertyRelative("hueShift").floatValue = 0f;
            color.FindPropertyRelative("saturation").floatValue = 8f;
            color.FindPropertyRelative("colorFilter").colorValue = Color.white;
        }

        static void SetWhiteBalance(SerializedProperty wb)
        {
            wb.FindPropertyRelative("enabled").boolValue = true;
            wb.FindPropertyRelative("temperature").floatValue = 4f;
            wb.FindPropertyRelative("tint").floatValue = 2f;
        }

        // Keep off: OilSkyLookTables owns day/night; strong split washes that out.
        static void SetSplit(SerializedProperty split)
        {
            split.FindPropertyRelative("enabled").boolValue = false;
            split.FindPropertyRelative("shadows").colorValue =
                new Color(0.45f, 0.47f, 0.52f, 1f);
            split.FindPropertyRelative("highlights").colorValue =
                new Color(0.55f, 0.52f, 0.46f, 1f);
            split.FindPropertyRelative("balance").floatValue = 0f;
        }

        static void SetSmh(SerializedProperty smh)
        {
            smh.FindPropertyRelative("enabled").boolValue = false;
            smh.FindPropertyRelative("shadows").colorValue =
                new Color(0.96f, 0.97f, 1f, 1f);
            smh.FindPropertyRelative("midtones").colorValue =
                new Color(1f, 0.99f, 0.97f, 1f);
            smh.FindPropertyRelative("highlights").colorValue =
                new Color(1.02f, 1f, 0.96f, 1f);
            smh.FindPropertyRelative("shadowsStart").floatValue = 0.05f;
            smh.FindPropertyRelative("shadowsEnd").floatValue = 0.30f;
            smh.FindPropertyRelative("highlightsStart").floatValue = 0.55f;
            smh.FindPropertyRelative("highLightsEnd").floatValue = 1f;
        }

        static void SetTone(SerializedProperty tone)
        {
            tone.FindPropertyRelative("enabled").boolValue = false;
            tone.FindPropertyRelative("mode").enumValueIndex =
                (int)PostFXSettings.ToneMappingSettings.Mode.None;
        }
    }
}
#endif
