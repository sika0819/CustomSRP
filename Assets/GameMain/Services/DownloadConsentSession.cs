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
    /// 流量窗只由用户主动 <see cref="EnsureReady"/>（区域 / 预设 / 音乐 / Critical / Rest 确认）弹出；
    /// Rest / RunLoop 的 <see cref="NotifyNeedCellularConsent"/> 只静默记账，绝不开窗。
    /// 门禁：无网 → 存储 → 蜂窝 →（AppLaunch Wi‑Fi 确认开关）→ Ready。
    /// </summary>
    public static class DownloadConsentSession
    {
        enum Phase
        {
            Idle = 0,
            RequestPrompt,
            Checking,
            MobileConfirm,
            WifiConfirm,
            StorageBlock,
            OfflineToast,
        }

        static Phase _phase;
        static readonly List<string> _banks = new List<string>(16);
        static Action _onReady;
        static Action _onCancel;
        static Action _onWifiLater;
        static bool _lockMove;
        static bool _moveLockedByUs;
        /// <summary>用户点了「切 Wi‑Fi」：静默/RunLoop 不再弹，直到离开蜂窝需确认态；用户 EnsureReady 会清。</summary>
        static bool _declinedCellularUntilWifi;
        /// <summary>本岛会话已点过 Wi‑Fi 确认「立即下载」；离岛复位。</summary>
        static bool _wifiConfirmAccepted;

        /// <summary>
        /// 用户主动下（岛门 / 预设 / 音乐 / Critical / Rest 确认）。
        /// 无网 → 存储 → 流量 →（可选 Wi‑Fi 确认）→ 过闸后 <paramref name="onReady"/>（只调一次）。
        /// 蜂窝「继续下载」为本岛会话级开闸，直到离岛 / 退登 <see cref="DownloadCellularGate.ResetForNewSession"/>。
        /// </summary>
        public static void EnsureReady(
            IList<string> banks,
            Action onReady,
            DownloadUiOptions opts = default)
        {
            _declinedCellularUntilWifi = false;
            Capture(
                banks,
                onReady,
                opts.OnCancel,
                opts.OnWifiConfirmLater,
                opts.LockMoveDuringPrompt);

            if (_lockMove)
                ApplyMoveLock(true);

            // 已全部落盘：申请窗 / Wi‑Fi / 流量都不要（含 Critical ShowRequestFirst）。
            if (AllBanksLocallyReady(_banks))
            {
                FireReady();
                return;
            }

            if (opts.ShowRequestFirst)
            {
                ShowRequestPrompt(opts.RequestSizeMb);
                return;
            }

            RunGateChecks();
        }

        /// <summary>
        /// 传输层：蜂窝未开闸且队列仍忙。
        /// FeatureOn → 弹流量窗续传；FeatureOff 由闸自动开闸（零弹窗），此处只记账。
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

            bool wifiConfirm = BoluoAppLaunchCache.IsWifiDownloadConfirmEnabled();
            if (!BackgroundDownloadPolicy.ShouldPromptMobileWhenBackgroundBlocked(
                    wifiConfirm,
                    hasBusyWork: true,
                    needsMobileDataPrompt: true))
                return;

            if (_phase == Phase.MobileConfirm || IsMobileConfirmPresent())
            {
                _phase = Phase.MobileConfirm;
                return;
            }

            if (_phase != Phase.Idle && _phase != Phase.Checking)
                return;

            ShowMobileConfirm();
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

        /// <summary>
        /// Wi‑Fi 确认点「立即下载」：关窗前离开 WifiConfirm 并记下已接受，
        /// 再 Close → FireReady，避免同帧 Progress 叠在未关的 §4.6 上（流量切 Wi‑Fi 后区域门尤甚）。
        /// </summary>
        public static void BeginWifiConfirmed()
        {
            if (_phase == Phase.WifiConfirm)
                _phase = Phase.Checking;
            _wifiConfirmAccepted = true;
        }

        /// <summary>Rest 等：仅在尚无 continue 时挂上，不抢用户 EnsureReady。</summary>
        public static void BindReadyIfEmpty(Action onReady)
        {
            if (_onReady == null && onReady != null)
                _onReady = onReady;
        }

        /// <summary>
        /// 同意链是否占着弹窗 / 门禁（非 Idle）。
        /// Bootstrap Rest 不得在 Critical 申请窗仍开时再 EnsureReady，否则会冲掉回调。
        /// </summary>
        public static bool IsPromptActive => _phase != Phase.Idle;

        /// <summary>本岛是否已接受 §4.6「立即下载」（接受后不再弹，可静默全量）。</summary>
        public static bool WifiConfirmAccepted => _wifiConfirmAccepted;

        /// <summary>离岛 / 关 Spawner：清会话与会话持有的移动锁。</summary>
        public static void ResetForLeaveIsland()
        {
            _phase = Phase.Idle;
            _banks.Clear();
            _onReady = null;
            _onCancel = null;
            _onWifiLater = null;
            _lockMove = false;
            _declinedCellularUntilWifi = false;
            _wifiConfirmAccepted = false;
            ReleaseMoveLock();
        }

        /// <summary>Wi‑Fi 确认窗 Form.OnClose。</summary>
        public static void NotifyWifiDialogClosed()
        {
            if (_phase == Phase.WifiConfirm)
                _phase = Phase.Idle;
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

        public static void EditorInvokeWifiConfirm() => OnWifiConfirmDownload();

        public static void EditorInvokeWifiLater() => OnWifiConfirmLater();

        /// <summary>单测：模拟申请窗点「立即下载」后的门禁链（跳过 UI）。</summary>
        public static void EditorInvokeRequestConfirmed() => OnRequestConfirmed();

        /// <summary>单测：模拟申请窗「返回」/ 蒙层关闭。</summary>
        public static void EditorInvokeRequestReturn() => OnCancelled();

        public static string EditorPhaseName => _phase.ToString();

        public static IReadOnlyList<string> EditorBanks => _banks;

        public static bool EditorWifiConfirmAccepted => _wifiConfirmAccepted;

        /// <summary>单测：不弹 UI，只挂 continue / bank（假装流量窗已开）。</summary>
        public static void EditorArmMobilePending(
            IList<string> banks,
            Action onReady,
            Action onCancel = null)
        {
            Capture(banks, onReady, onCancel, onWifiLater: null, lockMove: false);
            _phase = Phase.MobileConfirm;
        }
#endif

        static void Capture(
            IList<string> banks,
            Action onReady,
            Action onCancel,
            Action onWifiLater,
            bool lockMove)
        {
            // 弹窗 / 门禁占用中被新 EnsureReady 覆盖：先取消上一笔（Music/Preset OnCancel
            // 已捕获旧 album/presetId，不会误清新 Intent），再挂新回调。
            Action prevCancel = null;
            if (_phase != Phase.Idle
                && (_onReady != null || _onCancel != null)
                && !ReferenceEquals(_onReady, onReady))
            {
                prevCancel = _onCancel;
                _onReady = null;
                _onCancel = null;
                _onWifiLater = null;
            }

            _banks.Clear();
            MergeBanks(banks);
            _onReady = onReady;
            if (onCancel != null)
                _onCancel = onCancel;
            if (onWifiLater != null)
                _onWifiLater = onWifiLater;
            if (lockMove)
                _lockMove = true;

            if (prevCancel == null)
                return;
            try
            {
                prevCancel();
            }
            catch (Exception e)
            {
                Log.Warning("[DownloadConsent] prev OnCancel: " + e.Message);
            }
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
                {
                    UiDialogService.ShowNoNetwork(OnCancelled);
                }
                else
                {
                    OnCancelled();
                }

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

            // FeatureOff：蜂窝自动开闸（零弹窗）。FeatureOn：未开闸则弹流量窗。
            DownloadCellularGate.EnsureFeatureOffCellularAutoAllow();
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

            // 仅 Wi‑Fi/有线 + 开关开：插 §4.6 确认；蜂窝路径到这里已处理完。
            if (BoluoAppLaunchCache.IsWifiDownloadConfirmEnabled()
                && !_wifiConfirmAccepted
                && DownloadCellularGate.IsWifiOrLan())
            {
                if (_phase == Phase.WifiConfirm || IsWifiConfirmPresent())
                {
                    _phase = Phase.WifiConfirm;
                    return;
                }

                ShowWifiConfirm();
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
            // 开/关：会话开闸直到回 Wi‑Fi / 离岛。
            DownloadCellularGate.UserAllowCellularAlways();
            // 蜂窝确认后不再叠 Wi‑Fi 确认窗（文案是「已连 Wi‑Fi」）。
            FireReady();
        }

        static void OnMobileWifiOnly()
        {
            DownloadCellularGate.UserRequireWifiOnly();
            _declinedCellularUntilWifi = true;
            OnCancelled();
        }

        static void ShowWifiConfirm()
        {
            _phase = Phase.WifiConfirm;
            string size = DownloadNetworkGate.FormatSizeMb(_banks);
            Log.Info(
                "[DownloadConsent] wifi confirm size=" + size + " banks=" + _banks.Count);
            // 禁止 UI 未就绪时静默 Ready（旧现网 defer→自动 Resume 会把 §4.6 吃掉）。
            if (GameEntry.UI == null)
            {
                Log.Warning(
                    "[DownloadConsent] wifi confirm deferred — GameEntry.UI null; " +
                    "waiting Bootstrap uncover patrol / next EnsureReady");
                return;
            }

            UiDialogService.ShowWifiDownloadConfirm(
                size,
                OnWifiConfirmDownload,
                OnWifiConfirmLater);
        }

        static void OnWifiConfirmDownload()
        {
            // BeginWifiConfirmed 可能已置位；幂等保留。
            _wifiConfirmAccepted = true;
            FireReady();
        }

        static void OnWifiConfirmLater()
        {
            Action wifiLater = _onWifiLater;
            _onWifiLater = null;
            wifiLater?.Invoke();
            OnCancelled();
        }

        static void FireReady()
        {
            Action ready = _onReady;
            _phase = Phase.Idle;
            _onReady = null;
            _onCancel = null;
            _onWifiLater = null;
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
            _onWifiLater = null;
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

        static bool IsWifiConfirmPresent()
        {
            if (GameEntry.UI == null)
                return false;
            return GameEntry.UI.HasUIForm(UiAssets.WifiConfirmForm);
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
