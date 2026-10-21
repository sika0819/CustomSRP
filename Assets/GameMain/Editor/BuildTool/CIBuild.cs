#if UNITY_EDITOR
using UnityEditor;

namespace Game.Editor.BuildTool
{
    /// <summary>
    /// Unity CI 入口。通过 -executeMethod 调用，配置见环境变量 / -boluo* CLI。
    /// </summary>
    public static class CIBuild
    {
        /// <summary>导出 Android Gradle（UnityExport/Boluo_Android 或 YiShu）。</summary>
        public static void ExportAndroidGradle()
        {
            if (!BoluoCIBuildSupport.TryLoadConfigFromEnvironment(out var config, out string error))
            {
                BoluoCIBuildSupport.LogCiFailure($"配置错误: {error}");
                EditorApplication.Exit(2);
                return;
            }

            config.BuildTarget = BuildTarget.Android;
            int code = BoluoCIBuildSupport.ExportAndroidGradle(config);
            EditorApplication.Exit(code);
        }

        /// <summary>导出 iOS Xcode 工程（IOSProject/Boluo_Unity）。须以 -buildTarget iOS 启动。</summary>
        public static void BuildiOS()
        {
            if (!BoluoCIBuildSupport.TryLoadConfigFromEnvironment(out var config, out string error))
            {
                BoluoCIBuildSupport.LogCiFailure($"配置错误: {error}");
                EditorApplication.Exit(2);
                return;
            }

            config.BuildTarget = BuildTarget.iOS;
            int code = BoluoCIBuildSupport.BuildiOS(config);
            EditorApplication.Exit(code);
        }

        /// <summary>独立 Android APK（非宿主）。</summary>
        public static void BuildAndroidApk()
        {
            if (!BoluoCIBuildSupport.TryLoadConfigFromEnvironment(out var config, out string error))
            {
                BoluoCIBuildSupport.LogCiFailure($"配置错误: {error}");
                EditorApplication.Exit(2);
                return;
            }

            config.BuildTarget = BuildTarget.Android;
            int code = BoluoCIBuildSupport.BuildAndroidApk(config);
            EditorApplication.Exit(code);
        }

        /// <summary>
        /// 导出 Android Gradle 后跑宿主 gradlew assemble。
        /// 可选：-boluoHostRelease true / BOLUO_CI_HOST_RELEASE；-boluoInstallApk true。
        /// </summary>
        public static void ExportAndroidAndAssemble()
        {
            if (!BoluoCIBuildSupport.TryLoadConfigFromEnvironment(out var config, out string error))
            {
                BoluoCIBuildSupport.LogCiFailure($"配置错误: {error}");
                EditorApplication.Exit(2);
                return;
            }

            config.BuildTarget = BuildTarget.Android;
            int code = BoluoCIBuildSupport.ExportAndroidGradle(config);
            if (code != 0)
            {
                EditorApplication.Exit(code);
                return;
            }

            bool release = ParseBool(
                BoluoCIArgParser.Resolve("BOLUO_CI_HOST_RELEASE", "boluoHostRelease"), false);
            bool install = ParseBool(
                BoluoCIArgParser.Resolve("BOLUO_CI_INSTALL_APK", "boluoInstallApk"), false);

            if (!BoluoHostGradleBuilder.Assemble(
                    config.AndroidFlavor, release, msg => UnityEngine.Debug.Log($"[BoluoCI] {msg}"),
                    out string apk, out string gradleError))
            {
                BoluoCIBuildSupport.LogCiFailure(gradleError);
                EditorApplication.Exit(1);
                return;
            }

            UnityEngine.Debug.Log($"[BoluoCI] APK: {apk}");
            if (install &&
                !BoluoHostGradleBuilder.InstallApk(apk, msg => UnityEngine.Debug.Log($"[BoluoCI] {msg}"),
                    out string installError))
            {
                BoluoCIBuildSupport.LogCiFailure(installError);
                EditorApplication.Exit(1);
                return;
            }

            EditorApplication.Exit(0);
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
    }
}
#endif

