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

        public static string DeviceToken { get; private set; } = "";
        public static uint DeviceTokenExpireTime { get; private set; }
        public static string DeviceTokenVersion { get; private set; } = "";
        public static string UserToken { get; private set; } = "";
        public static string UserId { get; private set; } = "";
        public static bool IsGuest { get; private set; }

        public static void LoadFromPrefs()
        {
            DeviceToken = PlayerPrefs.GetString(DeviceTokenKey, "") ?? "";
            DeviceTokenVersion = PlayerPrefs.GetString(DeviceTokenVersionKey, "") ?? "";
            string expireRaw = PlayerPrefs.GetString(DeviceTokenExpireKey, "0");
            DeviceTokenExpireTime = uint.TryParse(expireRaw, out var ts) ? ts : 0u;
            UserToken = PlayerPrefs.GetString(UserTokenKey, "") ?? "";
            UserId = PlayerPrefs.GetString(UserIdKey, "") ?? "";
            IsGuest = PlayerPrefs.GetInt(IsGuestKey, 0) == 1;
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
        /// 登录/游客进岛后写入 userToken（对齐 BoluoProject ApplyUserSession）。
        /// </summary>
        public static void ApplyUserSession(UserSession session, bool isGuest)
        {
            if (session == null)
                return;
            UserToken = session.token ?? "";
            UserId = session.userId ?? "";
            IsGuest = isGuest;
            PlayerPrefs.SetString(UserTokenKey, UserToken);
            PlayerPrefs.SetString(UserIdKey, UserId);
            PlayerPrefs.SetInt(IsGuestKey, isGuest ? 1 : 0);
            PlayerPrefs.Save();
        }

        public static void ClearUserSession()
        {
            UserToken = "";
            UserId = "";
            IsGuest = false;
            PlayerPrefs.DeleteKey(UserTokenKey);
            PlayerPrefs.DeleteKey(UserIdKey);
            PlayerPrefs.DeleteKey(IsGuestKey);
        }

        public static void ClearDeviceToken()
        {
            DeviceToken = "";
            DeviceTokenExpireTime = 0;
            DeviceTokenVersion = "";
            PlayerPrefs.DeleteKey(DeviceTokenKey);
            PlayerPrefs.DeleteKey(DeviceTokenExpireKey);
            PlayerPrefs.DeleteKey(DeviceTokenVersionKey);
        }

        static uint GetUnixTimeSeconds()
        {
            return (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }
    }
}
