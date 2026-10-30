using System;
using System.Collections.Generic;
using System.Globalization;
using Game.BoluoApi;
using UnityEngine;
using UnityGameFramework.Runtime;

namespace Game
{
    /// <summary>
    /// 下载同意会话：唯一弹窗所有者。
    /// 再次 <see cref="EnsureReady"/> / <see cref="NotifyNeedCellularConsent"/> 时替换 continue，绝不静默丢回调。
    /// BankDownloader 只发 <see cref="NotifyNeedCellularConsent"/>，不直接开 UI。
    /// </summary>
    public static class DownloadConsentSession
    {
        enum Phase
        {
            Idle = 0,
            RequestPrompt,
            Checking,
            MobileConfirm,
            StorageBlock,
            OfflineToast,
        }

        static Phase _phase;
        static readonly List<string> _banks = new List<string>(16);
        static Action _onReady;
        static Action _onCancel;
        static bool _lockMove;
        static bool _moveLockedByUs;
        /// <summary>用户点了「切 Wi‑Fi」：静默/RunLoop 不再弹，直到离开蜂窝需确认态；用户 EnsureReady 会清。</summary>
        static bool _declinedCellularUntilWifi;

        /// <summary>
        /// 用户主动下（岛门 / 预设 / 音乐 / Critical）。
        /// 门禁全过或流量窗「继续」后调用 <paramref name="onReady"/>（只调一次）。
        /// </summary>
        public static void EnsureReady(
            IList<string> banks,
            Action onReady,
            DownloadUiOptions opts = default)
        {
            _declinedCellularUntilWifi = false;
            Capture(banks, onReady, opts.OnCancel, opts.LockMoveDuringPrompt);

            if (_lockMove)
                ApplyMoveLock(true);

            if (opts.ShowRequestFirst)
            {
                ShowRequestPrompt(opts.RequestSizeMb);
                return;
            }

            RunGateChecks();
        }

        /// <summary>
        /// 传输层 / Rest 巡检：队列被蜂窝拦住。合并 bank、必要时弹窗；
        /// 不覆盖已有 continue；用户拒流后不再连弹。
        /// </summary>
        public static void NotifyNeedCellularConsent(IList<string> busyBanks)
        {
            if (!DownloadCellularGate.NeedsMobileDataPrompt())
            {
                _declinedCellularUntilWifi = false;
                return;
            }

            if (_declinedCellularUntilWifi)
                return;

            MergeBanks(busyBanks);

            if (_phase == Phase.MobileConfirm || IsMobileConfirmPresent())
            {
                _phase = Phase.MobileConfirm;
                return;
            }

            ShowMobileConfirm();
        }

        /// <summary>Rest 等：仅在尚无 continue 时挂上，不抢用户 EnsureReady。</summary>
        public static void BindReadyIfEmpty(Action onReady)
        {
            if (_onReady == null && onReady != null)
                _onReady = onReady;
        }

        /// <summary>离岛 / 关 Spawner：清会话与会话持有的移动锁。</summary>
        public static void ResetForLeaveIsland()
        {
            _phase = Phase.Idle;
            _banks.Clear();
            _onReady = null;
            _onCancel = null;
            _lockMove = false;
            _declinedCellularUntilWifi = false;
            ReleaseMoveLock();
        }

        /// <summary>岛门后退结束等：放掉会话持有的平移锁。</summary>
        public static void ReleaseSessionMoveLock() => ReleaseMoveLock();

        /// <summary>流量窗 Form.OnClose。</summary>
        public static void NotifyMobileDialogClosed()
        {
            if (_phase == Phase.MobileConfirm)
                _phase = Phase.Idle;
        }

        /// <summary>申请窗 Form.OnClose（未点下载被顶掉时）。</summary>
        public static void NotifyRequestDialogClosed()
        {
            if (_phase == Phase.RequestPrompt)
                _phase = Phase.Idle;
        }

#if UNITY_EDITOR
        public static bool EditorHasPendingReady => _onReady != null;

        public static void EditorInvokeMobileAllow() => OnMobileAllow();

        public static void EditorInvokeMobileWifiOnly() => OnMobileWifiOnly();

        public static string EditorPhaseName => _phase.ToString();

        public static IReadOnlyList<string> EditorBanks => _banks;

        /// <summary>单测：不弹 UI，只挂 continue / bank（假装流量窗已开）。</summary>
        public static void EditorArmMobilePending(
            IList<string> banks,
            Action onReady,
            Action onCancel = null)
        {
            Capture(banks, onReady, onCancel, lockMove: false);
            _phase = Phase.MobileConfirm;
        }
#endif

        // —— 旧 DownloadNetworkGate UI 转发（迁移期）——

        public static bool TryShowOfflineCompat(Action onConfirm = null)
        {
            if (!DownloadCellularGate.IsOffline())
                return false;
            UiDialogService.ShowErrorToast(DownloadDialogCopy.OfflineToast);
            onConfirm?.Invoke();
            return true;
        }

        public static bool TryBlockStorageCompat(IList<string> bankNames, Action onConfirm = null)
        {
            long kb = DownloadNetworkGate.PendingKilobytes(bankNames);
            if (kb <= 0)
                return false;
            long requiredBytes = kb * 1024L;
            if (DownloadNetworkGate.AvailableStorageBytes() >= requiredBytes * 2L)
                return false;
            long mb = Math.Max(1L, (requiredBytes + 1024L * 1024L - 1L) / (1024L * 1024L));
            UiDialogService.ShowInsufficientStorage(
                mb.ToString(CultureInfo.InvariantCulture),
                onConfirm);
            return true;
        }

        /// <summary>
        /// 旧语义：拦截返回 false（窗会调 onAllow）；全过返回 true（调用方自己开下）。
        /// 窗已开时替换 continue，不丢回调。
        /// </summary>
        public static bool TryPassGatesCompat(
            IList<string> bankNames,
            Action onAllowAfterMobile = null,
            Action onWifiOnly = null,
            Action onBlockedRetreat = null)
        {
            if (TryShowOfflineCompat(onBlockedRetreat))
                return false;

            if (TryBlockStorageCompat(bankNames, onBlockedRetreat))
                return false;

            if (!DownloadCellularGate.NeedsMobileDataPrompt())
                return true;

            Capture(
                bankNames,
                onAllowAfterMobile,
                () =>
                {
                    onWifiOnly?.Invoke();
                    onBlockedRetreat?.Invoke();
                },
                lockMove: false);

            if (_phase == Phase.MobileConfirm || IsMobileConfirmPresent())
            {
                _phase = Phase.MobileConfirm;
                return false;
            }

            ShowMobileConfirm();
            return false;
        }

        public static bool TryShowMobileCompat(
            IList<string> banks,
            Action onAllowContinue = null,
            Action onWifiOnly = null,
            string sizeMb = null)
        {
            if (!DownloadCellularGate.NeedsMobileDataPrompt())
                return false;

            if (onAllowContinue != null || onWifiOnly != null)
                Capture(banks, onAllowContinue, onWifiOnly, lockMove: false);
            else
                MergeBanks(banks);

            if (_phase == Phase.MobileConfirm || IsMobileConfirmPresent())
            {
                _phase = Phase.MobileConfirm;
                return true;
            }

            ShowMobileConfirm();
            return true;
        }

        static void Capture(
            IList<string> banks,
            Action onReady,
            Action onCancel,
            bool lockMove)
        {
            _banks.Clear();
            MergeBanks(banks);
            _onReady = onReady;
            if (onCancel != null)
                _onCancel = onCancel;
            if (lockMove)
                _lockMove = true;
        }

        static void MergeBanks(IList<string> banks)
        {
            if (banks == null)
                return;
            for (int i = 0; i < banks.Count; i++)
            {
                string name = banks[i];
                if (string.IsNullOrEmpty(name) || _banks.Contains(name))
                    continue;
                _banks.Add(name);
            }
        }

        static void ShowRequestPrompt(string sizeMb)
        {
            _phase = Phase.RequestPrompt;
            string size = string.IsNullOrEmpty(sizeMb)
                ? DownloadNetworkGate.FormatSizeMb(_banks)
                : sizeMb;
            Log.Info("[DownloadConsent] request prompt size=" + size);
            if (GameEntry.UI == null)
                return;
            UiDialogService.ShowRequestDownload(
                size,
                onDownload: OnRequestConfirmed,
                onReturn: OnCancelled);
        }

        static void OnRequestConfirmed()
        {
            if (_phase == Phase.RequestPrompt)
                _phase = Phase.Checking;
            RunGateChecks();
        }

        static void RunGateChecks()
        {
            _phase = Phase.Checking;

            if (DownloadCellularGate.IsOffline())
            {
                _phase = Phase.OfflineToast;
                if (GameEntry.UI != null)
                    UiDialogService.ShowErrorToast(DownloadDialogCopy.OfflineToast);
                OnCancelled();
                return;
            }

            long kb = DownloadNetworkGate.PendingKilobytes(_banks);
            if (kb > 0)
            {
                long requiredBytes = kb * 1024L;
                if (DownloadNetworkGate.AvailableStorageBytes() < requiredBytes * 2L)
                {
                    _phase = Phase.StorageBlock;
                    long mb = Math.Max(
                        1L,
                        (requiredBytes + 1024L * 1024L - 1L) / (1024L * 1024L));
                    if (GameEntry.UI != null)
                    {
                        UiDialogService.ShowInsufficientStorage(
                            mb.ToString(CultureInfo.InvariantCulture),
                            OnCancelled);
                    }
                    else
                    {
                        OnCancelled();
                    }

                    return;
                }
            }

            if (DownloadCellularGate.NeedsMobileDataPrompt())
            {
                // 已在弹 / 异步 Open 中：只更新 continue，不叠窗。
                if (_phase == Phase.MobileConfirm || IsMobileConfirmPresent())
                {
                    _phase = Phase.MobileConfirm;
                    return;
                }

                ShowMobileConfirm();
                return;
            }

            FireReady();
        }

        static void ShowMobileConfirm()
        {
            _phase = Phase.MobileConfirm;
            string size = DownloadNetworkGate.FormatSizeMb(_banks);
            Log.Info(
                "[DownloadConsent] mobile confirm size=" + size + " banks=" + _banks.Count);
            // Editor 单测无 UI：只推进 phase，回调仍走 EditorInvoke*。
            if (GameEntry.UI == null)
                return;
            UiDialogService.ShowMobileDataConfirm(
                size,
                OnMobileAllow,
                OnMobileWifiOnly);
        }

        static void OnMobileAllow()
        {
            BankDownloader.PriorityBanks(_banks);
            DownloadCellularGate.UserAllowCellularAlways();
            FireReady();
        }

        static void OnMobileWifiOnly()
        {
            DownloadCellularGate.UserRequireWifiOnly();
            _declinedCellularUntilWifi = true;
            OnCancelled();
        }

        static void FireReady()
        {
            Action ready = _onReady;
            _phase = Phase.Idle;
            _onReady = null;
            _onCancel = null;
            _banks.Clear();
            bool lockMove = _lockMove;
            _lockMove = false;
            if (lockMove)
                ReleaseMoveLock();
            ready?.Invoke();
        }

        static void OnCancelled()
        {
            Action cancel = _onCancel;
            bool lockMove = _lockMove;
            _phase = Phase.Idle;
            _onReady = null;
            _onCancel = null;
            _banks.Clear();
            _lockMove = false;
            // 有 OnCancel（岛门）时由对方在后退后解锁；否则会话自己放。
            if (lockMove && cancel == null)
                ReleaseMoveLock();
            cancel?.Invoke();
        }

        static void ApplyMoveLock(bool locked)
        {
            if (!locked)
            {
                ReleaseMoveLock();
                return;
            }

            if (_moveLockedByUs)
                return;
            _moveLockedByUs = true;
            IslandJoystickCamera.Current?.SetDownloadMoveLock(true);
            IslandMainForm.Current?.CancelLocomotion();
            LocomotionInput.ClearAll();
        }

        static void ReleaseMoveLock()
        {
            if (!_moveLockedByUs)
                return;
            _moveLockedByUs = false;
            IslandJoystickCamera.Current?.SetDownloadMoveLock(false);
        }

        static bool IsMobileConfirmPresent()
        {
            if (GameEntry.UI == null)
                return false;
            return GameEntry.UI.HasUIForm(UiAssets.MobileDataConfirmForm);
        }
    }
}
