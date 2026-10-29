#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityGameFramework.Runtime;
using YooAsset.Editor;
using Game.Editor.BuildTool;

namespace Game.Editor
{
    /// <summary>
    /// 独立 APK / Xcode 打包（非宿主）。宿主导出请用 Build/BoLuo发布工具。
    /// YooAsset 构建走 <see cref="BoluoYooAssetBuildPipeline"/>。
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

        const string PackageName = BoluoYooAssetBuildPipeline.PackageName;

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
                    "独立 APK / Xcode（非宿主）。YooAsset + Player 构建。\n" +
                    "混合宿主 UaaL 导出请用菜单 Build/BoLuo发布工具。",
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
                    if (!BoluoYooAssetBuildPipeline.Run(
                            _targetPlatform, _copyToStreamingAssets, AppendLog))
                    {
                        return;
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

            if (!Application.isBatchMode)
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

            UnityEditor.Build.Reporting.BuildReport report = BuildPipeline.BuildPlayer(options);
            UnityEditor.Build.Reporting.BuildSummary summary = report.summary;

            if (summary.result == UnityEditor.Build.Reporting.BuildResult.Succeeded)
            {
                float sizeMB = summary.totalSize / (1024f * 1024f);
                AppendLog($"APK build OK: {_androidApkPath}");
                AppendLog($"Size: {sizeMB:F1} MB, Time: {summary.totalTime}");

                if (_installAfterBuild)
                {
                    InstallAPK();
                }

                if (Application.isBatchMode)
                    EditorApplication.Exit(0);
            }
            else
            {
                AppendLog($"APK build failed: {summary.result}");
                if (Application.isBatchMode)
                    EditorApplication.Exit(1);
            }
        }

        void ApplyAndroidPlayerSettings()
        {
            var android = NamedBuildTarget.Android;

            PlayerSettings.SetApplicationIdentifier(android, AndroidPackageId);
            PlayerSettings.productName = AndroidProductName;
            PlayerSettings.Android.bundleVersionCode = (int)(DateTimeOffset.Now.ToUnixTimeSeconds() / 86400);
            PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel26;
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            PlayerSettings.Android.appCategory = "game";
            PlayerSettings.SetScriptingBackend(android, ScriptingImplementation.IL2CPP);
            PlayerSettings.SetIl2CppCompilerConfiguration(android,
                _developmentBuild ? Il2CppCompilerConfiguration.Debug : Il2CppCompilerConfiguration.Release);
            PlayerSettings.SetManagedStrippingLevel(android, ManagedStrippingLevel.High);
            PlayerSettings.gcIncremental = true;
            // Keep TerrainLayer (Yoo IslandTerrain). Strip Engine Code drops class 1953259897.
            PlayerSettings.stripEngineCode = false;

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

            UnityEditor.Build.Reporting.BuildReport report = BuildPipeline.BuildPlayer(options);
            UnityEditor.Build.Reporting.BuildSummary summary = report.summary;

            if (summary.result == UnityEditor.Build.Reporting.BuildResult.Succeeded)
            {
                AppendLog($"Xcode build OK: {_iosXcodePath}");
                AppendLog($"Time: {summary.totalTime}");
                AppendLog("");
                AppendLog("=== Next Steps ===");
                AppendLog("1. Open Xcode project in: " + _iosXcodePath);
                AppendLog("2. Configure signing in Xcode");
                AppendLog("3. Build and run on device");

                if (Application.isBatchMode)
                    EditorApplication.Exit(0);
            }
            else
            {
                AppendLog($"Xcode build failed: {summary.result}");
                if (Application.isBatchMode)
                    EditorApplication.Exit(1);
            }
        }

        void ApplyIOSPlayerSettings()
        {
            var ios = NamedBuildTarget.iOS;

            PlayerSettings.SetApplicationIdentifier(ios, AndroidPackageId.Replace("com.customsrp.", "com.customsrp.ios."));
            PlayerSettings.productName = AndroidProductName;
            PlayerSettings.iOS.buildNumber = ((int)(DateTimeOffset.Now.ToUnixTimeSeconds() / 86400)).ToString();
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
    }
}
#endif
