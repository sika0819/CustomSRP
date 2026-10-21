#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;
#if UNITY_ANDROID
using UnityEditor.Android;
#endif

namespace Game.Editor.BuildTool
{
    /// <summary>
    /// Unity 每次导出都会覆盖 unityLibrary 的 Gradle/CMake；本补丁把它对齐到宿主命名。
    /// 宿主 settings：Boluo_Android → :unityLibraryNeo，YiShu → :unityLibraryYishu；
    /// AGP 需要 CMake ≥ 3.22.1。
    /// 与 AndroidProject/scripts/patch_unity_export_for_host.py 保持同语义（幂等）。
    /// </summary>
    public static class BoluoAndroidUnityExportHostPatch
    {
        const string CmakeMin = "3.22.1";

        static readonly Dictionary<string, string> FolderToHost = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "Boluo_Android", "unityLibraryNeo" },
            { "YiShu", "unityLibraryYishu" },
        };

        static readonly Regex ReCmake = new Regex(
            @"cmake_minimum_required\s*\(\s*VERSION\s+([0-9]+(?:\.[0-9]+)*)\s*\)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        static readonly Regex ReA11yDep = new Regex(
            @"project\(':(?:unityLibrary(?:Neo|Yishu)?):com\.unity3d\.accessibility\.androidlib'\)",
            RegexOptions.Compiled);

        static readonly Regex ReUnityProject = new Regex(
            @"project\(':(unityLibrary)(?!Neo|Yishu)((?::[^']*)?)'\)",
            RegexOptions.Compiled);

        /// <summary>对 gradleRoot（含 unityLibrary）或 unityLibrary 本身打补丁。</summary>
        public static void Apply(string gradleRootOrUnityLibrary)
        {
            if (string.IsNullOrEmpty(gradleRootOrUnityLibrary))
                return;

            string unityLibrary = ResolveUnityLibraryDir(gradleRootOrUnityLibrary);
            if (string.IsNullOrEmpty(unityLibrary) || !Directory.Exists(unityLibrary))
            {
                Debug.LogWarning($"[BoluoAndroidHostPatch] 未找到 unityLibrary: {gradleRootOrUnityLibrary}");
                return;
            }

            string folder = new DirectoryInfo(Path.GetDirectoryName(unityLibrary) ?? "").Name;
            if (!FolderToHost.TryGetValue(folder, out string host))
            {
                Debug.LogWarning($"[BoluoAndroidHostPatch] 未知导出目录名 '{folder}'，跳过");
                return;
            }

            int n = 0;
            n += PatchBuildGradle(Path.Combine(unityLibrary, "build.gradle"), host) ? 1 : 0;
            n += PatchA11yGradle(
                Path.Combine(unityLibrary, "com.unity3d.accessibility.androidlib", "build.gradle"),
                host) ? 1 : 0;
            n += PatchCmake(Path.Combine(unityLibrary, "src", "main", "cpp", "CMakeLists.txt")) ? 1 : 0;
            n += PatchCmake(Path.Combine(unityLibrary, "src", "main", "cpp", "GameActivity", "CMakeLists.txt")) ? 1 : 0;

            Debug.Log(n > 0
                ? $"[BoluoAndroidHostPatch] 已对齐宿主 :{host}（{n} 个文件）@ {folder}/unityLibrary"
                : $"[BoluoAndroidHostPatch] 已是宿主配置 :{host} @ {folder}/unityLibrary");
        }

        static string ResolveUnityLibraryDir(string path)
        {
            path = path.Replace('\\', '/').TrimEnd('/');
            if (path.EndsWith("/unityLibrary", StringComparison.OrdinalIgnoreCase)
                || string.Equals(Path.GetFileName(path), "unityLibrary", StringComparison.OrdinalIgnoreCase))
                return path;
            string nested = Path.Combine(path, "unityLibrary");
            return Directory.Exists(nested) ? nested : null;
        }

        static bool PatchBuildGradle(string path, string host)
        {
            if (!File.Exists(path))
                return false;

            string text = File.ReadAllText(path);
            string desired = $"project(':{host}:com.unity3d.accessibility.androidlib')";
            string newText = ReA11yDep.Replace(text, desired);
            newText = newText.Replace(
                "project(':unityLibrary:com.unity3d.accessibility.androidlib')",
                desired);

            const string marker = "宿主 settings 将本模块命名为";
            if (newText.Contains(desired) && !newText.Contains(marker))
            {
                newText = newText.Replace(
                    $"    implementation {desired}",
                    $"    // {marker} :{host}（非导出根的 :unityLibrary）\n    implementation {desired}");
            }

            return WriteIfChanged(path, text, newText);
        }

        static bool PatchA11yGradle(string path, string host)
        {
            if (!File.Exists(path))
                return false;

            string text = File.ReadAllText(path);
            string newText = ReUnityProject.Replace(text, m =>
            {
                string suffix = m.Groups[2].Success ? m.Groups[2].Value : "";
                return $"project(':{host}{suffix}')";
            });
            return WriteIfChanged(path, text, newText);
        }

        static bool PatchCmake(string path)
        {
            if (!File.Exists(path))
                return false;

            string text = File.ReadAllText(path);
            string newText = ReCmake.Replace(text, m =>
            {
                if (CompareVersion(m.Groups[1].Value, CmakeMin) >= 0)
                    return m.Value;
                return $"cmake_minimum_required(VERSION {CmakeMin})";
            });
            return WriteIfChanged(path, text, newText);
        }

        static bool WriteIfChanged(string path, string oldText, string newText)
        {
            if (string.Equals(oldText, newText, StringComparison.Ordinal))
                return false;
            File.WriteAllText(path, newText);
            return true;
        }

        static int CompareVersion(string a, string b)
        {
            string[] pa = a.Split('.');
            string[] pb = b.Split('.');
            int n = Math.Max(pa.Length, pb.Length);
            for (int i = 0; i < n; i++)
            {
                int va = i < pa.Length && int.TryParse(pa[i], out int x) ? x : 0;
                int vb = i < pb.Length && int.TryParse(pb[i], out int y) ? y : 0;
                if (va != vb)
                    return va.CompareTo(vb);
            }
            return 0;
        }
    }

#if UNITY_ANDROID
    /// <summary>Unity Export Project 回调：任意入口导出后都会跑。</summary>
    class BoluoAndroidUnityExportHostPatchPostGenerate : IPostGenerateGradleAndroidProject
    {
        public int callbackOrder => 900;

        public void OnPostGenerateGradleAndroidProject(string path)
        {
            BoluoAndroidUnityExportHostPatch.Apply(path);
        }
    }
#endif
}
#endif
