using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Game.BoluoApi;
using UnityEngine;

namespace Game
{
    /// <summary>
    /// 下载门禁纯计算（离线 / 存储 / 体积文案）。
    /// 弹窗与 continue 由 <see cref="DownloadConsentSession"/> 唯一拥有。
    /// </summary>
    public static class DownloadNetworkGate
    {
        public static bool IsOffline() => DownloadCellularGate.IsOffline();

        public static bool NeedsMobileDataPrompt() =>
            DownloadCellularGate.NeedsMobileDataPrompt();

        /// <summary>无网 toast。返回是否已拦截。</summary>
        public static bool TryShowOffline(Action onConfirm = null) =>
            DownloadConsentSession.TryShowOfflineCompat(onConfirm);

        public static bool TryBlockIfInsufficientStorage(
            long requiredBytes,
            Action onConfirm = null)
        {
            if (requiredBytes <= 0)
                return false;
            if (AvailableStorageBytes() >= requiredBytes * 2L)
                return false;

            long mb = Math.Max(1L, (requiredBytes + 1024L * 1024L - 1L) / (1024L * 1024L));
            UiDialogService.ShowInsufficientStorage(
                mb.ToString(CultureInfo.InvariantCulture),
                onConfirm);
            return true;
        }

        public static bool TryBlockIfInsufficientStorage(
            IList<string> bankNames,
            Action onConfirm = null) =>
            DownloadConsentSession.TryBlockStorageCompat(bankNames, onConfirm);

        /// <summary>转发 Session；窗已开时替换 continue。</summary>
        public static bool TryShowMobileDataPrompt(
            IList<string> banksToPrioritize,
            Action onAllowContinue = null,
            Action onWifiOnly = null,
            string sizeMb = null) =>
            DownloadConsentSession.TryShowMobileCompat(
                banksToPrioritize,
                onAllowContinue,
                onWifiOnly,
                sizeMb);

        public static bool TryShowMobileDataPrompt(
            string bankName,
            Action onAllowContinue = null,
            Action onWifiOnly = null)
        {
            string[] one = string.IsNullOrEmpty(bankName)
                ? Array.Empty<string>()
                : new[] { bankName };
            return TryShowMobileDataPrompt(one, onAllowContinue, onWifiOnly);
        }

        /// <summary>转发 Session。全过 true；拦截 false（窗回调里续）。</summary>
        public static bool TryPassDownloadGates(
            IList<string> bankNames,
            Action onAllowAfterMobile = null,
            Action onWifiOnly = null,
            Action onBlockedRetreat = null) =>
            DownloadConsentSession.TryPassGatesCompat(
                bankNames,
                onAllowAfterMobile,
                onWifiOnly,
                onBlockedRetreat);

        public static string FormatSizeMb(IList<string> bankNames)
        {
            long kb = PendingKilobytes(bankNames);
            if (kb <= 0)
                return "若干";
            float mb = kb / 1024f;
            return mb.ToString("0.#", CultureInfo.InvariantCulture);
        }

        public static long PendingKilobytes(IList<string> bankNames)
        {
            if (bankNames == null)
                return 0;
            long kb = 0;
            for (int i = 0; i < bankNames.Count; i++)
            {
                string name = bankNames[i];
                if (string.IsNullOrEmpty(name) || BankDownloader.IsLocalReady(name))
                    continue;
                if (PresetBankCatalog.TryGet(name, out BankRemoteInfo remote)
                    && remote != null
                    && long.TryParse(remote.size, out long parsed)
                    && parsed > 0)
                    kb += parsed;
            }

            return kb;
        }

        public static long AvailableStorageBytes()
        {
            try
            {
                string path = Application.persistentDataPath;
                var drive = new DriveInfo(Path.GetPathRoot(path) ?? path);
                if (drive.IsReady)
                    return drive.AvailableFreeSpace;
            }
            catch
            {
                // 读不到时不拦。
            }

            return long.MaxValue;
        }
    }
}
