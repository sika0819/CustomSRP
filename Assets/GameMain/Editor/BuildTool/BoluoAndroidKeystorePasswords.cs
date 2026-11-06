#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Game.Editor.BuildTool
{
    /// <summary>
    /// Unity 不把 Android keystore / alias 密码写入 ProjectSettings（重启必丢）。
    /// 从 AndroidProject/gradle.properties 的 boluoKeystorePass / boluoKeyPass 自动灌回
    /// PlayerSettings，避免每次导出弹窗重输。与宿主 Gradle 签名同源。
    /// </summary>
    [InitializeOnLoad]
    public sealed class BoluoAndroidKeystorePasswords : IPreprocessBuildWithReport
    {
        const string KeystorePassKey = "boluoKeystorePass";
        const string KeyPassKey = "boluoKeyPass";

        public int callbackOrder => -1000;

        static BoluoAndroidKeystorePasswords()
        {
            // 延一帧：避免与 ProjectSettings 反序列化抢写。
            // delayCall 是 void 委托，不能直接绑返回 bool 的 ApplyIfNeeded（CS0407）。
            EditorApplication.delayCall += () => ApplyIfNeeded();
        }

        public void OnPreprocessBuild(BuildReport report)
        {
            if (report.summary.platform == BuildTarget.Android)
                ApplyIfNeeded();
        }

        [MenuItem("Tools/Boluo/Android/Apply Keystore Passwords From gradle.properties")]
        public static void ApplyFromMenu()
        {
            if (ApplyIfNeeded())
                Debug.Log("[BoluoKeystore] 已从 gradle.properties 写入 PlayerSettings 密码");
            else
                Debug.LogWarning("[BoluoKeystore] 未找到可用密码（检查 AndroidProject/gradle.properties）");
        }

        /// <summary>环境变量 / CLI / gradle.properties 三级；供 CI 与 Editor 共用。</summary>
        public static bool TryResolve(out string keystorePass, out string keyPass)
        {
            keystorePass = BoluoCIArgParser.Resolve("BOLUO_ANDROID_KEYSTORE_PASS", "boluoKeystorePass")
                ?? ReadGradleProperty(KeystorePassKey);
            keyPass = BoluoCIArgParser.Resolve("BOLUO_ANDROID_KEY_PASS", "boluoKeyPass")
                ?? ReadGradleProperty(KeyPassKey);
            return !string.IsNullOrEmpty(keystorePass) || !string.IsNullOrEmpty(keyPass);
        }

        public static bool ApplyIfNeeded()
        {
            if (!TryResolve(out string storePass, out string keyPass))
                return false;

            bool changed = false;
            if (!string.IsNullOrEmpty(storePass)
                && PlayerSettings.Android.keystorePass != storePass)
            {
                PlayerSettings.Android.keystorePass = storePass;
                changed = true;
            }

            if (!string.IsNullOrEmpty(keyPass)
                && PlayerSettings.Android.keyaliasPass != keyPass)
            {
                PlayerSettings.Android.keyaliasPass = keyPass;
                changed = true;
            }

            // 确保走自定义 keystore（路径已在 ProjectSettings；只补开关）。
            if (!PlayerSettings.Android.useCustomKeystore)
            {
                PlayerSettings.Android.useCustomKeystore = true;
                changed = true;
            }

            return changed || (!string.IsNullOrEmpty(storePass) && !string.IsNullOrEmpty(keyPass));
        }

        static string ReadGradleProperty(string key)
        {
            string path = ResolveGradlePropertiesPath();
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return null;

            try
            {
                foreach (string raw in File.ReadLines(path))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                        continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0)
                        continue;
                    string k = line.Substring(0, eq).Trim();
                    if (!k.Equals(key, StringComparison.Ordinal))
                        continue;
                    string v = line.Substring(eq + 1).Trim();
                    return string.IsNullOrEmpty(v) ? null : v;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[BoluoKeystore] 读 {path} 失败: {e.Message}");
            }

            return null;
        }

        static string ResolveGradlePropertiesPath()
        {
            // CustomSRP → BoluoProjectNew/AndroidProject/gradle.properties
            string root = Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));
            string candidate = Path.Combine(root, "AndroidProject", "gradle.properties");
            if (File.Exists(candidate))
                return candidate;

            // 兼容单独打开 CustomSRP 且 AndroidProject 同级不在上一级的布局
            candidate = Path.Combine(Application.dataPath, "..", "AndroidProject", "gradle.properties");
            return File.Exists(candidate) ? Path.GetFullPath(candidate) : null;
        }
    }
}
#endif
