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
            BoluoSession.LoadFromPrefs();
            string prevId = BoluoSession.UserId ?? "";
            string nextId = userId ?? "";
            // userId 变化（含退登后空→新票）：只重置内存，分桶盘保留；下次 Load/InitAndArm 读新桶。
            // 勿要求 prev 非空——Leave 已 Clear，否则游客↔账号热切会跳过重置。
            if (!string.Equals(prevId, nextId, StringComparison.Ordinal)
                && !string.IsNullOrEmpty(nextId))
            {
                BoluoLogout.ResetInMemoryForAccountSwitch(
                    string.IsNullOrEmpty(prevId) ? "session apply after leave" : "userId switched");
            }

            BoluoSession.ApplyUserSession(
                new UserSession { token = token ?? "", userId = nextId },
                isGuest);
            PlayerPrefs.SetInt(IsGuestKey, isGuest ? 1 : 0);
            PlayerPrefs.Save();
        }

        public static void ApplyLocalEditorGuest()
        {
            // 已有游客票则复用，避免每次点「游客进岛」/ 兜底路径都换新 token。
            BoluoSession.LoadFromPrefs();
            if (BoluoSession.IsGuest && !string.IsNullOrEmpty(BoluoSession.UserToken)
                && !string.IsNullOrEmpty(BoluoSession.UserId))
                return;

            string device = SystemInfo.deviceUniqueIdentifier;
            if (string.IsNullOrEmpty(device))
                device = "editor";
            // 稳定 id：勿用 string.GetHashCode（跨进程不稳定）。
            string id = "guest_" + StableHex8(device);
            string token = "editor_guest_" + id;
            ApplyUserSession(token, id, isGuest: true);
        }

        static string StableHex8(string raw)
        {
            unchecked
            {
                uint hash = 2166136261u;
                string s = raw ?? "";
                for (int i = 0; i < s.Length; i++)
                    hash = (hash ^ s[i]) * 16777619u;
                return (hash & 0xFFFFFFFFu).ToString("x8");
            }
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
