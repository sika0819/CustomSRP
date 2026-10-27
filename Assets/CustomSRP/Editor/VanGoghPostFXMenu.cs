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
            so.FindProperty("oilFilter.enabled").boolValue = true;

            SerializedProperty canvas = so.FindProperty("canvas");
            canvas.FindPropertyRelative("enabled").boolValue = true;
            canvas.FindPropertyRelative("grain").floatValue = 0.035f;
            canvas.FindPropertyRelative("weave").floatValue = 0.02f;

            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssets();
        }

        static void SetBloom(SerializedProperty bloom)
        {
            bloom.FindPropertyRelative("enabled").boolValue = true;
            bloom.FindPropertyRelative("threshold").floatValue = 1f;
            bloom.FindPropertyRelative("intensity").floatValue = 0.5f;
            bloom.FindPropertyRelative("scatter").floatValue = 0.85f;
            bloom.FindPropertyRelative("fadeFireflies").boolValue = true;
            bloom.FindPropertyRelative("mode").enumValueIndex =
                (int)PostFXSettings.BloomSettings.Mode.Scattering;
        }

        static void SetColor(SerializedProperty color)
        {
            color.FindPropertyRelative("enabled").boolValue = true;
            color.FindPropertyRelative("postExposure").floatValue = 0.1f;
            color.FindPropertyRelative("contrast").floatValue = 18f;
            color.FindPropertyRelative("hueShift").floatValue = 0f;
            color.FindPropertyRelative("saturation").floatValue = 30f;
            color.FindPropertyRelative("colorFilter").colorValue = Color.white;
        }

        static void SetWhiteBalance(SerializedProperty wb)
        {
            wb.FindPropertyRelative("enabled").boolValue = true;
            wb.FindPropertyRelative("temperature").floatValue = 12f;
            wb.FindPropertyRelative("tint").floatValue = 5f;
        }

        static void SetSplit(SerializedProperty split)
        {
            split.FindPropertyRelative("enabled").boolValue = true;
            split.FindPropertyRelative("shadows").colorValue = new Color(0f, 0.2f, 0.4f, 1f);
            split.FindPropertyRelative("highlights").colorValue =
                new Color(1f, 215f / 255f, 0f, 1f);
            // This SRP stores balance on -100..100 and the LUT kernel adds balance * 0.01.
            // 45 is the 0.45 highlight shift on that scale.
            split.FindPropertyRelative("balance").floatValue = 45f;
        }

        static void SetSmh(SerializedProperty smh)
        {
            smh.FindPropertyRelative("enabled").boolValue = true;
            smh.FindPropertyRelative("shadows").colorValue = Hex(0x0F, 0x19, 0x2D);
            smh.FindPropertyRelative("midtones").colorValue = Hex(0xD8, 0xA7, 0x56);
            smh.FindPropertyRelative("highlights").colorValue = Hex(0xFF, 0xD7, 0x64);
            smh.FindPropertyRelative("shadowsStart").floatValue = 0.05f;
            smh.FindPropertyRelative("shadowsEnd").floatValue = 0.30f;
            smh.FindPropertyRelative("highlightsStart").floatValue = 0.55f;
            smh.FindPropertyRelative("highLightsEnd").floatValue = 1f;
        }

        static void SetTone(SerializedProperty tone)
        {
            tone.FindPropertyRelative("enabled").boolValue = true;
            tone.FindPropertyRelative("mode").enumValueIndex =
                (int)PostFXSettings.ToneMappingSettings.Mode.ACES;
        }

        static Color Hex(byte r, byte g, byte b) =>
            new Color(r / 255f, g / 255f, b / 255f, 1f);
    }
}
#endif
