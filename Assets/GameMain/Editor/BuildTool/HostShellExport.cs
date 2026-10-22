#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Game.Editor.BuildTool
{
    /// <summary>
    /// Flag 协议：Temp/host_shell_export.flag
    ///
    /// 默认安卓打包流程 = Unity 导出 Android 工程（unityLibrary）→ 壳工程 AndroidProject 编 APK。
    /// 当 Unity Editor 已经开着时，同一工程不能再起 batchmode 实例，CLI（CL/Android/pack.sh）
    /// 会写 flag，由本 watcher 接管导出，完成后写 Temp/host_shell_export.done 回执。
    ///
    /// flag 内容（key=value 逐行）：
    ///   flavor=skyworthUltra|skyworthNeo|yishu|phone*  （yishu→YiShu，其余→Boluo_Android）
    ///   env=dev|test|prerelease|product   环境宏（对齐壳工程 ENVIRONMENT）
    ///   mode=export|export+assemble  只导出（默认给 CLI）／导出后顺手 assemble
    ///   release=true|false            assemble 时是否 release 变体
    ///   version=1.1.60               版本号，缺省用 PlayerSettings.bundleVersion
    ///   skip_yoo=true|false           跳过 YooAsset 前置（默认 false：打 Yoo → 拷 StreamingAssets）
    ///
    /// 兼容：flag 内容为空或无法解析时按旧行为 = skyworthUltra + 导出后直接 assemble
    ///（.host_export_monitor.sh 依赖 "[HostShell] APK ready" 日志）。
    ///
    /// done 回执（Temp/host_shell_export.done）：
    ///   status=ok|error|retry
    ///   apk=<apk 路径>   仅 export+assemble 时有
    ///   message=<说明>
    ///
    /// status=retry：构建目标切换或 BOLUO_* 宏变更（都会触发域重载）。
    /// 此时请求被存到 Temp/host_shell_export.pending，域重载后由 ResumePending 自动重跑导出，
    /// CLI（pack.sh / pack.ps1）看到 retry 应继续等待而不是报错。
    /// </summary>
    public static class HostShellExport
    {
        public const string FlagRelPath = "Temp/host_shell_export.flag";
        public const string DoneRelPath = "Temp/host_shell_export.done";
        public const string PendingRelPath = "Temp/host_shell_export.pending";

        [InitializeOnLoadMethod]
        static void Watch()
        {
            EditorApplication.update -= Poll;
            EditorApplication.update += Poll;
        }

        static double _next;

        static void Poll()
        {
            if (EditorApplication.timeSinceStartup < _next)
                return;
            _next = EditorApplication.timeSinceStartup + 1.5;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                return;
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                return;

            string root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string flag = Path.Combine(root, FlagRelPath);
            if (!File.Exists(flag))
                return;

            string content;
            try
            {
                content = File.ReadAllText(flag);
            }
            catch
            {
                return;
            }
            try { File.Delete(flag); }
            catch { return; }

            var request = ParseRequest(content);
            // NOTE: EditorApplication.delayCall 在本工程的 Editor 实例上会卡住不回调
            // （域重载后 delayCall 队列不再被派发），导出因此永远不启动。
            // 改用 update 回调做一次性触发，update 在本 Editor 上是正常派发的。
            _pendingRequest = request;
            EditorApplication.update -= RunPending;
            EditorApplication.update += RunPending;
        }

        static ExportRequest _pendingRequest;

        static void RunPending()
        {
            // 域重载后 Unity 可能还在导入资源/编译脚本，等稳定再跑
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                return;

            EditorApplication.update -= RunPending;
            ExportRequest request = _pendingRequest;
            _pendingRequest = null;
            if (request != null)
                Run(request);
        }

        /// <summary>
        /// 域重载后恢复"待重试"的导出请求（切换构建目标 → 域重载 → 重新导出）。
        /// </summary>
        [InitializeOnLoadMethod]
        static void ResumePending()
        {
            string root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string pending = Path.Combine(root, PendingRelPath);
            if (!File.Exists(pending))
                return;

            string content;
            try
            {
                content = File.ReadAllText(pending);
                File.Delete(pending);
            }
            catch
            {
                return;
            }

            Debug.Log("[HostShell] 恢复待重试的导出请求");
            _pendingRequest = ParseRequest(content);
            EditorApplication.update -= RunPending;
            EditorApplication.update += RunPending;
        }

        // ── 请求解析 ────────────────────────────────────────────────────────────

        sealed class ExportRequest
        {
            public string Flavor = "skyworthUltra";
            public BoluoCIBuildSupport.CIEnvironment Environment = BoluoCIBuildSupport.CIEnvironment.Dev;
            public bool Assemble = true;
            public bool Release;
            public string Version;
            /// <summary>true = 跳过 YooAsset；默认 false（打 Yoo → 拷 StreamingAssets → 再导出）。</summary>
            public bool SkipYooAsset;
        }

        static ExportRequest ParseRequest(string content)
        {
            var request = new ExportRequest();
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrWhiteSpace(content))
            {
                foreach (string rawLine in content.Split('\n'))
                {
                    string line = rawLine.Trim();
                    if (line.Length == 0 || line.StartsWith("#"))
                        continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0)
                        continue;
                    values[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                }
            }

            if (values.Count == 0)
            {
                // 旧协议（.host_export_monitor.sh 写 "trigger"）：导出 + assemble
                return request;
            }

            if (values.TryGetValue("flavor", out string flavor))
                request.Flavor = flavor;

            if (values.TryGetValue("env", out string env))
                request.Environment = ParseEnvironment(env);

            if (values.TryGetValue("mode", out string mode))
            {
                request.Assemble = !mode.Equals("export", StringComparison.OrdinalIgnoreCase);
            }

            if (values.TryGetValue("release", out string release))
                request.Release = release.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                                 release.Equals("1", StringComparison.Ordinal);

            if (values.TryGetValue("version", out string version) && !string.IsNullOrWhiteSpace(version))
                request.Version = version;

            if (values.TryGetValue("skip_yoo", out string skipYoo) ||
                values.TryGetValue("skipYoo", out skipYoo) ||
                values.TryGetValue("skipYooAsset", out skipYoo))
            {
                request.SkipYooAsset =
                    skipYoo.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                    skipYoo.Equals("1", StringComparison.Ordinal);
            }

            return request;
        }

        static BoluoCIBuildSupport.CIEnvironment ParseEnvironment(string raw)
        {
            switch ((raw ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "dev":
                case "debug":
                    return BoluoCIBuildSupport.CIEnvironment.Dev;
                case "test":
                case "alpha":
                    return BoluoCIBuildSupport.CIEnvironment.Test;
                case "prerelease":
                case "beta":
                    return BoluoCIBuildSupport.CIEnvironment.PreRelease;
                case "product":
                case "release":
                    return BoluoCIBuildSupport.CIEnvironment.Product;
                default:
                    return BoluoCIBuildSupport.CIEnvironment.Dev;
            }
        }

        // ── 执行 ────────────────────────────────────────────────────────────────

        static void Run(ExportRequest request)
        {
            var config = new BoluoCIBuildSupport.CIConfig
            {
                BuildTarget = BuildTarget.Android,
                Version = string.IsNullOrWhiteSpace(request.Version)
                    ? PlayerSettings.bundleVersion
                    : request.Version,
                Environment = request.Environment,
                VersionBuildType = BoluoCIBuildSupport.CIVersionBuildType.Release,
                AndroidFlavor = request.Flavor,
                ScreenOrientation = "sensor",
                OutputDir = BoluoCIBuildSupport.GetDefaultAndroidUnityExportRoot(),
                SkipYooAsset = request.SkipYooAsset,
            };

            Debug.Log(
                $"[HostShell] export unityLibrary start (flavor={request.Flavor}, env={request.Environment}, " +
                $"skipYoo={request.SkipYooAsset}; 默认流程: Yoo→StreamingAssets→导出)");
            int code = BoluoCIBuildSupport.ExportAndroidGradle(config);
            if (code == 3)
            {
                // 构建目标切换或宏变更（会触发域重载）：存下请求，域重载后自动重跑
                WritePending(request);
                WriteDone("retry", null, "Unity 正在准备导出环境（域重载），完成后自动重试");
                return;
            }
            if (code != 0)
            {
                Debug.LogError("[HostShell] export failed code=" + code);
                WriteDone("error", null, "export failed code=" + code);
                return;
            }
            Debug.Log("[HostShell] export done");

            if (!request.Assemble)
            {
                WriteDone("ok", null, "export done");
                return;
            }

            if (!BoluoHostGradleBuilder.Assemble(
                    request.Flavor, request.Release, msg => Debug.Log("[HostShell] " + msg),
                    out string apk, out string err))
            {
                Debug.LogError("[HostShell] gradle failed: " + err);
                WriteDone("error", null, err);
                return;
            }

            Debug.Log("[HostShell] APK ready: " + apk);
            WriteDone("ok", apk, "APK ready");
        }

        // ── 回执 ────────────────────────────────────────────────────────────────

        static void WritePending(ExportRequest request)
        {
            try
            {
                string root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
                string pending = Path.Combine(root, PendingRelPath);
                Directory.CreateDirectory(Path.GetDirectoryName(pending));
                var sb = new System.Text.StringBuilder();
                sb.Append("flavor=").Append(request.Flavor).Append('\n');
                sb.Append("env=").Append(request.Environment).Append('\n');
                sb.Append("mode=").Append(request.Assemble ? "export+assemble" : "export").Append('\n');
                sb.Append("release=").Append(request.Release ? "true" : "false").Append('\n');
                sb.Append("skip_yoo=").Append(request.SkipYooAsset ? "true" : "false").Append('\n');
                if (!string.IsNullOrEmpty(request.Version))
                    sb.Append("version=").Append(request.Version).Append('\n');
                File.WriteAllText(pending, sb.ToString());
            }
            catch (Exception ex)
            {
                Debug.LogError("[HostShell] 写 pending 失败: " + ex.Message);
            }
        }

        static void WriteDone(string status, string apk, string message)
        {
            try
            {
                string root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
                string done = Path.Combine(root, DoneRelPath);
                Directory.CreateDirectory(Path.GetDirectoryName(done));
                var sb = new System.Text.StringBuilder();
                sb.Append("status=").Append(status).Append('\n');
                if (!string.IsNullOrEmpty(apk))
                    sb.Append("apk=").Append(apk).Append('\n');
                sb.Append("message=").Append(message ?? string.Empty).Append('\n');
                File.WriteAllText(done, sb.ToString());
            }
            catch (Exception ex)
            {
                Debug.LogError("[HostShell] 写 done 回执失败: " + ex.Message);
            }
        }
    }
}
#endif
