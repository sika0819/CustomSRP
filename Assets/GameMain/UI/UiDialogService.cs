using System;
using System.Collections.Generic;
using Game.Native;
using UnityEngine;

namespace Game
{
    /// <summary>
    /// Unified dialog service — single entry point for all popup/dialog/toast usage.
    /// Usage:
    /// <code>
    /// UiDialogService.ShowCommonDialog("标题", "正文", onConfirm: () => Debug.Log("OK"));
    /// int ticket = UiDialogService.OpenDownloadProgress();
    /// UiDialogService.SetDownloadProgress(ticket, 50f, 12f, 80f);
    /// </code>
    /// </summary>
    public static class UiDialogService
    {
        // Active progress form reference so callers can update progress.
        static DownloadProgressForm _activeProgressForm;
        static int _progressSeq;
        static int _activeProgressTicket;
        /// <summary>
        /// 已关闭的进度票。必须按票号单独退役——高水位会把更早仍在跑的区域门票一并废掉，
        /// 表现为进度条被 Critical 等后开窗关掉后无法再开，人却仍被门挡住。
        /// </summary>
        static readonly HashSet<int> _retiredTickets = new HashSet<int>();
        /// <summary>Open 已发出、OnOpen/Attach 还没到。传输完成时不能先退役。</summary>
        static readonly HashSet<int> _awaitingOpen = new HashSet<int>();
        /// <summary>传输已完成，但要等百分比画出、上滑结束再退役。</summary>
        static readonly HashSet<int> _heldClose = new HashSet<int>();
        static float _pendingPercent = -1f;
        static float _pendingDownloadedMb;
        static float _pendingTotalMb;
        /// <summary>有过一次真实进度推送。打开时写入的 0 不算。</summary>
        static bool _pendingExplicit;
        static string _lastRequestFailure;
        static float _lastRequestFailureTime;

        #region Toast / Tip（同一全局气泡 TipToastForm，勿在各 Form 自造）

        /// <summary>回岛仍静音时的滑动续播文案（粘滞 tip 直至续播成功）。</summary>
        public const string AudioInterruptedSwipeToast = "菠萝被暂停，滑动屏幕恢复";

        /// <summary>粘滞 tip 时长：直至 <see cref="DismissToast"/> / 续播成功，避免 3s 闪没。</summary>
        public const float AudioPauseTipStickySeconds = 3600f;

        /// <summary>
        /// 全局 tip/toast（同一套）。业务侧只调这里，禁止再包一层 ShowXxxToast / 直调 <see cref="UiTipToast"/>。
        /// </summary>
        public static void ShowToast(
            string message,
            float seconds = UiTipToast.DefaultSeconds,
            float anchoredY = UiTipToast.DefaultAnchoredY,
            bool error = false,
            bool aboveLoading = false)
        {
            UiTipToast.ShowMessage(message, seconds, anchoredY, error, aboveLoading);
        }

        /// <summary>
        /// 音频暂停 tip：按 TipKind 决定粘滞；到点弹窗场景由调用方勿调。
        /// </summary>
        public static void ShowAudioPauseTip(WanosAudio.MusicPauseUICallback.TipKind kind)
        {
            if (NowPlayingService.PausedForTimerEnd)
                return;

            float seconds = kind == WanosAudio.MusicPauseUICallback.TipKind.TransientInterrupt
                ? UiTipToast.DefaultSeconds
                : AudioPauseTipStickySeconds;
            ShowToast(AudioInterruptedSwipeToast, seconds);
        }

        /// <summary>立刻收起当前全局 tip。</summary>
        public static void DismissToast()
        {
            TipToastForm.DismissCurrent();
        }

        /// <summary>
        /// 启动期请求失败。气泡抬到加载罩之上，同一文案 8 秒内不重复，避免换票重试刷屏。
        /// </summary>
        public static void ShowRequestFailure(string reason)
        {
            if (string.IsNullOrWhiteSpace(reason))
                reason = "请求失败";
            float now = Time.realtimeSinceStartup;
            if (reason == _lastRequestFailure && now - _lastRequestFailureTime < 8f)
                return;
            _lastRequestFailure = reason;
            _lastRequestFailureTime = now;
            ShowToast(reason, error: true, aboveLoading: true);
        }

        #endregion

        #region Common Dialog

        /// <summary>
        /// Show a generic two-button dialog.
        /// </summary>
        public static void ShowCommonDialog(
            string title,
            string body,
            Action onConfirm = null,
            Action onCancel = null,
            string primaryLabel = "确定",
            string secondaryLabel = "取消")
        {
            var data = new CommonDialogForm.UserData
            {
                Title = title,
                Body = body,
                ShowPrimary = true,
                ShowSecondary = true,
                PrimaryLabel = primaryLabel,
                SecondaryLabel = secondaryLabel,
                OnPrimary = onConfirm,
                OnSecondary = onCancel,
            };
            UiAssets.OpenDialog(UiAssets.CommonDialogForm, data);
        }

        /// <summary>
        /// Show a single-button OK dialog.
        /// </summary>
        public static void ShowOkDialog(string title, string body, Action onOk = null)
        {
            var data = new CommonDialogForm.UserData
            {
                Title = title,
                Body = body,
                ShowPrimary = true,
                ShowSecondary = false,
                PrimaryLabel = "确定",
                OnPrimary = onOk,
            };
            UiAssets.OpenDialog(UiAssets.CommonDialogForm, data);
        }

        #endregion

        #region Download Dialogs

        /// <summary>
        /// Show the download request dialog.
        /// </summary>
        public static void ShowRequestDownload(
            string resourceSize,
            Action onDownload,
            Action onReturn = null)
        {
            var data = new RequestDownloadForm.UserData
            {
                ResourceSize = resourceSize,
                OnDownload = onDownload,
                OnReturn = onReturn,
            };
            UiAssets.OpenDialog(UiAssets.RequestDownloadForm, data);
        }

        /// <summary>
        /// Show download progress dialog. Returns the form if it opened synchronously.
        /// Prefer <see cref="SetDownloadProgress"/> with the ticket from
        /// <see cref="OpenDownloadProgress"/> — Yoo 加载是异步的，这里经常还是 null。
        /// </summary>
        public static DownloadProgressForm ShowDownloadProgress(Action onCancel = null)
        {
            OpenDownloadProgress(onCancel);
            return _activeProgressForm;
        }

        /// <summary>打开进度窗。返回的票据用来更新和关闭这一扇，避免关错后来的窗。</summary>
        public static int OpenDownloadProgress(Action onCancel = null)
        {
            int ticket = ++_progressSeq;
            _retiredTickets.Remove(ticket);
            _heldClose.Remove(ticket);
            _awaitingOpen.Add(ticket);
            _pendingPercent = 0f;
            _pendingDownloadedMb = 0f;
            _pendingTotalMb = 0f;
            _pendingExplicit = false;
            var data = new DownloadProgressForm.UserData
            {
                OnCancel = onCancel,
                Ticket = ticket,
            };
            UiAssets.OpenDialog(UiAssets.DownloadProgressForm, data);
            return ticket;
        }

        public static bool IsProgressRetired(int ticket)
        {
            return ticket > 0 && _retiredTickets.Contains(ticket);
        }

        public static bool IsProgressOpen(int ticket)
        {
            return ticket > 0
                && _activeProgressForm != null
                && _activeProgressForm.isActiveAndEnabled
                && _activeProgressTicket == ticket;
        }

        /// <summary>当前是否有任一进度窗挂在 Dialog 组（不要求票号）。</summary>
        public static bool HasActiveProgressForm()
        {
            return _activeProgressForm != null && _activeProgressForm.isActiveAndEnabled;
        }

        /// <summary>
        /// 同一票据再开进度窗。不重置进度，不把票据记成结束，也不新开下载。
        /// 这张已经开着并且还在显示，就什么都不做。
        /// 引用还在但物体已经关掉时清掉引用，否则再开会被当成重复窗立刻关掉。
        /// </summary>
        public static bool ReopenDownloadProgress(int ticket, Action onCancel = null)
        {
            if (ticket <= 0 || IsProgressRetired(ticket))
                return false;
            // 第一张还在异步加载：再开一张会叠成两扇，晚到的那张还会被当成重复窗关掉。
            if (_awaitingOpen.Contains(ticket))
                return false;
            if (_activeProgressForm != null && _activeProgressTicket == ticket)
            {
                if (_activeProgressForm.isActiveAndEnabled)
                    return false;
                _activeProgressForm = null;
                _activeProgressTicket = 0;
            }

            var data = new DownloadProgressForm.UserData
            {
                OnCancel = onCancel,
                Ticket = ticket,
            };
            _awaitingOpen.Add(ticket);
            UiAssets.OpenDialog(UiAssets.DownloadProgressForm, data);
            return true;
        }

        public static void AttachProgressForm(DownloadProgressForm form, int ticket)
        {
            if (ticket > 0 && IsProgressRetired(ticket))
            {
                // 异步 Open 晚于 Close：直接丢掉，避免闪一下再自关。
                if (form != null)
                    form.Dismiss();
                return;
            }

            _awaitingOpen.Remove(ticket);
            _activeProgressForm = form;
            _activeProgressTicket = ticket;
            // 只套真实推送。打开时的 0 不要进速度计，否则锚点被钉死。
            if (form != null && _pendingExplicit && _pendingPercent >= 0f)
                form.SetProgress(_pendingPercent, _pendingDownloadedMb, _pendingTotalMb);
            if (form != null && ticket > 0 && _heldClose.Contains(ticket))
                form.ArmTransferCompleteClose();
        }

        public static void DetachProgressForm(DownloadProgressForm form)
        {
            if (_activeProgressForm != form)
                return;
            int ticket = _activeProgressTicket;
            _activeProgressForm = null;
            _activeProgressTicket = 0;
            _awaitingOpen.Remove(ticket);
            // 传输完成的延迟关窗还没落到退役：人先关掉时把票结束，避免 MD5 期间再被打开。
            if (ticket > 0 && _heldClose.Contains(ticket) && !IsProgressRetired(ticket))
            {
                _retiredTickets.Add(ticket);
                _heldClose.Remove(ticket);
                _pendingPercent = -1f;
                _pendingExplicit = false;
            }
        }

        public static void SetDownloadProgress(
            int ticket,
            float percent,
            float downloadedMb = 0f,
            float totalMb = 0f)
        {
            if (ticket <= 0 || IsProgressRetired(ticket))
                return;
            _pendingExplicit = true;
            _pendingPercent = Mathf.Clamp(percent, 0f, 100f);
            _pendingDownloadedMb = Mathf.Max(0f, downloadedMb);
            _pendingTotalMb = Mathf.Max(0f, totalMb);
            if (_activeProgressTicket == ticket && _activeProgressForm != null)
                _activeProgressForm.SetProgress(_pendingPercent, _pendingDownloadedMb, _pendingTotalMb);
        }

        /// <summary>关掉这一张进度窗。下载器没有按 bank 取消，这里只关界面。</summary>
        public static void CloseDownloadProgress(int ticket)
        {
            if (ticket <= 0 || IsProgressRetired(ticket))
                return;

            DownloadProgressForm form = _activeProgressTicket == ticket ? _activeProgressForm : null;
            bool formOpen = form != null && form.isActiveAndEnabled;
            bool presented = formOpen && form.HasPresentedProgress;
            bool ready = formOpen && form.IsTransferCloseReady;
            if (SceneBankGateProgressPolicy.ShouldRetireOnTransferComplete(presented, ready))
            {
                RetireProgressTicket(ticket, dismissForm: true);
                return;
            }

            // 有真实进度还没画出来：等 OnOpen 套上，或等上滑结束 / 至少一帧。
            bool waitingToShow = _pendingExplicit && (formOpen || _awaitingOpen.Contains(ticket)) && !ready;
            if (waitingToShow)
            {
                _heldClose.Add(ticket);
                if (formOpen)
                    form.ArmTransferCompleteClose();
                return;
            }

            RetireProgressTicket(ticket, dismissForm: formOpen);
        }

        /// <summary>上滑结束（或至少过了一帧）后，由进度窗调用，这时才退役并关掉。</summary>
        public static void CompleteHeldDownloadClose(int ticket)
        {
            if (ticket <= 0 || IsProgressRetired(ticket) || !_heldClose.Contains(ticket))
                return;

            DownloadProgressForm form = _activeProgressTicket == ticket ? _activeProgressForm : null;
            bool wasPresented = form != null && form.HasPresentedProgress;
            if (form != null && !wasPresented && _pendingExplicit && _pendingPercent >= 0f)
                form.SetProgress(_pendingPercent, _pendingDownloadedMb, _pendingTotalMb);

            bool presented = form != null && form.HasPresentedProgress;
            // 刚在这次回调里套上百分比：再留可见时间，不能立刻关掉。
            if (presented && !wasPresented)
            {
                form.ArmTransferCompleteClose();
                return;
            }

            if (!SceneBankGateProgressPolicy.ShouldRetireOnTransferComplete(
                    presented, visibleLongEnough: true))
            {
                // 等完了仍然没有百分比：关掉，避免窗挂死。立刻关的路径不会走到这里。
                RetireProgressTicket(ticket, dismissForm: form != null);
                return;
            }

            RetireProgressTicket(ticket, dismissForm: true);
        }

        static void RetireProgressTicket(int ticket, bool dismissForm)
        {
            if (ticket <= 0)
                return;
            _retiredTickets.Add(ticket);
            _heldClose.Remove(ticket);
            _awaitingOpen.Remove(ticket);
            _pendingExplicit = false;
            DownloadProgressForm form = null;
            if (_activeProgressTicket == ticket)
            {
                _pendingPercent = -1f;
                form = _activeProgressForm;
            }

            if (dismissForm && form != null)
                form.Dismiss();
        }

        /// <summary>
        /// Show mobile data confirmation dialog.
        /// </summary>
        public static void ShowMobileDataConfirm(
            string resourceSize,
            Action onAllow,
            Action onWifiOnly = null)
        {
            var data = new MobileDataConfirmForm.UserData
            {
                ResourceSize = resourceSize,
                OnAllow = onAllow,
                OnWifiOnly = onWifiOnly,
            };
            UiAssets.OpenDialog(UiAssets.MobileDataConfirmForm, data);
        }

        /// <summary>
        /// Wi‑Fi 下载确认（AppLaunch 线上下载弹窗开关开启时）。
        /// </summary>
        public static void ShowWifiDownloadConfirm(
            string resourceSize,
            Action onDownload,
            Action onLater = null)
        {
            var data = new WifiConfirmForm.UserData
            {
                ResourceSize = resourceSize,
                OnDownload = onDownload,
                OnLater = onLater,
            };
            UiAssets.OpenDialog(UiAssets.WifiConfirmForm, data);
        }

        /// <summary>
        /// Show insufficient storage dialog.
        /// </summary>
        public static void ShowInsufficientStorage(
            string requiredSpace,
            Action onConfirm = null)
        {
            var data = new InsufficientStorageForm.UserData
            {
                RequiredSpace = requiredSpace,
                OnConfirm = onConfirm,
            };
            UiAssets.OpenDialog(UiAssets.InsufficientStorageForm, data);
        }

        /// <summary>
        /// Show no network dialog. Defaults to island download copy;
        /// pass title / body / primaryLabel to override (guest login quit path).
        /// </summary>
        public static void ShowNoNetwork(
            Action onConfirm = null,
            string title = null,
            string body = null,
            string primaryLabel = null)
        {
            var data = new NoNetworkForm.UserData
            {
                OnConfirm = onConfirm,
                Title = title,
                Body = body,
                PrimaryLabel = primaryLabel,
            };
            UiAssets.OpenDialog(UiAssets.NoNetworkForm, data);
        }

        #endregion

        #region Pause / Timer

        /// <summary>
        /// Show timer end dialog.
        /// </summary>
        public static void ShowTimerEnd(Action onCancel, Action onRepeat)
        {
            var data = new TimerEndForm.UserData
            {
                OnCancel = onCancel,
                OnRepeat = onRepeat,
            };
            UiAssets.OpenDialog(UiAssets.TimerEndForm, data);
        }

        #endregion

        #region Login / Account

        /// <summary>
        /// Show login agreement dialog.
        /// </summary>
        public static void ShowAgreement(
            Action onAgree,
            Action onDecline = null,
            string title = "用户协议及隐私保护",
            string body = "登录即表示您已阅读并同意《用户协议》和《隐私政策》")
        {
            var data = new AgreementForm.UserData
            {
                Title = title,
                Body = body,
                OnAgree = onAgree,
                OnDecline = onDecline,
            };
            UiAssets.OpenDialog(UiAssets.AgreementForm, data);
        }

#if UNITY_EDITOR
        /// <summary>
        /// 编辑器联调：注销确认期弹窗（资源在 Editor/UI，不进发包）。
        /// </summary>
        public static void ShowCancelAccountReminder(
            Action onLoginAndStopCancel,
            Action onCancel = null,
            string body = null)
        {
            var data = new CancelAccountReminderForm.UserData
            {
                OnLoginAndStopCancel = onLoginAndStopCancel,
                OnCancel = onCancel,
            };
            if (!string.IsNullOrEmpty(body))
                data.Body = body;
            UiAssets.OpenDialog(UiAssets.CancelAccountReminderForm, data);
        }
#endif

        #endregion

        #region Settings

        /// <summary>
        /// Show logout confirmation.
        /// </summary>
        public static void ShowLogoutConfirm(Action onConfirm, Action onCancel = null)
        {
            var data = new LogoutConfirmForm.UserData
            {
                OnConfirm = () =>
                {
                    HostIslandEntryGate.Leave();
                    onConfirm?.Invoke();
                },
                OnCancel = onCancel,
            };
            UiAssets.OpenDialog(UiAssets.LogoutConfirmForm, data);
        }

        /// <summary>
        /// Show save/discard confirmation for edit forms.
        /// </summary>
        public static void ShowSaveConfirm(Action onSave, Action onDiscard = null)
        {
            var data = new SaveConfirmForm.UserData
            {
                OnSave = onSave,
                OnDiscard = onDiscard,
            };
            UiAssets.OpenDialog(UiAssets.SaveConfirmForm, data);
        }

        #endregion
    }
}
