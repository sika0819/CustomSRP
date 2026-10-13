#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityGameFramework.Runtime;
using YooAsset.Editor;

using Game; // for SceneAssets

namespace Game.Editor
{
    /// <summary>
    /// 统一打包工具编辑器窗口。
    /// 整合 YooAsset 资源打包 + APK/Xcode 项目构建，支持 Android 和 iOS 全量一键打包。
    /// 
    /// 使用方式：
    /// - 菜单：Window/Game/Build Tool
    /// - 命令行：Unity -batchmode -quit -executeMethod Game.Editor.GameBuildWindow.BuildAndroid
    ///           Unity -batchmode -quit -executeMethod Game.Editor.GameBuildWindow.BuildIOS
    /// </summary>
    public class GameBuildWindow : EditorWindow
    {
        const string WindowTitle = "Game Build Tool";
        const string MenuPath = "Window/Game/Build Tool";

        const string PackageName = "DefaultPackage";

        // 构建配置
        BuildTarget _targetPlatform = BuildTarget.Android;
        bool _buildResource = true;
        bool _copyToStreamingAssets = true;
        bool _developmentBuild = false;
        bool _installAfterBuild = false;

        // 路径配置
        string _androidApkPath = "Builds/Android/Game.apk";
        string _iosXcodePath = "Builds/iOS";

        // GUI 状态
        Vector2 _scrollPosition;
        bool _isBuilding;
        string _buildLog = "";

        // Android 包配置
        const string AndroidPackageId = "com.customsrp.oilempty";
        const string AndroidProductName = "CustomSRP Game";

        [MenuItem(MenuPath, false, 1000)]
        public static void ShowWindow()
        {
            var window = GetWindow<GameBuildWindow>(WindowTitle, true);
            window.minSize = new Vector2(500, 600);
            window.Show();
        }

        /// <summary>
        /// 命令行调用：构建 Android 全量包
        /// </summary>
        public static void BuildAndroid()
        {
            var window = CreateInstance<GameBuildWindow>();
            window._targetPlatform = BuildTarget.Android;
            window._buildResource = true;
            window._copyToStreamingAssets = true;
            window._developmentBuild = false;
            window._installAfterBuild = false;
            window.ExecuteBuild();
        }

        /// <summary>
        /// 命令行调用：构建 Android 开发包
        /// </summary>
        public static void BuildAndroidDevelopment()
        {
            var window = CreateInstance<GameBuildWindow>();
            window._targetPlatform = BuildTarget.Android;
            window._buildResource = true;
            window._copyToStreamingAssets = true;
            window._developmentBuild = true;
            window._installAfterBuild = false;
            window.ExecuteBuild();
        }

        /// <summary>
        /// 命令行调用：构建 iOS 全量包
        /// </summary>
        public static void BuildIOS()
        {
            var window = CreateInstance<GameBuildWindow>();
            window._targetPlatform = BuildTarget.iOS;
            window._buildResource = true;
            window._copyToStreamingAssets = true;
            window._developmentBuild = false;
            window._installAfterBuild = false;
            window.ExecuteBuild();
        }

        void OnGUI()
        {
            _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition);
            {
                DrawHeader();
                EditorGUILayout.Space(10);
                DrawPlatformSelection();
                EditorGUILayout.Space(10);
                DrawBuildOptions();
                EditorGUILayout.Space(10);
                DrawBuildActions();
                EditorGUILayout.Space(10);
                DrawBuildLog();
            }
            EditorGUILayout.EndScrollView();
        }

        void DrawHeader()
        {
            EditorGUILayout.BeginVertical("box");
            {
                EditorGUILayout.LabelField("Game Build Tool", EditorStyles.boldLabel);
                EditorGUILayout.HelpBox(
                    "统一打包工具：整合 YooAsset 资源打包 + APK/Xcode 项目构建。\n" +
                    "支持 Android 和 iOS 全量一键打包。",
                    MessageType.Info);
            }
            EditorGUILayout.EndVertical();
        }

        void DrawPlatformSelection()
        {
            EditorGUILayout.BeginVertical("box");
            {
                EditorGUILayout.LabelField("Platform", EditorStyles.boldLabel);

                _targetPlatform = (BuildTarget)EditorGUILayout.EnumPopup(
                    "Target Platform", _targetPlatform);

                if (_targetPlatform == BuildTarget.Android)
                {
                    EditorGUI.indentLevel++;
                    _androidApkPath = EditorGUILayout.TextField("APK Path", _androidApkPath);
                    EditorGUI.indentLevel--;
                }
                else if (_targetPlatform == BuildTarget.iOS)
                {
                    EditorGUI.indentLevel++;
                    _iosXcodePath = EditorGUILayout.TextField("Xcode Project Path", _iosXcodePath);
                    EditorGUI.indentLevel--;
                }
            }
            EditorGUILayout.EndVertical();
        }

        void DrawBuildOptions()
        {
            EditorGUILayout.BeginVertical("box");
            {
                EditorGUILayout.LabelField("Build Options", EditorStyles.boldLabel);

                _buildResource = EditorGUILayout.Toggle(
                    "Build YooAsset Resources", _buildResource);

                if (_targetPlatform == BuildTarget.Android && _buildResource)
                {
                    EditorGUI.indentLevel++;
                    _copyToStreamingAssets = EditorGUILayout.Toggle(
                        "Copy to StreamingAssets", _copyToStreamingAssets);
                    EditorGUI.indentLevel--;
                }

                _developmentBuild = EditorGUILayout.Toggle(
                    "Development Build", _developmentBuild);

                if (_targetPlatform == BuildTarget.Android)
                {
                    _installAfterBuild = EditorGUILayout.Toggle(
                        "Install After Build (adb)", _installAfterBuild);
                }
            }
            EditorGUILayout.EndVertical();
        }

        void DrawBuildActions()
        {
            EditorGUILayout.BeginVertical("box");
            {
                EditorGUILayout.LabelField("Build Actions", EditorStyles.boldLabel);

                EditorGUILayout.BeginHorizontal();
                {
                    GUI.enabled = !_isBuilding && _targetPlatform == BuildTarget.Android;
                    if (GUILayout.Button("Build Android", GUILayout.Height(40)))
                    {
                        _targetPlatform = BuildTarget.Android;
                        ExecuteBuild();
                    }

                    GUI.enabled = !_isBuilding && _targetPlatform == BuildTarget.iOS;
                    if (GUILayout.Button("Build iOS", GUILayout.Height(40)))
                    {
                        _targetPlatform = BuildTarget.iOS;
                        ExecuteBuild();
                    }
                    GUI.enabled = true;
                }
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.Space(5);

                EditorGUILayout.BeginHorizontal();
                {
                    GUI.enabled = !_isBuilding;
                    if (GUILayout.Button("Build All (Android + iOS)"))
                    {
                        ExecuteBuildAll();
                    }

                    if (GUILayout.Button("Clean Build Cache"))
                    {
                        CleanBuildCache();
                    }
                    GUI.enabled = true;
                }
                EditorGUILayout.EndHorizontal();

                if (_isBuilding)
                {
                    EditorGUILayout.Space(10);
                    EditorGUILayout.LabelField("Building...", EditorStyles.boldLabel);
                    EditorGUILayout.LabelField("Please wait...");
                    Repaint();
                }
            }
            EditorGUILayout.EndVertical();
        }

        void DrawBuildLog()
        {
            if (!string.IsNullOrEmpty(_buildLog))
            {
                EditorGUILayout.BeginVertical("box");
                {
                    EditorGUILayout.LabelField("Build Log", EditorStyles.boldLabel);
                    EditorGUILayout.SelectableLabel(_buildLog, GUILayout.Height(100));
                }
                EditorGUILayout.EndVertical();
            }
        }

        void ExecuteBuild()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                AppendLog("Error: Exit Play Mode first!");
                return;
            }

            _isBuilding = true;
            _buildLog = "";

            try
            {
                AppendLog($"=== Build Start: {_targetPlatform} ===");
                AppendLog($"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                AppendLog($"Development: {_developmentBuild}");
                AppendLog($"Build Resource: {_buildResource}");
                AppendLog($"Copy to StreamingAssets: {_copyToStreamingAssets}");
                AppendLog("");

                // 1. Switch platform if needed
                if (!SwitchToTargetPlatform())
                {
                    return;
                }

                // 2. Build YooAsset resources
                if (_buildResource)
                {
                    if (!BuildYooAssetResources())
                    {
                        return;
                    }

                    if (_copyToStreamingAssets)
                    {
                        CopyPackageToStreamingAssets();
                    }
                }

                // 3. Build player
                if (_targetPlatform == BuildTarget.Android)
                {
                    BuildAndroidPlayer();
                }
                else if (_targetPlatform == BuildTarget.iOS)
                {
                    BuildIOSPlayer();
                }

                AppendLog("");
                AppendLog("=== Build Complete ===");
            }
            catch (Exception ex)
            {
                AppendLog($"Error: {ex.Message}");
                Log.Error($"[GameBuild] Build failed: {ex}");
            }
            finally
            {
                _isBuilding = false;
            }
        }

        void ExecuteBuildAll()
        {
            // Build Android first
            _targetPlatform = BuildTarget.Android;
            ExecuteBuild();

            if (!EditorApplication.isBatchMode)
            {
                // Show confirmation to continue with iOS
                if (EditorUtility.DisplayDialog("Build iOS?",
                    "Android build completed. Continue with iOS build?", "Yes", "No"))
                {
                    _targetPlatform = BuildTarget.iOS;
                    ExecuteBuild();
                }
            }
            else
            {
                // In batch mode, continue with iOS
                _targetPlatform = BuildTarget.iOS;
                ExecuteBuild();
            }
        }

        bool SwitchToTargetPlatform()
        {
            BuildTargetGroup group = _targetPlatform switch
            {
                BuildTarget.Android => BuildTargetGroup.Android,
                BuildTarget.iOS => BuildTargetGroup.iOS,
                _ => BuildTargetGroup.Unknown
            };

            if (group == BuildTargetGroup.Unknown)
            {
                AppendLog($"Error: Unsupported platform {_targetPlatform}");
                return false;
            }

            BuildTarget currentTarget = EditorUserBuildSettings.activeBuildTarget;
            if (currentTarget != _targetPlatform)
            {
                AppendLog($"Switching platform to {_targetPlatform}...");
                if (!EditorUserBuildSettings.SwitchActiveBuildTarget(group, _targetPlatform))
                {
                    AppendLog($"Error: Failed to switch to {_targetPlatform}");
                    return false;
                }
                AppendLog($"Platform switched to {_targetPlatform}");
            }

            return true;
        }

        bool BuildYooAssetResources()
        {
            AppendLog("--- Building YooAsset Resources ---");

            try
            {
                // 1. Setup YooAsset collector (ensures settings exist)
                if (!HasYooAssetSettings())
                {
                    AppendLog("Setting up YooAsset collector...");
                    YooAssetCollectorSetup.SetupAll();
                }

                // 2. Run preprocessing (InlandWater, MapItem, etc.)
                AppendLog("Running preprocessing...");
                RunPreBuildSteps();

                // 3. Clean old build cache to ensure full rebuild
                string outputRoot = AssetBundleBuilderHelper.GetDefaultBuildOutputRoot();
                string platform = _targetPlatform.ToString();
                string packageRoot = Path.Combine(outputRoot, platform, PackageName);

                if (Directory.Exists(packageRoot))
                {
                    AppendLog($"Cleaning old build cache: {packageRoot}");
                    Directory.Delete(packageRoot, true);
                }

                // 4. Execute fresh build
                string packageVersion = DateTime.Now.ToString("yyyy-MM-dd-HHmmss");
                UpdateSetting updateSetting = UpdateSetting.LoadOrDefault();

                var builtin = new BuiltinBuildParameters();
                var pipeline = new BuiltinBuildPipeline();
                var buildParameters = builtin;

                builtin.CompressOption = ECompressOption.LZ4;

                buildParameters.BuildOutputRoot = outputRoot;
                buildParameters.BuildinFileRoot = AssetBundleBuilderHelper.GetStreamingAssetsRoot();
                buildParameters.BuildPipeline = EBuildPipeline.BuiltinBuildPipeline.ToString();
                buildParameters.BuildTarget = _targetPlatform;
                buildParameters.BuildBundleType = (int)EBuildBundleType.AssetBundle;
                buildParameters.PackageName = PackageName;
                buildParameters.PackageVersion = packageVersion;
                buildParameters.VerifyBuildingResult = true;
                buildParameters.EnableSharePackRule = true;
                buildParameters.FileNameStyle = EFileNameStyle.BundleName_HashName;
                buildParameters.BuildinFileCopyOption = EBuildinFileCopyOption.ClearAndCopyAll;
                buildParameters.BuildinFileCopyParams = string.Empty;
                buildParameters.ClearBuildCacheFiles = true; // Force clean for full rebuild
                buildParameters.UseAssetDependencyDB = true;

                AppendLog($"Building package: {PackageName} v{packageVersion}");
                var buildResult = pipeline.Run(buildParameters, true);

                if (buildResult.Success)
                {
                    AppendLog($"YooAsset build OK: {buildResult.OutputPackageDirectory}");
                    return true;
                }

                AppendLog($"YooAsset build failed: {buildResult.ErrorInfo}");
                return false;
            }
            catch (Exception ex)
            {
                AppendLog($"YooAsset build error: {ex.Message}");
                Log.Error($"[GameBuild] YooAsset error: {ex}");
                return false;
            }
        }

        /// <summary>
        /// Run preprocessing steps before building YooAsset resources.
        /// This ensures InlandWater, MapItem, and other dynamic content is up to date.
        /// </summary>
        void RunPreBuildSteps()
        {
            // Build InlandWater prefab if needed
            try
            {
                if (AssetDatabase.LoadAssetAtPath<GameObject>(SceneAssets.IslandInlandWaterPrefabPath) == null)
                {
                    AppendLog("Building InlandWater prefab...");
                    IslandInlandWaterBuilder.Build();
                }
            }
            catch (Exception ex)
            {
                AppendLog($"InlandWater build skipped: {ex.Message}");
            }

            // Ensure MapItem foliage settings
            try
            {
                MapItemEditorMenu.EnsureFoliageClipAndShadows();
            }
            catch (Exception ex)
            {
                AppendLog($"MapItem foliage setup skipped: {ex.Message}");
            }

            AppendLog("Preprocessing complete.");
        }

        bool HasYooAssetSettings()
        {
            string[] guids = AssetDatabase.FindAssets("t:AssetBundleCollectorSetting");
            return guids.Length > 0;
        }

        void CopyPackageToStreamingAssets()
        {
            AppendLog("--- Copying to StreamingAssets ---");

            try
            {
                string outputRoot = AssetBundleBuilderHelper.GetDefaultBuildOutputRoot();
                string platform = _targetPlatform.ToString();
                string packageRoot = Path.Combine(outputRoot, platform, PackageName);

                if (!Directory.Exists(packageRoot))
                {
                    AppendLog($"Warning: Package output not found: {packageRoot}");
                    AppendLog("Skipping copy. (This is normal for iOS build first).");
                    return;
                }

                string[] versions = Directory.GetDirectories(packageRoot);
                if (versions.Length == 0)
                {
                    AppendLog($"Warning: No versions in {packageRoot}");
                    return;
                }

                Array.Sort(versions);
                string latest = versions[versions.Length - 1];

                string streamingRoot = AssetBundleBuilderHelper.GetStreamingAssetsRoot();
                string dest = Path.Combine(streamingRoot, PackageName);

                if (Directory.Exists(dest))
                    Directory.Delete(dest, true);

                Directory.CreateDirectory(streamingRoot);
                CopyDirectoryRecursive(latest, dest);

                AssetDatabase.Refresh();
                AppendLog($"Copied {latest} -> {dest}");
            }
            catch (Exception ex)
            {
                AppendLog($"Copy error: {ex.Message}");
            }
        }

        void BuildAndroidPlayer()
        {
            AppendLog("--- Building Android Player ---");

            ApplyAndroidPlayerSettings();

            string[] scenes = EditorBuildSettings.scenes
                .Where(s => s.enabled)
                .Select(s => s.path)
                .ToArray();

            if (scenes.Length == 0)
            {
                AppendLog("Warning: No scenes in build settings, using Launch.unity");
                scenes = new[] { "Assets/GameMain/Scenes/Launch.unity" };
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_androidApkPath));

            var options = new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = _androidApkPath,
                target = BuildTarget.Android,
                options = _developmentBuild
                    ? BuildOptions.Development | BuildOptions.ConnectWithProfiler
                    : BuildOptions.None
            };

            AppendLog($"Building APK: {_androidApkPath}");
            AppendLog($"Scenes: {string.Join(", ", scenes)}");

            BuildReport report = BuildPipeline.BuildPlayer(options);
            BuildSummary summary = report.summary;

            if (summary.result == BuildResult.Succeeded)
            {
                float sizeMB = summary.totalSize / (1024f * 1024f);
                AppendLog($"APK build OK: {_androidApkPath}");
                AppendLog($"Size: {sizeMB:F1} MB, Time: {summary.totalTime}");

                if (_installAfterBuild)
                {
                    InstallAPK();
                }

                if (EditorApplication.isBatchMode)
                    EditorApplication.Exit(0);
            }
            else
            {
                AppendLog($"APK build failed: {summary.result}");
                if (EditorApplication.isBatchMode)
                    EditorApplication.Exit(1);
            }
        }

        void ApplyAndroidPlayerSettings()
        {
            var android = NamedBuildTarget.Android;

            PlayerSettings.SetApplicationIdentifier(android, AndroidPackageId);
            PlayerSettings.productName = AndroidProductName;
            PlayerSettings.Android.bundleVersionCode = (int)(DateTimeOffset.Now.ToUnixTimeSeconds() / 86400);
            PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel25;
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            PlayerSettings.Android.androidIsGame = true;
            PlayerSettings.SetScriptingBackend(android, ScriptingImplementation.IL2CPP);
            PlayerSettings.SetIl2CppCompilerConfiguration(android,
                _developmentBuild ? Il2CppCompilerConfiguration.Debug : Il2CppCompilerConfiguration.Release);
            PlayerSettings.SetManagedStrippingLevel(android, ManagedStrippingLevel.High);
            PlayerSettings.gcIncremental = true;
            PlayerSettings.stripEngineCode = true;

            QualitySettings.vSyncCount = 0;
            QualitySettings.antiAliasing = 0;
            QualitySettings.shadows = ShadowQuality.HardOnly;
            QualitySettings.shadowResolution = ShadowResolution.Medium;
            QualitySettings.shadowDistance = 40f;
        }

        void BuildIOSPlayer()
        {
            AppendLog("--- Building iOS Player ---");

            ApplyIOSPlayerSettings();

            string[] scenes = EditorBuildSettings.scenes
                .Where(s => s.enabled)
                .Select(s => s.path)
                .ToArray();

            if (scenes.Length == 0)
            {
                AppendLog("Warning: No scenes in build settings, using Launch.unity");
                scenes = new[] { "Assets/GameMain/Scenes/Launch.unity" };
            }

            Directory.CreateDirectory(_iosXcodePath);

            var options = new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = _iosXcodePath,
                target = BuildTarget.iOS,
                options = _developmentBuild
                    ? BuildOptions.Development | BuildOptions.ConnectWithProfiler
                    : BuildOptions.None
            };

            AppendLog($"Building Xcode project: {_iosXcodePath}");
            AppendLog($"Scenes: {string.Join(", ", scenes)}");

            BuildReport report = BuildPipeline.BuildPlayer(options);
            BuildSummary summary = report.summary;

            if (summary.result == BuildResult.Succeeded)
            {
                AppendLog($"Xcode build OK: {_iosXcodePath}");
                AppendLog($"Time: {summary.totalTime}");
                AppendLog("");
                AppendLog("=== Next Steps ===");
                AppendLog("1. Open Xcode project in: " + _iosXcodePath);
                AppendLog("2. Configure signing in Xcode");
                AppendLog("3. Build and run on device");

                if (EditorApplication.isBatchMode)
                    EditorApplication.Exit(0);
            }
            else
            {
                AppendLog($"Xcode build failed: {summary.result}");
                if (EditorApplication.isBatchMode)
                    EditorApplication.Exit(1);
            }
        }

        void ApplyIOSPlayerSettings()
        {
            var ios = NamedBuildTarget.iOS;

            PlayerSettings.SetApplicationIdentifier(ios, AndroidPackageId.Replace("com.customsrp.", "com.customsrp.ios."));
            PlayerSettings.productName = AndroidProductName;
            PlayerSettings.iOS.buildNumber = (int)(DateTimeOffset.Now.ToUnixTimeSeconds() / 86400).ToString();
            PlayerSettings.SetScriptingBackend(ios, ScriptingImplementation.IL2CPP);
            PlayerSettings.SetManagedStrippingLevel(ios, ManagedStrippingLevel.High);
            PlayerSettings.gcIncremental = true;

            QualitySettings.vSyncCount = 0;
            QualitySettings.antiAliasing = 0;
        }

        void InstallAPK()
        {
            AppendLog("--- Installing APK via adb ---");

            try
            {
                string apkFullPath = Path.GetFullPath(_androidApkPath);
                if (!File.Exists(apkFullPath))
                {
                    AppendLog($"APK not found: {apkFullPath}");
                    return;
                }

                string adbPath = FindAdb();
                if (string.IsNullOrEmpty(adbPath))
                {
                    AppendLog("adb not found. Install Android SDK platform-tools.");
                    return;
                }

                // Install
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = adbPath,
                    Arguments = $"install -r \"{apkFullPath}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using var process = System.Diagnostics.Process.Start(psi);
                if (process != null)
                {
                    string stdout = process.StandardOutput.ReadToEnd();
                    string stderr = process.StandardError.ReadToEnd();
                    process.WaitForExit();

                    if (process.ExitCode == 0)
                    {
                        AppendLog("APK installed successfully.");

                        // Launch app
                        string activityName = "com.unity3d.player.UnityPlayerGameActivity";
                        psi.Arguments = $"shell am start -n {AndroidPackageId}/{activityName}";
                        using var launchProcess = System.Diagnostics.Process.Start(psi);
                        launchProcess?.WaitForExit();
                        AppendLog("App launched on device.");
                    }
                    else
                    {
                        AppendLog($"adb install failed: {stderr}");
                    }
                }
            }
            catch (Exception ex)
            {
                AppendLog($"Install error: {ex.Message}");
            }
        }

        string FindAdb()
        {
            string[] candidates =
            {
                "/opt/homebrew/bin/adb",
                "/usr/local/bin/adb",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Library/Android/sdk/platform-tools/adb"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Android/Sdk/platform-tools/adb"),
                "adb"
            };

            string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (string dir in pathEnv.Split(Path.PathSeparator))
            {
                if (string.IsNullOrEmpty(dir)) continue;
                string candidate = Path.Combine(dir, "adb");
                if (File.Exists(candidate)) return candidate;
            }

            foreach (string candidate in candidates)
            {
                if (File.Exists(candidate)) return candidate;
            }

            return null;
        }

        void CleanBuildCache()
        {
            if (!EditorUtility.DisplayDialog("Clean Build Cache",
                "This will delete all build outputs. Continue?", "Yes", "Cancel"))
            {
                return;
            }

            try
            {
                // Clean YooAsset build output
                string outputRoot = AssetBundleBuilderHelper.GetDefaultBuildOutputRoot();
                if (Directory.Exists(outputRoot))
                {
                    Directory.Delete(outputRoot, true);
                    AppendLog($"Deleted: {outputRoot}");
                }

                // Clean StreamingAssets package
                string streamingRoot = AssetBundleBuilderHelper.GetStreamingAssetsRoot();
                string packageDest = Path.Combine(streamingRoot, PackageName);
                if (Directory.Exists(packageDest))
                {
                    Directory.Delete(packageDest, true);
                    AppendLog($"Deleted: {packageDest}");
                }

                // Clean Builds folder
                if (Directory.Exists("Builds"))
                {
                    Directory.Delete("Builds", true);
                    AppendLog("Deleted: Builds/");
                }

                AssetDatabase.Refresh();
                AppendLog("Build cache cleaned.");
            }
            catch (Exception ex)
            {
                AppendLog($"Clean error: {ex.Message}");
            }
        }

        void AppendLog(string message)
        {
            _buildLog += message + "\n";
            Log.Info($"[GameBuild] {message}");
            Repaint();
        }

        static void CopyDirectoryRecursive(string sourceDir, string destDir)
        {
            Directory.CreateDirectory(destDir);

            foreach (string file in Directory.GetFiles(sourceDir))
            {
                string name = Path.GetFileName(file);
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
