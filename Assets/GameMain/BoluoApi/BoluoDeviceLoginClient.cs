using System;
using System.Collections.Generic;
using System.Text;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json;
using UnityGameFramework.Runtime;
using Game.Utility;
using Game.Native;
using UnityEngine.Networking;

namespace Game.BoluoApi
{
    /// <summary>
    /// 公共域两步换票：boluoRsa → boluoDeviceLogin，写入 <see cref="BoluoSession"/>。
    /// Editor / 无原生宿主时自换取；真机 hybrid 后续可改走原生 SSOT。
    /// </summary>
    public static class BoluoDeviceLoginClient
    {
        const string RsaPath = "/api/commonUser/boluoRsa";
        const string DeviceLoginPath = "/api/commonUser/boluoDeviceLogin";
        const string TouristLoginPath = "/api/commonUser/touristLogin";
        const int MaxTicketAttempts = 3;

        static readonly Http.RetryPolicy Retry = Http.RetryPolicy.DefaultApi;
        static readonly Http.RetryPolicy BootRetry = Http.RetryPolicy.BootApi;

        [Serializable]
        class CipherTextRequest
        {
            public string deviceId;
            public string channel;
            public int appId;
            public string version;
            public int osType;
            public string osVersion;
            public string brand;
            public string model;
            public string originEnd;
            public string deviceType;
            public string company;
        }

        [Serializable]
        class DeviceLoginRequest
        {
            public string cipherText;
            public float latitude;
            public float longitude;
            public float accuracy;
        }

        [Serializable]
        class CipherTextResponse
        {
            public int code;
            public string msg;
            public CipherTextData data;
        }

        [Serializable]
        class CipherTextData
        {
            public string cipherText;
        }

        [Serializable]
        class DeviceLoginResponse
        {
            public int code;
            public string msg;
            public DeviceLoginData data;
        }

        [Serializable]
        class DeviceLoginData
        {
            public string deviceToken;
            public uint expireTime;
        }

        [Serializable]
        class TouristLoginRequest
        {
            public float latitude;
            public float longitude;
            public float accuracy;
        }

        [Serializable]
        class TouristLoginResponse
        {
            public int code;
            public string msg;
            public UserSession data;
        }

        /// <summary>
        /// Hybrid 票只向宿主 Query。Editor 宿主自己打 HTTP 时关掉，避免 Query 自己。
        /// </summary>
        static bool PreferHybridTicket()
        {
#if UNITY_EDITOR
            if (Game.Native.EditorNativeHost.HostOwnsHttp)
                return false;
#endif
            return Game.Native.NativeSession.IsHybridAvailable();
        }

        /// <summary>
        /// 独立包 / 无原生 userToken 时走 POST touristLogin；hybrid 优先 JNI。
        /// upgrade/bank 需要 Header userToken，否则 banks=0。
        /// </summary>
        public static UniTask<bool> EnsureUserTokenAsync() => EnsureUserTokenAsync(failFast: false);

        /// <summary>
        /// 独立包 / 无原生 userToken 时走 POST touristLogin；hybrid 优先 JNI。
        /// </summary>
        public static async UniTask<bool> EnsureUserTokenAsync(bool failFast)
        {
            // Hybrid：宿主 SSOT 优先。退登后 Prefs 可能仍有旧游客票；
            // 若先 short-circuit Prefs，游客→正式会沿用脏 token / 跳过 JNI 新票。
            if (PreferHybridTicket())
            {
                if (Game.Native.NativeSession.TryGetUserSession(
                        out string hybridToken, out bool hybridIsGuest, out string hybridUserId,
                        out bool hybridSaveLocal)
                    && !string.IsNullOrEmpty(hybridToken))
                {
                    // 身份只认原生 userId。缺 userId 停在罩上，不把票当成游客。
                    if (string.IsNullOrEmpty(hybridUserId))
                    {
                        Log.Warning(
                            $"[DeviceLogin] hybrid userToken len={hybridToken.Length} " +
                            "without userId — wait host");
                        return false;
                    }

                    // 真机宿主不报游客。Editor 宿主 blob 带 isGuest，否则本地游客会被写成正式账号。
#if !UNITY_EDITOR
                    hybridIsGuest = false;
#endif
                    Game.GuestSession.ApplyUserSession(
                        hybridToken, hybridUserId, hybridIsGuest, saveLocal: hybridSaveLocal);
                    Log.Info(
                        $"[DeviceLogin] hybrid userToken len={hybridToken.Length} " +
                        $"userId={hybridUserId} guest={hybridIsGuest} saveLocal={hybridSaveLocal}");
                    return true;
                }

                // Hybrid 宿主暂无票：清掉 Unity 侧任何旧用户票，停在罩上等宿主。
                // 107 已经 Clear 之后 Prefs 是空的，若继续往下会 POST touristLogin，
                // 把「正要登正式账号」锁成 Unity 游客票。
                BoluoSession.LoadFromPrefs();
                if (!string.IsNullOrEmpty(BoluoSession.UserToken))
                {
                    Log.Warning(
                        "[DeviceLogin] hybrid empty userToken — clear stale Unity session, wait host");
                    IslandEntryLog.Wait("userTicket", "staleCleared");
                    Game.GuestSession.Clear();
                }
                else
                {
                    Log.Warning("[DeviceLogin] hybrid empty userToken — wait host");
                    IslandEntryLog.Wait("userTicket", "hostEmpty");
                }

                return false;
            }

            BoluoSession.LoadFromPrefs();
            if (!string.IsNullOrEmpty(BoluoSession.UserToken))
                return true;

            if (string.IsNullOrEmpty(BoluoSession.DeviceToken))
            {
                bool deviceOk = await EnsureDeviceTokenAsync(failFast);
                if (!deviceOk)
                {
                    Log.Warning("[DeviceLogin] EnsureUserToken skip — deviceToken missing");
                    return false;
                }
            }

            return await RequestTouristLoginAsync(failFast);
        }

        /// <summary>
        /// 若本地 deviceToken 有效则跳过；否则 boluoRsa + boluoDeviceLogin。
        /// </summary>
        public static UniTask<bool> EnsureDeviceTokenAsync() => EnsureDeviceTokenAsync(failFast: false);

        /// <summary>
        /// 若本地 deviceToken 有效则跳过；否则 boluoRsa + boluoDeviceLogin。
        /// <paramref name="forceRefresh"/> 为真时忽略仍有效的本地票（Editor 占位票换真票）。
        /// </summary>
        public static async UniTask<bool> EnsureDeviceTokenAsync(bool failFast, bool forceRefresh = false)
        {
            SignatureLib.Init();
            BoluoSession.LoadFromPrefs();

            if (!forceRefresh && BoluoSession.IsDeviceTokenValid())
            {
                Log.Info(
                    $"[DeviceLogin] reuse deviceToken len={BoluoSession.DeviceToken.Length} " +
                    $"expire={BoluoSession.DeviceTokenExpireTime}");
                IslandEntryLog.Skip(
                    "deviceTicket",
                    "reuse",
                    "len=" + BoluoSession.DeviceToken.Length);
                return true;
            }

            // Hybrid：设备票只向宿主要。强制重刷时宿主自己打 HTTP，不 Query 占位票。
            if (!forceRefresh && PreferHybridTicket())
            {
                bool ok = Game.Native.NativeSession.EnsureDeviceToken(forceRefresh: true);
                if (ok)
                {
                    Log.Info(
                        $"[DeviceLogin] hybrid native token len={BoluoSession.DeviceToken.Length} " +
                        $"expire={BoluoSession.DeviceTokenExpireTime}");
                    return true;
                }

                Log.Warning("[DeviceLogin] hybrid EnsureDeviceToken failed — stay on cover, no Unity ticket");
                return false;
            }

            string userBase = BoluoApiEnvironment.ResolveUserApiBase();
            Log.Info(
                $"[DeviceLogin] refresh env={BoluoApiEnvironment.GetEnvironmentType()} " +
                $"userBase={userBase} channel={BoluoDeviceInfo.GetChannel()} " +
                $"originEnd={BoluoDeviceInfo.GetOriginEnd()} " +
                $"appId={BoluoDeviceInfo.GetAppId()} version={BoluoDeviceInfo.GetVersion()}");

            // channel 与 originEnd 独立配置；闺蜜机错档（大写渠道 + boluoapp）易 400。
            if (string.Equals(BoluoDeviceInfo.GetChannel(), "BOLUOCWGMJ", StringComparison.OrdinalIgnoreCase)
                && string.Equals(BoluoDeviceInfo.GetOriginEnd(), BoluoDeviceInfo.DefaultOriginEndApp, StringComparison.Ordinal))
            {
                Log.Warning(
                    "[DeviceLogin] channel=BOLUOCWGMJ 但 originEnd=boluoapp，疑似错档；" +
                    "闺蜜机应使用 originEnd=boluocwgmj（Editor：环境·渠道·版本窗口覆盖）。");
            }

            int maxAttempts = failFast ? 1 : MaxTicketAttempts;
            string lastFail = null;
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                if (attempt > 1)
                {
                    Log.Warning($"[DeviceLogin] retry ticket {attempt}/{maxAttempts}");
                    await UniTask.Delay(TimeSpan.FromSeconds(0.4f), ignoreTimeScale: true);
                }

                var (cipher, cipherFail) = await RequestCipherTextAsync(failFast);
                if (string.IsNullOrEmpty(cipher))
                {
                    lastFail = cipherFail ?? "boluoRsa 未拿到 cipherText";
                    Log.Warning($"[DeviceLogin] {lastFail}");
                    continue;
                }

                var (loginOk, loginFail) = await RequestDeviceLoginAsync(cipher, failFast);
                if (loginOk && !string.IsNullOrEmpty(BoluoSession.DeviceToken))
                {
                    Log.Info(
                        $"[DeviceLogin] ok deviceTokenLen={BoluoSession.DeviceToken.Length} " +
                        $"expire={BoluoSession.DeviceTokenExpireTime}");
                    return true;
                }

                lastFail = loginFail ?? "boluoDeviceLogin 未拿到 deviceToken";
                Log.Warning($"[DeviceLogin] {lastFail}");
            }

            Log.Error(
                "[DeviceLogin] deviceToken 换票失败 — 后续 upgrade/bank 易 400。" +
                " Editor 常见原因是 UnityWebRequest 连不上 wanos-common-*。");
            UiDialogService.ShowRequestFailure(
                string.IsNullOrEmpty(lastFail) ? "登录失败：deviceToken 换票失败" : $"登录失败：{lastFail}");
            return false;
        }

        static async UniTask<(string cipher, string fail)> RequestCipherTextAsync(bool failFast = false)
        {
            var body = new CipherTextRequest
            {
                deviceId = BoluoDeviceInfo.GetIdentifier(),
                channel = BoluoDeviceInfo.GetChannel(),
                appId = BoluoDeviceInfo.GetAppId(),
                version = BoluoDeviceInfo.GetVersion(),
                osType = BoluoDeviceInfo.GetOsType(),
                osVersion = BoluoDeviceInfo.GetOsVersion(),
                brand = BoluoDeviceInfo.GetBrand(),
                model = BoluoDeviceInfo.GetModel(),
                originEnd = BoluoDeviceInfo.GetOriginEnd(),
                deviceType = BoluoDeviceInfo.GetDeviceType(),
                company = BoluoDeviceInfo.GetCompanyName(),
            };
            string json = JsonConvert.SerializeObject(body);
            string url = BoluoApiEnvironment.ResolveUserApiBase() + RsaPath;

            var (transportOk, responseText) = await PostJsonAsync(url, json, failFast: failFast);
            if (!transportOk)
                return (null, $"boluoRsa 网络失败（{url}）");
            if (string.IsNullOrEmpty(responseText))
                return (null, "boluoRsa 空响应");

            CipherTextResponse resp;
            try
            {
                resp = JsonConvert.DeserializeObject<CipherTextResponse>(responseText);
            }
            catch (Exception e)
            {
                return (null, $"boluoRsa 解析失败: {e.Message}");
            }

            if (resp == null)
                return (null, "boluoRsa 解析失败");
            if (resp.code != 0 || resp.data == null || string.IsNullOrEmpty(resp.data.cipherText))
                return (null, $"boluoRsa code={resp.code} msg={resp.msg}");

            return (resp.data.cipherText, null);
        }

        static async UniTask<(bool ok, string fail)> RequestDeviceLoginAsync(string cipherText, bool failFast = false)
        {
            var body = new DeviceLoginRequest
            {
                cipherText = cipherText,
                latitude = BoluoDeviceInfo.GetLatitude(),
                longitude = BoluoDeviceInfo.GetLongitude(),
                accuracy = BoluoDeviceInfo.GetAccuracy(),
            };
            string json = JsonConvert.SerializeObject(body);
            string url = BoluoApiEnvironment.ResolveUserApiBase() + DeviceLoginPath;

            var (transportOk, responseText) = await PostJsonAsync(url, json, failFast: failFast);
            if (!transportOk)
                return (false, $"boluoDeviceLogin 网络失败（{url}）");
            if (string.IsNullOrEmpty(responseText))
                return (false, "boluoDeviceLogin 空响应");

            DeviceLoginResponse resp;
            try
            {
                resp = JsonConvert.DeserializeObject<DeviceLoginResponse>(responseText);
            }
            catch (Exception e)
            {
                return (false, $"boluoDeviceLogin 解析失败: {e.Message}");
            }

            if (resp == null)
                return (false, "boluoDeviceLogin 解析失败");
            if (resp.code != 0 || resp.data == null || string.IsNullOrEmpty(resp.data.deviceToken))
                return (false, $"boluoDeviceLogin code={resp.code} msg={resp.msg}");

            BoluoSession.SetDeviceToken(resp.data.deviceToken, resp.data.expireTime);
            return (true, null);
        }

        static async UniTask<bool> RequestTouristLoginAsync(bool failFast = false)
        {
            var body = new TouristLoginRequest
            {
                latitude = BoluoDeviceInfo.GetLatitude(),
                longitude = BoluoDeviceInfo.GetLongitude(),
                accuracy = BoluoDeviceInfo.GetAccuracy(),
            };
            string json = JsonConvert.SerializeObject(body);
            string url = BoluoApiEnvironment.ResolveUserApiBase() + TouristLoginPath;

            var (transportOk, responseText) = await PostJsonAsync(
                url, json, forceEmptyUserToken: true, failFast: failFast);
            if (!transportOk)
            {
                Log.Warning($"[DeviceLogin] touristLogin 网络失败（{url}）");
                UiDialogService.ShowRequestFailure("登录失败：游客登录网络失败");
                return false;
            }

            if (string.IsNullOrEmpty(responseText))
            {
                Log.Warning("[DeviceLogin] touristLogin 空响应");
                UiDialogService.ShowRequestFailure("登录失败：游客登录返回空");
                return false;
            }

            TouristLoginResponse resp;
            try
            {
                resp = JsonConvert.DeserializeObject<TouristLoginResponse>(responseText);
            }
            catch (Exception e)
            {
                Log.Warning($"[DeviceLogin] touristLogin 解析失败: {e.Message}");
                UiDialogService.ShowRequestFailure("登录失败：游客登录响应无法解析");
                return false;
            }

            if (resp == null || resp.code != 0 || resp.data == null || string.IsNullOrEmpty(resp.data.token))
            {
                Log.Warning($"[DeviceLogin] touristLogin code={resp?.code} msg={resp?.msg}");
                UiDialogService.ShowRequestFailure(
                    string.IsNullOrEmpty(resp?.msg)
                        ? $"登录失败：游客登录 code={resp?.code}"
                        : $"登录失败：{resp.msg}");
                return false;
            }

            BoluoSession.ApplyUserSession(resp.data, isGuest: true, saveLocal: false);
            Log.Info(
                $"[DeviceLogin] touristLogin ok userToken len={resp.data.token.Length} " +
                $"userId={resp.data.userId ?? ""}");
            return true;
        }

        static async UniTask<(bool ok, string text)> PostJsonAsync(
            string url,
            string bodyJson,
            bool forceEmptyUserToken = false,
            bool failFast = false)
        {
            SignatureLib.Init();
            BoluoSession.LoadFromPrefs();

            byte[] body = Encoding.UTF8.GetBytes(bodyJson ?? "{}");
            string responseText = null;
            bool transportOk = false;
            var policy = failFast ? BootRetry : Retry;

            await Http.SendWithRetry(
                () =>
                {
                    SignatureLib.SignatureGeneration(bodyJson ?? "{}", out string sign, out string nonce);
                    var headers = BuildHeaders(sign, nonce, forceEmptyUserToken);
                    var req = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST)
                    {
                        uploadHandler = new UploadHandlerRaw(body),
                        downloadHandler = new DownloadHandlerBuffer(),
                        timeout = policy.RequestTimeoutSeconds,
                    };
                    req.SetRequestHeader("Content-Type", "application/json");
                    foreach (var kv in headers)
                        req.SetRequestHeader(kv.Key, kv.Value ?? "");
                    return req;
                },
                policy,
                req =>
                {
                    if (req != null && req.result == UnityWebRequest.Result.Success)
                    {
                        responseText = req.downloadHandler?.text;
                        transportOk = true;
                    }
                    else if (req != null)
                    {
                        Log.Warning(
                            $"[DeviceLogin] HTTP fail url={url} result={req.result} " +
                            $"code={req.responseCode} err={req.error}");
                    }
                    req?.Dispose();
                },
                req => req != null && req.result == UnityWebRequest.Result.Success,
                (attempt, delay, err) =>
                    Log.Warning($"[DeviceLogin] retry attempt={attempt} wait={delay:0.#}s err={err}"))
                .ToUniTask();

            return (transportOk, responseText);
        }

        static Dictionary<string, string> BuildHeaders(string sign, string nonceStr, bool forceEmptyUserToken = false)
        {
            return new Dictionary<string, string>
            {
                { "deviceToken", BoluoSession.DeviceToken ?? "" },
                { "userToken", forceEmptyUserToken ? "" : BoluoSession.UserToken ?? "" },
                { "deviceId", BoluoDeviceInfo.GetIdentifier() },
                { "channel", BoluoDeviceInfo.GetChannel() },
                { "version", BoluoDeviceInfo.GetVersion() },
                { "healthChannel", BoluoDeviceInfo.GetHealthChannel() },
                { "thirdPartyId", BoluoDeviceInfo.GetThirdPartyId() },
                { "originEnd", BoluoDeviceInfo.GetOriginEnd() },
                { "notCheckSign", "0" },
                { "sign", sign ?? "" },
                { "nonceStr", nonceStr ?? "" },
            };
        }
    }
}
