#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEngine;
#if UNITY_IOS
using UnityEditor.iOS.Xcode;
#endif

namespace Game.Editor.BuildTool
{
    /// <summary>
    /// iOS 导出后补齐 Unity 不会自动挂上的系统依赖：
    /// MediaPlayer（BoluoMusicPlayerIOS NowPlaying）、VideoToolbox / zlib（libWIAEngine）。
    /// </summary>
    public static class BoluoIOSPostProcessBuild
    {
        [PostProcessBuild(100)]
        public static void OnPostProcessBuild(BuildTarget target, string pathToBuiltProject)
        {
#if UNITY_IOS
            if (target != BuildTarget.iOS)
                return;

            string pbxPath = PBXProject.GetPBXProjectPath(pathToBuiltProject);
            if (!File.Exists(pbxPath))
            {
                Debug.LogWarning($"[BoluoIOSPostProcess] pbxproj 未找到: {pbxPath}");
                return;
            }

            var proj = new PBXProject();
            proj.ReadFromFile(pbxPath);

            string frameworkGuid = proj.GetUnityFrameworkTargetGuid();
            string mainGuid = proj.GetUnityMainTargetGuid();

            string[] frameworks =
            {
                "VideoToolbox.framework",
                "MediaPlayer.framework",
            };

            foreach (string fw in frameworks)
            {
                EnsureFramework(proj, frameworkGuid, fw);
                EnsureFramework(proj, mainGuid, fw);
            }

            if (!string.IsNullOrEmpty(frameworkGuid))
                proj.AddBuildProperty(frameworkGuid, "OTHER_LDFLAGS", "-lz");

            proj.WriteToFile(pbxPath);
            Debug.Log("[BoluoIOSPostProcess] VideoToolbox / MediaPlayer / -lz 已写入 UnityFramework");
#endif
        }

#if UNITY_IOS
        static void EnsureFramework(PBXProject proj, string targetGuid, string fw)
        {
            if (string.IsNullOrEmpty(targetGuid))
                return;
            if (!proj.ContainsFramework(targetGuid, fw))
            {
                proj.AddFrameworkToProject(targetGuid, fw, false);
                Debug.Log($"[BoluoIOSPostProcess] +{fw} → {targetGuid}");
            }
        }
#endif
    }
}
#endif
