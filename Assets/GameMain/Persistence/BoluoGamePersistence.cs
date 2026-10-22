using System.Text;
using UnityEngine;

namespace Game
{
    /// <summary>
    /// 游戏进度 / 本地偏好 PlayerPrefs 统一入口。
    /// 游客：不写岛内进度键，且主存档、位置、首进岛、下载红点、活动弹窗日期等不读磁盘。
    /// 账号 token / 设备 token 由 <see cref="BoluoApi.BoluoSession"/> 管理，不在此类。
    /// </summary>
    public static class BoluoGamePersistence
    {
        static bool IsGuestSession => GuestSession.IsGuest;

        /// <summary>正式账号且已登录才读写岛内进度（游客 / 退登后都不碰盘）。</summary>
        static bool AllowsAccountProgressDisk =>
            GuestSession.IsLoggedIn && !IsGuestSession;

        public static class Keys
        {
            public const string BoluoSaveData = "BoluoSaveData";
            public const string PlayerPos = "PlayerPos";
            public const string DaoYuFirstRunComplete = "DaoYuFirstRunComplete";
            public const string TutorialType = "TutorialType";
            public const string GongNengView = "GongNengView";
            public const string LocationPermissionRequested = "LocationPermissionRequested";
            public const string DownloadedUnplayed = "BoluoDownloadedUnplayed";
            public const string ActivityWorldSleepPopupDate = "WorldSleepDayActivityPopupDate";
            public const string AppleUserIdKey = "AppleUserIdKey";
            public const string FirstNetworkProbeComplete = "Boluo.FirstNetworkProbeComplete";

            public const string AccPwFailPrefix = "Boluo.AccPwLogin.Fail.";
            public const string AccPwLockUtcTicksPrefix = "Boluo.AccPwLogin.LockUtcTicks.";
            public const string AccPwPhoneRegistry = "Boluo.AccPwLogin.PhoneRegistry";
        }

        /// <summary>
        /// 切账号 / 退登：清账号绑定进度（位置、大气存档、首进岛、教程、红点等）。
        /// 保留设备级键（位置权限、首次联网、密码节流）。
        /// </summary>
        public static void ClearAccountBoundProgress()
        {
            PlayerPrefs.DeleteKey(Keys.BoluoSaveData);
            PlayerPrefs.DeleteKey(Keys.PlayerPos);
            PlayerPrefs.DeleteKey(Keys.DaoYuFirstRunComplete);
            PlayerPrefs.DeleteKey(Keys.TutorialType);
            PlayerPrefs.DeleteKey(Keys.GongNengView);
            PlayerPrefs.DeleteKey(Keys.DownloadedUnplayed);
            PlayerPrefs.DeleteKey(Keys.ActivityWorldSleepPopupDate);
            PlayerPrefs.DeleteKey(Keys.AppleUserIdKey);
            PlayerPrefs.Save();
        }

        #region BoluoSaveData

        public static string LoadBoluoSaveDataRaw()
        {
            if (!AllowsAccountProgressDisk)
                return "";
            return PlayerPrefs.GetString(Keys.BoluoSaveData, "") ?? "";
        }

        public static void PersistBoluoSaveDataRaw(string json)
        {
            if (!AllowsAccountProgressDisk)
                return;
            PlayerPrefs.SetString(Keys.BoluoSaveData, json ?? "");
        }

        #endregion

        #region 岛屿角色位置 / 首进标记

        public static void PersistPlayerPositionJson(string json)
        {
            if (!AllowsAccountProgressDisk)
                return;
            PlayerPrefs.SetString(Keys.PlayerPos, json ?? "");
        }

        public static bool HasPlayerPositionSaved()
        {
            if (!AllowsAccountProgressDisk)
                return false;
            return PlayerPrefs.HasKey(Keys.PlayerPos);
        }

        public static string LoadPlayerPositionJson()
        {
            if (!AllowsAccountProgressDisk)
                return "";
            return PlayerPrefs.GetString(Keys.PlayerPos, "") ?? "";
        }

        public static bool HasDaoYuFirstRunCompleteMarker()
        {
            if (!AllowsAccountProgressDisk)
                return false;
            return PlayerPrefs.HasKey(Keys.DaoYuFirstRunComplete);
        }

        public static void SetDaoYuFirstRunCompleteMarker()
        {
            if (!AllowsAccountProgressDisk)
                return;
            PlayerPrefs.SetInt(Keys.DaoYuFirstRunComplete, 1);
        }

        #endregion

        #region 教程 / 功能引导

        public static bool HasTutorialTypeMarker() =>
            PlayerPrefs.HasKey(Keys.TutorialType);

        public static void TrySetTutorialTypeMarker()
        {
            if (!AllowsAccountProgressDisk)
                return;
            PlayerPrefs.SetInt(Keys.TutorialType, 1);
        }

        public static bool HasGongNengViewMarker() =>
            PlayerPrefs.HasKey(Keys.GongNengView);

        public static void TrySetGongNengViewMarker()
        {
            if (!AllowsAccountProgressDisk)
                return;
            PlayerPrefs.SetInt(Keys.GongNengView, 1);
        }

        #endregion

        #region 位置权限「已弹过」

        public static int GetLocationPermissionRequestedFlag() =>
            PlayerPrefs.GetInt(Keys.LocationPermissionRequested, 0);

        public static void TrySetLocationPermissionRequestedFlag()
        {
            if (IsGuestSession)
                return;
            PlayerPrefs.SetInt(Keys.LocationPermissionRequested, 1);
            PlayerPrefs.Save();
        }

        #endregion

        #region 下载未播红点

        public static string LoadDownloadedUnplayedRaw()
        {
            if (!AllowsAccountProgressDisk)
                return "";
            return PlayerPrefs.GetString(Keys.DownloadedUnplayed, "") ?? "";
        }

        public static void PersistDownloadedUnplayedRaw(string json)
        {
            if (!AllowsAccountProgressDisk)
                return;
            PlayerPrefs.SetString(Keys.DownloadedUnplayed, json ?? "");
        }

        #endregion

        #region 活动弹窗「今日已展示」

        public static string LoadActivityWorldSleepPopupDate()
        {
            if (!AllowsAccountProgressDisk)
                return "";
            return PlayerPrefs.GetString(Keys.ActivityWorldSleepPopupDate, "") ?? "";
        }

        public static void TryPersistActivityWorldSleepPopupDateToday(string yyyyMmDd)
        {
            if (!AllowsAccountProgressDisk)
                return;
            PlayerPrefs.SetString(Keys.ActivityWorldSleepPopupDate, yyyyMmDd ?? "");
            PlayerPrefs.Save();
        }

        #endregion

        #region Apple / 首次联网 / Flush

        /// <summary>游客也可写入（登录链路辅助）。</summary>
        public static void PersistAppleUserId(string userId)
        {
            PlayerPrefs.SetString(Keys.AppleUserIdKey, userId ?? "");
        }

        public static string LoadAppleUserId() =>
            PlayerPrefs.GetString(Keys.AppleUserIdKey, "") ?? "";

        public static bool HasFirstNetworkProbeComplete() =>
            PlayerPrefs.HasKey(Keys.FirstNetworkProbeComplete);

        public static void SetFirstNetworkProbeComplete()
        {
            PlayerPrefs.SetInt(Keys.FirstNetworkProbeComplete, 1);
            PlayerPrefs.Save();
        }

        public static string AccPwFailKey(string phoneDigits) =>
            Keys.AccPwFailPrefix + phoneDigits;

        public static string AccPwLockKey(string phoneDigits) =>
            Keys.AccPwLockUtcTicksPrefix + phoneDigits;

        public static void PlayerPrefsSave() => PlayerPrefs.Save();

        #endregion

        #region Debug

        public static string BuildDebugSnapshot(int maxValuePreviewChars = 80)
        {
            var sb = new StringBuilder(512);
            sb.Append("[BoluoGamePersistence] IsGuestSession=").Append(IsGuestSession)
                .Append(" GameProgressDisk=").Append(IsGuestSession ? "isolated" : "read/write")
                .AppendLine();
            AppendKeyLine(sb, Keys.BoluoSaveData, maxValuePreviewChars);
            AppendKeyLine(sb, Keys.PlayerPos, maxValuePreviewChars);
            AppendKeyLine(sb, Keys.DaoYuFirstRunComplete, maxValuePreviewChars);
            AppendKeyLine(sb, Keys.TutorialType, maxValuePreviewChars);
            AppendKeyLine(sb, Keys.GongNengView, maxValuePreviewChars);
            AppendKeyLine(sb, Keys.LocationPermissionRequested, maxValuePreviewChars);
            AppendKeyLine(sb, Keys.DownloadedUnplayed, maxValuePreviewChars);
            AppendKeyLine(sb, Keys.ActivityWorldSleepPopupDate, maxValuePreviewChars);
            AppendKeyLine(sb, Keys.AppleUserIdKey, maxValuePreviewChars);
            AppendKeyLine(sb, Keys.FirstNetworkProbeComplete, maxValuePreviewChars);
            return sb.ToString();
        }

        static void AppendKeyLine(StringBuilder sb, string key, int maxLen)
        {
            if (!PlayerPrefs.HasKey(key))
            {
                sb.Append("  - ").Append(key).Append(": (absent)").AppendLine();
                return;
            }

            string raw;
            try
            {
                raw = PlayerPrefs.GetString(key, "");
                if (string.IsNullOrEmpty(raw))
                    raw = "int=" + PlayerPrefs.GetInt(key);
            }
            catch
            {
                raw = "(read error)";
            }

            if (raw.Length > maxLen)
                raw = raw.Substring(0, maxLen) + "…";
            sb.Append("  - ").Append(key).Append(": ").Append(raw).AppendLine();
        }

        #endregion
    }
}
