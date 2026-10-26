using System.Text;
using UnityEngine;

namespace Game
{
    /// <summary>
    /// 游戏进度 / 本地偏好 PlayerPrefs 统一入口。
    /// 游客：不写岛内进度键，且主存档、位置、首进岛、下载红点、活动弹窗日期等不读磁盘。
    /// 正式账号：进度键按 userId 分桶（<c>BaseKey.userId</c>）；退登保留，仅注销清该桶。
    /// 账号 token / 设备 token 由 <see cref="BoluoApi.BoluoSession"/> 管理，不在此类。
    /// </summary>
    public static class BoluoGamePersistence
    {
        static bool IsGuestSession => GuestSession.IsGuest;

        /// <summary>正式账号、已登录且有 userId 才读写岛内进度（游客 / 无 userId / 退登后都不碰盘）。</summary>
        public static bool AllowsAccountProgressDisk =>
            GuestSession.IsLoggedIn
            && !IsGuestSession
            && !string.IsNullOrEmpty(GuestSession.UserId);

        static string ProgressUserId =>
            AllowsAccountProgressDisk ? (GuestSession.UserId ?? "") : "";

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

        static readonly string[] AccountBoundBaseKeys =
        {
            Keys.BoluoSaveData,
            Keys.PlayerPos,
            Keys.DaoYuFirstRunComplete,
            Keys.TutorialType,
            Keys.GongNengView,
            Keys.DownloadedUnplayed,
            Keys.ActivityWorldSleepPopupDate,
            Keys.AppleUserIdKey,
        };

        static string BucketKey(string baseKey, string userId) =>
            string.IsNullOrEmpty(userId) ? baseKey : baseKey + "." + userId;

        static string CurrentBucketKey(string baseKey) =>
            BucketKey(baseKey, ProgressUserId);

        /// <summary>
        /// 注销：清指定（或当前）账号绑定进度桶；并清无后缀旧键（迁移前遗留）。
        /// 保留设备级键（位置权限、首次联网、密码节流）。
        /// </summary>
        public static void ClearAccountBoundProgress(string userId = null)
        {
            string id = userId;
            if (string.IsNullOrEmpty(id))
                id = GuestSession.UserId ?? "";

            if (!string.IsNullOrEmpty(id))
            {
                for (int i = 0; i < AccountBoundBaseKeys.Length; i++)
                    PlayerPrefs.DeleteKey(BucketKey(AccountBoundBaseKeys[i], id));
            }

            // 旧版无后缀键：注销时一并清掉，避免再被迁到别人桶
            for (int i = 0; i < AccountBoundBaseKeys.Length; i++)
                PlayerPrefs.DeleteKey(AccountBoundBaseKeys[i]);

            PlayerPrefs.Save();
        }

        /// <summary>
        /// 若分桶键不存在而旧无后缀键存在，则搬到当前 userId 桶并删旧键。
        /// </summary>
        static void MigrateLegacyStringKey(string baseKey)
        {
            if (!AllowsAccountProgressDisk)
                return;
            string bucket = CurrentBucketKey(baseKey);
            if (PlayerPrefs.HasKey(bucket))
                return;
            if (!PlayerPrefs.HasKey(baseKey))
                return;
            string raw = PlayerPrefs.GetString(baseKey, "") ?? "";
            PlayerPrefs.SetString(bucket, raw);
            PlayerPrefs.DeleteKey(baseKey);
        }

        static void MigrateLegacyIntKey(string baseKey)
        {
            if (!AllowsAccountProgressDisk)
                return;
            string bucket = CurrentBucketKey(baseKey);
            if (PlayerPrefs.HasKey(bucket))
                return;
            if (!PlayerPrefs.HasKey(baseKey))
                return;
            int v = PlayerPrefs.GetInt(baseKey, 0);
            PlayerPrefs.SetInt(bucket, v);
            PlayerPrefs.DeleteKey(baseKey);
        }

        #region BoluoSaveData

        public static string LoadBoluoSaveDataRaw()
        {
            if (!AllowsAccountProgressDisk)
                return "";
            MigrateLegacyStringKey(Keys.BoluoSaveData);
            return PlayerPrefs.GetString(CurrentBucketKey(Keys.BoluoSaveData), "") ?? "";
        }

        public static void PersistBoluoSaveDataRaw(string json)
        {
            if (!AllowsAccountProgressDisk)
                return;
            PlayerPrefs.SetString(CurrentBucketKey(Keys.BoluoSaveData), json ?? "");
        }

        #endregion

        #region 岛屿角色位置 / 首进标记

        /// <returns>true 表示已写入当前 userId 桶（未 flush；调用方按需 <see cref="PlayerPrefsSave"/>）。</returns>
        public static bool PersistPlayerPositionJson(string json)
        {
            if (!AllowsAccountProgressDisk)
                return false;
            PlayerPrefs.SetString(CurrentBucketKey(Keys.PlayerPos), json ?? "");
            return true;
        }

        public static bool HasPlayerPositionSaved()
        {
            if (!AllowsAccountProgressDisk)
                return false;
            MigrateLegacyStringKey(Keys.PlayerPos);
            return PlayerPrefs.HasKey(CurrentBucketKey(Keys.PlayerPos));
        }

        public static string LoadPlayerPositionJson()
        {
            if (!AllowsAccountProgressDisk)
                return "";
            MigrateLegacyStringKey(Keys.PlayerPos);
            return PlayerPrefs.GetString(CurrentBucketKey(Keys.PlayerPos), "") ?? "";
        }

        public static bool HasDaoYuFirstRunCompleteMarker()
        {
            if (!AllowsAccountProgressDisk)
                return false;
            MigrateLegacyIntKey(Keys.DaoYuFirstRunComplete);
            return PlayerPrefs.HasKey(CurrentBucketKey(Keys.DaoYuFirstRunComplete));
        }

        public static void SetDaoYuFirstRunCompleteMarker()
        {
            if (!AllowsAccountProgressDisk)
                return;
            PlayerPrefs.SetInt(CurrentBucketKey(Keys.DaoYuFirstRunComplete), 1);
        }

        #endregion

        #region 教程 / 功能引导

        public static bool HasTutorialTypeMarker()
        {
            if (AllowsAccountProgressDisk)
            {
                MigrateLegacyIntKey(Keys.TutorialType);
                return PlayerPrefs.HasKey(CurrentBucketKey(Keys.TutorialType));
            }

            return PlayerPrefs.HasKey(Keys.TutorialType);
        }

        public static void TrySetTutorialTypeMarker()
        {
            if (!AllowsAccountProgressDisk)
                return;
            PlayerPrefs.SetInt(CurrentBucketKey(Keys.TutorialType), 1);
        }

        public static bool HasGongNengViewMarker()
        {
            if (AllowsAccountProgressDisk)
            {
                MigrateLegacyIntKey(Keys.GongNengView);
                return PlayerPrefs.HasKey(CurrentBucketKey(Keys.GongNengView));
            }

            return PlayerPrefs.HasKey(Keys.GongNengView);
        }

        public static void TrySetGongNengViewMarker()
        {
            if (!AllowsAccountProgressDisk)
                return;
            PlayerPrefs.SetInt(CurrentBucketKey(Keys.GongNengView), 1);
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
            MigrateLegacyStringKey(Keys.DownloadedUnplayed);
            return PlayerPrefs.GetString(CurrentBucketKey(Keys.DownloadedUnplayed), "") ?? "";
        }

        public static void PersistDownloadedUnplayedRaw(string json)
        {
            if (!AllowsAccountProgressDisk)
                return;
            PlayerPrefs.SetString(CurrentBucketKey(Keys.DownloadedUnplayed), json ?? "");
        }

        #endregion

        #region 活动弹窗「今日已展示」

        public static string LoadActivityWorldSleepPopupDate()
        {
            if (!AllowsAccountProgressDisk)
                return "";
            MigrateLegacyStringKey(Keys.ActivityWorldSleepPopupDate);
            return PlayerPrefs.GetString(CurrentBucketKey(Keys.ActivityWorldSleepPopupDate), "") ?? "";
        }

        public static void TryPersistActivityWorldSleepPopupDateToday(string yyyyMmDd)
        {
            if (!AllowsAccountProgressDisk)
                return;
            PlayerPrefs.SetString(CurrentBucketKey(Keys.ActivityWorldSleepPopupDate), yyyyMmDd ?? "");
            PlayerPrefs.Save();
        }

        #endregion

        #region Apple / 首次联网 / Flush

        /// <summary>游客也可写入（登录链路辅助）；正式账号写入分桶键。</summary>
        public static void PersistAppleUserId(string appleUserId)
        {
            if (AllowsAccountProgressDisk)
                PlayerPrefs.SetString(CurrentBucketKey(Keys.AppleUserIdKey), appleUserId ?? "");
            else
                PlayerPrefs.SetString(Keys.AppleUserIdKey, appleUserId ?? "");
        }

        public static string LoadAppleUserId()
        {
            if (AllowsAccountProgressDisk)
            {
                MigrateLegacyStringKey(Keys.AppleUserIdKey);
                return PlayerPrefs.GetString(CurrentBucketKey(Keys.AppleUserIdKey), "") ?? "";
            }

            return PlayerPrefs.GetString(Keys.AppleUserIdKey, "") ?? "";
        }

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
                .Append(" userId=").Append(GuestSession.UserId ?? "")
                .Append(" GameProgressDisk=").Append(AllowsAccountProgressDisk ? "read/write" : "isolated")
                .AppendLine();
            string uid = ProgressUserId;
            for (int i = 0; i < AccountBoundBaseKeys.Length; i++)
            {
                string baseKey = AccountBoundBaseKeys[i];
                AppendKeyLine(sb, baseKey, maxValuePreviewChars);
                if (!string.IsNullOrEmpty(uid))
                    AppendKeyLine(sb, BucketKey(baseKey, uid), maxValuePreviewChars);
            }

            AppendKeyLine(sb, Keys.LocationPermissionRequested, maxValuePreviewChars);
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
