using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Game.BoluoApi;
using UnityEngine;

namespace Game
{
    /// <summary>
    /// 下载前门禁：无网 → 存储 → 流量。文案走 <see cref="DownloadDialogCopy"/>。
    /// </summary>
    public static class DownloadNetworkGate
    {
        public static bool IsOffline() => DownloadCellularGate.IsOffline();

        public static bool NeedsMobileDataPrompt() =>
            DownloadCellularGate.NeedsMobileDataPrompt();

        /// <summary>无网弹层。返回是否已拦截。</summary>
        public static bool TryShowOffline(Action onConfirm = null)
        {
            if (!IsOffline())
                return false;
            UiDialogService.ShowNoNetwork(onConfirm);
            return true;
        }

        /// <summary>
        /// 空间不足则弹存储窗并阻断。requiredMb 为需预留的 MB 数字文案。
        /// </summary>
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

        /// <summary>按 bank 名估算待下体积后做存储门禁。</summary>
        public static bool TryBlockIfInsufficientStorage(
            IList<string> bankNames,
            Action onConfirm = null)
        {
            long kb = PendingKilobytes(bankNames);
            if (kb <= 0)
                return false;
            return TryBlockIfInsufficientStorage(kb * 1024L, onConfirm);
        }

        /// <summary>
        /// 需要流量窗时弹出。onAllow 在 Priority 之后、开闸之后由门面调用（调用方只写业务续下）。
        /// 返回是否已拦截（已弹窗）。
        /// </summary>
        public static bool TryShowMobileDataPrompt(
            IList<string> banksToPrioritize,
            Action onAllowContinue = null,
            Action onWifiOnly = null,
            string sizeMb = null)
        {
            if (!NeedsMobileDataPrompt())
                return false;

            string size = string.IsNullOrEmpty(sizeMb)
                ? FormatSizeMb(banksToPrioritize)
                : sizeMb;

            UiDialogService.ShowMobileDataConfirm(
                size,
                () =>
                {
                    // iOS 时序：先 Priority，再开闸；禁双 Resume。
                    BankDownloader.PriorityBanks(banksToPrioritize);
                    DownloadCellularGate.UserAllowCellularAlways();
                    onAllowContinue?.Invoke();
                },
                () =>
                {
                    DownloadCellularGate.UserRequireWifiOnly();
                    onWifiOnly?.Invoke();
                });
            return true;
        }

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

        /// <summary>
        /// 完整决策链（无申请窗路径）：无网 → 存储 → 流量。
        /// 全过则返回 true，调用方开下；任一拦截返回 false。
        /// </summary>
        public static bool TryPassDownloadGates(
            IList<string> bankNames,
            Action onAllowAfterMobile = null,
            Action onWifiOnly = null,
            Action onBlockedRetreat = null)
        {
            if (TryShowOffline(onBlockedRetreat))
                return false;

            if (TryBlockIfInsufficientStorage(bankNames, onBlockedRetreat))
                return false;

            if (TryShowMobileDataPrompt(
                    bankNames,
                    onAllowAfterMobile,
                    () =>
                    {
                        onWifiOnly?.Invoke();
                        onBlockedRetreat?.Invoke();
                    }))
                return false;

            return true;
        }

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
