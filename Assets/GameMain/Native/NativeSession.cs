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
            {
                IslandEntryLog.Skip(
                    "deviceTicket",
                    "reuse",
                    "len=" + BoluoSession.DeviceToken.Length);
                return true;
            }

            if (!IsHybridAvailable())
            {
                IslandEntryLog.Fail("deviceTicket", "hybridOff");
                return false;
            }

            int flags = 0;
            if (forceRefresh || !BoluoSession.IsDeviceTokenValid())
                flags |= MsgType.FlagForceRefresh;

            int code = NativeBridgeTransport.Query(
                MsgType.SessionGetToken, MsgType.TokenKindDevice, flags, 0, null, out string blob);
            if (code != BridgeCode.Ok)
            {
                Log.Warning($"[NativeSession] EnsureDeviceToken code={code}");
                IslandEntryLog.Fail("deviceTicket", "query", "code=" + code);
                return false;
            }

            if (!TryParseExpireToken(blob, out uint expire, out string token))
            {
                Log.Warning("[NativeSession] EnsureDeviceToken bad blob");
                IslandEntryLog.Fail("deviceTicket", "badBlob");
                return false;
            }

            if (string.IsNullOrEmpty(token))
            {
                Log.Warning("[NativeSession] EnsureDeviceToken empty token");
                IslandEntryLog.Fail("deviceTicket", "empty");
                return false;
            }

            // expire=0（未知）时写 24h 占位，避免 IsDeviceTokenValid 立刻失败
            if (expire == 0)
                expire = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 86400u;

            BoluoSession.SetDeviceToken(token, expire);
            Log.Info(
                $"[NativeSession] deviceToken len={token.Length} expire={expire} force={forceRefresh}");
            IslandEntryLog.Ok(
                "deviceTicket",
                "ready",
                "len=" + token.Length + " expire=" + expire + " force=" + (forceRefresh ? 1 : 0));
            return !string.IsNullOrEmpty(BoluoSession.DeviceToken);
        }

        public static bool TryGetUserToken(out string token)
        {
            return TryGetUserSession(out token, out _, out _, out _);
        }

        /// <summary>
        /// Hybrid 用户票。blob：<c>expire\ttoken\tuserId\tsaveLocal</c>。
        /// 真机 <paramref name="isGuest"/> 恒为 false。缺 userId 时调用方停在罩上等待。
        /// <paramref name="saveLocal"/> 为 false 时不写岛内进度。
        /// </summary>
        public static bool TryGetUserSession(
            out string token, out bool isGuest, out string userId, out bool saveLocal)
        {
            token = "";
            isGuest = false;
            userId = "";
            saveLocal = false;
            int code = NativeBridgeTransport.Query(
                MsgType.SessionGetToken, MsgType.TokenKindUser, 0, 0, null, out string blob);
            if (code != BridgeCode.Ok)
            {
                IslandEntryLog.Fail("userTicket", "query", "code=" + code);
                return false;
            }

            if (!TryParseUserSessionBlob(blob, out _, out token, out isGuest, out userId, out saveLocal))
            {
                IslandEntryLog.Fail("userTicket", "badBlob");
                return false;
            }

            if (string.IsNullOrEmpty(token))
            {
                IslandEntryLog.Fail("userTicket", "empty");
                return false;
            }

            if (string.IsNullOrEmpty(userId))
                IslandEntryLog.Fail("userTicket", "userIdEmpty", "len=" + token.Length);
            else
                IslandEntryLog.Ok(
                    "userTicket",
                    "unity",
                    "len=" + token.Length + " userId=" + userId + " saveLocal=" + (saveLocal ? 1 : 0));
            return true;
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
            return TryParseUserSessionBlob(blob, out expire, out token, out _, out _, out _);
        }

        /// <summary>
        /// 解析 user token blob。真机 <paramref name="isGuest"/> 恒为 false。
        /// 四段新格式：<c>expire\ttoken\tuserId\tsaveLocal</c>（末段 0/1，第三段不是 0/1）。
        /// 四段旧格式：<c>expire\ttoken\tisGuest\tuserId</c>（第三段 0/1），userId 取末段，
        /// saveLocal = isGuest 为 0。
        /// 三段没有 saveLocal：userId 照读，saveLocal=false。
        /// 一段/两段只有 token 时 userId 为空。
        /// </summary>
        public static bool TryParseUserSessionBlob(
            string blob, out uint expire, out string token, out bool isGuest, out string userId,
            out bool saveLocal)
        {
            expire = 0;
            token = "";
            isGuest = false;
            userId = "";
            saveLocal = false;
            if (string.IsNullOrEmpty(blob))
                return false;

            string[] parts = blob.Split('\t');
            if (parts.Length >= 4)
            {
                uint.TryParse(parts[0], out expire);
                token = parts[1] ?? "";
                string third = parts[2] ?? "";
                string fourth = parts[3] ?? "";
                bool thirdIsFlag = third == "0" || third == "1";
                bool fourthIsFlag = fourth == "0" || fourth == "1";
                if (fourthIsFlag && !thirdIsFlag)
                {
                    userId = third;
                    saveLocal = fourth == "1";
                }
                else if (thirdIsFlag)
                {
                    userId = fourth;
                    saveLocal = third == "0";
                }
                else
                {
                    userId = third;
                }

                return !string.IsNullOrEmpty(token);
            }

            if (parts.Length == 3)
            {
                uint.TryParse(parts[0], out expire);
                token = parts[1] ?? "";
                userId = parts[2] ?? "";
                return !string.IsNullOrEmpty(token);
            }

            if (parts.Length == 1)
            {
                token = parts[0];
                return !string.IsNullOrEmpty(token);
            }

            if (parts.Length == 2)
            {
                // deviceToken：expire\ttoken。或误传 token\tuserId：首段不是 uint 则当 userId。
                if (uint.TryParse(parts[0], out expire))
                    token = parts[1] ?? "";
                else
                {
                    token = parts[0] ?? "";
                    userId = parts[1] ?? "";
                }

                return !string.IsNullOrEmpty(token);
            }

            return false;
        }
    }
}
