using System;
using System.IO;
using Game.Editor.BuildTool;
using UnityEditor;
using UnityEngine;
using YooAsset;
using YooAsset.Editor;

using UnityGameFramework.Runtime;
namespace Game.Editor
{
    /// <summary>
    /// DefaultPackage 构建与拷贝到 StreamingAssets/package。
    /// 
    /// 注意：推荐使用 Window/Game/Build Tool 统一打包窗口。
    /// 本类保留以兼容旧工作流和命令行打包。
    /// </summary>
    public static class YooAssetBuildMenu
    {
        const string PackageName = "DefaultPackage";

        /// <summary>
        /// 打开统一打包窗口
        /// </summary>
        [MenuItem("Game/YooAsset/Open Build Window", false, 1)]
        public static void OpenBuildWindow()
        {
            GameBuildWindow.ShowWindow();
        }

        [MenuItem("Game/YooAsset/Build DefaultPackage (Builtin)", false, 200)]
        public static void BuildDefaultPackageBuiltin()
        {
            BuildPackageBuiltin();
        }

        /// <returns>false if the Yoo package build failed.</returns>
        public static bool BuildPackageBuiltin()
        {
            return Build(EBuildPipeline.BuiltinBuildPipeline);
        }

        /// <summary>
        /// Batch: -executeMethod Game.Editor.YooAssetBuildMenu.BuildPackageThenAndroidRelease
        /// </summary>
        public static void BuildPackageThenAndroidRelease()
        {
            EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android);
            if (!Build(EBuildPipeline.BuiltinBuildPipeline))
                return;
            BuildAndroidIslandRelease.Build();
        }

        [MenuItem("Game/YooAsset/Build DefaultPackage (Scriptable)", false, 201)]
        public static void BuildDefaultPackageScriptable()
        {
            Build(EBuildPipeline.ScriptableBuildPipeline);
        }

        [MenuItem("Game/YooAsset/Copy Latest Package To StreamingAssets", false, 220)]
        public static void CopyLatestToStreamingAssets()
        {
            BoluoYooAssetBuildPipeline.CopyPackageToStreamingAssets(
                EditorUserBuildSettings.activeBuildTarget,
                msg => Log.Info($"[YooAsset] {msg}"));
        }

        [MenuItem("Game/YooAsset/Open Bundles Output", false, 240)]
        public static void OpenBundlesOutput()
        {
            string path = AssetBundleBuilderHelper.GetDefaultBuildOutputRoot();
            if (!Directory.Exists(path))
                Directory.CreateDirectory(path);
            EditorUtility.RevealInFinder(path);
        }

        [MenuItem("Game/YooAsset/Open StreamingAssets Package", false, 241)]
        public static void OpenStreamingPackage()
        {
            string path = AssetBundleBuilderHelper.GetStreamingAssetsRoot();
            if (!Directory.Exists(path))
                Directory.CreateDirectory(path);
            EditorUtility.RevealInFinder(path);
        }

        // --- iOS 快捷操作 ---
        [MenuItem("Game/YooAsset/iOS/Build Resources for iOS", false, 300)]
        public static void BuildForIOS()
        {
            if (!EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.iOS, BuildTarget.iOS))
            {
                Log.Error("[YooAsset] Failed to switch to iOS platform");
                return;
            }
            Build(EBuildPipeline.BuiltinBuildPipeline);
        }

        [MenuItem("Game/YooAsset/iOS/Copy iOS Package to StreamingAssets", false, 301)]
        public static void CopyIOSToStreamingAssets()
        {
            BoluoYooAssetBuildPipeline.CopyPackageToStreamingAssets(
                BuildTarget.iOS,
                msg => Log.Info($"[YooAsset] {msg}"));
        }

        static bool Build(EBuildPipeline buildPipeline)
        {
            BuildTarget buildTarget = EditorUserBuildSettings.activeBuildTarget;
            string outputRoot = AssetBundleBuilderHelper.GetDefaultBuildOutputRoot();
            string packageVersion = DateTime.Now.ToString("yyyy-MM-dd-HHmmss");
            UpdateSetting updateSetting = UpdateSetting.LoadOrDefault();

            IBuildPipeline pipeline;
            BuildParameters buildParameters;

            if (buildPipeline == EBuildPipeline.BuiltinBuildPipeline)
            {
                var builtin = new BuiltinBuildParameters();
                pipeline = new BuiltinBuildPipeline();
                buildParameters = builtin;
                builtin.CompressOption = ECompressOption.LZ4;
            }
            else
            {
                var scriptable = new ScriptableBuildParameters();
                pipeline = new ScriptableBuildPipeline();
                buildParameters = scriptable;
                scriptable.CompressOption = ECompressOption.LZ4;
                scriptable.BuiltinShadersBundleName = $"{PackageName}_shaders";
                scriptable.ReplaceAssetPathWithAddress = updateSetting.GetReplaceAssetPathWithAddress();
                scriptable.TrackSpriteAtlasDependencies = true;
            }

            buildParameters.BuildOutputRoot = outputRoot;
            buildParameters.BuildinFileRoot = AssetBundleBuilderHelper.GetStreamingAssetsRoot();
            buildParameters.BuildPipeline = buildPipeline.ToString();
            buildParameters.BuildTarget = buildTarget;
            buildParameters.BuildBundleType = (int)EBuildBundleType.AssetBundle;
            buildParameters.PackageName = PackageName;
            buildParameters.PackageVersion = packageVersion;
            buildParameters.VerifyBuildingResult = true;
            buildParameters.EnableSharePackRule = true;
            buildParameters.FileNameStyle = EFileNameStyle.BundleName_HashName;
            buildParameters.BuildinFileCopyOption = EBuildinFileCopyOption.ClearAndCopyAll;
            buildParameters.BuildinFileCopyParams = string.Empty;
            buildParameters.ClearBuildCacheFiles = false;
            buildParameters.UseAssetDependencyDB = true;

            Log.Info($"[YooAsset] Build start pipeline={buildPipeline} target={buildTarget} version={packageVersion}");
            var buildResult = pipeline.Run(buildParameters, true);
            if (buildResult.Success)
            {
                Log.Info($"[YooAsset] Build ok: {buildResult.OutputPackageDirectory}");
                return true;
            }

            Log.Error($"[YooAsset] Build failed: {buildResult.ErrorInfo}");
            return false;
        }
    }
}
