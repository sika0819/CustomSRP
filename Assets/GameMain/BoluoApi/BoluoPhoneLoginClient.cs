using System;
using System.Collections.Generic;
using System.Text;
using Cysharp.Threading.Tasks;
using Game.Utility;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine.Networking;
using UnityGameFramework.Runtime;

namespace Game.BoluoApi
{
    /// <summary>
    /// 公共域手机号登录（验证码 / 密码），对齐 Android <c>LoginApiClient</c>。
    /// Editor 联调进正式账号用；需先有 deviceToken。
    /// 注销确认期：登录 data.isCancel=true → 弹窗；主按钮走 <see cref="LoginAndStopCancelAsync"/>。
    /// </summary>
    public static class BoluoPhoneLoginClient
    {
        const string VerifyCodePath = "/api/commonUser/getVerifyCode";
        const string MobileLoginPath = "/api/commonUser/mobileLogin";
        const string PasswordLoginPath = "/api/commonUser/passwordLogin";
        const string LoginAndStopCancelPath = "/api/commonUser/loginAndStopCancel";
        const string DefaultNation = "+86";

        static readonly Http.RetryPolicy Retry = Http.RetryPolicy.DefaultApi;

        public readonly struct LoginResult
        {
            public readonly bool Ok;
            public readonly string Message;
            public readonly string UserId;
            public readonly int Code;
            /// <summary>注销确认期：需弹注销提醒，暂不进岛。</summary>
            public readonly bool IsCancel;
            public readonly string CancelMsg;

            public LoginResult(
                bool ok,
                string message,
                string userId = "",
                int code = 0,
                bool isCancel = false,
                string cancelMsg = "")
            {
                Ok = ok;
                Message = message ?? "";
                UserId = userId ?? "";
                Code = code;
                IsCancel = isCancel;
                CancelMsg = cancelMsg ?? "";
            }

            public static LoginResult Fail(string message, int code = -1) =>
                new LoginResult(false, message, "", code);

            public static LoginResult Success(
                string userId,
                bool isCancel = false,
                string cancelMsg = "") =>
                new LoginResult(true, "ok", userId, 0, isCancel, cancelMsg);
        }

        /// <summary>GET getVerifyCode；签名体为 mobile/nation JSON（与旧 Unity 一致）。</summary>
        public static async UniTask<LoginResult> RequestVerifyCodeAsync(
            string mobile,
            string nation = DefaultNation)
        {
            mobile = DigitsOnly(mobile);
            if (!IsCnMobile(mobile))
                return LoginResult.Fail("手机号格式不正确");

            nation = string.IsNullOrEmpty(nation) ? DefaultNation : nation;
            if (!await EnsureDeviceTokenReadyAsync())
                return LoginResult.Fail("deviceToken 不可用，请先换票");

            string signJson = JsonConvert.SerializeObject(new VerifyCodeBody
            {
                mobile = mobile,
                nation = nation,
            });
            string url =
                BoluoApiEnvironment.ResolveUserApiBase()
                + VerifyCodePath
                + "?mobile=" + UnityWebRequest.EscapeURL(mobile)
                + "&nation=" + UnityWebRequest.EscapeURL(nation);

            var (transportOk, responseText) = await GetSignedAsync(url, signJson);
            if (!transportOk)
                return LoginResult.Fail($"getVerifyCode 网络失败（{url}）");

            return ParseCodeOnly(responseText, "getVerifyCode");
        }

        /// <summary>POST mobileLogin → 正式账号会话（isGuest=false）。</summary>
        public static async UniTask<LoginResult> MobileLoginAsync(
            string mobile,
            string verifyCode,
            string nation = DefaultNation)
        {
            mobile = DigitsOnly(mobile);
            verifyCode = (verifyCode ?? "").Trim();
            if (!IsCnMobile(mobile))
                return LoginResult.Fail("手机号格式不正确");
            if (verifyCode.Length < 4)
                return LoginResult.Fail("请输入验证码");

            nation = string.IsNullOrEmpty(nation) ? DefaultNation : nation;
            if (!await EnsureDeviceTokenReadyAsync())
                return LoginResult.Fail("deviceToken 不可用，请先换票");

            var body = new MobileLoginBody
            {
                mobile = mobile,
                verifyCode = verifyCode,
                nation = nation,
                latitude = BoluoDeviceInfo.GetLatitude(),
                longitude = BoluoDeviceInfo.GetLongitude(),
                accuracy = BoluoDeviceInfo.GetAccuracy(),
            };
            return await PostLoginAsync(MobileLoginPath, body, "mobileLogin");
        }

        /// <summary>POST passwordLogin → 正式账号会话（isGuest=false）。</summary>
        public static async UniTask<LoginResult> PasswordLoginAsync(
            string mobile,
            string password,
            string nation = DefaultNation)
        {
            mobile = DigitsOnly(mobile);
            password = password ?? "";
            if (!IsCnMobile(mobile))
                return LoginResult.Fail("手机号格式不正确");
            if (password.Length < 6)
                return LoginResult.Fail("请输入密码");

            if (AccountPasswordLoginThrottle.IsLocked(mobile))
                return LoginResult.Fail(AccountPasswordLoginThrottle.LockedTip);

            nation = string.IsNullOrEmpty(nation) ? DefaultNation : nation;
            if (!await EnsureDeviceTokenReadyAsync())
                return LoginResult.Fail("deviceToken 不可用，请先换票");

            var body = new PasswordLoginBody
            {
                mobile = mobile,
                password = password,
                nation = nation,
                latitude = BoluoDeviceInfo.GetLatitude(),
                longitude = BoluoDeviceInfo.GetLongitude(),
                accuracy = BoluoDeviceInfo.GetAccuracy(),
            };
            var result = await PostLoginAsync(PasswordLoginPath, body, "passwordLogin");
            if (!result.Ok && IsPasswordServerFailure(result.Code))
            {
                if (AccountPasswordLoginThrottle.RecordServerFailure(mobile, out _))
                    return LoginResult.Fail(AccountPasswordLoginThrottle.LockedTip, result.Code);
            }

            return result;
        }

        /// <summary>
        /// POST loginAndStopCancel（注销提醒主按钮）。需已有正式 userToken。
        /// data 兼容：①完整 LoginDetail；②<code>{"result":true}</code> 复用本次登录会话。
        /// </summary>
        public static async UniTask<LoginResult> LoginAndStopCancelAsync()
        {
            BoluoSession.LoadFromPrefs();
            if (string.IsNullOrEmpty(BoluoSession.UserToken))
                return LoginResult.Fail("无 userToken，无法终止注销");
            if (!await EnsureDeviceTokenReadyAsync())
                return LoginResult.Fail("deviceToken 不可用，请先换票");

            var body = new StopCancelBody
            {
                latitude = BoluoDeviceInfo.GetLatitude(),
                longitude = BoluoDeviceInfo.GetLongitude(),
                accuracy = BoluoDeviceInfo.GetAccuracy(),
            };
            string json = JsonConvert.SerializeObject(body);
            string url = BoluoApiEnvironment.ResolveUserApiBase() + LoginAndStopCancelPath;
            var (transportOk, responseText) = await PostJsonAsync(
                url, json, forceEmptyUserToken: false);
            if (!transportOk)
                return LoginResult.Fail($"loginAndStopCancel 网络失败（{url}）");
            if (string.IsNullOrEmpty(responseText))
                return LoginResult.Fail("loginAndStopCancel 空响应");

            StopCancelResponse resp;
            try
            {
                resp = JsonConvert.DeserializeObject<StopCancelResponse>(responseText);
            }
            catch (Exception e)
            {
                return LoginResult.Fail($"loginAndStopCancel 解析失败: {e.Message}");
            }

            if (resp == null)
                return LoginResult.Fail("loginAndStopCancel 解析失败");
            if (resp.code != 0)
                return LoginResult.Fail(
                    $"loginAndStopCancel code={resp.code} msg={resp.msg}", resp.code);
            if (resp.data == null)
                return LoginResult.Fail("终止注销响应异常，请重试", resp.code);

            string token = ResolveToken(resp.data);
            string userId = resp.data.userId ?? "";
            if (!string.IsNullOrEmpty(token))
            {
                GuestSession.ApplyUserSession(token, userId, isGuest: false);
                AccountPasswordLoginThrottle.ClearAll();
                Log.Info(
                    $"[PhoneLogin] loginAndStopCancel ok (new token) userId={userId} " +
                    $"tokenLen={token.Length}");
                return LoginResult.Success(userId);
            }

            if (!IsTruthyResult(resp.data.result))
            {
                if (resp.data.result != null)
                    return LoginResult.Fail("操作失败，请稍后重试", resp.code);
                return LoginResult.Fail("终止注销响应异常，请重试", resp.code);
            }

            // result=true：沿用本次登录已落盘会话
            GuestSession.LoadFromPrefs();
            if (!GuestSession.IsLoggedIn || GuestSession.IsGuest)
                return LoginResult.Fail("终止注销响应异常，请重试", resp.code);

            Log.Info(
                $"[PhoneLogin] loginAndStopCancel ok (reuse session) userId={GuestSession.UserId}");
            return LoginResult.Success(GuestSession.UserId);
        }

        /// <summary>对齐原生 LoginCancelFlowPolicy.resolveBodyText。</summary>
        public static string ResolveCancelBodyText(string cancelMsg)
        {
            string trimmed = (cancelMsg ?? "").Trim();
            if (trimmed.Length > 0)
                return trimmed;
            return "当前账号申请了注销，目前处于确认期。如果你继续登录，注销流程将自动终止。";
        }

        static async UniTask<LoginResult> PostLoginAsync(string path, object body, string tag)
        {
            string json = JsonConvert.SerializeObject(body);
            string url = BoluoApiEnvironment.ResolveUserApiBase() + path;
            var (transportOk, responseText) = await PostJsonAsync(
                url, json, forceEmptyUserToken: true);
            if (!transportOk)
                return LoginResult.Fail($"{tag} 网络失败（{url}）");
            if (string.IsNullOrEmpty(responseText))
                return LoginResult.Fail($"{tag} 空响应");

            UserLoginResponse resp;
            try
            {
                resp = JsonConvert.DeserializeObject<UserLoginResponse>(responseText);
            }
            catch (Exception e)
            {
                return LoginResult.Fail($"{tag} 解析失败: {e.Message}");
            }

            if (resp == null)
                return LoginResult.Fail($"{tag} 解析失败");
            if (resp.code != 0 || resp.data == null)
                return LoginResult.Fail(
                    $"{tag} code={resp.code} msg={resp.msg}", resp.code);

            string token = ResolveToken(resp.data);
            string userId = resp.data.userId ?? "";
            if (string.IsNullOrEmpty(token))
                return LoginResult.Fail($"{tag} 无 token", resp.code);

            bool isCancel = ParseIsCancel(resp.data.isCancel);
            string cancelMsg = resp.data.cancelMsg ?? "";

            GuestSession.ApplyUserSession(token, userId, isGuest: false);
            AccountPasswordLoginThrottle.ClearAll();
            Log.Info(
                $"[PhoneLogin] {tag} ok userId={userId} tokenLen={token.Length} " +
                $"isCancel={isCancel}");
            return LoginResult.Success(userId, isCancel, cancelMsg);
        }

        static LoginResult ParseCodeOnly(string responseText, string tag)
        {
            if (string.IsNullOrEmpty(responseText))
                return LoginResult.Fail($"{tag} 空响应");

            CodeOnlyResponse resp;
            try
            {
                resp = JsonConvert.DeserializeObject<CodeOnlyResponse>(responseText);
            }
            catch (Exception e)
            {
                return LoginResult.Fail($"{tag} 解析失败: {e.Message}");
            }

            if (resp == null)
                return LoginResult.Fail($"{tag} 解析失败");
            if (resp.code != 0)
                return LoginResult.Fail($"{tag} code={resp.code} msg={resp.msg}", resp.code);

            Log.Info($"[PhoneLogin] {tag} ok");
            return LoginResult.Success("");
        }

        static async UniTask<bool> EnsureDeviceTokenReadyAsync()
        {
            SignatureLib.Init();
            BoluoSession.LoadFromPrefs();
            if (BoluoSession.IsDeviceTokenValid())
                return true;
            return await BoluoDeviceLoginClient.EnsureDeviceTokenAsync(failFast: true);
        }

        static async UniTask<(bool ok, string text)> PostJsonAsync(
            string url,
            string bodyJson,
            bool forceEmptyUserToken = false)
        {
            SignatureLib.Init();
            BoluoSession.LoadFromPrefs();

            byte[] body = Encoding.UTF8.GetBytes(bodyJson ?? "{}");
            string responseText = null;
            bool transportOk = false;

            await Http.SendWithRetry(
                () =>
                {
                    SignatureLib.SignatureGeneration(bodyJson ?? "{}", out string sign, out string nonce);
                    var headers = BuildHeaders(sign, nonce, forceEmptyUserToken);
                    var req = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST)
                    {
                        uploadHandler = new UploadHandlerRaw(body),
                        downloadHandler = new DownloadHandlerBuffer(),
                        timeout = Retry.RequestTimeoutSeconds,
                    };
                    req.SetRequestHeader("Content-Type", "application/json");
                    foreach (var kv in headers)
                        req.SetRequestHeader(kv.Key, kv.Value ?? "");
                    return req;
                },
                Retry,
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
                            $"[PhoneLogin] HTTP fail url={url} result={req.result} " +
                            $"code={req.responseCode} err={req.error}");
                    }

                    req?.Dispose();
                },
                req => req != null && req.result == UnityWebRequest.Result.Success,
                (attempt, delay, err) =>
                    Log.Warning($"[PhoneLogin] retry attempt={attempt} wait={delay:0.#}s err={err}"))
                .ToUniTask();

            return (transportOk, responseText);
        }

        static async UniTask<(bool ok, string text)> GetSignedAsync(string url, string signJson)
        {
            SignatureLib.Init();
            BoluoSession.LoadFromPrefs();

            string responseText = null;
            bool transportOk = false;

            await Http.SendWithRetry(
                () =>
                {
                    SignatureLib.SignatureGeneration(signJson ?? "{}", out string sign, out string nonce);
                    var headers = BuildHeaders(sign, nonce, forceEmptyUserToken: true);
                    var req = UnityWebRequest.Get(url);
                    req.timeout = Retry.RequestTimeoutSeconds;
                    foreach (var kv in headers)
                        req.SetRequestHeader(kv.Key, kv.Value ?? "");
                    return req;
                },
                Retry,
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
                            $"[PhoneLogin] GET fail url={url} result={req.result} " +
                            $"code={req.responseCode} err={req.error}");
                    }

                    req?.Dispose();
                },
                req => req != null && req.result == UnityWebRequest.Result.Success,
                (attempt, delay, err) =>
                    Log.Warning($"[PhoneLogin] GET retry attempt={attempt} wait={delay:0.#}s err={err}"))
                .ToUniTask();

            return (transportOk, responseText);
        }

        static Dictionary<string, string> BuildHeaders(
            string sign,
            string nonceStr,
            bool forceEmptyUserToken)
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

        static string ResolveToken(UserLoginData data)
        {
            if (data == null)
                return "";
            if (!string.IsNullOrEmpty(data.token))
                return data.token;
            return data.userToken ?? "";
        }

        static string ResolveToken(StopCancelData data)
        {
            if (data == null)
                return "";
            if (!string.IsNullOrEmpty(data.token))
                return data.token;
            return data.userToken ?? "";
        }

        static bool ParseIsCancel(JToken raw)
        {
            if (raw == null || raw.Type == JTokenType.Null)
                return false;
            switch (raw.Type)
            {
                case JTokenType.Boolean:
                    return raw.Value<bool>();
                case JTokenType.Integer:
                case JTokenType.Float:
                    return raw.Value<int>() != 0;
                case JTokenType.String:
                {
                    string s = (raw.Value<string>() ?? "").Trim();
                    return s == "1"
                        || string.Equals(s, "true", StringComparison.OrdinalIgnoreCase);
                }
                default:
                    return false;
            }
        }

        static bool IsTruthyResult(JToken raw)
        {
            if (raw == null || raw.Type == JTokenType.Null)
                return false;
            switch (raw.Type)
            {
                case JTokenType.Boolean:
                    return raw.Value<bool>();
                case JTokenType.Integer:
                case JTokenType.Float:
                    return raw.Value<int>() != 0;
                case JTokenType.String:
                {
                    string s = (raw.Value<string>() ?? "").Trim();
                    return string.Equals(s, "true", StringComparison.OrdinalIgnoreCase)
                        || s == "1"
                        || string.Equals(s, "ok", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(s, "success", StringComparison.OrdinalIgnoreCase);
                }
                default:
                    return false;
            }
        }

        /// <summary>密码错误类业务码（对齐原生节流：仅服务端密码错累计）。</summary>
        static bool IsPasswordServerFailure(int code) =>
            code == 20004 || code == 20005 || code == 40003;

        public static string DigitsOnly(string raw)
        {
            if (string.IsNullOrEmpty(raw))
                return "";
            var sb = new StringBuilder(raw.Length);
            for (int i = 0; i < raw.Length; i++)
            {
                char c = raw[i];
                if (c >= '0' && c <= '9')
                    sb.Append(c);
            }

            return sb.ToString();
        }

        public static bool IsCnMobile(string digits) =>
            !string.IsNullOrEmpty(digits) && digits.Length == 11 && digits[0] == '1';

        [Serializable]
        class VerifyCodeBody
        {
            public string mobile;
            public string nation;
        }

        [Serializable]
        class MobileLoginBody
        {
            public string mobile;
            public string verifyCode;
            public string nation;
            public float latitude;
            public float longitude;
            public float accuracy;
        }

        [Serializable]
        class PasswordLoginBody
        {
            public string mobile;
            public string password;
            public string nation;
            public float latitude;
            public float longitude;
            public float accuracy;
        }

        [Serializable]
        class StopCancelBody
        {
            public float latitude;
            public float longitude;
            public float accuracy;
        }

        [Serializable]
        class CodeOnlyResponse
        {
            public int code;
            public string msg;
        }

        // Newtonsoft 反序列化。JToken 不能进 Unity 序列化，不加 [Serializable]，避免 UAC1001。
        class UserLoginResponse
        {
            public int code;
            public string msg;
            public UserLoginData data;
        }

        class UserLoginData
        {
            public string token;
            public string userToken;
            public string userId;
            public JToken isCancel;
            public string cancelMsg;
        }

        class StopCancelResponse
        {
            public int code;
            public string msg;
            public StopCancelData data;
        }

        class StopCancelData
        {
            public string token;
            public string userToken;
            public string userId;
            public JToken result;
        }
    }
}
