using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityGameFramework.Runtime;
using Game.Utility;
using UnityEngine;
using UnityEngine.Networking;

namespace Game.BoluoApi
{
    /// <summary>
    /// POST {BaseUrl}/boluo/v1/system/upgrade/bank ，body "{}"，Header 带 libSignKit 签名。
    /// 传输失败按 DefaultApi 指数退避重试（每次重签）；启动 / Editor 走 BootApi（8s×1）。
    /// </summary>
    public static class BankUpgradeClient
    {
        const string Path = "/boluo/v1/system/upgrade/bank";
        const string EmptyBody = "{}";

        static readonly Http.RetryPolicy Retry = Http.RetryPolicy.DefaultApi;

        static readonly Http.RetryPolicy BootRetry = Http.RetryPolicy.BootApi;

        /// <summary>
        /// 拉取 bank 工程元数据。成功返回 data；失败返回 null（banks 可为空，由调用方决定）。
        /// </summary>
        public static UniTask<BankUpgradeData> RequestUpgradeBank() => RequestUpgradeBank(failFast: false);

        /// <summary>
        /// 拉取 bank 工程元数据。成功返回 data；失败返回 null（banks 可为空，由调用方决定）。
        /// </summary>
        public static async UniTask<BankUpgradeData> RequestUpgradeBank(bool failFast)
        {
            SignatureLib.Init();
            BoluoSession.LoadFromPrefs();

            string baseUrl = BoluoApiEnvironment.ResolveBoluoBaseUrl();
            string url = baseUrl + Path;
            var retry = failFast
                ? BootRetry
#if UNITY_EDITOR
                : BootRetry;
#else
                : Retry;
#endif
            Log.Info(
                $"[BankUpgrade] POST {url} env={BoluoApiEnvironment.GetEnvironmentType()} " +
                $"version={BoluoDeviceInfo.GetVersion()} channel={BoluoDeviceInfo.GetChannel()} " +
                $"originEnd={BoluoDeviceInfo.GetOriginEnd()} " +
                $"deviceTokenLen={BoluoSession.DeviceToken?.Length ?? 0} " +
                $"userTokenLen={BoluoSession.UserToken?.Length ?? 0} " +
                $"retryMax={retry.MaxAttempts}");

            byte[] body = Encoding.UTF8.GetBytes(EmptyBody);
            string responseText = null;
            bool transportOk = false;
            long lastHttpCode = 0;
            string lastTransportError = null;
            bool didAuthRetry = false;

            for (int authRound = 0; authRound < 2; authRound++)
            {
                responseText = null;
                transportOk = false;
                lastHttpCode = 0;
                lastTransportError = null;

                await Http.SendWithRetry(
                    () =>
                    {
                        SignatureLib.SignatureGeneration(EmptyBody, out string sign, out string nonce);
                        var headers = BuildHeaders(sign, nonce);
                        var req = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST)
                        {
                            uploadHandler = new UploadHandlerRaw(body),
                            downloadHandler = new DownloadHandlerBuffer(),
                            timeout = retry.RequestTimeoutSeconds,
                        };
                        req.SetRequestHeader("Content-Type", "application/json");
                        foreach (var kv in headers)
                            req.SetRequestHeader(kv.Key, kv.Value ?? "");
                        return req;
                    },
                    retry,
                    req =>
                    {
                        if (req != null)
                            lastHttpCode = req.responseCode;
                        if (req != null && req.result == UnityWebRequest.Result.Success)
                        {
                            responseText = req.downloadHandler?.text;
                            transportOk = true;
                        }
                        else if (req != null)
                        {
                            lastTransportError = string.IsNullOrEmpty(req.error)
                                ? $"HTTP {req.responseCode}"
                                : req.error;
                        }
                        else if (string.IsNullOrEmpty(lastTransportError))
                        {
                            lastTransportError = "no network";
                        }

                        req?.Dispose();
                    },
                    req => req != null && req.result == UnityWebRequest.Result.Success,
                    (attempt, delay, err) =>
                    {
                        lastTransportError = err;
                        Log.Warning($"[BankUpgrade] retry attempt={attempt} wait={delay:0.#}s err={err}");
                    })
                    .ToUniTask();

                if (!transportOk)
                {
                    // HTTP 401：本地过期时间还没到，服务端已拒绝。清票后强制换票再打一轮。
                    if (!didAuthRetry && lastHttpCode == 401)
                    {
                        didAuthRetry = true;
                        if (await RefreshExpiredDeviceTokenAsync("HTTP 401"))
                            continue;
                        Fail("升级失败：deviceToken 已过期，刷新失败");
                        return null;
                    }

                    break;
                }

                if (TryParseResponse(responseText, out var data, out bool deviceTokenExpired, out string failReason))
                    return data;

                // 业务码 400「deviceToken 已过期」同样是 HTTP 200，原先在解析处直接失败，不会进 401 分支。
                if (!didAuthRetry && deviceTokenExpired)
                {
                    didAuthRetry = true;
                    if (await RefreshExpiredDeviceTokenAsync("业务码 deviceToken 已过期"))
                        continue;
                    Fail(string.IsNullOrEmpty(failReason)
                        ? "升级失败：deviceToken 已过期，刷新失败"
                        : failReason);
                    return null;
                }

                Fail(failReason);
                return null;
            }

            if (!transportOk)
                Fail(DescribeTransport(lastHttpCode, lastTransportError));

            return null;
        }

        /// <summary>终态失败：打日志，并在加载罩上方弹出原因。过期票换票成功不走这里。</summary>
        static void Fail(string reason)
        {
            if (string.IsNullOrEmpty(reason))
                reason = "升级失败";
            Log.Error($"[BankUpgrade] {reason}");
            UiDialogService.ShowRequestFailure(reason);
        }

        static string DescribeTransport(long httpCode, string err)
        {
            if (Application.internetReachability == NetworkReachability.NotReachable
                || string.Equals(err, "no network", StringComparison.OrdinalIgnoreCase))
                return "升级失败：无网络";
            if (httpCode > 0)
            {
                return string.IsNullOrEmpty(err) || err.StartsWith("HTTP ", StringComparison.Ordinal)
                    ? $"升级失败：HTTP {httpCode}"
                    : $"升级失败：HTTP {httpCode} {err}";
            }

            if (!string.IsNullOrEmpty(err))
                return $"升级失败：{err}";
            return "升级失败：网络请求失败";
        }

        /// <summary>
        /// 服务端业务文案。本地过期时间可能还没到（或原生 expire=0 被写成 24h 占位），不能只看 Prefs。
        /// </summary>
        static bool IsDeviceTokenExpiredMessage(string msg)
        {
            if (string.IsNullOrEmpty(msg))
                return false;
            return msg.IndexOf("deviceToken", StringComparison.OrdinalIgnoreCase) >= 0
                && msg.IndexOf("过期", StringComparison.Ordinal) >= 0;
        }

        /// <summary>
        /// 丢掉本地 deviceToken，再走换票。Hybrid 向宿主 Query 20 强制刷新；Editor 走 boluoRsa。
        /// </summary>
        static async UniTask<bool> RefreshExpiredDeviceTokenAsync(string reason)
        {
            Log.Warning($"[BankUpgrade] {reason} → clear deviceToken and refresh");
            BoluoSession.ClearDeviceToken();
            bool ok = await BoluoDeviceLoginClient.EnsureDeviceTokenAsync(failFast: true);
            if (!ok || string.IsNullOrEmpty(BoluoSession.DeviceToken))
            {
                Log.Warning("[BankUpgrade] deviceToken refresh failed");
                return false;
            }

            Log.Info(
                $"[BankUpgrade] deviceToken refreshed len={BoluoSession.DeviceToken.Length} " +
                $"expire={BoluoSession.DeviceTokenExpireTime}");
            return true;
        }

        public static UniTask<Dictionary<string, string>> BuildSignedHeaders(string bodyJson)
        {
            SignatureLib.SignatureGeneration(bodyJson ?? EmptyBody, out string sign, out string nonce);
            return UniTask.FromResult(BuildHeaders(sign, nonce));
        }

        public static Dictionary<string, string> BuildHeaders(string sign, string nonceStr, string notCheckSign = "0")
        {
            return new Dictionary<string, string>
            {
                { "deviceToken", BoluoSession.DeviceToken ?? "" },
                { "userToken", BoluoSession.UserToken ?? "" },
                { "deviceId", BoluoDeviceInfo.GetIdentifier() },
                { "channel", BoluoDeviceInfo.GetChannel() },
                { "version", BoluoDeviceInfo.GetVersion() },
                { "healthChannel", BoluoDeviceInfo.GetHealthChannel() },
                { "thirdPartyId", BoluoDeviceInfo.GetThirdPartyId() },
                { "originEnd", BoluoDeviceInfo.GetOriginEnd() },
                { "notCheckSign", notCheckSign },
                { "sign", sign ?? "" },
                { "nonceStr", nonceStr ?? "" },
            };
        }

        static bool TryParseResponse(
            string json, out BankUpgradeData data, out bool deviceTokenExpired, out string failReason)
        {
            data = null;
            deviceTokenExpired = false;
            failReason = null;
            if (string.IsNullOrEmpty(json))
            {
                failReason = "升级失败：服务器返回空";
                return false;
            }

            if (json[0] == '\uFEFF')
                json = json.Substring(1);

            BankUpgradeResponse resp;
            try
            {
                json = NormalizeUpgradeJson(json);
                resp = JsonConvert.DeserializeObject<BankUpgradeResponse>(json);
            }
            catch (Exception e)
            {
                Log.Warning($"[BankUpgrade] JSON parse fail: {e.Message}");
                failReason = "升级失败：响应无法解析";
                return false;
            }

            if (resp == null)
            {
                failReason = "升级失败：响应为空";
                return false;
            }

            if (resp.code != 0)
            {
                deviceTokenExpired = IsDeviceTokenExpiredMessage(resp.msg);
                failReason = string.IsNullOrEmpty(resp.msg)
                    ? $"升级失败：code={resp.code}"
                    : $"升级失败：{resp.msg}";
                // 过期留给调用方清票换票；换票成功不弹。其它业务错误由调用方弹 Toast。
                if (deviceTokenExpired)
                    Log.Warning($"[BankUpgrade] business error code={resp.code} msg={resp.msg}");
                return false;
            }

            if (resp.data == null)
            {
                failReason = "升级失败：数据为空";
                return false;
            }

            int bankCount = resp.data.banks?.Count ?? 0;
            int withUrl = 0;
            var versionCounts = new Dictionary<string, int>();
            if (resp.data.banks != null)
            {
                foreach (var b in resp.data.banks)
                {
                    if (b == null) continue;
                    if (!string.IsNullOrEmpty(b.url))
                        withUrl++;
                    var v = string.IsNullOrEmpty(b.version) ? "(空)" : b.version;
                    versionCounts.TryGetValue(v, out var c);
                    versionCounts[v] = c + 1;
                }
            }
            string versionSummary = versionCounts.Count == 0
                ? "(no banks)"
                : string.Join(",", versionCounts.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}×{kv.Value}"));

            Log.Info(
                $"[BankUpgrade] ok banks={bankCount} withUrl={withUrl} " +
                $"appVersion={resp.data.appVersion} bankVersion={resp.data.bankVersion} " +
                $"runMode={resp.data.runMode} panorama={resp.data.panorama} yishu={resp.data.yishu} " +
                $"builderAccountId={resp.data.builderAccountId} " +
                $"clientVersion={BoluoDeviceInfo.GetVersion()} bankVersions=[{versionSummary}]");

            if (bankCount == 0)
            {
                Log.Warning(
                    $"[BankUpgrade] banks empty — check clientVersion={BoluoDeviceInfo.GetVersion()} " +
                    $"/ channel={BoluoDeviceInfo.GetChannel()} / originEnd={BoluoDeviceInfo.GetOriginEnd()} " +
                    $"/ builderAccountId={resp.data.builderAccountId}");
            }

            data = resp.data;
            return true;
        }

        /// <summary>
        /// 上传稿是 PascalCase；接口多数转 camelCase。数字时间戳 / Size 也可能没转成字符串。
        /// </summary>
        static string NormalizeUpgradeJson(string json)
        {
            var root = JObject.Parse(json);
            if (root["data"] is JObject data)
            {
                FoldObject(data);
                if (data["banks"] is JArray banks)
                {
                    for (int i = 0; i < banks.Count; i++)
                    {
                        if (banks[i] is JObject bank)
                            FoldObject(bank);
                    }
                }

                if (data["musicLists"] is JArray lists)
                    FoldMusicLists(lists);
            }

            return root.ToString(Formatting.None);
        }

        static void FoldMusicLists(JArray lists)
        {
            for (int i = 0; i < lists.Count; i++)
            {
                if (lists[i] is not JObject album)
                    continue;
                FoldObject(album);
                if (album["musics"] is not JArray tracks)
                    continue;
                for (int j = 0; j < tracks.Count; j++)
                {
                    if (tracks[j] is JObject track)
                        FoldObject(track);
                }
            }
        }

        static void FoldObject(JObject obj)
        {
            var keys = new List<string>();
            foreach (var p in obj.Properties())
                keys.Add(p.Name);

            for (int i = 0; i < keys.Count; i++)
            {
                string name = keys[i];
                string camel = ToCamel(name);
                var token = obj[name];
                if (!string.Equals(name, camel, StringComparison.Ordinal))
                {
                    if (obj[camel] == null)
                        obj[camel] = token;
                    obj.Remove(name);
                    token = obj[camel];
                }

                if (token != null && IsStringField(camel))
                    obj[camel] = Stringify(token);
            }
        }

        static bool IsStringField(string camel)
        {
            switch (camel)
            {
                case "appVersion":
                case "bankVersion":
                case "projectName":
                case "projectGuid":
                case "projectDirectory":
                case "nativeVersion":
                case "studioVersion":
                case "runMode":
                case "lastModifiedTimestamp":
                case "buildTimestamp":
                case "buildGuid":
                case "builderAccountId":
                case "guid":
                case "bankName":
                case "version":
                case "author":
                case "url":
                case "md5":
                case "size":
                case "musicsName":
                case "bankGuid":
                case "eventName":
                case "musicName":
                    return true;
                default:
                    return false;
            }
        }

        static JToken Stringify(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null)
                return "";
            if (token.Type == JTokenType.String)
                return token;
            if (token.Type == JTokenType.Integer || token.Type == JTokenType.Float)
            {
                if (token.Type == JTokenType.Float)
                    return ((int)token.Value<double>()).ToString();
                return token.ToString();
            }

            return token.ToString();
        }

        static string ToCamel(string name)
        {
            if (string.IsNullOrEmpty(name) || char.IsLower(name[0]))
                return name;
            if (name.Length == 1)
                return char.ToLowerInvariant(name[0]).ToString();
            return char.ToLowerInvariant(name[0]) + name.Substring(1);
        }
    }
}
