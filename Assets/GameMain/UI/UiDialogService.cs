using System;

namespace Game
{
    /// <summary>
    /// Unified dialog service — single entry point for all popup/dialog/toast usage.
    /// Usage:
    /// <code>
    /// UiDialogService.ShowCommonDialog("标题", "正文", onConfirm: () => Debug.Log("OK"));
    /// UiDialogService.ShowDownloadProgress(0f);  // then call SetProgress(50f) to update
    /// </code>
    /// </summary>
    public static class UiDialogService
    {
        // Active progress form reference so callers can update progress.
        static DownloadProgressForm _activeProgressForm;
        static int _activeProgressSerial;

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
        /// Show download progress dialog. Returns the form reference for SetProgress.
        /// </summary>
        public static DownloadProgressForm ShowDownloadProgress(Action onCancel = null)
        {
            var data = new DownloadProgressForm.UserData
            {
                OnCancel = onCancel,
            };
            // We open the form and capture its instance via OnOpen callback.
            // The form stores itself in _activeProgressForm on open.
            UiAssets.OpenDialog(UiAssets.DownloadProgressForm, data);
            return _activeProgressForm;
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
        /// Show no network dialog.
        /// </summary>
        public static void ShowNoNetwork(Action onConfirm = null)
        {
            var data = new NoNetworkForm.UserData
            {
                OnConfirm = onConfirm,
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
                    BoluoLogout.ExecuteLeave();
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
