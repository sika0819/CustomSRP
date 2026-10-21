#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Game.Editor.BuildTool
{
    /// <summary>
    /// 宿主 UaaL 发布工具：环境宏 / flavor / 版本 → YooAsset → 导出 →（可选）gradlew 编 APK。
    /// 菜单：Build/BoLuo发布工具
    /// </summary>
    public class BoluoPublishWindow : EditorWindow
    {
        const string PrefEnv = "BoluoPublish_Env";
        const string PrefFlavor = "BoluoPublish_Flavor";
        const string PrefVersion = "BoluoPublish_Version";
        const string PrefDisableLog = "BoluoPublish_DisableLog";
        const string PrefSkipYoo = "BoluoPublish_SkipYoo";
        const string PrefDevBuild = "BoluoPublish_DevBuild";
        const string PrefOrientation = "BoluoPublish_Orientation";
        const string PrefHostRelease = "BoluoPublish_HostRelease";
        const string PrefInstallApk = "BoluoPublish_InstallApk";

        static readonly string[] EnvLabels = { "dev", "test", "prerelease", "product" };
        static readonly string[] FlavorLabels = { "skyworthUltra (Boluo_Android)", "yishu (YiShu)" };
        static readonly string[] FlavorValues = { "skyworthUltra", "yishu" };
        static readonly string[] OrientationLabels = { "sensor", "landscape", "portrait" };

        int _envIndex;
        int _flavorIndex;
        string _version = "1.1.60";
        string _buildNumber = "";
        bool _disableLog;
        bool _skipYooAsset;
        bool _developmentBuild;
        int _orientationIndex;
        bool _hostRelease;
        bool _installAfterApk;
        Vector2 _scroll;
        string _log = "";
        bool _busy;
        string _lastApkPath = "";

        [MenuItem("Build/BoLuo发布工具", false, 100)]
        public static void Open()
        {
            var win = GetWindow<BoluoPublishWindow>(false, "BoLuo 发布工具", true);
            win.minSize = new Vector2(480, 560);
            win.Show();
        }

        void OnEnable()
        {
            string env = EditorPrefs.GetString(PrefEnv, "dev");
            _envIndex = Mathf.Max(0, Array.IndexOf(EnvLabels, env));
            string flavor = EditorPrefs.GetString(PrefFlavor, "skyworthUltra");
            _flavorIndex = Mathf.Max(0, Array.IndexOf(FlavorValues, flavor));
            _version = EditorPrefs.GetString(PrefVersion, PlayerSettings.bundleVersion);
            _disableLog = EditorPrefs.GetBool(PrefDisableLog, false);
            _skipYooAsset = EditorPrefs.GetBool(PrefSkipYoo, false);
            _developmentBuild = EditorPrefs.GetBool(PrefDevBuild, false);
            string orient = EditorPrefs.GetString(PrefOrientation, "sensor");
            _orientationIndex = Mathf.Max(0, Array.IndexOf(OrientationLabels, orient));
            _hostRelease = EditorPrefs.GetBool(PrefHostRelease, false);
            _installAfterApk = EditorPrefs.GetBool(PrefInstallApk, false);
        }

        void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            DrawPaths();
            EditorGUILayout.Space(8);
            DrawConfig();
            EditorGUILayout.Space(8);
            DrawActions();
            EditorGUILayout.Space(8);
            DrawLog();
            EditorGUILayout.EndScrollView();
        }

        void DrawPaths()
        {
            EditorGUILayout.LabelField("输出路径", EditorStyles.boldLabel);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                string androidRoot = BoluoCIBuildSupport.GetDefaultAndroidUnityExportRoot();
                string folder = BoluoCIBuildSupport.ResolveAndroidGradleExportFolderName(
                    FlavorValues[Mathf.Clamp(_flavorIndex, 0, FlavorValues.Length - 1)]);
                EditorGUILayout.LabelField("Android Export", Path.Combine(androidRoot, folder, "unityLibrary"));
                EditorGUILayout.LabelField("Android 宿主", BoluoHostGradleBuilder.GetAndroidProjectRoot());
                EditorGUILayout.LabelField(
                    "iOS",
                    Path.Combine(BoluoCIBuildSupport.GetDefaultIosOutputRoot(),
                        BoluoCIBuildSupport.IosUnityExportFolderName));
                if (!string.IsNullOrEmpty(_lastApkPath))
                    EditorGUILayout.LabelField("最近 APK", _lastApkPath);
            }
        }

        void DrawConfig()
        {
            EditorGUILayout.LabelField("构建配置", EditorStyles.boldLabel);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                _envIndex = EditorGUILayout.Popup("API 环境", _envIndex, EnvLabels);
                _flavorIndex = EditorGUILayout.Popup("Android Flavor", _flavorIndex, FlavorLabels);
                _version = EditorGUILayout.TextField("版本 (x.y.z)", _version);
                _buildNumber = EditorGUILayout.TextField("Build 号（空=自动）", _buildNumber);
                _orientationIndex = EditorGUILayout.Popup("屏幕方向", _orientationIndex, OrientationLabels);
                _disableLog = EditorGUILayout.Toggle("禁用 Log (BOLUO_DISABLELOG)", _disableLog);
                _skipYooAsset = EditorGUILayout.Toggle("跳过 YooAsset", _skipYooAsset);
                _developmentBuild = EditorGUILayout.Toggle("Unity Development Build", _developmentBuild);
                EditorGUILayout.Space(4);
                _hostRelease = EditorGUILayout.Toggle("宿主 Release（否则 Debug）", _hostRelease);
                _installAfterApk = EditorGUILayout.Toggle("编完 APK 后 adb 安装", _installAfterApk);
            }
        }

        void DrawActions()
        {
            EditorGUILayout.LabelField("导出 / 打包", EditorStyles.boldLabel);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                GUI.enabled = !_busy;

                string flavor = FlavorValues[_flavorIndex];
                string task = BoluoHostGradleBuilder.ResolveAssembleTask(flavor, _hostRelease);
                if (GUILayout.Button($"一键：导出宿主 + {task}", GUILayout.Height(40)))
                    RunExportAndAssemble();

                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("仅导出 Android Gradle", GUILayout.Height(28)))
                    RunExport(android: true);
                if (GUILayout.Button("仅编宿主 APK（已导出）", GUILayout.Height(28)))
                    RunAssembleOnly();
                EditorGUILayout.EndHorizontal();

                if (GUILayout.Button("导出 iOS Xcode（宿主）", GUILayout.Height(28)))
                    RunExport(android: false);

                EditorGUILayout.Space(4);
                if (GUILayout.Button("独立 APK（非宿主 / Unity 直出）", GUILayout.Height(24)))
                    RunStandaloneApk();

                GUI.enabled = true;

                if (_busy)
                    EditorGUILayout.HelpBox("构建中，请等待（Unity 导出 + Gradle 可能较久）…", MessageType.Warning);
            }
        }

        void DrawLog()
        {
            if (string.IsNullOrEmpty(_log))
                return;
            EditorGUILayout.LabelField("日志", EditorStyles.boldLabel);
            EditorGUILayout.SelectableLabel(_log, GUILayout.MinHeight(140));
        }

        void SavePrefs()
        {
            EditorPrefs.SetString(PrefEnv, EnvLabels[_envIndex]);
            EditorPrefs.SetString(PrefFlavor, FlavorValues[_flavorIndex]);
            EditorPrefs.SetString(PrefVersion, _version);
            EditorPrefs.SetBool(PrefDisableLog, _disableLog);
            EditorPrefs.SetBool(PrefSkipYoo, _skipYooAsset);
            EditorPrefs.SetBool(PrefDevBuild, _developmentBuild);
            EditorPrefs.SetString(PrefOrientation, OrientationLabels[_orientationIndex]);
            EditorPrefs.SetBool(PrefHostRelease, _hostRelease);
            EditorPrefs.SetBool(PrefInstallApk, _installAfterApk);
        }

        BoluoCIBuildSupport.CIConfig BuildConfig(BuildTarget target)
        {
            SavePrefs();
            var config = new BoluoCIBuildSupport.CIConfig
            {
                BuildTarget = target,
                Version = _version,
                BuildNumberOverride = string.IsNullOrWhiteSpace(_buildNumber) ? null : _buildNumber.Trim(),
                DisableLog = _disableLog,
                SkipYooAsset = _skipYooAsset,
                DevelopmentBuild = _developmentBuild,
                ScreenOrientation = OrientationLabels[_orientationIndex],
                AndroidFlavor = FlavorValues[_flavorIndex],
                OutputDir = target == BuildTarget.iOS
                    ? BoluoCIBuildSupport.GetDefaultIosOutputRoot()
                    : BoluoCIBuildSupport.GetDefaultAndroidUnityExportRoot(),
            };

            config.Environment = EnvLabels[_envIndex] switch
            {
                "dev" => BoluoCIBuildSupport.CIEnvironment.Dev,
                "test" => BoluoCIBuildSupport.CIEnvironment.Test,
                "prerelease" => BoluoCIBuildSupport.CIEnvironment.PreRelease,
                _ => BoluoCIBuildSupport.CIEnvironment.Product,
            };
            return config;
        }

        void RunExportAndAssemble()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                AppendLog("请先退出 Play Mode");
                return;
            }

            _busy = true;
            _log = "";
            _lastApkPath = "";
            try
            {
                var config = BuildConfig(BuildTarget.Android);
                string flavor = config.AndroidFlavor;

                AppendLog("=== 1/2 Unity 导出 Android Gradle ===");
                int code = BoluoCIBuildSupport.ExportAndroidGradle(config);
                if (code != 0)
                {
                    AppendLog($"=== 导出失败 exit={code} ===");
                    return;
                }

                AppendLog("=== 2/2 宿主 Gradle 编 APK ===");
                if (!BoluoHostGradleBuilder.Assemble(flavor, _hostRelease, AppendLog, out string apk, out string err))
                {
                    AppendLog($"Gradle 失败: {err}");
                    return;
                }

                _lastApkPath = apk;
                AppendLog($"=== APK 就绪: {apk} ===");

                if (_installAfterApk)
                {
                    if (!BoluoHostGradleBuilder.InstallApk(apk, AppendLog, out string installErr))
                        AppendLog($"安装失败: {installErr}");
                }
            }
            catch (Exception ex)
            {
                AppendLog($"异常: {ex.Message}");
                Debug.LogException(ex);
            }
            finally
            {
                _busy = false;
            }
        }

        void RunAssembleOnly()
        {
            _busy = true;
            _log = "";
            try
            {
                SavePrefs();
                string flavor = FlavorValues[_flavorIndex];
                AppendLog("=== 仅编宿主 APK ===");
                if (!BoluoHostGradleBuilder.Assemble(flavor, _hostRelease, AppendLog, out string apk, out string err))
                {
                    AppendLog($"失败: {err}");
                    return;
                }

                _lastApkPath = apk;
                AppendLog($"=== APK 就绪: {apk} ===");
                if (_installAfterApk)
                {
                    if (!BoluoHostGradleBuilder.InstallApk(apk, AppendLog, out string installErr))
                        AppendLog($"安装失败: {installErr}");
                }
            }
            catch (Exception ex)
            {
                AppendLog($"异常: {ex.Message}");
                Debug.LogException(ex);
            }
            finally
            {
                _busy = false;
            }
        }

        void RunExport(bool android)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                AppendLog("请先退出 Play Mode");
                return;
            }

            _busy = true;
            _log = "";
            try
            {
                var config = BuildConfig(android ? BuildTarget.Android : BuildTarget.iOS);
                AppendLog($"=== {(android ? "Android Gradle" : "iOS")} 导出开始 ===");
                int code = android
                    ? BoluoCIBuildSupport.ExportAndroidGradle(config)
                    : BoluoCIBuildSupport.BuildiOS(config);
                AppendLog(code == 0 ? "=== 成功 ===" : $"=== 失败 exit={code} ===");
            }
            catch (Exception ex)
            {
                AppendLog($"异常: {ex.Message}");
                Debug.LogException(ex);
            }
            finally
            {
                _busy = false;
            }
        }

        void RunStandaloneApk()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                AppendLog("请先退出 Play Mode");
                return;
            }

            _busy = true;
            _log = "";
            try
            {
                var config = BuildConfig(BuildTarget.Android);
                config.OutputDir = Path.GetFullPath(
                    Path.Combine(Application.dataPath, "..", "Builds", "Android", "Game.apk"));
                AppendLog("=== 独立 APK 开始 ===");
                int code = BoluoCIBuildSupport.BuildAndroidApk(config);
                AppendLog(code == 0 ? "=== 成功 ===" : $"=== 失败 exit={code} ===");
            }
            catch (Exception ex)
            {
                AppendLog($"异常: {ex.Message}");
                Debug.LogException(ex);
            }
            finally
            {
                _busy = false;
            }
        }

        void AppendLog(string msg)
        {
            _log += msg + "\n";
            Debug.Log($"[BoluoPublish] {msg}");
            Repaint();
        }
    }
}
#endif
