#if UNITY_EDITOR
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Game.Editor.BuildTool
{
    /// <summary>
    /// 宿主 AndroidProject：gradlew assemble / install，供发布窗一键「导出后编 APK」。
    /// </summary>
    public static class BoluoHostGradleBuilder
    {
        public static string GetAndroidProjectRoot() =>
            Path.Combine(BoluoCIBuildSupport.GetRepoRoot(), "AndroidProject");

        /// <summary>
        /// skyworthUltra → :app:assembleSkyworthUltraDebug
        /// yishu → :app:assembleYishuDebug
        /// </summary>
        public static string ResolveAssembleTask(string flavor, bool release)
        {
            string suffix = release ? "Release" : "Debug";
            if (!BoluoCIBuildSupport.TryNormalizeAndroidFlavor(flavor, out string norm, out _))
                norm = "skyworthUltra";
            string flavorPascal = norm == "yishu" ? "Yishu" : "SkyworthUltra";
            return $":app:assemble{flavorPascal}{suffix}";
        }

        public static string ResolveInstallTask(string flavor, bool release)
        {
            string suffix = release ? "Release" : "Debug";
            if (!BoluoCIBuildSupport.TryNormalizeAndroidFlavor(flavor, out string norm, out _))
                norm = "skyworthUltra";
            string flavorPascal = norm == "yishu" ? "Yishu" : "SkyworthUltra";
            return $":app:install{flavorPascal}{suffix}";
        }

        public static bool EnsureLocalProperties(out string error)
        {
            error = null;
            string projectRoot = GetAndroidProjectRoot();
            string localProps = Path.Combine(projectRoot, "local.properties");
            string sdkRoot = ResolveAndroidSdkRoot();
            if (string.IsNullOrEmpty(sdkRoot))
            {
                error = "未找到 Android SDK。请设置 ANDROID_HOME / ANDROID_SDK_ROOT，或创建 AndroidProject/local.properties。";
                return false;
            }

            // AGP accepts forward slashes on all platforms.
            string sdkDirLine = "sdk.dir=" + sdkRoot.Replace("\\", "/");

            if (File.Exists(localProps))
            {
                string text = File.ReadAllText(localProps);
                if (text.Contains("sdk.dir="))
                    return true;
            }

            try
            {
                var sb = new StringBuilder();
                if (File.Exists(localProps))
                    sb.AppendLine(File.ReadAllText(localProps).TrimEnd());
                sb.AppendLine(sdkDirLine);
                File.WriteAllText(localProps, sb.ToString());
                Debug.Log($"[BoluoGradle] 已写入 local.properties: {sdkDirLine}");
                return true;
            }
            catch (Exception ex)
            {
                error = $"写入 local.properties 失败: {ex.Message}";
                return false;
            }
        }

        public static bool ValidateUnityLibrary(string flavor, out string warning)
        {
            warning = null;
            string folder = BoluoCIBuildSupport.ResolveAndroidGradleExportFolderName(flavor);
            string unityLibrary = Path.Combine(
                BoluoCIBuildSupport.GetDefaultAndroidUnityExportRoot(), folder, "unityLibrary");
            if (!Directory.Exists(unityLibrary))
            {
                warning = $"缺少 unityLibrary：{unityLibrary}（请先导出）";
                return false;
            }

            string unityClasses = Path.Combine(unityLibrary, "libs", "unity-classes.jar");
            if (!File.Exists(unityClasses))
            {
                warning = $"unityLibrary 不完整（缺 unity-classes.jar）：{unityClasses}";
                return false;
            }

            return true;
        }

        /// <summary>跑 assemble；成功时 apkPath 指向最新 APK。</summary>
        public static bool Assemble(
            string flavor,
            bool release,
            Action<string> log,
            out string apkPath,
            out string error)
        {
            apkPath = null;
            error = null;
            void Log(string msg)
            {
                log?.Invoke(msg);
                Debug.Log($"[BoluoGradle] {msg}");
            }

            if (!EnsureLocalProperties(out error))
                return false;

            if (!ValidateUnityLibrary(flavor, out string warn))
            {
                error = warn;
                return false;
            }

            if (!string.IsNullOrEmpty(warn))
                Log(warn);

            string projectRoot = GetAndroidProjectRoot();
            string task = ResolveAssembleTask(flavor, release);
            Log($"Gradle {task} @ {projectRoot}");

            if (!RunGradle(projectRoot, $"{task} --no-daemon", Log, out error))
                return false;

            if (!TryLocateApk(projectRoot, flavor, release, out apkPath))
            {
                error = "Gradle 成功但未找到 APK（检查 app/build/outputs/apk/）";
                return false;
            }

            Log($"APK: {apkPath}");
            return true;
        }

        public static bool InstallApk(string apkPath, Action<string> log, out string error)
        {
            error = null;
            void Log(string msg)
            {
                log?.Invoke(msg);
                Debug.Log($"[BoluoGradle] {msg}");
            }

            if (string.IsNullOrEmpty(apkPath) || !File.Exists(apkPath))
            {
                error = $"APK 不存在: {apkPath}";
                return false;
            }

            string adb = FindAdb();
            if (string.IsNullOrEmpty(adb))
            {
                error = "未找到 adb（Android SDK platform-tools）";
                return false;
            }

            Log($"adb install -r {apkPath}");
            var psi = new ProcessStartInfo
            {
                FileName = adb,
                Arguments = $"install -r \"{apkPath}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null)
            {
                error = "无法启动 adb";
                return false;
            }

            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                error = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
                return false;
            }

            Log("安装成功");
            return true;
        }

        static bool RunGradle(string projectRoot, string arguments, Action<string> log, out string error)
        {
            error = null;
            string gradlew = Application.platform == RuntimePlatform.WindowsEditor
                ? Path.Combine(projectRoot, "gradlew.bat")
                : Path.Combine(projectRoot, "gradlew");
            if (!File.Exists(gradlew))
            {
                error = $"未找到 gradlew: {gradlew}";
                return false;
            }

            string javaHome = ResolveJavaHome();
            var psi = new ProcessStartInfo
            {
                FileName = gradlew,
                Arguments = arguments,
                WorkingDirectory = projectRoot,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            if (!string.IsNullOrEmpty(javaHome))
                psi.EnvironmentVariables["JAVA_HOME"] = javaHome;

            string sdk = ResolveAndroidSdkRoot();
            if (!string.IsNullOrEmpty(sdk))
            {
                psi.EnvironmentVariables["ANDROID_HOME"] = sdk;
                psi.EnvironmentVariables["ANDROID_SDK_ROOT"] = sdk;
            }

            // Make gradlew executable on macOS/Linux
            if (Application.platform != RuntimePlatform.WindowsEditor)
            {
                try
                {
                    var chmod = Process.Start(new ProcessStartInfo
                    {
                        FileName = "/bin/chmod",
                        Arguments = $"+x \"{gradlew}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });
                    chmod?.WaitForExit(5000);
                }
                catch
                {
                    // ignore
                }
            }

            using var process = Process.Start(psi);
            if (process == null)
            {
                error = "无法启动 Gradle";
                return false;
            }

            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            // Tail log for Editor visibility
            string combined = (stdout ?? "") + "\n" + (stderr ?? "");
            string[] lines = combined.Split('\n');
            int start = Math.Max(0, lines.Length - 40);
            for (int i = start; i < lines.Length; i++)
            {
                if (!string.IsNullOrWhiteSpace(lines[i]))
                    log?.Invoke(lines[i].TrimEnd());
            }

            if (process.ExitCode != 0)
            {
                error = $"Gradle exit={process.ExitCode}";
                return false;
            }

            return true;
        }

        static bool TryLocateApk(string projectRoot, string flavor, bool release, out string apkPath)
        {
            apkPath = null;
            if (!BoluoCIBuildSupport.TryNormalizeAndroidFlavor(flavor, out string norm, out _))
                norm = "skyworthUltra";
            string flavorDir = norm == "yishu" ? "yishu" : "skyworthUltra";
            string buildType = release ? "release" : "debug";
            string apkDir = Path.Combine(projectRoot, "app", "build", "outputs", "apk", flavorDir, buildType);
            if (!Directory.Exists(apkDir))
            {
                // Fallback: search all apks under outputs/apk
                string root = Path.Combine(projectRoot, "app", "build", "outputs", "apk");
                if (!Directory.Exists(root))
                    return false;
                string[] all = Directory.GetFiles(root, "*.apk", SearchOption.AllDirectories);
                if (all.Length == 0)
                    return false;
                apkPath = all.OrderByDescending(File.GetLastWriteTimeUtc).First();
                return true;
            }

            string[] apks = Directory.GetFiles(apkDir, "*.apk", SearchOption.TopDirectoryOnly);
            if (apks.Length == 0)
                return false;
            apkPath = apks.OrderByDescending(File.GetLastWriteTimeUtc).First();
            return true;
        }

        static string ResolveAndroidSdkRoot()
        {
            string env = Environment.GetEnvironmentVariable("ANDROID_HOME")
                         ?? Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT");
            if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env))
                return env.Trim();

            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string[] candidates =
            {
                Path.Combine(home, "Library/Android/sdk"),
                Path.Combine(home, "Android/Sdk"),
                Path.Combine(home, "AppData/Local/Android/Sdk"),
            };
            foreach (string c in candidates)
            {
                if (Directory.Exists(c))
                    return c;
            }

            // Unity External Tools
            try
            {
                string unitySdk = EditorPrefs.GetString("AndroidSdkRoot");
                if (!string.IsNullOrEmpty(unitySdk) && Directory.Exists(unitySdk))
                    return unitySdk;
            }
            catch
            {
                // ignore
            }

            return null;
        }

        /// <summary>宿主 README 要求 JDK 21。</summary>
        static string ResolveJavaHome()
        {
            string fromEnv = Environment.GetEnvironmentVariable("BOLUO_GRADLE_JAVA_HOME")
                             ?? Environment.GetEnvironmentVariable("JAVA_HOME");
            if (!string.IsNullOrWhiteSpace(fromEnv) && Directory.Exists(fromEnv))
                return fromEnv.Trim();

            if (Application.platform == RuntimePlatform.OSXEditor)
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = "/usr/libexec/java_home",
                        Arguments = "-v 21",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true
                    };
                    using var p = Process.Start(psi);
                    if (p != null)
                    {
                        string path = p.StandardOutput.ReadToEnd().Trim();
                        p.WaitForExit(5000);
                        if (p.ExitCode == 0 && Directory.Exists(path))
                            return path;
                    }
                }
                catch
                {
                    // ignore
                }
            }

            return fromEnv;
        }

        static string FindAdb()
        {
            string sdk = ResolveAndroidSdkRoot();
            if (!string.IsNullOrEmpty(sdk))
            {
                string candidate = Path.Combine(sdk, "platform-tools",
                    Application.platform == RuntimePlatform.WindowsEditor ? "adb.exe" : "adb");
                if (File.Exists(candidate))
                    return candidate;
            }

            string[] fallbacks =
            {
                "/opt/homebrew/bin/adb",
                "/usr/local/bin/adb",
                "adb"
            };
            foreach (string f in fallbacks)
            {
                if (f == "adb" || File.Exists(f))
                    return f;
            }

            return null;
        }
    }
}
#endif
