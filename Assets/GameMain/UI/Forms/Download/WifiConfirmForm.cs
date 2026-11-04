using System;

namespace Game
{
    /// <summary>
    /// 后台 Rest Wi‑Fi 确认（AppLaunch 线上下载弹窗开关开启时）——未关过必须先显示。
    /// 「立即下载」继续门禁链；「稍后」/ 蒙层关闭后：不再弹，可传则静默 Rest。
    /// </summary>
    public class WifiConfirmForm : GameFormLogic
    {
        public class UserData
        {
            /// <summary>Resource size number, e.g. "500". Trailing MB/KB is stripped.</summary>
            public string ResourceSize = "500";
            public Action OnDownload;
            public Action OnLater;
        }

        UserData _userData;
        NoticeCardPresenter _card;

        protected override bool CloseWhenAnyButtonClicked => false;
        protected override bool SlidesUpFromBottom => true;
        protected override bool CloseOnBackdropClick => true;
        protected override bool PinDocumentOrder => true;

        protected override void OnBackdropDismiss()
        {
            Action later = _userData?.OnLater;
            CloseSelfImmediate();
            later?.Invoke();
        }

        public override void OnOpen(object userData)
        {
            base.OnOpen(userData);
            _userData = userData as UserData;
            _card = new NoticeCardPresenter();
            _card.Bind(Root);

            DownloadDialogCopy.DialogCopy copy = DownloadDialogCopy.Wifi(
                _userData != null ? _userData.ResourceSize : null);
            _card.Apply(new NoticeCardContent
            {
                Title = copy.Title,
                Body = copy.Body,
                Icon = NoticeCardIcon.NeedDownload,
                PrimaryLabel = copy.PrimaryLabel,
                SecondaryLabel = copy.SecondaryLabel,
                ShowSecondary = copy.ShowSecondary,
            });

            OnClick("primary", () =>
            {
                // 与 RequestDownloadForm 对齐：先离 phase、再同步关窗、最后回调。
                // 回调里会 OpenDownloadProgress；若仍叠在未关的 §4.6 上，
                // 流量→Wi‑Fi 后走区域门时进度窗偶发不 Attach。
                DownloadConsentSession.BeginWifiConfirmed();
                Action download = _userData?.OnDownload;
                CloseSelfImmediate();
                download?.Invoke();
            });
            OnClick("secondary", () =>
            {
                Action later = _userData?.OnLater;
                CloseSelfImmediate();
                later?.Invoke();
            });
        }

        public override void OnClose(bool isShutdown, object userData)
        {
            DownloadConsentSession.NotifyWifiDialogClosed();
            _card?.Unbind();
            _card = null;
            _userData = null;
            base.OnClose(isShutdown, userData);
        }
    }
}
