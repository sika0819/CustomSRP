#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Game.Editor.BuildTool
{
    /// <summary>
    /// Flag：Temp/host_shell_export.flag
    /// 导出 unityLibrary 到 AndroidProject/UnityExport/Boluo_Android，再编宿主 skyworthUltraDebug。
    /// </summary>
    public static class HostShellExport
    {
        public const string FlagRelPath = "Temp/host_shell_export.flag";

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
            try { File.Delete(flag); }
            catch { return; }

            EditorApplication.delayCall += Run;
        }

        static void Run()
        {
            var config = new BoluoCIBuildSupport.CIConfig
            {
                BuildTarget = BuildTarget.Android,
                Version = PlayerSettings.bundleVersion,
                Environment = BoluoCIBuildSupport.CIEnvironment.Dev,
                VersionBuildType = BoluoCIBuildSupport.CIVersionBuildType.Release,
                AndroidFlavor = "skyworthUltra",
                ScreenOrientation = "sensor",
                OutputDir = BoluoCIBuildSupport.GetDefaultAndroidUnityExportRoot(),
                SkipYooAsset = false,
            };

            Debug.Log("[HostShell] export unityLibrary start");
            int code = BoluoCIBuildSupport.ExportAndroidGradle(config);
            if (code != 0)
            {
                Debug.LogError("[HostShell] export failed code=" + code);
                return;
            }

            if (!BoluoHostGradleBuilder.Assemble(
                    "skyworthUltra", false, msg => Debug.Log("[HostShell] " + msg),
                    out string apk, out string err))
            {
                Debug.LogError("[HostShell] gradle failed: " + err);
                return;
            }

            Debug.Log("[HostShell] APK ready: " + apk);
        }
    }
}
#endif
