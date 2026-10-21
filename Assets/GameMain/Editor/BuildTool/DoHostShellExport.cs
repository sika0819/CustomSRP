// 触发 Unity Editor 脚本重编译后执行壳工程导出
// 用法：Unity -batchmode -projectpath <path> -executeMethod DoHostShellExport.Run -quit
#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEngine;

class DoHostShellExport
{
    static readonly string ApkOutFlag = Path.Combine(
        Path.GetFullPath(Path.Combine(Application.dataPath, "..")),
        "Temp", "host_shell_apk.flag");

    [MenuItem("Game/DoHostShellExport")]
    public static void Run()
    {
        Run(null);
    }

    public static void Run(string _)
    {
        Debug.Log("[DoHostShellExport] Starting...");

        // 1) 强制脚本重编译（等待完成）
        Debug.Log("[DoHostShellExport] Requesting script reload...");
        EditorUtility.RequestScriptReload();

        // 等待 domain reload 完成
        int wait = 0;
        while (wait < 30)
        {
            System.Threading.Thread.Sleep(1000);
            wait++;
            Debug.Log($"[DoHostShellExport] Waiting for script reload... {wait}s");
            if (!EditorApplication.isCompiling && !EditorApplication.isUpdating)
            {
                Debug.Log("[DoHostShellExport] Script reload done.");
                break;
            }
        }
        if (EditorApplication.isCompiling || EditorApplication.isUpdating)
        {
            Debug.LogError("[DoHostShellExport] Script reload timeout!");
            EditorApplication.Exit(1);
            return;
        }

        // 2) 执行导出
        var config = new Game.Editor.BuildTool.BoluoCIBuildSupport.CIConfig
        {
            BuildTarget = BuildTarget.Android,
            Version = PlayerSettings.bundleVersion,
            Environment = Game.Editor.BuildTool.BoluoCIBuildSupport.CIEnvironment.Dev,
            VersionBuildType = Game.Editor.BuildTool.BoluoCIBuildSupport.CIVersionBuildType.Release,
            AndroidFlavor = "skyworthUltra",
            ScreenOrientation = "sensor",
            OutputDir = Game.Editor.BuildTool.BoluoCIBuildSupport.GetDefaultAndroidUnityExportRoot(),
            SkipYooAsset = false,
        };

        Debug.Log("[DoHostShellExport] Running ExportAndroidGradle...");
        int code = Game.Editor.BuildTool.BoluoCIBuildSupport.ExportAndroidGradle(config);
        if (code != 0)
        {
            Debug.LogError($"[DoHostShellExport] ExportAndroidGradle failed code={code}");
            EditorApplication.Exit(1);
            return;
        }

        // 3) Gradle 编 APK
        Debug.Log("[DoHostShellExport] Running Gradle assemble...");
        if (!Game.Editor.BuildTool.BoluoHostGradleBuilder.Assemble(
                "skyworthUltra", false,
                msg => Debug.Log("[Gradle] " + msg),
                out string apk, out string err))
        {
            Debug.LogError($"[DoHostShellExport] Gradle failed: {err}");
            EditorApplication.Exit(1);
            return;
        }

        Debug.Log($"[DoHostShellExport] APK ready: {apk}");

        // 4) 写 flag 供后续监控脚本检测
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ApkOutFlag));
            File.WriteAllText(ApkOutFlag, apk);
            Debug.Log($"[DoHostShellExport] Wrote flag: {ApkOutFlag}");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[DoHostShellExport] Failed to write flag: {ex.Message}");
        }

        EditorApplication.Exit(0);
    }
}
#endif
