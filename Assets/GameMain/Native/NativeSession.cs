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
            token = "";
            int code = NativeBridgeTransport.Query(
                MsgType.SessionGetToken, MsgType.TokenKindUser, 0, 0, null, out string blob);
            if (code != BridgeCode.Ok)
                return false;
            if (!TryParseExpireToken(blob, out _, out token))
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

        public static bool TryParseExpireToken(string blob, out uint expire, out string token)
        {
            expire = 0;
            token = "";
            if (string.IsNullOrEmpty(blob))
                return false;

            int tab = blob.IndexOf('\t');
            if (tab < 0)
            {
                token = blob;
                return true;
            }

            string expStr = blob.Substring(0, tab);
            token = tab + 1 < blob.Length ? blob.Substring(tab + 1) : "";
            uint.TryParse(expStr, out expire);
            return !string.IsNullOrEmpty(token);
        }
    }
}
