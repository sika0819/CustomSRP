#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Game;
using UnityEditor;
using UnityEngine;
using YooAsset.Editor;

namespace Game.Editor.BuildTool
{
    /// <summary>
    /// YooAsset 前置构建：Collector 检查 → 预处理 → BuiltinBuild →（可选）拷贝 StreamingAssets。
    /// GameBuildWindow / 宿主导出共用。
    /// </summary>
    public static class BoluoYooAssetBuildPipeline
    {
        public const string PackageName = "DefaultPackage";

        /// <summary>
        /// Bundles 输出根下除版本目录外还会有 OutputCache / Simulate，不能按字典序取“最新”。
        /// </summary>
        static readonly HashSet<string> NonVersionDirNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "OutputCache",
            "Simulate",
        };

        public static bool Run(
            BuildTarget target,
            bool copyToStreamingAssets,
            Action<string> log = null)
        {
            void Log(string msg)
            {
                log?.Invoke(msg);
                Debug.Log($"[BoluoYoo] {msg}");
            }

            try
            {
                if (!HasYooAssetSettings())
                {
                    Log("Setting up YooAsset collector...");
                    YooAssetCollectorSetup.SetupAll();
                }

                Log("Running preprocessing...");
                RunPreBuildSteps(Log);

                string outputRoot = AssetBundleBuilderHelper.GetDefaultBuildOutputRoot();
                string platform = target.ToString();
                string packageRoot = Path.Combine(outputRoot, platform, PackageName);

                if (Directory.Exists(packageRoot))
                {
                    Log($"Cleaning old build cache: {packageRoot}");
                    Directory.Delete(packageRoot, true);
                }

                string packageVersion = DateTime.Now.ToString("yyyy-MM-dd-HHmmss");
                var builtin = new BuiltinBuildParameters();
                var pipeline = new BuiltinBuildPipeline();

                builtin.CompressOption = ECompressOption.LZ4;
                builtin.BuildOutputRoot = outputRoot;
                builtin.BuildinFileRoot = AssetBundleBuilderHelper.GetStreamingAssetsRoot();
                builtin.BuildPipeline = EBuildPipeline.BuiltinBuildPipeline.ToString();
                builtin.BuildTarget = target;
                builtin.BuildBundleType = (int)YooAsset.EBuildBundleType.AssetBundle;
                builtin.PackageName = PackageName;
                builtin.PackageVersion = packageVersion;
                builtin.VerifyBuildingResult = true;
                builtin.EnableSharePackRule = true;
                builtin.FileNameStyle = YooAsset.EFileNameStyle.BundleName_HashName;
                builtin.BuildinFileCopyOption = EBuildinFileCopyOption.ClearAndCopyAll;
                builtin.BuildinFileCopyParams = string.Empty;
                builtin.ClearBuildCacheFiles = true;
                builtin.UseAssetDependencyDB = true;

                Log($"Building package: {PackageName} v{packageVersion}");
                var buildResult = pipeline.Run(builtin, true);

                if (!buildResult.Success)
                {
                    Log($"YooAsset build failed: {buildResult.ErrorInfo}");
                    return false;
                }

                Log($"YooAsset build OK: {buildResult.OutputPackageDirectory}");

                // ClearAndCopyAll 已写入 StreamingAssets；再从确切输出目录拷一次，
                // 避免旧逻辑误选 OutputCache / Simulate 覆盖掉 .version。
                if (copyToStreamingAssets)
                {
                    if (!string.IsNullOrEmpty(buildResult.OutputPackageDirectory) &&
                        Directory.Exists(buildResult.OutputPackageDirectory))
                        CopyPackageDirectoryToStreamingAssets(buildResult.OutputPackageDirectory, Log);
                    else
                        CopyPackageToStreamingAssets(target, Log);
                }

                return true;
            }
            catch (Exception ex)
            {
                Log($"YooAsset build error: {ex.Message}");
                UnityGameFramework.Runtime.Log.Error($"[BoluoYoo] {ex}");
                return false;
            }
        }

        static void RunPreBuildSteps(Action<string> log)
        {
            try
            {
                if (AssetDatabase.LoadAssetAtPath<GameObject>(SceneAssets.IslandInlandWaterPrefabPath) == null)
                {
                    log("Building InlandWater prefab...");
                    IslandInlandWaterBuilder.Build();
                }
            }
            catch (Exception ex)
            {
                log($"InlandWater build skipped: {ex.Message}");
            }

            try
            {
                MapItemEditorMenu.EnsureFoliageClipAndShadows();
            }
            catch (Exception ex)
            {
                log($"MapItem foliage setup skipped: {ex.Message}");
            }

            log("Preprocessing complete.");
        }

        static bool HasYooAssetSettings()
        {
            string[] guids = AssetDatabase.FindAssets("t:AssetBundleCollectorSetting");
            return guids.Length > 0;
        }

        public static void CopyPackageToStreamingAssets(BuildTarget target, Action<string> log = null)
        {
            void Log(string msg)
            {
                log?.Invoke(msg);
                Debug.Log($"[BoluoYoo] {msg}");
            }

            try
            {
                string outputRoot = AssetBundleBuilderHelper.GetDefaultBuildOutputRoot();
                string packageRoot = Path.Combine(outputRoot, target.ToString(), PackageName);

                if (!Directory.Exists(packageRoot))
                {
                    Log($"Warning: Package output not found: {packageRoot}");
                    return;
                }

                string latest = FindLatestPackageVersionDir(packageRoot, PackageName);
                if (string.IsNullOrEmpty(latest))
                {
                    Log($"Warning: No package version dir with {PackageName}.version under {packageRoot}");
                    return;
                }

                CopyPackageDirectoryToStreamingAssets(latest, Log);
            }
            catch (Exception ex)
            {
                Log($"Copy error: {ex.Message}");
            }
        }

        /// <summary>
        /// Bundles/.../DefaultPackage 下除版本目录外还有 OutputCache / Simulate。
        /// 按名字排序会误选它们，导致 StreamingAssets 缺少 DefaultPackage.version。
        /// </summary>
        public static string FindLatestPackageVersionDir(string packageRoot, string packageName)
        {
            string versionFileName = $"{packageName}.version";
            string[] candidates = Directory.GetDirectories(packageRoot)
                .Where(dir =>
                {
                    string name = Path.GetFileName(dir);
                    if (NonVersionDirNames.Contains(name))
                        return false;
                    return File.Exists(Path.Combine(dir, versionFileName));
                })
                .OrderBy(dir => dir, StringComparer.Ordinal)
                .ToArray();

            return candidates.Length > 0 ? candidates[candidates.Length - 1] : null;
        }

        public static void CopyPackageDirectoryToStreamingAssets(string sourcePackageDir, Action<string> log = null)
        {
            void Log(string msg)
            {
                log?.Invoke(msg);
                Debug.Log($"[BoluoYoo] {msg}");
            }

            if (string.IsNullOrEmpty(sourcePackageDir) || !Directory.Exists(sourcePackageDir))
            {
                Log($"Warning: Package directory not found: {sourcePackageDir}");
                return;
            }

            string versionPath = Path.Combine(sourcePackageDir, $"{PackageName}.version");
            if (!File.Exists(versionPath))
            {
                Log($"Warning: Missing {PackageName}.version in {sourcePackageDir}");
                return;
            }

            string streamingRoot = AssetBundleBuilderHelper.GetStreamingAssetsRoot();
            string dest = Path.Combine(streamingRoot, PackageName);

            if (Directory.Exists(dest))
                Directory.Delete(dest, true);

            Directory.CreateDirectory(streamingRoot);
            CopyDirectoryRecursive(sourcePackageDir, dest);
            AssetDatabase.Refresh();
            Log($"Copied {sourcePackageDir} -> {dest}");
        }

        public static void CopyDirectoryRecursive(string sourceDir, string destDir)
        {
            Directory.CreateDirectory(destDir);

            foreach (string file in Directory.GetFiles(sourceDir))
            {
                string name = Path.GetFileName(file);
                if (name.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                    continue;
                File.Copy(file, Path.Combine(destDir, name), true);
            }

            foreach (string dir in Directory.GetDirectories(sourceDir))
            {
                string name = Path.GetFileName(dir);
                CopyDirectoryRecursive(dir, Path.Combine(destDir, name));
            }
        }
    }
}
#endif
