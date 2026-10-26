using System;
using Game.BoluoApi;
using Game.Native;
using UnityGameFramework.Runtime;

namespace Game.Native
{
    /// <summary>Hybrid：从原生 SSOT 读/刷新 token。</summary>
    public static class NativeSession
    {
        public static bool IsHybridAvailable() => NativeBridgeTransport.IsHybridAvailable();

        public static bool EnsureDeviceToken(bool forceRefresh = false)
        {
            BoluoSession.LoadFromPrefs();
            if (!forceRefresh && BoluoSession.IsDeviceTokenValid())
                return true;

            if (!IsHybridAvailable())
                return false;

            int flags = 0;
            if (forceRefresh || !BoluoSession.IsDeviceTokenValid())
                flags |= MsgType.FlagForceRefresh;

            int code = NativeBridgeTransport.Query(
                MsgType.SessionGetToken, MsgType.TokenKindDevice, flags, 0, null, out string blob);
            if (code != BridgeCode.Ok)
            {
                Log.Warning($"[NativeSession] EnsureDeviceToken code={code}");
                return false;
            }

            if (!TryParseExpireToken(blob, out uint expire, out string token))
            {
                Log.Warning("[NativeSession] EnsureDeviceToken bad blob");
                return false;
            }

            if (string.IsNullOrEmpty(token))
            {
                Log.Warning("[NativeSession] EnsureDeviceToken empty token");
                return false;
            }

            // expire=0（未知）时写 24h 占位，避免 IsDeviceTokenValid 立刻失败
            if (expire == 0)
                expire = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 86400u;

            BoluoSession.SetDeviceToken(token, expire);
            Log.Info(
                $"[NativeSession] deviceToken len={token.Length} expire={expire} force={forceRefresh}");
            return !string.IsNullOrEmpty(BoluoSession.DeviceToken);
        }

        public static bool TryGetUserToken(out string token)
        {
            return TryGetUserSession(out token, out _, out _);
        }

        /// <summary>
        /// Hybrid 用户票。blob 新格式 <c>expire\ttoken\tisGuest\tuserId</c>；
        /// 旧格式 <c>token</c> / <c>expire\ttoken</c> → isGuest=true、userId 空（不写进度盘）。
        /// </summary>
        public static bool TryGetUserSession(out string token, out bool isGuest, out string userId)
        {
            token = "";
            isGuest = true;
            userId = "";
            int code = NativeBridgeTransport.Query(
                MsgType.SessionGetToken, MsgType.TokenKindUser, 0, 0, null, out string blob);
            if (code != BridgeCode.Ok)
                return false;
            if (!TryParseUserSessionBlob(blob, out _, out token, out isGuest, out userId))
                return false;
            return !string.IsNullOrEmpty(token);
        }

        public static bool TryGetHostChannel(out string channel)
        {
            channel = NativeBridgeTransport.GetHostChannel();
            return !string.IsNullOrEmpty(channel);
        }

        public static bool TryGetHostOriginEnd(out string originEnd)
        {
            originEnd = NativeBridgeTransport.GetHostOriginEnd();
            return !string.IsNullOrEmpty(originEnd);
        }

        public static bool TryGetHostAppId(out int appId)
        {
            appId = NativeBridgeTransport.GetHostAppId();
            return appId > 0;
        }

        public static bool TryGetHostApiEnvironmentType(out string env)
        {
            env = NativeBridgeTransport.GetHostApiEnvironmentType();
            return !string.IsNullOrEmpty(env);
        }

        public static bool TryGetHostDeviceId(out string deviceId)
        {
            deviceId = NativeBridgeTransport.GetHostDeviceId();
            return !string.IsNullOrEmpty(deviceId);
        }

        static bool _loggedSignKitMissing;

        public static void SetSignEnvironment(int env)
        {
#if UNITY_IOS && !UNITY_EDITOR
            Log.Info($"[NativeSession] iOS hybrid 忽略 SetEnv({env})，由原生签前 SetEnv");
            return;
#else
            try
            {
                NativeSignBridge.SetEnvironment(env);
                Log.Info($"[NativeSession] SetEnv ok current={NativeSignBridge.GetEnvironment()}");
            }
            catch (DllNotFoundException e)
            {
                LogSignKitMissingOnce(e.Message);
            }
            catch (Exception e)
            {
                Log.Error($"[NativeSession] SetEnv failed: {e.Message}");
            }
#endif
        }

        public static string GenerateSign(string jsonBody, string fixedNonceStr = null)
        {
            string result = NativeSignBridge.Generate(jsonBody, fixedNonceStr);
            if (string.IsNullOrEmpty(result))
                throw new Exception("GenerateSign returned empty");
            return result;
        }

        static void LogSignKitMissingOnce(string detail)
        {
            if (_loggedSignKitMissing)
                return;
            _loggedSignKitMissing = true;
            Log.Warning(
                "[NativeSession] libSignKit missing — 独立 APK 需 Plugins/libSignKit/Android/*.so。" +
                $" detail={detail}");
        }

        public static bool TryParseExpireToken(string blob, out uint expire, out string token)
        {
            return TryParseUserSessionBlob(blob, out expire, out token, out _, out _);
        }

        /// <summary>
        /// 解析 user token blob。
        /// 四段：<c>expire\ttoken\tisGuest\tuserId</c>（isGuest 为 0/1）；
        /// 一段/两段旧格式：仅 token（或 expire+token），isGuest=true。
        /// </summary>
        public static bool TryParseUserSessionBlob(
            string blob, out uint expire, out string token, out bool isGuest, out string userId)
        {
            expire = 0;
            token = "";
            isGuest = true;
            userId = "";
            if (string.IsNullOrEmpty(blob))
                return false;

            string[] parts = blob.Split('\t');
            if (parts.Length >= 4)
            {
                uint.TryParse(parts[0], out expire);
                token = parts[1] ?? "";
                isGuest = parts[2] != "0";
                userId = parts[3] ?? "";
                return !string.IsNullOrEmpty(token);
            }

            if (parts.Length == 1)
            {
                token = parts[0];
                return !string.IsNullOrEmpty(token);
            }

            if (parts.Length == 2)
            {
                // expire\ttoken 或误传 token\tuserId：首段可解析为 uint 则当 expire
                if (uint.TryParse(parts[0], out expire))
                    token = parts[1] ?? "";
                else
                {
                    token = parts[0] ?? "";
                    userId = parts[1] ?? "";
                }

                return !string.IsNullOrEmpty(token);
            }

            // 三段：expire\ttoken\tisGuest（无 userId）
            if (parts.Length == 3)
            {
                uint.TryParse(parts[0], out expire);
                token = parts[1] ?? "";
                isGuest = parts[2] != "0";
                return !string.IsNullOrEmpty(token);
            }

            return false;
        }
    }
}
