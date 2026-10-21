using System;
using Game.BoluoApi;
using UnityEngine;

namespace Game
{
    /// <summary>
    /// 游客/用户会话薄包装；SSOT 为 <see cref="BoluoSession"/>。
    /// </summary>
    public static class GuestSession
    {
        const string IsGuestKey = "Boluo.IsGuest";

        public static string UserToken
        {
            get
            {
                BoluoSession.LoadFromPrefs();
                return BoluoSession.UserToken;
            }
        }

        public static string UserId
        {
            get
            {
                BoluoSession.LoadFromPrefs();
                return BoluoSession.UserId;
            }
        }

        public static bool IsGuest
        {
            get
            {
                BoluoSession.LoadFromPrefs();
                return BoluoSession.IsGuest || PlayerPrefs.GetInt(IsGuestKey, 0) == 1;
            }
        }

        public static bool IsLoggedIn => !string.IsNullOrEmpty(UserToken);

        public static void LoadFromPrefs()
        {
            BoluoSession.LoadFromPrefs();
        }

        public static void ApplyUserSession(string token, string userId, bool isGuest)
        {
            BoluoSession.ApplyUserSession(
                new UserSession { token = token ?? "", userId = userId ?? "" },
                isGuest);
            PlayerPrefs.SetInt(IsGuestKey, isGuest ? 1 : 0);
            PlayerPrefs.Save();
        }

        public static void ApplyLocalEditorGuest()
        {
            string device = SystemInfo.deviceUniqueIdentifier;
            if (string.IsNullOrEmpty(device))
                device = "editor";
            string id = "guest_" + Mathf.Abs(device.GetHashCode()).ToString("x8");
            string token = "editor_guest_" + id + "_" + DateTime.UtcNow.Ticks.ToString("x");
            ApplyUserSession(token, id, isGuest: true);
        }

        public static void Clear()
        {
            BoluoSession.ClearUserSession();
            PlayerPrefs.DeleteKey(IsGuestKey);
            // Legacy keys from pre-port GuestSession
            PlayerPrefs.DeleteKey("Game.UserToken");
            PlayerPrefs.DeleteKey("Game.UserId");
            PlayerPrefs.DeleteKey("Game.IsGuest");
            // ClearUserSession 已 Save；再清旧键后强制落盘一次
            PlayerPrefs.Save();
        }

        public static void SyncFromBoluo()
        {
            BoluoSession.LoadFromPrefs();
            if (!string.IsNullOrEmpty(BoluoSession.UserToken))
            {
                PlayerPrefs.SetInt(IsGuestKey, BoluoSession.IsGuest ? 1 : 0);
                PlayerPrefs.Save();
            }
        }
    }
}
