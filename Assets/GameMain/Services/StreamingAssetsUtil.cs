using System;
using System.Collections;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

using UnityGameFramework.Runtime;
namespace Game
{
    /// <summary>
    /// StreamingAssets 读取（对齐 BoluoUnity Offline/Buildin：编辑器与真机统一走 streamingAssetsPath）。
    /// Android jar 内路径不能用 File.Exists，需 UnityWebRequest。
    /// </summary>
    public static class StreamingAssetsUtil
    {
        public const string BootRelativePath = "Game/boot.json";

        public static string RootPath => Application.streamingAssetsPath;

        public static string Combine(string relativePath)
        {
            if (string.IsNullOrEmpty(relativePath))
                return RootPath;
            string normalized = relativePath.Replace('\\', '/').TrimStart('/');
            return Path.Combine(RootPath, normalized).Replace('\\', '/');
        }

        public static bool TryReadTextSync(string relativePath, out string text)
        {
            text = null;
            string full = Combine(relativePath);
#if UNITY_ANDROID && !UNITY_EDITOR
            // APK assets are not plain files — use ReadTextCoroutine on device.
            return false;
#else
            try
            {
                if (!File.Exists(full))
                    return false;
                text = File.ReadAllText(full, Encoding.UTF8);
                return true;
            }
            catch (Exception e)
            {
                Log.Warning("[StreamingAssets] ReadText failed: " + e.Message);
                return false;
            }
#endif
        }

        public static BootManifest ParseBootManifestOrDefault(string json)
        {
            if (!string.IsNullOrEmpty(json))
            {
                try
                {
                    BootManifest parsed = JsonUtility.FromJson<BootManifest>(json);
                    if (parsed != null)
                        return parsed;
                }
                catch (Exception e)
                {
                    Log.Warning("[StreamingAssets] boot.json parse failed: " + e.Message);
                }
            }

            return BootManifest.DefaultForRuntime();
        }

        public static IEnumerator ReadTextCoroutine(string relativePath, Action<bool, string> done)
        {
            string full = Combine(relativePath);
#if UNITY_ANDROID && !UNITY_EDITOR
            using (UnityWebRequest req = UnityWebRequest.Get(full))
            {
                yield return req.SendWebRequest();
                if (req.result != UnityWebRequest.Result.Success)
                {
                    done?.Invoke(false, req.error);
                    yield break;
                }

                done?.Invoke(true, req.downloadHandler.text);
            }
#else
            yield return null;
            if (TryReadTextSync(relativePath, out string text))
                done?.Invoke(true, text);
            else
                done?.Invoke(false, "missing " + full);
#endif
        }

        public static BootManifest LoadBootManifestOrDefault()
        {
            if (TryReadTextSync(BootRelativePath, out string json))
                return ParseBootManifestOrDefault(json);
            return BootManifest.DefaultForRuntime();
        }
    }

    [Serializable]
    public sealed class BootManifest
    {
        public string packageName = "DefaultPackage";
        public string version = "0.0.0";
        public string playMode = "EditorSimulateMode";
        public string yooFolder = "package";
        public bool allowGuestLogin = true;

        public static BootManifest Default => DefaultForRuntime();

        /// <summary>Device-safe defaults when StreamingAssets sync read is unavailable.</summary>
        public static BootManifest DefaultForRuntime()
        {
#if UNITY_EDITOR
            return new BootManifest();
#else
            return new BootManifest
            {
                packageName = "DefaultPackage",
                version = "0.1.0",
                playMode = "OfflinePlayMode",
                yooFolder = "package",
                allowGuestLogin = true,
            };
#endif
        }
    }
}
