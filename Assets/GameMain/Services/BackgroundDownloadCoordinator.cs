using System.Collections.Generic;
using Game.BoluoApi;

namespace Game
{
    /// <summary>
    /// 全量待下 bank 收集 + FeatureOn+WiFi 同意链体积 / 静默全量踢动。
    /// 用户入口下载列表始终用点击 pending，不把全量塞进 User Flow。
    /// </summary>
    public static class BackgroundDownloadCoordinator
    {
        /// <summary>catalog 内全部未 <see cref="BankDownloader.IsLocalReady"/> 且有 url 的 stem。</summary>
        public static List<string> CollectAllPendingBankNames()
        {
            var names = new List<string>(64);
            var seen = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            var all = PresetBankCatalog.AllRemotes();
            for (int i = 0; i < all.Count; i++)
            {
                BankRemoteInfo info = all[i];
                if (info == null || string.IsNullOrEmpty(info.bankName))
                    continue;
                if (string.IsNullOrEmpty(info.url))
                    continue;
                string stem = AudioEnginePaths.StripBankExtension(info.bankName);
                if (string.IsNullOrEmpty(stem) || !seen.Add(stem))
                    continue;
                if (BankDownloader.IsLocalReady(stem))
                    continue;
                names.Add(stem);
            }

            return names;
        }

        /// <summary>
        /// FeatureOn+WiFi 且尚未接受 §4.6：同意链体积用全部剩余；否则用点击 pending。
        /// 下载 / 进度窗银行列表请用点击 pending，勿用本方法结果去 DownloadBanks。
        /// </summary>
        public static string[] BanksForWifiConsentSize(string[] clickedPending)
        {
            if (clickedPending == null || clickedPending.Length == 0)
                return System.Array.Empty<string>();

            bool wifiConfirm = BoluoAppLaunchCache.IsWifiDownloadConfirmEnabled();
            if (!BackgroundDownloadPolicy.ShouldWifiConfirmOnceThenDownloadAll(
                    wifiConfirm,
                    DownloadCellularGate.IsWifiOrLan()))
                return clickedPending;

            if (DownloadConsentSession.WifiConfirmAccepted)
                return clickedPending;

            List<string> all = CollectAllPendingBankNames();
            if (all.Count == 0)
                return clickedPending;
            return all.ToArray();
        }

        /// <summary>
        /// FeatureOn+WiFi：确认后（或本岛已接受）踢静默全量，同 FeatureOff Bootstrap。
        /// </summary>
        public static void KickSilentAllIfNeeded()
        {
            bool wifiConfirm = BoluoAppLaunchCache.IsWifiDownloadConfirmEnabled();
            if (!BackgroundDownloadPolicy.ShouldWifiConfirmOnceThenDownloadAll(
                    wifiConfirm,
                    DownloadCellularGate.IsWifiOrLan()))
                return;

            BankUpgradeBootstrap.KickSilentAllRemaining(loadIntoEngine: true);
        }
    }
}
