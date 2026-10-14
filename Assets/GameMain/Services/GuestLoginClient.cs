using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

using UnityGameFramework.Runtime;
namespace Game
{
    /// <summary>
    /// 编辑器/独立包游客登录。对齐 BoluoUnity：优先本地游客会话进岛；
    /// 可选尝试公共域 touristLogin（失败则回退本地游客，不挡进岛）。
    /// </summary>
    public static class GuestLoginClient
    {
        public const string EditorTryRemoteKey = "Game.Editor.TryRemoteGuestLogin";
        /// <summary>Legacy key；环境以 <see cref="Game.BoluoApi.BoluoApiEnvironment"/> 为准。</summary>
        public const string ApiEnvKey = "Game.Editor.ApiEnv";

        const string TouristLoginPath = "/api/commonUser/touristLogin";

        public static string ResolveUserApiBase()
        {
            // Prefer BoluoApiEnvironment (EditorPrefs Boluo.EditorTest.ApiEnvOverride).
            // Sync legacy Game.Editor.ApiEnv into override when set and override empty.
#if UNITY_EDITOR
            string legacy = UnityEditor.EditorPrefs.GetString(ApiEnvKey, "");
            if (!string.IsNullOrEmpty(legacy)
                && string.IsNullOrEmpty(Game.BoluoApi.BoluoApiEnvironment.GetEditorOverride()))
            {
                Game.BoluoApi.BoluoApiEnvironment.SetEditorOverride(legacy);
            }
#endif
            return Game.BoluoApi.BoluoApiEnvironment.ResolveUserApiBase();
        }

        public static bool ShouldTryRemoteInEditor()
        {
#if UNITY_EDITOR
            return UnityEditor.EditorPrefs.GetBool(EditorTryRemoteKey, false);
#else
            return false;
#endif
        }

        /// <summary>
        /// 协程：读 StreamingAssets boot → 游客会话 → 完成回调。
        /// </summary>
        public static IEnumerator EnsureGuestThenEnter(Action<bool, string> done)
        {
            BootManifest boot = null;
            yield return StreamingAssetsUtil.ReadTextCoroutine(
                StreamingAssetsUtil.BootRelativePath,
                (ok, text) =>
                {
                    boot = ok
                        ? StreamingAssetsUtil.ParseBootManifestOrDefault(text)
                        : StreamingAssetsUtil.LoadBootManifestOrDefault();
                });
            boot ??= StreamingAssetsUtil.LoadBootManifestOrDefault();

            Log.Info(
                $"[GuestLogin] StreamingAssets boot package={boot.packageName} " +
                $"version={boot.version} playMode={boot.playMode}");

            if (!boot.allowGuestLogin)
            {
                done?.Invoke(false, "boot.json disallow guest login");
                yield break;
            }

            GuestSession.LoadFromPrefs();
            if (GuestSession.IsLoggedIn && GuestSession.IsGuest)
            {
                Log.Info($"[GuestLogin] reuse guest userId={GuestSession.UserId}");
                done?.Invoke(true, "reuse");
                yield break;
            }

#if UNITY_EDITOR
            if (ShouldTryRemoteInEditor())
            {
                bool remoteOk = false;
                yield return TryTouristLoginCoroutine(ok => remoteOk = ok);
                if (remoteOk)
                {
                    done?.Invoke(true, "remote");
                    yield break;
                }

                Log.Warning("[GuestLogin] remote touristLogin failed — fallback local editor guest");
            }

            GuestSession.ApplyLocalEditorGuest();
            Log.Info($"[GuestLogin] local editor guest userId={GuestSession.UserId}");
            done?.Invoke(true, "local");
#else
            bool ok = false;
            yield return TryTouristLoginCoroutine(success => ok = success);
            if (!ok)
            {
                GuestSession.ApplyLocalEditorGuest();
                Log.Warning("[GuestLogin] touristLogin failed — local guest fallback");
            }

            done?.Invoke(true, ok ? "remote" : "local");
#endif
        }

        static IEnumerator TryTouristLoginCoroutine(Action<bool> done)
        {
            string url = ResolveUserApiBase() + TouristLoginPath;
            string body = "{\"latitude\":0,\"longitude\":0,\"accuracy\":0}";
            byte[] raw = Encoding.UTF8.GetBytes(body);
            using (UnityWebRequest req = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST))
            {
                req.uploadHandler = new UploadHandlerRaw(raw);
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Content-Type", "application/json");
                req.timeout = 8;
                yield return req.SendWebRequest();
                if (req.result != UnityWebRequest.Result.Success)
                {
                    Log.Warning($"[GuestLogin] touristLogin network fail: {req.error}");
                    done?.Invoke(false);
                    yield break;
                }

                string text = req.downloadHandler.text;
                TouristLoginResponse resp = null;
                try
                {
                    resp = JsonUtility.FromJson<TouristLoginResponse>(text);
                }
                catch (Exception e)
                {
                    Log.Warning("[GuestLogin] touristLogin parse fail: " + e.Message);
                    done?.Invoke(false);
                    yield break;
                }

                if (resp == null || resp.code != 0 || resp.data == null || string.IsNullOrEmpty(resp.data.token))
                {
                    Log.Warning($"[GuestLogin] touristLogin code={resp?.code} msg={resp?.msg}");
                    done?.Invoke(false);
                    yield break;
                }

                GuestSession.ApplyUserSession(resp.data.token, resp.data.userId, isGuest: true);
                Log.Info($"[GuestLogin] touristLogin ok userId={GuestSession.UserId}");
                done?.Invoke(true);
            }
        }

        [Serializable]
        class TouristLoginResponse
        {
            public int code;
            public string msg;
            public TouristLoginData data;
        }

        [Serializable]
        class TouristLoginData
        {
            public string token;
            public string userId;
        }
    }
}
