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
        const string PrefInstallApk = "BoluoPublish_InstallApk";

        static readonly string[] EnvLabels = { "dev", "test", "prerelease", "product" };

        // 对齐 AndroidProject/Docs/渠道.md §4（12 flavor）；Unity 导出：yishu→YiShu，其余→Boluo_Android
        static readonly string[] FlavorLabels =
        {
            "skyworthUltra · 创维默认 (Boluo_Android)",
            "skyworthNeo · 旧变体 (Boluo_Android)",
            "yishu · 一树投影 (YiShu)",
            "phoneAppstore · 应用宝",
            "phoneBaidu · 百度",
            "phone360 · 360",
            "phoneXiaomi · 小米",
            "phoneOppo · OPPO",
            "phoneVivo · VIVO",
            "phoneHonor · Honor",
            "phoneSamsung · 三星",
            "phoneMeizu · 魅族",
        };

        static readonly string[] FlavorValues =
        {
            "skyworthUltra",
            "skyworthNeo",
            "yishu",
            "phoneAppstore",
            "phoneBaidu",
            "phone360",
            "phoneXiaomi",
            "phoneOppo",
            "phoneVivo",
            "phoneHonor",
            "phoneSamsung",
            "phoneMeizu",
        };

        static readonly string[] OrientationLabels = { "sensor", "landscape", "portrait" };

        int _envIndex;
        int _flavorIndex;
        string _version = "1.1.60";
        string _buildNumber = "";
        bool _disableLog;
        bool _skipYooAsset;
        bool _developmentBuild;
        int _orientationIndex;
        bool _installAfterApk;
        Vector2 _scroll;
        string _log = "";
        bool _busy;
        string _lastApkPath = "";
        Action _pendingAction;

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
            _installAfterApk = EditorPrefs.GetBool(PrefInstallApk, false);
        }

        void OnDisable()
        {
            EditorApplication.update -= RunPending;
            _pendingAction = null;
        }

        void OnGUI()
        {
            // 按钮回调里同步跑导出会在 EndScrollView 之前 Repaint/重入 OnGUI，
            // 打乱 Layout 栈 → EndLayoutGroup 报错并表现为 Editor「卡死」刷屏。
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            try
            {
                DrawPaths();
                EditorGUILayout.Space(8);
                DrawConfig();
                EditorGUILayout.Space(8);
                DrawActions();
                EditorGUILayout.Space(8);
                DrawLog();
            }
            finally
            {
                EditorGUILayout.EndScrollView();
            }
        }

        /// <summary>
        /// 等本帧 OnGUI（含 EndScrollView）结束后再跑重活。
        /// 本工程 delayCall 可能不派发，与 HostShellExport 一样用 update 一次性触发。
        /// </summary>
        void Schedule(Action action)
        {
            if (_busy || action == null)
                return;
            _busy = true;
            _log = "";
            _lastApkPath = "";
            _pendingAction = action;
            EditorApplication.update -= RunPending;
            EditorApplication.update += RunPending;
            Repaint();
        }

        void RunPending()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                return;

            EditorApplication.update -= RunPending;
            Action action = _pendingAction;
            _pendingAction = null;
            if (action == null)
            {
                _busy = false;
                return;
            }

            try
            {
                action();
            }
            finally
            {
                _busy = false;
                Repaint();
            }
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
                string hostBt = CurrentHostBuildType();
                EditorGUILayout.HelpBox(
                    $"宿主 Gradle buildType → {hostBt}（dev→debug / test→alpha / prerelease→beta / product→release）",
                    MessageType.Info);
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
                string task = BoluoHostGradleBuilder.ResolveAssembleTask(flavor, CurrentHostBuildType());
                if (GUILayout.Button($"一键：导出宿主 + {task}", GUILayout.Height(40)))
                    Schedule(RunExportAndAssemble);

                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("仅导出 Android Gradle", GUILayout.Height(28)))
                    Schedule(() => RunExport(android: true));
                if (GUILayout.Button("仅编宿主 APK（已导出）", GUILayout.Height(28)))
                    Schedule(RunAssembleOnly);
                EditorGUILayout.EndHorizontal();

                if (GUILayout.Button("导出 iOS Xcode（宿主）", GUILayout.Height(28)))
                    Schedule(() => RunExport(android: false));

                EditorGUILayout.Space(4);
                if (GUILayout.Button("独立 APK（非宿主 / Unity 直出）", GUILayout.Height(24)))
                    Schedule(RunStandaloneApk);

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
            EditorPrefs.SetBool(PrefInstallApk, _installAfterApk);
        }

        BoluoCIBuildSupport.CIEnvironment CurrentEnvironment() =>
            EnvLabels[_envIndex] switch
            {
                "dev" => BoluoCIBuildSupport.CIEnvironment.Dev,
                "test" => BoluoCIBuildSupport.CIEnvironment.Test,
                "prerelease" => BoluoCIBuildSupport.CIEnvironment.PreRelease,
                _ => BoluoCIBuildSupport.CIEnvironment.Product,
            };

        string CurrentHostBuildType() =>
            BoluoHostGradleBuilder.HostBuildTypeFromEnvironment(CurrentEnvironment());

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
                Environment = CurrentEnvironment(),
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

                string hostBt = CurrentHostBuildType();
                AppendLog($"=== 2/2 宿主 Gradle 编 APK（{hostBt}） ===");
                if (!BoluoHostGradleBuilder.Assemble(flavor, hostBt, AppendLog, out string apk, out string err))
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
        }

        void RunAssembleOnly()
        {
            try
            {
                SavePrefs();
                string flavor = FlavorValues[_flavorIndex];
                string hostBt = CurrentHostBuildType();
                AppendLog($"=== 仅编宿主 APK（{hostBt}） ===");
                if (!BoluoHostGradleBuilder.Assemble(flavor, hostBt, AppendLog, out string apk, out string err))
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
        }

        void RunExport(bool android)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                AppendLog("请先退出 Play Mode");
                return;
            }

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
        }

        void RunStandaloneApk()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                AppendLog("请先退出 Play Mode");
                return;
            }

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
        }

        void AppendLog(string msg)
        {
            _log += msg + "\n";
            Debug.Log($"[BoluoPublish] {msg}");
            // 避免在 IMGUI Layout/Repaint 事件中间再触发重绘把栈打乱
            if (Event.current == null)
                Repaint();
        }
    }
}
#endif
