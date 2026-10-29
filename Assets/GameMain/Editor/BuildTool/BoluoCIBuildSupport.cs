#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Game.Editor.BuildTool
{
    /// <summary>
    /// BoluoProjectNew CI / 发布工具核心：环境宏、版本、YooAsset 前置、UaaL 导出。
    /// Android → AndroidProject/UnityExport/{Boluo_Android|YiShu}
    /// iOS → IOSProject/Boluo_Unity
    /// </summary>
    public static class BoluoCIBuildSupport
    {
        public const string IosUnityExportFolderName = "Boluo_Unity";
        public const string AndroidGradleExportFolderNeo = "Boluo_Android";
        public const string AndroidGradleExportFolderYishu = "YiShu";

        const string DisableLogDefine = "BOLUO_DISABLELOG";
        const string DevDefine = "BOLUO_DEV";
        const string TestDefine = "BOLUO_TEST";
        const string ProductDefine = "BOLUO_PRODUCT";
        const string PrereleaseDefine = "BOLUO_PRERELEASE";
        const string PadDefine = "BOLUO_PAD";

        static readonly BuildTargetGroup[] TargetPlatforms =
        {
            BuildTargetGroup.Android,
            BuildTargetGroup.iOS,
            BuildTargetGroup.Standalone
        };

        static NamedBuildTarget NamedTarget(BuildTargetGroup group)
        {
            switch (group)
            {
                case BuildTargetGroup.Android:
                    return NamedBuildTarget.Android;
                case BuildTargetGroup.iOS:
                    return NamedBuildTarget.iOS;
                case BuildTargetGroup.Standalone:
                    return NamedBuildTarget.Standalone;
                default:
                    throw new ArgumentOutOfRangeException(nameof(group), group, "未映射的构建平台");
            }
        }

        public enum CIEnvironment
        {
            None,
            Dev,
            Test,
            PreRelease,
            Product
        }

        public enum CIVersionBuildType
        {
            Release,
            Test
        }

        public sealed class CIConfig
        {
            public CIEnvironment Environment = CIEnvironment.Product;
            public CIVersionBuildType VersionBuildType = CIVersionBuildType.Release;
            public string Version = "1.0.0";
            public string OutputDir;
            public string Channel;
            public string ResourceVersion;
            public string BuildNumberOverride;
            public string AndroidKeystorePass;
            public string AndroidKeyPass;
            public bool DisableLog;
            public bool SkipYooAsset;
            public bool DevelopmentBuild;
            public string ScreenOrientation;
            public string AndroidAbi;
            public string AndroidFlavor;
            public BuildTarget BuildTarget;
        }

        public static string GetRepoRoot()
        {
            // Assets → CustomSRP → BoluoProjectNew
            return Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));
        }

        public static string GetDefaultAndroidUnityExportRoot()
        {
            return Path.Combine(GetRepoRoot(), "AndroidProject", "UnityExport");
        }

        public static string GetDefaultIosOutputRoot()
        {
            return Path.Combine(GetRepoRoot(), "IOSProject");
        }

        public static bool TryLoadConfigFromEnvironment(out CIConfig config, out string error) =>
            TryLoadConfig(out config, out error);

        public static bool TryLoadConfig(out CIConfig config, out string error)
        {
            string versionRaw = BoluoCIArgParser.Resolve("BOLUO_CI_VERSION", "boluoVersion", "version")
                                ?? PlayerSettings.bundleVersion;
            string channel = BoluoCIArgParser.Resolve("BOLUO_CI_CHANNEL", "boluoChannel", "channel");
            string outputDir = BoluoCIArgParser.Resolve("BOLUO_CI_OUTPUT_DIR", "boluoOutput", "output");
            string resourceVersion = BoluoCIArgParser.Resolve("BOLUO_CI_RESOURCE_VERSION", "boluoResourceVersion", "resourceVersion");
            string buildNumberOverride = BoluoCIArgParser.Resolve("BOLUO_CI_BUILD_NUMBER", "boluoBuildNumber", "buildNumber");

            config = new CIConfig
            {
                OutputDir = outputDir,
                Channel = channel,
                Version = versionRaw,
                AndroidKeystorePass = BoluoCIArgParser.Resolve("BOLUO_ANDROID_KEYSTORE_PASS", "boluoKeystorePass"),
                AndroidKeyPass = BoluoCIArgParser.Resolve("BOLUO_ANDROID_KEY_PASS", "boluoKeyPass"),
                ResourceVersion = resourceVersion,
                BuildNumberOverride = buildNumberOverride,
                DisableLog = ParseBool(BoluoCIArgParser.Resolve("BOLUO_CI_DISABLE_LOG", "boluoDisableLog"), false),
                SkipYooAsset = ParseBool(BoluoCIArgParser.Resolve("BOLUO_CI_SKIP_YOOASSET", "boluoSkipYooAsset"), false),
                DevelopmentBuild = ParseBool(BoluoCIArgParser.Resolve("BOLUO_DEV_BUILD", "boluoDevBuild"), false),
                AndroidAbi = BoluoCIArgParser.Resolve("BOLUO_CI_ANDROID_ABI", "boluoAndroidAbi"),
                AndroidFlavor = BoluoCIArgParser.Resolve("BOLUO_CI_ANDROID_FLAVOR", "boluoAndroidFlavor", "androidFlavor"),
            };

            string target = (BoluoCIArgParser.Resolve("BOLUO_CI_BUILD_TARGET", "boluoPlatform", "platform") ?? "android")
                .ToLowerInvariant();
            config.BuildTarget = target switch
            {
                "ios" => BuildTarget.iOS,
                "android" => BuildTarget.Android,
                _ => BuildTarget.NoTarget
            };

            if (config.BuildTarget == BuildTarget.NoTarget)
            {
                error = $"不支持的 BOLUO_CI_BUILD_TARGET: {target}（可用 android / ios）";
                return false;
            }

            string envRaw = BoluoCIArgParser.Resolve("BOLUO_CI_ENV", "boluoEnv", "env") ?? "product";
            if (!TryParseEnvironment(envRaw, out config.Environment))
            {
                error = "BOLUO_CI_ENV / -boluoEnv 无效，可用: dev | test | prerelease | product | none";
                return false;
            }

            string versionType = (BoluoCIArgParser.Resolve("BOLUO_CI_VERSION_TYPE", "boluoVersionType", "versionType") ?? "release")
                .ToLowerInvariant();
            config.VersionBuildType = versionType is "test" or "debug"
                ? CIVersionBuildType.Test
                : CIVersionBuildType.Release;

            config.Version = NormalizeMarketingVersion(config.Version);
            if (!TryParseMarketingVersion(config.Version, out _))
            {
                error = $"版本号格式错误，须为 x.x.x，当前: {config.Version}";
                return false;
            }

            if (!string.IsNullOrWhiteSpace(config.BuildNumberOverride) &&
                !int.TryParse(config.BuildNumberOverride.Trim(), out _))
            {
                error = $"Build 号须为整数，当前: {config.BuildNumberOverride}";
                return false;
            }

            string orientationRaw = (BoluoCIArgParser.Resolve("BOLUO_CI_ORIENTATION", "boluoOrientation", "orientation") ?? "")
                .Trim()
                .ToLowerInvariant();
            if (!string.IsNullOrEmpty(orientationRaw) &&
                orientationRaw != "landscape" &&
                orientationRaw != "portrait" &&
                orientationRaw != "sensor")
            {
                error = $"方向无效，可用: landscape | portrait | sensor，当前: {orientationRaw}";
                return false;
            }

            if (string.IsNullOrEmpty(orientationRaw) && config.BuildTarget == BuildTarget.Android)
                orientationRaw = "sensor";

            config.ScreenOrientation = orientationRaw;

            if (!string.IsNullOrWhiteSpace(config.AndroidAbi))
            {
                string abiNorm = config.AndroidAbi.Trim().ToLowerInvariant();
                config.AndroidAbi = abiNorm switch
                {
                    "arm64" or "arm64-v8a" => "arm64-v8a",
                    "armeabi" or "armv7" or "armeabi-v7a" => "armeabi-v7a",
                    _ => null
                };
                if (config.AndroidAbi == null)
                {
                    error = $"ABI 无效，可用: arm64-v8a | armeabi-v7a，当前: {abiNorm}";
                    return false;
                }
            }

            if (!TryNormalizeAndroidFlavor(config.AndroidFlavor, out string flavorNorm, out string flavorError))
            {
                error = flavorError;
                return false;
            }

            config.AndroidFlavor = flavorNorm;

            if (string.IsNullOrWhiteSpace(config.OutputDir))
            {
                config.OutputDir = config.BuildTarget == BuildTarget.iOS
                    ? GetDefaultIosOutputRoot()
                    : GetDefaultAndroidUnityExportRoot();
            }

            error = null;
            Debug.Log(
                $"[BoluoCI] version={config.Version} env={config.Environment} type={config.VersionBuildType}" +
                (string.IsNullOrEmpty(config.ScreenOrientation) ? "" : $" orient={config.ScreenOrientation}") +
                (string.IsNullOrEmpty(config.AndroidFlavor) ? "" : $" flavor={config.AndroidFlavor}") +
                $" output={config.OutputDir}");
            return true;
        }

        /// <summary>
        /// 对齐 AndroidProject/Docs/渠道.md §4：创维 / 一树 / 手机 9 市场。
        /// 短名别名（gmj/pad/xiaomi…）映射到正式 productFlavor。
        /// </summary>
        public static bool TryNormalizeAndroidFlavor(string raw, out string flavor, out string error)
        {
            flavor = "skyworthUltra";
            error = null;
            if (string.IsNullOrWhiteSpace(raw))
                return true;

            string key = raw.Trim().ToLowerInvariant().Replace("_", "").Replace("-", "");
            switch (key)
            {
                case "skyworthultra":
                case "ultra":
                case "sks32":
                case "4k":
                case "gmj":
                case "guimiji":
                case "cwgmj":
                case "pad":
                    flavor = "skyworthUltra";
                    return true;
                case "skyworthneo":
                case "neo":
                    flavor = "skyworthNeo";
                    return true;
                case "yishu":
                case "yishuprojection":
                    flavor = "yishu";
                    return true;
                case "phoneappstore":
                case "appstore":
                case "yingyongbao":
                case "yyb":
                    flavor = "phoneAppstore";
                    return true;
                case "phonebaidu":
                case "baidu":
                    flavor = "phoneBaidu";
                    return true;
                case "phone360":
                case "qihoo360":
                case "360":
                    flavor = "phone360";
                    return true;
                case "phonexiaomi":
                case "xiaomi":
                    flavor = "phoneXiaomi";
                    return true;
                case "phoneoppo":
                case "oppo":
                    flavor = "phoneOppo";
                    return true;
                case "phonevivo":
                case "vivo":
                    flavor = "phoneVivo";
                    return true;
                case "phonehonor":
                case "honor":
                    flavor = "phoneHonor";
                    return true;
                case "phonesamsung":
                case "samsung":
                case "gallery":
                case "phonegallery":
                    flavor = "phoneSamsung";
                    return true;
                case "phonemeizu":
                case "meizu":
                    flavor = "phoneMeizu";
                    return true;
                default:
                    error =
                        "flavor 无效，可用: skyworthUltra | skyworthNeo | yishu | " +
                        "phoneAppstore|Baidu|360|Xiaomi|Oppo|Vivo|Honor|Samsung|Meizu，当前: " + raw;
                    return false;
            }
        }

        /// <summary>yishu → YiShu；其余（创维 + 手机 9 市场）→ Boluo_Android。</summary>
        public static string ResolveAndroidGradleExportFolderName(string flavor)
        {
            if (!TryNormalizeAndroidFlavor(flavor, out string normalized, out _))
                return AndroidGradleExportFolderNeo;
            return normalized == "yishu"
                ? AndroidGradleExportFolderYishu
                : AndroidGradleExportFolderNeo;
        }

        /// <summary>productFlavor → Gradle 任务 Pascal：phoneXiaomi → PhoneXiaomi。</summary>
        public static string ToGradleFlavorPascal(string flavor)
        {
            if (!TryNormalizeAndroidFlavor(flavor, out string norm, out _) || string.IsNullOrEmpty(norm))
                norm = "skyworthUltra";
            return char.ToUpperInvariant(norm[0]) + norm.Substring(1);
        }

        public static string ResolveAndroidGradleRoot(string outputDir, string flavor = null)
        {
            string folderName = ResolveAndroidGradleExportFolderName(flavor);
            string normalized = (outputDir ?? "").Replace('\\', '/').TrimEnd('/');
            if (string.IsNullOrEmpty(normalized))
                return folderName;

            string leaf = Path.GetFileName(normalized);
            if (string.Equals(leaf, AndroidGradleExportFolderNeo, StringComparison.OrdinalIgnoreCase)
                || string.Equals(leaf, AndroidGradleExportFolderYishu, StringComparison.OrdinalIgnoreCase))
                return normalized;

            return Path.Combine(normalized, folderName).Replace('\\', '/');
        }

        public static List<string> BuildBoluoDefineList(CIConfig config)
        {
            var boluoDefines = new List<string>();
            if (config.DisableLog)
                boluoDefines.Add(DisableLogDefine);

            if (config.ScreenOrientation == "landscape" || config.ScreenOrientation == "sensor")
                boluoDefines.Add(PadDefine);

            switch (config.Environment)
            {
                case CIEnvironment.Dev:
                    boluoDefines.Add(DevDefine);
                    break;
                case CIEnvironment.Test:
                    boluoDefines.Add(TestDefine);
                    break;
                case CIEnvironment.PreRelease:
                    boluoDefines.Add(PrereleaseDefine);
                    break;
                case CIEnvironment.Product:
                    boluoDefines.Add(ProductDefine);
                    break;
            }

            return boluoDefines;
        }

        /// <summary>
        /// 写入 BOLUO_* 宏。若任一平台宏实际变更，返回 true（会触发脚本重编 / 域重载）。
        /// 调用方必须在变更后 defer 构建：否则 YooAsset/BuildPlayer 会报
        /// "script class layout is incompatible between the editor and the player"。
        /// </summary>
        public static bool ApplyDefines(CIConfig config)
        {
            var boluoDefines = BuildBoluoDefineList(config);
            bool changed = false;

            foreach (BuildTargetGroup group in TargetPlatforms)
            {
                NamedBuildTarget named = NamedTarget(group);
                var defines = PlayerSettings.GetScriptingDefineSymbols(named)
                    .Split(';').Where(d => !string.IsNullOrEmpty(d)).ToList();
                var before = new HashSet<string>(defines, StringComparer.Ordinal);
                defines.RemoveAll(d =>
                    d == DisableLogDefine || d == PadDefine ||
                    d == DevDefine || d == TestDefine || d == PrereleaseDefine || d == ProductDefine);
                defines.AddRange(boluoDefines);
                var after = new HashSet<string>(defines, StringComparer.Ordinal);
                if (before.SetEquals(after))
                    continue;
                PlayerSettings.SetScriptingDefineSymbols(named, string.Join(";", defines));
                changed = true;
            }

            ApplyScreenOrientation(config);
            Debug.Log(changed
                ? $"[BoluoCI] 已应用宏（有变更，需域重载）: {string.Join(", ", boluoDefines)}"
                : $"[BoluoCI] 宏已对齐（无变更）: {string.Join(", ", boluoDefines)}");
            return changed;
        }

        public static void ApplyVersion(CIConfig config)
        {
            if (!TryParseMarketingVersion(NormalizeMarketingVersion(config.Version), out string marketing))
                throw new InvalidOperationException($"版本号无效: {config.Version}");

            string buildNumber = !string.IsNullOrWhiteSpace(config.BuildNumberOverride)
                ? config.BuildNumberOverride.Trim()
                : DeriveBuildNumber(marketing);

            PlayerSettings.bundleVersion = marketing;
            PlayerSettings.iOS.buildNumber = buildNumber;

            if (!string.IsNullOrWhiteSpace(config.ResourceVersion) &&
                int.TryParse(config.ResourceVersion.Trim(), out int versionCode))
            {
                PlayerSettings.Android.bundleVersionCode = versionCode;
            }
            else if (int.TryParse(buildNumber, out int bn))
            {
                PlayerSettings.Android.bundleVersionCode = bn;
            }

            string spec = config.VersionBuildType == CIVersionBuildType.Test
                ? $"{marketing}.{DateTime.Now:MMddHHmm}"
                : marketing;
            Debug.Log($"[BoluoCI] 版本 marketing={marketing} build={buildNumber} spec={spec}");
        }

        public static void LogCiFailure(string message) =>
            Debug.LogError($"[BoluoCI] CI_FAILURE: {message}");

        /// <summary>导出 Android Gradle 工程到 UnityExport/{Boluo_Android|YiShu}。</summary>
        public static int ExportAndroidGradle(CIConfig config)
        {
            // 构建目标必须是 Android：Unity 不允许在活动目标不是 Android 时构建 Android 目标
            //（报错 "build target was unsupported"）。已开着 Editor 时切换目标会触发域重载，
            // 所以这里返回 3（需重试），由调用方在域重载后再跑一次。
            if (!EnsureBuildTarget(BuildTarget.Android, out string targetError))
            {
                LogCiFailure(targetError);
                return 3;
            }
            if (string.IsNullOrEmpty(config.ScreenOrientation))
                config.ScreenOrientation = "sensor";

            // 宏变更会触发域重载：先 return 3，由 HostShell / CLI 在重载后再跑，避免 AB 构建踩 class layout
            if (ApplyDefines(config))
            {
                Debug.Log("[BoluoCI] 宏变更，等待域重载后重试导出");
                return 3;
            }
            try
            {
                ApplyVersion(config);
            }
            catch (Exception ex)
            {
                LogCiFailure(ex.Message);
                return 2;
            }

            if (!config.SkipYooAsset)
            {
                if (!BoluoYooAssetBuildPipeline.Run(BuildTarget.Android, copyToStreamingAssets: true))
                {
                    LogCiFailure("YooAsset 前置构建失败");
                    return 1;
                }
            }

            AndroidArchitecture originalArch = PlayerSettings.Android.targetArchitectures;
            try
            {
                PrepareAndroidPlayerSettings(config);
                AssetDatabase.Refresh();
                AssetDatabase.SaveAssets();

                Directory.CreateDirectory(config.OutputDir);
                string gradleRoot = ResolveAndroidGradleRoot(config.OutputDir, config.AndroidFlavor);

                string playerData = Path.Combine(gradleRoot, "unityLibrary", "src", "main", "assets", "bin", "Data");
                if (Directory.Exists(playerData))
                {
                    Directory.Delete(playerData, true);
                    Debug.Log($"[BoluoCI] 已删除旧 Player Data: {playerData}");
                }

                Debug.Log($"[BoluoCI] 导出 Android Gradle → {gradleRoot}");
                if (!BuildGradleProject(gradleRoot, config))
                    return 1;

                string unityLibrary = Path.Combine(gradleRoot, "unityLibrary");
                if (!Directory.Exists(unityLibrary))
                {
                    LogCiFailure($"导出完成但未找到 unityLibrary: {unityLibrary}");
                    return 1;
                }

                // 导出会覆盖 Gradle/CMake；对齐宿主 :unityLibraryNeo|:unityLibraryYishu + CMake≥3.22.1
                BoluoAndroidUnityExportHostPatch.Apply(gradleRoot);

                Debug.Log($"[BoluoCI] Android unityLibrary 导出成功: {unityLibrary}");
                return 0;
            }
            finally
            {
                if (PlayerSettings.Android.targetArchitectures != originalArch)
                    PlayerSettings.Android.targetArchitectures = originalArch;
            }
        }

        /// <summary>导出 iOS Xcode 工程到 IOSProject/Boluo_Unity。</summary>
        public static int BuildiOS(CIConfig config)
        {
            SwitchBuildTarget(BuildTarget.iOS);
            if (ApplyDefines(config))
            {
                Debug.Log("[BoluoCI] 宏变更，等待域重载后重试导出");
                return 3;
            }
            try
            {
                ApplyVersion(config);
            }
            catch (Exception ex)
            {
                LogCiFailure(ex.Message);
                return 2;
            }

            if (!config.SkipYooAsset)
            {
                if (!BoluoYooAssetBuildPipeline.Run(BuildTarget.iOS, copyToStreamingAssets: true))
                {
                    LogCiFailure("YooAsset 前置构建失败");
                    return 1;
                }
            }

            Directory.CreateDirectory(config.OutputDir);
            string xcodeProjectPath = Path.Combine(config.OutputDir, IosUnityExportFolderName).Replace('\\', '/');

            Debug.Log($"[BoluoCI] 导出 Xcode → {xcodeProjectPath}");
            if (!BuildPlayer(xcodeProjectPath, BuildTarget.iOS, BuildTargetGroup.iOS, config))
                return 1;

            Debug.Log($"[BoluoCI] iOS 导出成功: {xcodeProjectPath}");
            return 0;
        }

        /// <summary>独立 APK（非宿主），默认 Builds/Android/Game.apk。</summary>
        public static int BuildAndroidApk(CIConfig config)
        {
            if (!EnsureBuildTarget(BuildTarget.Android, out string apkTargetError))
            {
                LogCiFailure(apkTargetError);
                return 3;
            }
            if (string.IsNullOrEmpty(config.ScreenOrientation))
                config.ScreenOrientation = "sensor";

            BoluoAndroidBuildHelper.ApplyBeforeApkPack();
            if (ApplyDefines(config))
            {
                Debug.Log("[BoluoCI] 宏变更，等待域重载后重试导出");
                return 3;
            }
            try
            {
                ApplyVersion(config);
            }
            catch (Exception ex)
            {
                LogCiFailure(ex.Message);
                return 2;
            }

            if (!config.SkipYooAsset)
            {
                if (!BoluoYooAssetBuildPipeline.Run(BuildTarget.Android, copyToStreamingAssets: true))
                {
                    LogCiFailure("YooAsset 前置构建失败");
                    return 1;
                }
            }

            PrepareAndroidPlayerSettings(config);

            string apkPath = string.IsNullOrWhiteSpace(config.OutputDir)
                ? Path.Combine(Application.dataPath, "..", "Builds", "Android", "Game.apk")
                : config.OutputDir;
            apkPath = Path.GetFullPath(apkPath);
            if (!apkPath.EndsWith(".apk", StringComparison.OrdinalIgnoreCase))
                apkPath = Path.Combine(apkPath, "Game.apk");

            Directory.CreateDirectory(Path.GetDirectoryName(apkPath) ?? ".");

            var options = new BuildPlayerOptions
            {
                scenes = GetEnabledScenes(),
                locationPathName = apkPath,
                target = BuildTarget.Android,
                targetGroup = BuildTargetGroup.Android,
                options = BuildPlayerOptionsFlags(config)
            };

            BuildReport report;
            try
            {
                report = BoluoAndroidBuildHelper.BuildApk(options);
            }
            catch (Exception ex)
            {
                LogCiFailure($"BuildApk 异常: {ex.Message}");
                return 1;
            }

            if (report.summary.result != BuildResult.Succeeded)
            {
                LogCiFailure($"APK 失败: {report.summary.result}");
                LogBuildReportErrors(report);
                return 1;
            }

            Debug.Log($"[BoluoCI] APK 成功: {apkPath}");
            return 0;
        }

        static void PrepareAndroidPlayerSettings(CIConfig config)
        {
            if (PlayerSettings.Android.predictiveBackSupport)
                PlayerSettings.Android.predictiveBackSupport = false;

            if (!string.IsNullOrEmpty(config.AndroidKeystorePass))
                PlayerSettings.Android.keystorePass = config.AndroidKeystorePass;
            if (!string.IsNullOrEmpty(config.AndroidKeyPass))
                PlayerSettings.Android.keyaliasPass = config.AndroidKeyPass;

            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);
            // TerrainLayer (class ID 1953259897) only appears in YooAsset bundles. Strip Engine
            // Code drops it → Instantiate IslandTerrain fails → no splat textures / collision.
            // ForceIncludeEngineTypes + Resources keep-alive help, but disable strip for Android
            // player so streamed TerrainData layers always deserialize.
            if (PlayerSettings.stripEngineCode)
            {
                Debug.Log("[BoluoCI] stripEngineCode=false (keep TerrainLayer for Yoo IslandTerrain)");
                PlayerSettings.stripEngineCode = false;
            }
            // 调试符号包（libil2cpp 未裁剪符号 + libunity.sym.so）。不打开 Development Build，
            // 运行时内存布局与正式包一致，只多一份符号化用的符号。
            EditorUserBuildSettings.androidCreateSymbols = AndroidCreateSymbols.Debugging;

            bool yishu = string.Equals(config.AndroidFlavor, "yishu", StringComparison.Ordinal);
            if (!string.IsNullOrEmpty(config.AndroidAbi))
            {
                PlayerSettings.Android.targetArchitectures = config.AndroidAbi == "armeabi-v7a"
                    ? AndroidArchitecture.ARMv7
                    : AndroidArchitecture.ARM64;
            }
            else if (yishu)
            {
                PlayerSettings.Android.targetArchitectures =
                    AndroidArchitecture.ARMv7 | AndroidArchitecture.ARM64;
            }
            else
            {
                PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            }
        }

        static void ApplyScreenOrientation(CIConfig config)
        {
            if (string.IsNullOrEmpty(config.ScreenOrientation))
                return;

            switch (config.ScreenOrientation)
            {
                case "portrait":
                    PlayerSettings.defaultInterfaceOrientation = UIOrientation.Portrait;
                    break;
                case "landscape":
                    PlayerSettings.defaultInterfaceOrientation = UIOrientation.LandscapeLeft;
                    break;
                case "sensor":
                    PlayerSettings.defaultInterfaceOrientation = UIOrientation.AutoRotation;
                    PlayerSettings.allowedAutorotateToPortrait = true;
                    PlayerSettings.allowedAutorotateToPortraitUpsideDown = false;
                    PlayerSettings.allowedAutorotateToLandscapeLeft = true;
                    PlayerSettings.allowedAutorotateToLandscapeRight = true;
                    break;
            }
        }

        static bool BuildGradleProject(string locationPathName, CIConfig config)
        {
            var buildOptions = new BuildPlayerOptions
            {
                scenes = GetEnabledScenes(),
                locationPathName = locationPathName,
                target = BuildTarget.Android,
                targetGroup = BuildTargetGroup.Android,
                options = BuildPlayerOptionsFlags(config) | BuildOptions.CleanBuildCache
            };

            BuildReport report;
            try
            {
                report = BoluoAndroidBuildHelper.BuildGradleProject(buildOptions);
            }
            catch (Exception ex)
            {
                LogCiFailure($"BuildGradleProject 异常: {ex.Message}");
                return false;
            }

            if (report.summary.result != BuildResult.Succeeded)
            {
                LogCiFailure($"BuildGradleProject 失败: {report.summary.result}");
                LogBuildReportErrors(report);
                return false;
            }

            return true;
        }

        static bool BuildPlayer(
            string locationPathName,
            BuildTarget target,
            BuildTargetGroup targetGroup,
            CIConfig config)
        {
            var buildOptions = new BuildPlayerOptions
            {
                scenes = GetEnabledScenes(),
                locationPathName = locationPathName,
                target = target,
                targetGroup = targetGroup,
                options = BuildPlayerOptionsFlags(config) | BuildOptions.CleanBuildCache
            };

            BuildReport report;
            try
            {
                report = BuildPipeline.BuildPlayer(buildOptions);
            }
            catch (Exception ex)
            {
                LogCiFailure($"BuildPlayer 异常 ({target}): {ex.Message}");
                return false;
            }

            if (report.summary.result != BuildResult.Succeeded)
            {
                LogCiFailure($"BuildPlayer 失败: {report.summary.result}");
                LogBuildReportErrors(report);
                return false;
            }

            return true;
        }

        static BuildOptions BuildPlayerOptionsFlags(CIConfig config)
        {
            var options = BuildOptions.None;
            if (config.DevelopmentBuild)
                options |= BuildOptions.Development | BuildOptions.AllowDebugging;
            return options;
        }

        static string[] GetEnabledScenes()
        {
            string[] scenes = EditorBuildSettings.scenes
                .Where(s => s.enabled)
                .Select(s => s.path)
                .ToArray();
            if (scenes.Length == 0)
                scenes = new[] { "Assets/GameMain/Scenes/Launch.unity" };
            return scenes;
        }

        /// <summary>
        /// 确保活动构建目标就是 target。
        /// 返回 true = 可以直接构建；false = 需要切换（已发起切换 / batchmode 无法切换），调用方应重试。
        /// </summary>
        static bool EnsureBuildTarget(BuildTarget target, out string error)
        {
            if (EditorUserBuildSettings.activeBuildTarget == target)
            {
                error = null;
                return true;
            }

            BuildTargetGroup group = target == BuildTarget.iOS
                ? BuildTargetGroup.iOS
                : BuildTargetGroup.Android;

            if (Application.isBatchMode)
            {
                error = $"batchmode 下无法切换构建目标，请用 -buildTarget {target} 启动 Unity 后重试";
                return false;
            }

            Debug.Log($"[BoluoCI] Switching platform → {target}（切换完成（域重载）后需重试导出）");
            EditorUserBuildSettings.SwitchActiveBuildTarget(group, target);
            error = $"构建目标正在切换到 {target}，请在切换完成后重试导出（exit=3）";
            return false;
        }

        static void SwitchBuildTarget(BuildTarget target)
        {
            EnsureBuildTarget(target, out _);
        }

        static void LogBuildReportErrors(BuildReport report)
        {
            if (report?.steps == null)
                return;

            int logged = 0;
            const int maxLines = 40;
            foreach (var step in report.steps)
            {
                foreach (var message in step.messages)
                {
                    if (message.type != LogType.Error && message.type != LogType.Exception &&
                        message.type != LogType.Assert)
                        continue;
                    if (string.IsNullOrWhiteSpace(message.content))
                        continue;
                    LogCiFailure($"[{step.name}] {message.content}");
                    logged++;
                    if (logged >= maxLines)
                        return;
                }
            }
        }

        static bool TryParseEnvironment(string raw, out CIEnvironment env)
        {
            env = CIEnvironment.None;
            if (string.IsNullOrWhiteSpace(raw))
                return false;

            switch (raw.Trim().ToLowerInvariant())
            {
                case "none":
                    env = CIEnvironment.None;
                    return true;
                case "dev":
                case "development":
                    env = CIEnvironment.Dev;
                    return true;
                case "test":
                    env = CIEnvironment.Test;
                    return true;
                case "prerelease":
                case "pre":
                    env = CIEnvironment.PreRelease;
                    return true;
                case "product":
                case "prod":
                case "release":
                    env = CIEnvironment.Product;
                    return true;
                default:
                    return false;
            }
        }

        static bool ParseBool(string raw, bool defaultValue)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return defaultValue;
            raw = raw.Trim().ToLowerInvariant();
            if (raw is "1" or "true" or "yes" or "on")
                return true;
            if (raw is "0" or "false" or "no" or "off")
                return false;
            return defaultValue;
        }

        static string NormalizeMarketingVersion(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return "1.0.0";
            raw = raw.Trim();
            // Strip trailing -test / .build stamps for parsing
            Match m = Regex.Match(raw, @"^(\d+\.\d+\.\d+)");
            return m.Success ? m.Groups[1].Value : raw;
        }

        static bool TryParseMarketingVersion(string raw, out string marketing)
        {
            marketing = null;
            if (string.IsNullOrWhiteSpace(raw))
                return false;
            Match m = Regex.Match(raw.Trim(), @"^(\d+\.\d+\.\d+)$");
            if (!m.Success)
                return false;
            marketing = m.Groups[1].Value;
            return true;
        }

        static string DeriveBuildNumber(string marketing)
        {
            // 1.1.60 → 11060
            string[] parts = marketing.Split('.');
            if (parts.Length == 3
                && int.TryParse(parts[0], out int major)
                && int.TryParse(parts[1], out int minor)
                && int.TryParse(parts[2], out int patch))
            {
                return (major * 10000 + minor * 100 + patch).ToString();
            }

            return ((int)(DateTimeOffset.Now.ToUnixTimeSeconds() / 86400)).ToString();
        }
    }
}
#endif
