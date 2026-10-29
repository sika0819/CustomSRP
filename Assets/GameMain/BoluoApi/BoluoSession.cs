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

        public static string DeviceToken { get; private set; } = "";
        public static uint DeviceTokenExpireTime { get; private set; }
        public static string DeviceTokenVersion { get; private set; } = "";
        public static string UserToken { get; private set; } = "";
        public static string UserId { get; private set; } = "";
        public static bool IsGuest { get; private set; }
        /// <summary>这场会话要不要把岛内进度写入 PlayerPrefs。游客恒为 false。</summary>
        public static bool SaveLocal { get; private set; }

        /// <summary>
        /// 游客票只留本进程内存。LoadFromPrefs 若回读磁盘会把内存票盖成空，进岛中途掉线。
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
        /// 正式账号写入 userToken。游客没有持久化数据：只更新内存，并清掉磁盘上的用户票，
        /// 避免杀进程后把游客号恢复进来，也避免盖掉时留下上一张正式票。
        /// </summary>
        public static void ApplyUserSession(UserSession session, bool isGuest, bool saveLocal)
        {
            if (session == null)
                return;
            UserToken = session.token ?? "";
            UserId = session.userId ?? "";
            IsGuest = isGuest;
            SaveLocal = saveLocal;
            // 真机游客票 isGuest 恒为 false，靠 saveLocal=0 识别。两者任一为游客语义都不落盘。
            bool persistAccount = saveLocal && !isGuest;
            _userSessionMemoryOnly = !persistAccount;
            if (!persistAccount)
            {
                DeleteUserSessionKeys();
                PlayerPrefs.Save();
                return;
            }

            PlayerPrefs.SetString(UserTokenKey, UserToken);
            PlayerPrefs.SetString(UserIdKey, UserId);
            PlayerPrefs.SetInt(IsGuestKey, 0);
            PlayerPrefs.SetInt(SaveLocalKey, 1);
            PlayerPrefs.Save();
        }

        public static void ClearUserSession()
        {
            _userSessionMemoryOnly = false;
            UserToken = "";
            UserId = "";
            IsGuest = false;
            SaveLocal = false;
            DeleteUserSessionKeys();
            // 必须立即落盘，防止杀进程后仍以旧 token 自动登录
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
