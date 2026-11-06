#if UNITY_EDITOR
using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace Game.Editor.BuildTool
{
    /// <summary>
    /// Flag 协议：Temp/host_shell_editmode_tests.flag
    /// CLI / Agent 在 Editor 已开时写 flag，由本 watcher 跑 EditMode 测试，写
    /// Temp/host_shell_editmode_tests.done 与 Temp/host_shell_editmode_tests.xml。
    ///
    /// flag 内容（key=value，可空）：
    ///   filter=SilentRestDownloadOrderTests|MusicRowUiStateTests
    ///   assembly=Game.Tests.Editor
    /// </summary>
    public static class HostShellEditModeTests
    {
        public const string FlagRelPath = "Temp/host_shell_editmode_tests.flag";
        public const string DoneRelPath = "Temp/host_shell_editmode_tests.done";
        public const string XmlRelPath = "Temp/host_shell_editmode_tests.xml";

        [InitializeOnLoadMethod]
        static void Watch()
        {
            EditorApplication.update -= Poll;
            EditorApplication.update += Poll;
        }

        static double _next;
        static Request _pending;
        static bool _running;
        static TestRunnerApi _api;
        static Callbacks _callbacks;

        static void Poll()
        {
            if (_running)
                return;
            if (EditorApplication.timeSinceStartup < _next)
                return;
            _next = EditorApplication.timeSinceStartup + 1.0;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                return;
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                return;

            string root = ProjectRoot();
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

            try
            {
                File.Delete(flag);
            }
            catch
            {
                return;
            }

            _pending = Parse(content);
            EditorApplication.update -= RunPending;
            EditorApplication.update += RunPending;
        }

        static void RunPending()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                return;
            EditorApplication.update -= RunPending;
            Request request = _pending;
            _pending = null;
            if (request != null)
                Start(request);
        }

        static void Start(Request request)
        {
            string root = ProjectRoot();
            string done = Path.Combine(root, DoneRelPath);
            string xml = Path.Combine(root, XmlRelPath);
            try
            {
                if (File.Exists(done))
                    File.Delete(done);
            }
            catch
            {
                // ignore
            }

            Debug.Log(
                $"[HostShellEditModeTests] start assembly={request.Assembly} filter={request.Filter}");

            _running = true;
            _api = ScriptableObject.CreateInstance<TestRunnerApi>();
            _callbacks = new Callbacks(xml, done, Finish);
            _api.RegisterCallbacks(_callbacks);

            var filter = new Filter
            {
                testMode = TestMode.EditMode,
                assemblyNames = string.IsNullOrEmpty(request.Assembly)
                    ? null
                    : new[] { request.Assembly },
                groupNames = string.IsNullOrEmpty(request.Filter)
                    ? null
                    : new[] { request.Filter },
            };

            try
            {
                _api.Execute(new ExecutionSettings(filter));
            }
            catch (Exception e)
            {
                WriteDone(done, "error", e.Message);
                Finish();
            }
        }

        static void Finish()
        {
            if (_api != null && _callbacks != null)
            {
                try
                {
                    _api.UnregisterCallbacks(_callbacks);
                }
                catch
                {
                    // ignore
                }
            }

            _callbacks = null;
            _api = null;
            _running = false;
        }

        static string ProjectRoot() =>
            Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        static Request Parse(string content)
        {
            var req = new Request
            {
                Assembly = "Game.Tests.Editor",
                Filter =
                    "SilentRestDownloadOrderTests|MusicRowUiStateTests|PresetCardUiStateTests|PresetBankCatalogGapsTests",
            };
            if (string.IsNullOrWhiteSpace(content))
                return req;
            string[] lines = content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                    continue;
                int eq = line.IndexOf('=');
                if (eq <= 0)
                    continue;
                string key = line.Substring(0, eq).Trim();
                string val = line.Substring(eq + 1).Trim();
                if (string.Equals(key, "filter", StringComparison.OrdinalIgnoreCase))
                    req.Filter = val;
                else if (string.Equals(key, "assembly", StringComparison.OrdinalIgnoreCase))
                    req.Assembly = val;
            }

            return req;
        }

        static void WriteDone(string path, string status, string message)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
                var sb = new StringBuilder();
                sb.Append("status=").Append(status).Append('\n');
                if (!string.IsNullOrEmpty(message))
                    sb.Append("message=").Append(message.Replace('\n', ' ')).Append('\n');
                File.WriteAllText(path, sb.ToString());
            }
            catch (Exception e)
            {
                Debug.LogWarning("[HostShellEditModeTests] write done failed: " + e.Message);
            }
        }

        sealed class Request
        {
            public string Assembly;
            public string Filter;
        }

        sealed class Callbacks : ICallbacks
        {
            readonly string _xmlPath;
            readonly string _donePath;
            readonly Action _onFinish;
            readonly StringBuilder _xml = new StringBuilder(4096);
            int _total;
            int _passed;
            int _failed;
            int _skipped;
            string _lastFail;

            public Callbacks(string xmlPath, string donePath, Action onFinish)
            {
                _xmlPath = xmlPath;
                _donePath = donePath;
                _onFinish = onFinish;
            }

            public void RunStarted(ITestAdaptor testsToRun)
            {
                _xml.Clear();
                _xml.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
                _xml.AppendLine("<test-run>");
            }

            public void RunFinished(ITestResultAdaptor result)
            {
                _xml.AppendLine("</test-run>");
                try
                {
                    File.WriteAllText(_xmlPath, _xml.ToString());
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[HostShellEditModeTests] write xml failed: " + e.Message);
                }

                string status = _failed > 0 ? "error" : "ok";
                string message =
                    $"total={_total} passed={_passed} failed={_failed} skipped={_skipped}";
                if (!string.IsNullOrEmpty(_lastFail))
                    message += " firstFail=" + _lastFail;
                WriteDone(_donePath, status, message);
                Debug.Log($"[HostShellEditModeTests] done {message}");
                _onFinish?.Invoke();
            }

            public void TestStarted(ITestAdaptor test)
            {
            }

            public void TestFinished(ITestResultAdaptor result)
            {
                if (result.HasChildren)
                    return;

                _total++;
                string name = result.FullName ?? result.Name ?? "?";
                string outcome = result.TestStatus.ToString();
                if (result.TestStatus == TestStatus.Passed)
                    _passed++;
                else if (result.TestStatus == TestStatus.Skipped
                         || result.TestStatus == TestStatus.Inconclusive)
                    _skipped++;
                else
                {
                    _failed++;
                    if (string.IsNullOrEmpty(_lastFail))
                        _lastFail = name;
                }

                _xml.Append("  <test-case name=\"")
                    .Append(Escape(name))
                    .Append("\" result=\"")
                    .Append(Escape(outcome))
                    .AppendLine("\" />");
                if (result.TestStatus != TestStatus.Passed
                    && !string.IsNullOrEmpty(result.Message))
                {
                    _xml.Append("    <message><![CDATA[")
                        .Append(result.Message)
                        .AppendLine("]]></message>");
                }
            }

            static string Escape(string s) =>
                (s ?? string.Empty)
                .Replace("&", "&amp;")
                .Replace("\"", "&quot;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;");
        }
    }
}
#endif
