using System;
using System.Collections.Generic;
using System.Globalization;
using Game.BoluoApi;
using UnityEngine;
using UnityGameFramework.Runtime;

namespace Game
{
    /// <summary>
    /// 下载同意会话：唯一弹窗所有者（经 GameFramework UI Form）。
    /// 流量窗只由用户主动 <see cref="EnsureReady"/>（区域 / 预设 / 音乐 / Critical 点下载）弹出；
    /// Rest / RunLoop 的 <see cref="NotifyNeedCellularConsent"/> 只静默记账，绝不开窗。
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
        /// 用户主动下（岛门 / 预设 / 音乐 / Critical 点下载）。
        /// 无网 → 存储 → 流量（文案体积按 bank 实算）→ 过闸后 <paramref name="onReady"/>（只调一次）。
        /// </summary>
        public static void EnsureReady(
            IList<string> banks,
            Action onReady,
            DownloadUiOptions opts = default)
        {
            _declinedCellularUntilWifi = false;
            // 每次用户点下载都重新要流量确认（清掉上次批次的 AllowAlways）。
            DownloadCellularGate.RevokeCellularAllow();
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
        /// 传输层 / Rest：蜂窝未开闸时只合并 bank，<b>不开流量窗</b>。
        /// 进岛静默 Rest、Wi‑Fi→蜂窝打断均走此路径；用户点下载才由 <see cref="EnsureReady"/> 弹窗。
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
            // 有意不 ShowMobileConfirm：流量窗只服务用户主动 EnsureReady。
        }

        /// <summary>
        /// 申请窗点「立即下载」：关窗前先离开 RequestPrompt，
        /// 避免 OnClose 把 phase 打成 Idle 后 Rest Notify 抢弹流量窗。
        /// </summary>
        public static void BeginRequestConfirmed()
        {
            if (_phase == Phase.RequestPrompt)
                _phase = Phase.Checking;
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

        /// <summary>单测：模拟申请窗点「立即下载」后的门禁链（跳过 UI）。</summary>
        public static void EditorInvokeRequestConfirmed() => OnRequestConfirmed();

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

        /// <summary>
        /// 旧 API / 编辑器预览强制弹流量窗。玩法路径请用 <see cref="EnsureReady"/>，
        /// 勿在 Rest / RunLoop 里调本方法。
        /// </summary>
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
            // 先占 phase，再收掉可能已开的流量窗（Rest 抢弹），保证用户先看到申请窗。
            _phase = Phase.RequestPrompt;
            DismissMobileConfirmIfPresent();
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
            if (_phase == Phase.RequestPrompt || _phase == Phase.Checking)
                _phase = Phase.Checking;
            RunGateChecks();
        }

        static void RunGateChecks()
        {
            _phase = Phase.Checking;

            // 全部已落盘：无需离线/流量窗（音乐/区域下过的同名 bank 切预设直接过）。
            if (AllBanksLocallyReady(_banks))
            {
                FireReady();
                return;
            }

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

        /// <summary>流量窗：体积文案按当前会话 bank 的 PendingKilobytes 实算（经 GF UI Form）。</summary>
        static void ShowMobileConfirm()
        {
            _phase = Phase.MobileConfirm;
            // 始终按会话 bank 重算，不用写死 500；无 catalog 体积时 FormatSizeMb →「若干」。
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

        /// <summary>会话 bank 是否均已 <see cref="BankDownloader.IsLocalReady"/>（空列表视为就绪）。</summary>
        static bool AllBanksLocallyReady(IList<string> banks)
        {
            if (banks == null || banks.Count == 0)
                return true;
            for (int i = 0; i < banks.Count; i++)
            {
                string name = banks[i];
                if (string.IsNullOrEmpty(name))
                    continue;
                if (!BankDownloader.IsLocalReady(name))
                    return false;
            }

            return true;
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

        static bool IsRequestPromptPresent()
        {
            if (GameEntry.UI == null)
                return false;
            return GameEntry.UI.HasUIForm(UiAssets.RequestDownloadForm);
        }

        /// <summary>静默收流量窗（不走「切 Wi‑Fi」）。phase 已非 MobileConfirm 时 OnClose 为 no-op。</summary>
        static void DismissMobileConfirmIfPresent()
        {
            if (GameEntry.UI == null || !IsMobileConfirmPresent())
                return;
            var form = GameEntry.UI.GetUIForm(UiAssets.MobileDataConfirmForm);
            if (form != null)
                GameEntry.UI.CloseUIForm(form.SerialId);
        }
    }
}
