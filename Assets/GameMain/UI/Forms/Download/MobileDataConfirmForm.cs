using System;

namespace Game
{
    /// <summary>
    /// Mobile data (cellular) confirmation dialog.
    /// Asks whether to continue downloading over mobile data.
    /// </summary>
    public class MobileDataConfirmForm : GameFormLogic
    {
        public class UserData
        {
            /// <summary>Resource size number, e.g. "500". Trailing MB/KB is stripped.</summary>
            public string ResourceSize = "500";
            public Action OnAllow;
            public Action OnWifiOnly;
        }

        UserData _userData;
        NoticeCardPresenter _card;

        protected override bool CloseWhenAnyButtonClicked => false;
        protected override bool SlidesUpFromBottom => true;
        protected override bool CloseOnBackdropClick => true;
        protected override bool PinDocumentOrder => true;

        protected override void OnBackdropDismiss()
        {
            _userData?.OnWifiOnly?.Invoke();
            CloseSelf();
        }

        public override void OnOpen(object userData)
        {
            base.OnOpen(userData);
            _userData = userData as UserData;
            _card = new NoticeCardPresenter();
            _card.Bind(Root);

            DownloadDialogCopy.DialogCopy copy = DownloadDialogCopy.Mobile(
                _userData != null ? _userData.ResourceSize : null);
            _card.Apply(new NoticeCardContent
            {
                Title = copy.Title,
                Body = copy.Body,
                Icon = NoticeCardIcon.Cellular,
                PrimaryLabel = copy.PrimaryLabel,
                SecondaryLabel = copy.SecondaryLabel,
                ShowSecondary = copy.ShowSecondary,
            });

            OnClick("primary", () =>
            {
                // 先回调再关：Allow 开闸后 Session phase 清空，避免关窗瞬间又叠一张。
                Action allow = _userData?.OnAllow;
                allow?.Invoke();
                CloseSelf();
            });
            OnClick("secondary", () =>
            {
                Action wifiOnly = _userData?.OnWifiOnly;
                wifiOnly?.Invoke();
                CloseSelf();
            });
        }

        public override void OnClose(bool isShutdown, object userData)
        {
            DownloadConsentSession.NotifyMobileDialogClosed();
            _card?.Unbind();
            _card = null;
            _userData = null;
            base.OnClose(isShutdown, userData);
        }
    }
}
