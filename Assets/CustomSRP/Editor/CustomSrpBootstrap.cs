using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

using UnityGameFramework.Runtime;
namespace CustomSRP.Editor
{
    public static class CustomSrpBootstrap
    {
        const string AssetPath = "Assets/CustomSRP/Settings/CustomRenderPipelineAsset.asset";
        const string PostFXSettingsPath = "Assets/CustomSRP/Settings/PostFXSettings.asset";
        const string ColorLUTComputePath = "Assets/CustomSRP/Shaders/ColorLUT.compute";
        const string PostFXSettingsFolder = "Assets/CustomSRP/Settings";

        [MenuItem("CustomSRP/Create & Assign Pipeline Asset")]
        public static void CreateAndAssign()
        {
            EnsureFolders();

            var asset = AssetDatabase.LoadAssetAtPath<CustomRenderPipelineAsset>(AssetPath);
            if (asset == null)
            {
                asset = ScriptableObject.CreateInstance<CustomRenderPipelineAsset>();
                AssetDatabase.CreateAsset(asset, AssetPath);
                AssetDatabase.SaveAssets();
            }

            EnsurePostFXSettingsAssigned(asset);
            EnsureColorLUTComputeOnAllPostFXSettings();
            EnsureCameraShadersAssigned(asset);

            GraphicsSettings.defaultRenderPipeline = asset;
            QualitySettings.renderPipeline = asset;
            EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssets();

            Log.Info($"[CustomSRP] Assigned pipeline asset: {AssetPath}");
            Selection.activeObject = asset;
            EditorGUIUtility.PingObject(asset);
        }

        [InitializeOnLoadMethod]
        static void AutoAssignIfMissing()
        {
            EditorApplication.delayCall += () =>
            {
                if (EditorApplication.isPlayingOrWillChangePlaymode)
                {
                    return;
                }

                var asset = AssetDatabase.LoadAssetAtPath<CustomRenderPipelineAsset>(AssetPath);
                if (asset == null)
                {
                    return;
                }

                if (GraphicsSettings.defaultRenderPipeline == null)
                {
                    GraphicsSettings.defaultRenderPipeline = asset;
                    QualitySettings.renderPipeline = asset;
                }

                EnsurePostFXSettingsAssigned(asset);
                EnsureColorLUTComputeOnAllPostFXSettings();
                EnsureCameraShadersAssigned(asset);
            };
        }

        [MenuItem("CustomSRP/Fix Color LUT Compute References")]
        public static void FixColorLUTComputeReferencesMenu()
        {
            EnsureColorLUTComputeOnAllPostFXSettings();
            Log.Info("[CustomSRP] Color LUT compute references refreshed.");
        }

        public static void EnsureColorLUTComputeOnAllPostFXSettings()
        {
            ComputeShader compute =
                AssetDatabase.LoadAssetAtPath<ComputeShader>(ColorLUTComputePath);
            if (compute == null)
            {
                AssetDatabase.ImportAsset(ColorLUTComputePath);
                compute = AssetDatabase.LoadAssetAtPath<ComputeShader>(ColorLUTComputePath);
            }

            if (compute == null)
            {
                Log.Error(
                    $"[CustomSRP] Missing Color LUT compute at {ColorLUTComputePath}");
                return;
            }

            string[] guids = AssetDatabase.FindAssets(
                "t:PostFXSettings", new[] { PostFXSettingsFolder });
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var settings = AssetDatabase.LoadAssetAtPath<PostFXSettings>(path);
                if (settings == null)
                {
                    continue;
                }

                var so = new SerializedObject(settings);
                SerializedProperty computeProp =
                    so.FindProperty("colorLUTComputeShader");
                SerializedProperty shaderProp = so.FindProperty("shader");
                bool dirty = false;
                if (computeProp != null &&
                    computeProp.objectReferenceValue != compute)
                {
                    computeProp.objectReferenceValue = compute;
                    dirty = true;
                }

                Shader postFxShader = Shader.Find("Hidden/CustomSRP/Post FX Stack");
                if (shaderProp != null &&
                    postFxShader != null &&
                    shaderProp.objectReferenceValue != postFxShader)
                {
                    shaderProp.objectReferenceValue = postFxShader;
                    dirty = true;
                }

                if (dirty)
                {
                    so.ApplyModifiedPropertiesWithoutUndo();
                    EditorUtility.SetDirty(settings);
                }
            }

            AssetDatabase.SaveAssets();
        }

        public static void EnsureCameraRendererShaderAssigned(CustomRenderPipelineAsset pipelineAsset) =>
            EnsureCameraShadersAssigned(pipelineAsset);

        public static void EnsureCameraShadersAssigned(CustomRenderPipelineAsset pipelineAsset)
        {
            if (pipelineAsset == null)
            {
                return;
            }

            var so = new SerializedObject(pipelineAsset);
            bool dirty = false;

            dirty |= AssignShaderProperty(
                so, "settings.cameraRendererShader", "cameraRendererShader",
                "Hidden/CustomSRP/Camera Renderer");
            dirty |= AssignShaderProperty(
                so, "settings.cameraDebuggerShader", null,
                "Hidden/CustomSRP/Camera Debugger");

            if (dirty)
            {
                so.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(pipelineAsset);
                AssetDatabase.SaveAssets();
            }
        }

        static bool AssignShaderProperty(
            SerializedObject so,
            string nestedPath,
            string legacyPath,
            string shaderName)
        {
            Shader shader = Shader.Find(shaderName);
            if (shader == null)
            {
                Log.Error($"[CustomSRP] {shaderName} shader not found.");
                return false;
            }

            bool dirty = false;
            SerializedProperty nested = so.FindProperty(nestedPath);
            if (nested != null && nested.objectReferenceValue != shader)
            {
                nested.objectReferenceValue = shader;
                dirty = true;
            }

            if (legacyPath != null)
            {
                SerializedProperty legacy = so.FindProperty(legacyPath);
                if (legacy != null && legacy.objectReferenceValue != shader)
                {
                    legacy.objectReferenceValue = shader;
                    dirty = true;
                }
            }

            return dirty;
        }

        public static PostFXSettings EnsurePostFXSettingsAssigned(CustomRenderPipelineAsset pipelineAsset)
        {
            EnsureFolders();

            bool created = false;
            var settings = AssetDatabase.LoadAssetAtPath<PostFXSettings>(PostFXSettingsPath);
            if (settings == null)
            {
                settings = ScriptableObject.CreateInstance<PostFXSettings>();
                AssetDatabase.CreateAsset(settings, PostFXSettingsPath);
                created = true;
            }

            var so = new SerializedObject(settings);
            SerializedProperty shaderProp = so.FindProperty("shader");
            Shader shader = Shader.Find("Hidden/CustomSRP/Post FX Stack");
            if (shader != null && shaderProp.objectReferenceValue != shader)
            {
                shaderProp.objectReferenceValue = shader;
            }

            SerializedProperty computeProp = so.FindProperty("colorLUTComputeShader");
            ComputeShader colorLutCompute =
                AssetDatabase.LoadAssetAtPath<ComputeShader>(ColorLUTComputePath);
            if (computeProp != null &&
                colorLutCompute != null &&
                computeProp.objectReferenceValue != colorLutCompute)
            {
                computeProp.objectReferenceValue = colorLutCompute;
            }

            if (created)
                ApplyOilIslandPostFxDefaults(so);

            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(settings);

            if (pipelineAsset != null)
            {
                var pipelineSo = new SerializedObject(pipelineAsset);
                SerializedProperty postFx = pipelineSo.FindProperty("settings.postFXSettings");
                if (postFx == null)
                {
                    postFx = pipelineSo.FindProperty("postFXSettings");
                }

                if (postFx != null && postFx.objectReferenceValue != settings)
                {
                    postFx.objectReferenceValue = settings;
                    pipelineSo.ApplyModifiedPropertiesWithoutUndo();
                    EditorUtility.SetDirty(pipelineAsset);
                }
            }

            AssetDatabase.SaveAssets();
            return settings;
        }

        [MenuItem("CustomSRP/Generate Post FX Settings (Neutral Toggles)")]
        public static void GenerateNeutralPostFxSettings()
        {
            EnsureFolders();
            var settings = AssetDatabase.LoadAssetAtPath<PostFXSettings>(PostFXSettingsPath);
            if (settings == null)
            {
                settings = ScriptableObject.CreateInstance<PostFXSettings>();
                AssetDatabase.CreateAsset(settings, PostFXSettingsPath);
            }

            var so = new SerializedObject(settings);
            SerializedProperty shaderProp = so.FindProperty("shader");
            Shader shader = Shader.Find("Hidden/CustomSRP/Post FX Stack");
            if (shader != null)
                shaderProp.objectReferenceValue = shader;

            SerializedProperty computeProp = so.FindProperty("colorLUTComputeShader");
            ComputeShader colorLutCompute =
                AssetDatabase.LoadAssetAtPath<ComputeShader>(ColorLUTComputePath);
            if (computeProp != null && colorLutCompute != null)
                computeProp.objectReferenceValue = colorLutCompute;

            ApplyNeutralToggleDefaults(so);
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(settings);

            var pipeline = AssetDatabase.LoadAssetAtPath<CustomRenderPipelineAsset>(AssetPath);
            if (pipeline != null)
                EnsurePostFXSettingsAssigned(pipeline);

            AssetDatabase.SaveAssets();
            Selection.activeObject = settings;
            EditorGUIUtility.PingObject(settings);
            Log.Info(
                $"[CustomSRP] Generated Post FX Settings (all toggles off): {PostFXSettingsPath}");
        }

        /// <summary>
        /// All effects off + identity params. Turn on individual Enabled flags in the Inspector.
        /// </summary>
        static void ApplyNeutralToggleDefaults(SerializedObject so)
        {
            SerializedProperty bloom = so.FindProperty("bloom");
            bloom.FindPropertyRelative("enabled").boolValue = false;
            bloom.FindPropertyRelative("ignoreRenderScale").boolValue = false;
            bloom.FindPropertyRelative("maxIterations").intValue = 4;
            bloom.FindPropertyRelative("downscaleLimit").intValue = 2;
            bloom.FindPropertyRelative("bicubicUpsampling").boolValue = false;
            bloom.FindPropertyRelative("threshold").floatValue = 1.1f;
            bloom.FindPropertyRelative("thresholdKnee").floatValue = 0.5f;
            bloom.FindPropertyRelative("intensity").floatValue = 0.12f;
            bloom.FindPropertyRelative("fadeFireflies").boolValue = true;
            bloom.FindPropertyRelative("mode").enumValueIndex = 1;
            bloom.FindPropertyRelative("scatter").floatValue = 0.65f;

            SerializedProperty adj = so.FindProperty("colorAdjustments");
            adj.FindPropertyRelative("enabled").boolValue = false;
            adj.FindPropertyRelative("postExposure").floatValue = 0f;
            adj.FindPropertyRelative("contrast").floatValue = 0f;
            adj.FindPropertyRelative("colorFilter").colorValue = Color.white;
            adj.FindPropertyRelative("hueShift").floatValue = 0f;
            adj.FindPropertyRelative("saturation").floatValue = 0f;

            SerializedProperty wb = so.FindProperty("whiteBalance");
            wb.FindPropertyRelative("enabled").boolValue = false;
            wb.FindPropertyRelative("temperature").floatValue = 0f;
            wb.FindPropertyRelative("tint").floatValue = 0f;

            SerializedProperty split = so.FindProperty("splitToning");
            split.FindPropertyRelative("enabled").boolValue = false;
            split.FindPropertyRelative("shadows").colorValue = Color.gray;
            split.FindPropertyRelative("highlights").colorValue = Color.gray;
            split.FindPropertyRelative("balance").floatValue = 0f;

            SerializedProperty mixer = so.FindProperty("channelMixer");
            mixer.FindPropertyRelative("enabled").boolValue = false;
            mixer.FindPropertyRelative("red").vector3Value = Vector3.right;
            mixer.FindPropertyRelative("green").vector3Value = Vector3.up;
            mixer.FindPropertyRelative("blue").vector3Value = Vector3.forward;

            SerializedProperty smh = so.FindProperty("shadowsMidtonesHighlights");
            smh.FindPropertyRelative("enabled").boolValue = false;
            smh.FindPropertyRelative("shadows").colorValue = Color.white;
            smh.FindPropertyRelative("midtones").colorValue = Color.white;
            smh.FindPropertyRelative("highlights").colorValue = Color.white;
            smh.FindPropertyRelative("shadowsStart").floatValue = 0f;
            smh.FindPropertyRelative("shadowsEnd").floatValue = 0.3f;
            smh.FindPropertyRelative("highlightsStart").floatValue = 0.55f;
            smh.FindPropertyRelative("highLightsEnd").floatValue = 1f;

            SerializedProperty tone = so.FindProperty("toneMapping");
            tone.FindPropertyRelative("enabled").boolValue = false;
            tone.FindPropertyRelative("mode").enumValueIndex = 0;

            SerializedProperty oil = so.FindProperty("oilFilter");
            if (oil != null)
                oil.FindPropertyRelative("enabled").boolValue = false;
        }

        static void ApplyOilIslandPostFxDefaults(SerializedObject so) =>
            ApplyNeutralToggleDefaults(so);

        static void EnsureFolders()
        {
            if (!AssetDatabase.IsValidFolder("Assets/CustomSRP/Settings"))
            {
                if (!AssetDatabase.IsValidFolder("Assets/CustomSRP"))
                {
                    AssetDatabase.CreateFolder("Assets", "CustomSRP");
                }

                AssetDatabase.CreateFolder("Assets/CustomSRP", "Settings");
            }
        }
    }
}
