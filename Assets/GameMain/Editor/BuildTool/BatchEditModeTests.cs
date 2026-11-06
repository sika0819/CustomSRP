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
    /// batchmode：-executeMethod Game.Editor.BuildTool.BatchEditModeTests.Run
    /// 可选 -testFilter / -testResults（与 UTF CLI 同名），缺省写 Temp/EditMode_bank_download_tests.xml。
    /// </summary>
    public static class BatchEditModeTests
    {
        static TestRunnerApi _api;
        static Callbacks _callbacks;

        public static void Run()
        {
            string root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string results = Arg("testResults")
                ?? Path.Combine(root, "Temp", "EditMode_bank_download_tests.xml");
            string filter = Arg("testFilter")
                ?? "SilentRestDownloadOrderTests|MusicRowUiStateTests|PresetCardUiStateTests|PresetBankCatalogGapsTests";
            string assembly = Arg("assemblyNames") ?? "Game.Tests.Editor";

            Directory.CreateDirectory(Path.GetDirectoryName(results) ?? root);
            Debug.Log($"[BatchEditModeTests] start assembly={assembly} filter={filter} results={results}");

            _api = ScriptableObject.CreateInstance<TestRunnerApi>();
            _callbacks = new Callbacks(results);
            _api.RegisterCallbacks(_callbacks);
            var f = new Filter
            {
                testMode = TestMode.EditMode,
                assemblyNames = new[] { assembly },
                groupNames = string.IsNullOrEmpty(filter) ? null : new[] { filter },
            };
            _api.Execute(new ExecutionSettings(f));
        }

        static string Arg(string name)
        {
            string[] args = Environment.GetCommandLineArgs();
            string key = "-" + name;
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], key, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            }

            return null;
        }

        sealed class Callbacks : ICallbacks
        {
            readonly string _xmlPath;
            readonly StringBuilder _xml = new StringBuilder(8192);
            int _total;
            int _passed;
            int _failed;
            int _skipped;
            string _firstFail;

            public Callbacks(string xmlPath) => _xmlPath = xmlPath;

            public void RunStarted(ITestAdaptor testsToRun)
            {
                _xml.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
                _xml.Append("<test-run total=\"0\" passed=\"0\" failed=\"0\" skipped=\"0\" result=\"Inconclusive\">\n");
            }

            public void RunFinished(ITestResultAdaptor result)
            {
                string status = _failed > 0 ? "Failed" : "Passed";
                string body = _xml.ToString().Replace(
                    "<test-run total=\"0\" passed=\"0\" failed=\"0\" skipped=\"0\" result=\"Inconclusive\">",
                    $"<test-run total=\"{_total}\" passed=\"{_passed}\" failed=\"{_failed}\" skipped=\"{_skipped}\" result=\"{status}\">");
                body += "</test-run>\n";
                File.WriteAllText(_xmlPath, body);
                Debug.Log(
                    $"[BatchEditModeTests] done total={_total} passed={_passed} failed={_failed} skipped={_skipped}");
                if (!string.IsNullOrEmpty(_firstFail))
                    Debug.LogError("[BatchEditModeTests] firstFail=" + _firstFail);

                if (_api != null)
                {
                    try
                    {
                        _api.UnregisterCallbacks(this);
                    }
                    catch
                    {
                        // ignore
                    }
                }

                EditorApplication.Exit(_failed > 0 ? 1 : 0);
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
                    if (string.IsNullOrEmpty(_firstFail))
                        _firstFail = name;
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
