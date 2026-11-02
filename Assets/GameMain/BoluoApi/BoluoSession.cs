using System;
using UnityEngine;

namespace Game.BoluoApi
{
    [Serializable]
    public class UserSession
    {
        public string token;
        public string userId;
        public uint expireTime;
    }

    /// <summary>
    /// 会话票：deviceToken / userToken。须 <see cref="ApplyUserSession"/> 后
    /// upgrade/bank Header 才有 userToken。
    /// </summary>
    public static class BoluoSession
    {
        const string DeviceTokenKey = "Boluo.DeviceToken";
        const string DeviceTokenExpireKey = "Boluo.DeviceTokenExpire";
        const string DeviceTokenVersionKey = "Boluo.DeviceTokenVersion";
        const string UserTokenKey = "Boluo.UserToken";
        const string UserIdKey = "Boluo.UserId";
        const string IsGuestKey = "Boluo.IsGuest";
        const string SaveLocalKey = "Boluo.SaveLocal";

        // 游客身份缓存：退登清活跃会话后仍保留；未过期且同设备再登游客直接复用，不重打 touristLogin。
        const string GuestCacheTokenKey = "Boluo.GuestCache.Token";
        const string GuestCacheUserIdKey = "Boluo.GuestCache.UserId";
        const string GuestCacheExpireKey = "Boluo.GuestCache.Expire";
        const string GuestCacheDeviceIdKey = "Boluo.GuestCache.DeviceId";
        const uint GuestCacheDefaultTtlSeconds = 30u * 24u * 3600u;

        public static string DeviceToken { get; private set; } = "";
        public static uint DeviceTokenExpireTime { get; private set; }
        public static string DeviceTokenVersion { get; private set; } = "";
        public static string UserToken { get; private set; } = "";
        public static string UserId { get; private set; } = "";
        public static bool IsGuest { get; private set; }
        /// <summary>这场会话要不要把岛内进度写入 PlayerPrefs。游客恒为 false。</summary>
        public static bool SaveLocal { get; private set; }

        /// <summary>
        /// 游客票只留本进程内存作活跃会话。LoadFromPrefs 若回读磁盘会把内存票盖成空，进岛中途掉线。
        /// 进 Play 复位，避免关域重载关闭时把上一局的门闩带进下一局。
        /// </summary>
        static bool _userSessionMemoryOnly;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetUserSessionDiskGate()
        {
            _userSessionMemoryOnly = false;
        }

        public static void LoadFromPrefs()
        {
            DeviceToken = PlayerPrefs.GetString(DeviceTokenKey, "") ?? "";
            DeviceTokenVersion = PlayerPrefs.GetString(DeviceTokenVersionKey, "") ?? "";
            string expireRaw = PlayerPrefs.GetString(DeviceTokenExpireKey, "0");
            DeviceTokenExpireTime = uint.TryParse(expireRaw, out var ts) ? ts : 0u;
            if (_userSessionMemoryOnly)
                return;
            UserToken = PlayerPrefs.GetString(UserTokenKey, "") ?? "";
            UserId = PlayerPrefs.GetString(UserIdKey, "") ?? "";
            IsGuest = PlayerPrefs.GetInt(IsGuestKey, 0) == 1;
            SaveLocal = PlayerPrefs.GetInt(SaveLocalKey, 0) == 1;
        }

        public static bool IsDeviceTokenValid()
        {
            if (string.IsNullOrEmpty(DeviceToken))
                return false;
            if (DeviceTokenExpireTime == 0)
                return false;
            if (GetUnixTimeSeconds() >= DeviceTokenExpireTime)
                return false;
            string ver = BoluoDeviceInfo.GetVersion();
            if (string.IsNullOrEmpty(DeviceTokenVersion) || DeviceTokenVersion != ver)
                return false;
            return true;
        }

        public static void SetDeviceToken(string deviceToken)
        {
            SetDeviceToken(deviceToken, DeviceTokenExpireTime);
        }

        public static void SetDeviceToken(string deviceToken, uint expireTime)
        {
            DeviceToken = deviceToken ?? "";
            DeviceTokenExpireTime = expireTime;
            DeviceTokenVersion = BoluoDeviceInfo.GetVersion() ?? "";
            PlayerPrefs.SetString(DeviceTokenKey, DeviceToken);
            PlayerPrefs.SetString(DeviceTokenExpireKey, DeviceTokenExpireTime.ToString());
            PlayerPrefs.SetString(DeviceTokenVersionKey, DeviceTokenVersion);
            PlayerPrefs.Save();
        }

        /// <summary>
        /// 正式账号写入 userToken。游客没有持久化活跃会话：只更新内存，并清掉磁盘上的用户票，
        /// 避免杀进程后把游客号自动登进来；另写 <see cref="PersistGuestCache"/> 供下次游客复用。
        /// </summary>
        public static void ApplyUserSession(UserSession session, bool isGuest, bool saveLocal)
        {
            if (session == null)
                return;
            UserToken = session.token ?? "";
            UserId = session.userId ?? "";
            IsGuest = isGuest;
            SaveLocal = saveLocal;
            // 真机游客票 isGuest 恒为 false，靠 saveLocal=0 识别。两者任一为游客语义都不落活跃盘。
            bool persistAccount = saveLocal && !isGuest;
            _userSessionMemoryOnly = !persistAccount;
            if (!persistAccount)
            {
                DeleteUserSessionKeys();
                // 游客（或 saveLocal=0）：缓存身份票，退登后可复用。
                if (isGuest || !saveLocal)
                    PersistGuestCache(UserToken, UserId, session.expireTime);
                PlayerPrefs.Save();
                return;
            }

            PlayerPrefs.SetString(UserTokenKey, UserToken);
            PlayerPrefs.SetString(UserIdKey, UserId);
            PlayerPrefs.SetInt(IsGuestKey, 0);
            PlayerPrefs.SetInt(SaveLocalKey, 1);
            PlayerPrefs.Save();
        }

        /// <summary>
        /// 未过期且同设备的游客缓存 → 灌入内存活跃会话。成功则不必 POST touristLogin。
        /// </summary>
        public static bool TryHydrateReusableGuest(string currentDeviceId)
        {
            string token = PlayerPrefs.GetString(GuestCacheTokenKey, "") ?? "";
            if (string.IsNullOrEmpty(token))
                return false;

            string expireRaw = PlayerPrefs.GetString(GuestCacheExpireKey, "0");
            uint expire = uint.TryParse(expireRaw, out var ts) ? ts : 0u;
            uint now = GetUnixTimeSeconds();
            if (expire > 0 && now >= expire)
            {
                ClearGuestCache();
                return false;
            }

            string cachedDevice = PlayerPrefs.GetString(GuestCacheDeviceIdKey, "") ?? "";
            string device = currentDeviceId ?? "";
            if (!string.IsNullOrEmpty(cachedDevice)
                && !string.IsNullOrEmpty(device)
                && !string.Equals(cachedDevice, device, StringComparison.Ordinal))
            {
                ClearGuestCache();
                return false;
            }

            UserToken = token;
            UserId = PlayerPrefs.GetString(GuestCacheUserIdKey, "") ?? "";
            IsGuest = true;
            SaveLocal = false;
            _userSessionMemoryOnly = true;
            DeleteUserSessionKeys();
            PlayerPrefs.Save();
            return true;
        }

        public static void ClearGuestCache()
        {
            PlayerPrefs.DeleteKey(GuestCacheTokenKey);
            PlayerPrefs.DeleteKey(GuestCacheUserIdKey);
            PlayerPrefs.DeleteKey(GuestCacheExpireKey);
            PlayerPrefs.DeleteKey(GuestCacheDeviceIdKey);
            PlayerPrefs.Save();
        }

        static void PersistGuestCache(string token, string userId, uint expireTime)
        {
            if (string.IsNullOrEmpty(token))
                return;
            uint expire = expireTime;
            if (expire == 0)
                expire = GetUnixTimeSeconds() + GuestCacheDefaultTtlSeconds;
            PlayerPrefs.SetString(GuestCacheTokenKey, token);
            PlayerPrefs.SetString(GuestCacheUserIdKey, userId ?? "");
            PlayerPrefs.SetString(GuestCacheExpireKey, expire.ToString());
            PlayerPrefs.SetString(GuestCacheDeviceIdKey, BoluoDeviceInfo.GetIdentifier() ?? "");
        }

        public static void ClearUserSession()
        {
            _userSessionMemoryOnly = false;
            UserToken = "";
            UserId = "";
            IsGuest = false;
            SaveLocal = false;
            DeleteUserSessionKeys();
            // 必须立即落盘，防止杀进程后仍以旧活跃 token 自动登录。
            // 游客身份缓存（GuestCache.*）故意保留，供下次游客复用。
            PlayerPrefs.Save();
        }

        static void DeleteUserSessionKeys()
        {
            PlayerPrefs.DeleteKey(UserTokenKey);
            PlayerPrefs.DeleteKey(UserIdKey);
            PlayerPrefs.DeleteKey(IsGuestKey);
            PlayerPrefs.DeleteKey(SaveLocalKey);
        }

        public static void ClearDeviceToken()
        {
            DeviceToken = "";
            DeviceTokenExpireTime = 0;
            DeviceTokenVersion = "";
            PlayerPrefs.DeleteKey(DeviceTokenKey);
            PlayerPrefs.DeleteKey(DeviceTokenExpireKey);
            PlayerPrefs.DeleteKey(DeviceTokenVersionKey);
            PlayerPrefs.Save();
        }

        static uint GetUnixTimeSeconds()
        {
            return (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }
    }
}
