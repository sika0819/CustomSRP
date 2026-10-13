using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

using UnityGameFramework.Runtime;
namespace Game.Editor
{
    /// <summary>
    /// Android Release 构建。
    /// 
    /// 注意：推荐使用 Window/Game/Build Tool 统一打包窗口。
    /// 本类保留以兼容旧工作流和命令行打包。
    /// </summary>
    public static class BuildAndroidIslandRelease
    {
        public const string ApkPath = "Builds/Android/Game-Release.apk";

        [MenuItem("Game/Build Android (Legacy)", false, 300)]
        public static void Build()
        {
            string[] scenes = EditorBuildSettings.scenes
                .Where(s => s.enabled)
                .Select(s => s.path)
                .ToArray();
            if (scenes.Length == 0)
                scenes = new[] { "Assets/GameMain/Scenes/Launch.unity" };

            Directory.CreateDirectory("Builds/Android");

            // 低配机预期落 Very Low：不升档；只保证该档有 Hard 阴影（见 QualitySettings.asset）。
            // 运行时 TuneLightsForMobileOil 再兜底 QualitySettings.shadows / 主光 Hard。

            var options = new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = ApkPath,
                target = BuildTarget.Android,
                options = BuildOptions.None,
            };

            Log.Info(
                "[Game] Android Island Release build start scenes=" + string.Join(",", scenes) +
                " (Very Low keeps Hard shadows)");
            BuildReport report = BuildPipeline.BuildPlayer(options);
            BuildSummary summary = report.summary;
            if (summary.result == BuildResult.Succeeded)
            {
                Log.Info(
                    $"[Game] Android APK OK: {ApkPath} " +
                    $"({summary.totalSize / (1024 * 1024f):F1} MB, {summary.totalTime})");
                if (Application.isBatchMode)
                    EditorApplication.Exit(0);
            }
            else
            {
                Log.Error($"[Game] Android build failed: {summary.result}");
                if (Application.isBatchMode)
                    EditorApplication.Exit(1);
            }
        }
    }
}
