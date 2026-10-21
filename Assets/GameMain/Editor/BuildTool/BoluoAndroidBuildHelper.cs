#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Game.Editor.BuildTool
{
    /// <summary>
    /// Android：APK 直出 vs Export Gradle Project（unityLibrary）。
    /// </summary>
    public static class BoluoAndroidBuildHelper
    {
        public static void ApplyBeforeApkPack()
        {
            EditorUserBuildSettings.exportAsGoogleAndroidProject = false;
            EditorUserBuildSettings.buildAppBundle = false;
        }

        public static BuildReport BuildApk(BuildPlayerOptions options)
        {
            if (options.target != BuildTarget.Android)
                return BuildPipeline.BuildPlayer(options);

            bool exportProject = EditorUserBuildSettings.exportAsGoogleAndroidProject;
            bool appBundle = EditorUserBuildSettings.buildAppBundle;
            EditorUserBuildSettings.exportAsGoogleAndroidProject = false;
            EditorUserBuildSettings.buildAppBundle = false;
            try
            {
                BuildReport report = BuildPipeline.BuildPlayer(options);
                if (report.summary.result == BuildResult.Succeeded)
                    ValidateApkOutput(options.locationPathName);
                return report;
            }
            finally
            {
                EditorUserBuildSettings.exportAsGoogleAndroidProject = exportProject;
                EditorUserBuildSettings.buildAppBundle = appBundle;
            }
        }

        /// <summary>导出 Gradle 工程（unityLibrary），供混合宿主嵌入。</summary>
        public static BuildReport BuildGradleProject(BuildPlayerOptions options)
        {
            if (options.target != BuildTarget.Android)
                return BuildPipeline.BuildPlayer(options);

            bool exportProject = EditorUserBuildSettings.exportAsGoogleAndroidProject;
            bool appBundle = EditorUserBuildSettings.buildAppBundle;
            EditorUserBuildSettings.exportAsGoogleAndroidProject = true;
            EditorUserBuildSettings.buildAppBundle = false;
            Debug.Log("[BoluoBuild][Android] 导出 Gradle: Export Project=true, App Bundle=false");
            try
            {
                return BuildPipeline.BuildPlayer(options);
            }
            finally
            {
                EditorUserBuildSettings.exportAsGoogleAndroidProject = exportProject;
                EditorUserBuildSettings.buildAppBundle = appBundle;
            }
        }

        public static void ValidateApkOutput(string locationPathName)
        {
            if (string.IsNullOrEmpty(locationPathName))
                throw new InvalidOperationException("APK 输出路径为空");

            if (File.Exists(locationPathName) &&
                locationPathName.EndsWith(".apk", StringComparison.OrdinalIgnoreCase))
            {
                Debug.Log(
                    $"[BoluoBuild][Android] APK: {locationPathName} ({new FileInfo(locationPathName).Length} bytes)");
                return;
            }

            if (Directory.Exists(locationPathName))
            {
                throw new InvalidOperationException(
                    $"构建结果是 Gradle 工程目录而非 APK: {locationPathName}");
            }

            throw new FileNotFoundException($"未找到 APK: {locationPathName}");
        }
    }
}
#endif
