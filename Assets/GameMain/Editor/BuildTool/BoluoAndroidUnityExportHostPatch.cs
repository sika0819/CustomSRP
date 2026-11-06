#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using Debug = UnityEngine.Debug;
#if UNITY_ANDROID
using UnityEditor.Android;
#endif

namespace Game.Editor.BuildTool
{
    /// <summary>
    /// Unity 每次导出都会覆盖 unityLibrary 的 Gradle/CMake。
    /// 轻量 C# 补丁（模块名 / CMake）之后，必须再跑
    /// AndroidProject/scripts/patch_unity_export_for_host.py（SSOT）：
    /// 剥 Hub ndkPath、洗本机 unity.*Path、修 il2cpp SDK resolve、修 launcher keystore 路径。
    /// 保证不装 Unity 的 Windows/Mac 也能用 Android SDK NDK 编包。
    /// </summary>
    public static class BoluoAndroidUnityExportHostPatch
    {
        const string CmakeMin = "3.22.1";
        const string HostPatchScriptRel = "scripts/patch_unity_export_for_host.py";

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

        /// <summary>
        /// 对 gradleRoot（含 unityLibrary）或 unityLibrary 本身打补丁。
        /// 失败返回 false（导出应视为未完成，勿提交未清洗的 Gradle）。
        /// </summary>
        public static bool Apply(string gradleRootOrUnityLibrary)
        {
            if (string.IsNullOrEmpty(gradleRootOrUnityLibrary))
                return false;

            string unityLibrary = ResolveUnityLibraryDir(gradleRootOrUnityLibrary);
            if (string.IsNullOrEmpty(unityLibrary) || !Directory.Exists(unityLibrary))
            {
                Debug.LogWarning($"[BoluoAndroidHostPatch] 未找到 unityLibrary: {gradleRootOrUnityLibrary}");
                return false;
            }

            string folder = new DirectoryInfo(Path.GetDirectoryName(unityLibrary) ?? "").Name;
            if (!FolderToHost.TryGetValue(folder, out string host))
            {
                Debug.LogWarning($"[BoluoAndroidHostPatch] 未知导出目录名 '{folder}'，跳过");
                return false;
            }

            int n = 0;
            n += PatchBuildGradle(Path.Combine(unityLibrary, "build.gradle"), host) ? 1 : 0;
            n += PatchA11yGradle(
                Path.Combine(unityLibrary, "com.unity3d.accessibility.androidlib", "build.gradle"),
                host) ? 1 : 0;
            n += PatchCmake(Path.Combine(unityLibrary, "src", "main", "cpp", "CMakeLists.txt")) ? 1 : 0;
            n += PatchCmake(Path.Combine(unityLibrary, "src", "main", "cpp", "GameActivity", "CMakeLists.txt")) ? 1 : 0;
            n += PatchCmake(Path.Combine(unityLibrary, "src", "main", "cpp", "FramePacing", "CMakeLists.txt")) ? 1 : 0;

            Debug.Log(n > 0
                ? $"[BoluoAndroidHostPatch] 轻量对齐 :{host}（{n} 个文件）@ {folder}/unityLibrary"
                : $"[BoluoAndroidHostPatch] 轻量已是宿主配置 :{host} @ {folder}/unityLibrary");

            if (!RunPythonHostPatch(unityLibrary, out string pyError))
            {
                Debug.LogError($"[BoluoAndroidHostPatch] Python SSOT 失败: {pyError}");
                return false;
            }

            return true;
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

        static string FindAndroidProjectRoot(string unityLibrary)
        {
            // unityLibrary → Boluo_Android|YiShu → UnityExport → AndroidProject
            string viaExport = Path.GetFullPath(Path.Combine(unityLibrary, "..", "..", ".."));
            if (File.Exists(Path.Combine(viaExport, HostPatchScriptRel)))
                return viaExport;

            string viaRepo = Path.GetFullPath(Path.Combine(BoluoCIBuildSupport.GetRepoRoot(), "AndroidProject"));
            if (File.Exists(Path.Combine(viaRepo, HostPatchScriptRel)))
                return viaRepo;

            return null;
        }

        static string FindPythonExecutable()
        {
            foreach (string name in new[] { "python3", "python", "py" })
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = name,
                        Arguments = name == "py" ? "-3 -c \"import sys; print(sys.executable)\"" : "-c \"import sys; print(sys.executable)\"",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                    };
                    using (var p = Process.Start(psi))
                    {
                        if (p == null)
                            continue;
                        string stdout = p.StandardOutput.ReadToEnd().Trim();
                        p.WaitForExit(15000);
                        if (p.ExitCode == 0 && !string.IsNullOrEmpty(stdout) && File.Exists(stdout))
                            return stdout;
                        if (p.ExitCode == 0)
                            return name;
                    }
                }
                catch
                {
                    // try next
                }
            }
            return null;
        }

        static bool RunPythonHostPatch(string unityLibrary, out string error)
        {
            error = null;
            string androidRoot = FindAndroidProjectRoot(unityLibrary);
            if (string.IsNullOrEmpty(androidRoot))
            {
                error = $"找不到 {HostPatchScriptRel}（期望在 AndroidProject 根）";
                return false;
            }

            string script = Path.Combine(androidRoot, HostPatchScriptRel);
            if (!File.Exists(script))
            {
                error = $"缺少 host patch 脚本: {script}";
                return false;
            }

            string python = FindPythonExecutable();
            if (string.IsNullOrEmpty(python))
            {
                error = "未找到 python3/python。请安装 Python 3 后重试导出（或手动: python3 AndroidProject/scripts/patch_unity_export_for_host.py）";
                return false;
            }

            var psi = new ProcessStartInfo
            {
                FileName = python,
                Arguments = QuoteArg(script) + " " + QuoteArg(unityLibrary),
                WorkingDirectory = androidRoot,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            try
            {
                using (var p = Process.Start(psi))
                {
                    if (p == null)
                    {
                        error = "无法启动 Python 进程";
                        return false;
                    }

                    var stdout = new StringBuilder();
                    var stderr = new StringBuilder();
                    p.OutputDataReceived += (_, e) =>
                    {
                        if (e.Data != null)
                            stdout.AppendLine(e.Data);
                    };
                    p.ErrorDataReceived += (_, e) =>
                    {
                        if (e.Data != null)
                            stderr.AppendLine(e.Data);
                    };
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    if (!p.WaitForExit(120000))
                    {
                        try { p.Kill(); } catch { /* ignore */ }
                        error = "Python host patch 超时（>120s）";
                        return false;
                    }

                    string outText = stdout.ToString().Trim();
                    string errText = stderr.ToString().Trim();
                    if (!string.IsNullOrEmpty(outText))
                        Debug.Log($"[BoluoAndroidHostPatch] {outText}");
                    if (!string.IsNullOrEmpty(errText))
                        Debug.LogWarning($"[BoluoAndroidHostPatch] stderr: {errText}");

                    if (p.ExitCode != 0)
                    {
                        string detail = !string.IsNullOrEmpty(errText) ? errText : outText;
                        if (detail.Length > 800)
                            detail = detail.Substring(detail.Length - 800);
                        error = string.IsNullOrEmpty(detail)
                            ? $"exit={p.ExitCode} script={script}"
                            : $"exit={p.ExitCode} script={script}\n{detail}";
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }

            Debug.Log($"[BoluoAndroidHostPatch] Python SSOT OK: {script}");
            return true;
        }

        static string QuoteArg(string path)
        {
            if (string.IsNullOrEmpty(path))
                return "\"\"";
            if (path.IndexOfAny(new[] { ' ', '\t', '"' }) < 0)
                return path;
            return "\"" + path.Replace("\"", "\\\"") + "\"";
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
            if (!BoluoAndroidUnityExportHostPatch.Apply(path))
            {
                Debug.LogError(
                    "[BoluoAndroidHostPatch] PostGenerate host patch 失败。" +
                    "请手动: python3 AndroidProject/scripts/patch_unity_export_for_host.py ；勿提交未清洗的 UnityExport Gradle。");
            }
        }
    }
#endif
}
#endif
