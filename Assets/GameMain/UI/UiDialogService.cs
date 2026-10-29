using System;
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
        static int _retiredThrough;
        static float _pendingPercent = -1f;
        static float _pendingDownloadedMb;
        static float _pendingTotalMb;
        static string _lastRequestFailure;
        static float _lastRequestFailureTime;

        #region Toast

        /// <summary>
        /// Show a brief info toast on the global tip layer.
        /// </summary>
        public static void ShowToast(string message)
        {
            UiTipToast.ShowMessage(message, 3f);
        }

        /// <summary>
        /// Show an error toast (red-tinted bubble) on the global tip layer.
        /// </summary>
        public static void ShowErrorToast(string message)
        {
            UiTipToast.ShowMessage(message, 4f, error: true);
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
            UiTipToast.ShowMessage(reason, 5f, error: true, aboveLoading: true);
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
            _pendingPercent = 0f;
            _pendingDownloadedMb = 0f;
            _pendingTotalMb = 0f;
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
            return ticket > 0 && ticket <= _retiredThrough;
        }

        public static bool IsProgressOpen(int ticket)
        {
            return ticket > 0
                && _activeProgressForm != null
                && _activeProgressTicket == ticket;
        }

        /// <summary>
        /// 同一票据再开进度窗。不重置进度，不把票据记成结束，也不新开下载。
        /// 这张已经开着并且还在显示，就什么都不做。
        /// 引用还在但物体已经关掉时清掉引用，否则再开会被当成重复窗立刻关掉。
        /// </summary>
        public static bool ReopenDownloadProgress(int ticket, Action onCancel = null)
        {
            if (ticket <= 0 || ticket <= _retiredThrough)
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
            UiAssets.OpenDialog(UiAssets.DownloadProgressForm, data);
            return true;
        }

        public static void AttachProgressForm(DownloadProgressForm form, int ticket)
        {
            _activeProgressForm = form;
            _activeProgressTicket = ticket;
            if (form != null && _pendingPercent >= 0f)
                form.SetProgress(_pendingPercent, _pendingDownloadedMb, _pendingTotalMb);
        }

        public static void DetachProgressForm(DownloadProgressForm form)
        {
            if (_activeProgressForm != form)
                return;
            _activeProgressForm = null;
            _activeProgressTicket = 0;
        }

        public static void SetDownloadProgress(
            int ticket,
            float percent,
            float downloadedMb = 0f,
            float totalMb = 0f)
        {
            if (ticket <= 0 || ticket <= _retiredThrough)
                return;
            _pendingPercent = Mathf.Clamp(percent, 0f, 100f);
            _pendingDownloadedMb = Mathf.Max(0f, downloadedMb);
            _pendingTotalMb = Mathf.Max(0f, totalMb);
            if (_activeProgressTicket == ticket && _activeProgressForm != null)
                _activeProgressForm.SetProgress(_pendingPercent, _pendingDownloadedMb, _pendingTotalMb);
        }

        /// <summary>关掉这一张进度窗。下载器没有按 bank 取消，这里只关界面。</summary>
        public static void CloseDownloadProgress(int ticket)
        {
            if (ticket <= 0)
                return;
            if (ticket > _retiredThrough)
                _retiredThrough = ticket;
            if (_activeProgressTicket == ticket)
                _pendingPercent = -1f;
            if (_activeProgressTicket == ticket && _activeProgressForm != null)
                _activeProgressForm.Dismiss();
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
        /// Show pause tip dialog. Auto-hides after a few seconds by default.
        /// </summary>
        public static void ShowPauseTip(
            PauseTipForm.TipMode mode = PauseTipForm.TipMode.Brief,
            string title = null,
            string body = null,
            Action onResume = null)
        {
            var data = new PauseTipForm.UserData
            {
                Mode = mode,
                Title = title ?? "音频中断",
                Body = body ?? "音频被其它应用占用，滑动或点击空白可继续播放。",
                OnResume = onResume,
            };
            UiAssets.OpenDialog(UiAssets.PauseTipForm, data);
        }

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
