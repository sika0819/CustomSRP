using System;
using UnityEngine;

namespace Game
{
    /// <summary>
    /// 游客/用户会话（BoluoUnity BoluoSession 子集）。编辑器游客登录进岛写入此处。
    /// </summary>
    public static class GuestSession
    {
        const string UserTokenKey = "Game.UserToken";
        const string UserIdKey = "Game.UserId";
        const string IsGuestKey = "Game.IsGuest";

        public static string UserToken { get; private set; } = "";
        public static string UserId { get; private set; } = "";
        public static bool IsGuest { get; private set; }
        public static bool IsLoggedIn => !string.IsNullOrEmpty(UserToken);

        public static void LoadFromPrefs()
        {
            UserToken = PlayerPrefs.GetString(UserTokenKey, "") ?? "";
            UserId = PlayerPrefs.GetString(UserIdKey, "") ?? "";
            IsGuest = PlayerPrefs.GetInt(IsGuestKey, 0) == 1;
        }

        public static void ApplyUserSession(string token, string userId, bool isGuest)
        {
            UserToken = token ?? "";
            UserId = userId ?? "";
            IsGuest = isGuest;
            PlayerPrefs.SetString(UserTokenKey, UserToken);
            PlayerPrefs.SetString(UserIdKey, UserId);
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
            UserToken = "";
            UserId = "";
            IsGuest = false;
            PlayerPrefs.DeleteKey(UserTokenKey);
            PlayerPrefs.DeleteKey(UserIdKey);
            PlayerPrefs.DeleteKey(IsGuestKey);
        }
    }
}
