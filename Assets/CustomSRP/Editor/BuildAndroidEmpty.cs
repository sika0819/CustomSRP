using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.Rendering;

namespace CustomSRP.Editor
{
    /// <summary>
    /// Batch: Unity -batchmode -quit -executeMethod CustomSRP.Editor.BuildAndroidEmpty.Build
    /// Default menu builds a Release perf APK (no Development/Profiler).
    /// </summary>
    public static class BuildAndroidEmpty
    {
        public const string ApkPath = "Builds/Android/CustomSRP-OilEmpty.apk";
        public const string PackageId = "com.customsrp.oilempty";
        const string PipelineAssetPath =
            "Assets/CustomSRP/Settings/CustomRenderPipelineAsset.asset";

        [MenuItem("CustomSRP/Build Android Empty (Oil NPR Perf)")]
        public static void Build()
        {
            BuildInternal(development: false);
        }

        [MenuItem("CustomSRP/Build Android Empty (Oil NPR Dev+Profiler)")]
        public static void BuildDevelopment()
        {
            BuildInternal(development: true);
        }

        public static void BuildInternal(bool development)
        {
            ApplyAndroidPlayerPerfSettings();
            ApplyOilMobilePipelineSettings();

            // Refresh Empty with mobile oil / render-scale presets before packaging.
            CreateEmptyScene.Create(mobilePerf: true);

            Directory.CreateDirectory("Builds/Android");

            var options = new BuildPlayerOptions
            {
                scenes = new[] { "Assets/CustomSRP/Scenes/Empty.unity" },
                locationPathName = ApkPath,
                target = BuildTarget.Android,
                options = development
                    ? BuildOptions.Development | BuildOptions.ConnectWithProfiler
                    : BuildOptions.None
            };

            BuildReport report = BuildPipeline.BuildPlayer(options);
            BuildSummary summary = report.summary;
            if (summary.result == BuildResult.Succeeded)
            {
                Debug.Log(
                    $"[CustomSRP] Android APK OK ({(development ? "Dev+Profiler" : "Perf/Release")}): " +
                    $"{ApkPath} ({summary.totalSize / (1024 * 1024f):F1} MB, {summary.totalTime})");
                if (Application.isBatchMode)
                {
                    EditorApplication.Exit(0);
                }
            }
            else
            {
                Debug.LogError(
                    $"[CustomSRP] Android build failed: {summary.result}");
                if (Application.isBatchMode)
                {
                    EditorApplication.Exit(1);
                }
            }
        }

        static void ApplyAndroidPlayerPerfSettings()
        {
            var android = NamedBuildTarget.Android;
            PlayerSettings.SetApplicationIdentifier(android, PackageId);
            PlayerSettings.productName = "CustomSRP OilEmpty";
            PlayerSettings.bundleVersion = "1.0.0";
            PlayerSettings.Android.bundleVersionCode = 1;
            PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel25;
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            PlayerSettings.Android.androidIsGame = true;
            PlayerSettings.SetScriptingBackend(android, ScriptingImplementation.IL2CPP);
            PlayerSettings.SetIl2CppCompilerConfiguration(
                android, Il2CppCompilerConfiguration.Release);
            PlayerSettings.SetManagedStrippingLevel(android, ManagedStrippingLevel.High);
            PlayerSettings.gcIncremental = true;
            PlayerSettings.stripEngineCode = true;
            PlayerSettings.SetGraphicsAPIs(
                BuildTarget.Android,
                new[] { GraphicsDeviceType.Vulkan, GraphicsDeviceType.OpenGLES3 });
            PlayerSettings.MTRendering = true;
#if UNITY_2023_1_OR_NEWER || UNITY_6000_0_OR_NEWER
            PlayerSettings.Android.optimizedFramePacing = true;
#endif
            QualitySettings.vSyncCount = 0;
            QualitySettings.antiAliasing = 0;
            QualitySettings.shadows = ShadowQuality.HardOnly;
            QualitySettings.shadowResolution = ShadowResolution.Medium;
            QualitySettings.shadowDistance = 40f;
            QualitySettings.lodBias = 0.7f;
            QualitySettings.maximumLODLevel = 0;
            QualitySettings.particleRaycastBudget = 64;
            QualitySettings.asyncUploadTimeSlice = 4;
            QualitySettings.asyncUploadBufferSize = 16;
        }

        /// <summary>
        /// Cheap shadow / buffer defaults for the Oil Empty Android package.
        /// Camera still overrides copy/HDR/postFX; this cuts atlas + cascade cost.
        /// </summary>
        static void ApplyOilMobilePipelineSettings()
        {
            var asset = AssetDatabase.LoadAssetAtPath<CustomRenderPipelineAsset>(
                PipelineAssetPath);
            if (asset == null)
            {
                Debug.LogWarning($"[CustomSRP] Missing pipeline asset: {PipelineAssetPath}");
                return;
            }

            var so = new SerializedObject(asset);
            SerializedProperty settings = so.FindProperty("settings");
            SerializedProperty buffer = settings.FindPropertyRelative("cameraBuffer");
            buffer.FindPropertyRelative("allowHDR").boolValue = false;
            buffer.FindPropertyRelative("copyColor").boolValue = false;
            buffer.FindPropertyRelative("copyDepth").boolValue = false;
            buffer.FindPropertyRelative("renderScale").floatValue = 0.75f;
            buffer.FindPropertyRelative("fxaa").FindPropertyRelative("enabled").boolValue = false;

            SerializedProperty shadows = settings.FindPropertyRelative("shadows");
            shadows.FindPropertyRelative("filterQuality").enumValueIndex =
                (int)ShadowSettings.FilterQuality.Low;
            shadows.FindPropertyRelative("maxDistance").floatValue = 35f;
            shadows.FindPropertyRelative("distanceFade").floatValue = 0.15f;

            SerializedProperty directional = shadows.FindPropertyRelative("directional");
            directional.FindPropertyRelative("atlasSize").enumValueIndex =
                IndexOfMapSize(ShadowSettings.MapSize._512);
            directional.FindPropertyRelative("cascadeCount").intValue = 2;
            directional.FindPropertyRelative("cascadeRatio1").floatValue = 0.25f;
            directional.FindPropertyRelative("cascadeRatio2").floatValue = 0.5f;
            directional.FindPropertyRelative("cascadeRatio3").floatValue = 0.75f;
            directional.FindPropertyRelative("softCascadeBlend").boolValue = false;

            SerializedProperty other = shadows.FindPropertyRelative("other");
            other.FindPropertyRelative("atlasSize").enumValueIndex =
                IndexOfMapSize(ShadowSettings.MapSize._512);

            settings.FindPropertyRelative("postFXSettings").objectReferenceValue = null;
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssets();
        }

        static int IndexOfMapSize(ShadowSettings.MapSize size)
        {
            var values = (ShadowSettings.MapSize[])System.Enum.GetValues(
                typeof(ShadowSettings.MapSize));
            for (int i = 0; i < values.Length; i++)
            {
                if (values[i] == size)
                {
                    return i;
                }
            }

            return 2;
        }
    }
}
