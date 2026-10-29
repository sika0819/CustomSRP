using System;

namespace Game
{
    /// <summary>
    /// Insufficient storage dialog — shown when device storage is too low.
    /// </summary>
    public class InsufficientStorageForm : GameFormLogic
    {
        public class UserData
        {
            /// <summary>Required space number, e.g. "500". Trailing MB/KB is stripped.</summary>
            public string RequiredSpace = "500";
            public Action OnConfirm;
        }

        UserData _userData;
        NoticeCardPresenter _card;

        protected override bool CloseWhenAnyButtonClicked => false;
        protected override bool SlidesUpFromBottom => false;
        protected override bool CloseOnBackdropClick => true;
        protected override bool PinDocumentOrder => true;

        protected override void OnBackdropDismiss()
        {
            _userData?.OnConfirm?.Invoke();
            CloseSelf();
        }

        public override void OnOpen(object userData)
        {
            base.OnOpen(userData);
            _userData = userData as UserData;
            _card = new NoticeCardPresenter();
            _card.Bind(Root);

            DownloadDialogCopy.DialogCopy copy = DownloadDialogCopy.Storage(
                _userData != null ? _userData.RequiredSpace : null);
            _card.Apply(new NoticeCardContent
            {
                Title = copy.Title,
                Body = copy.Body,
                Icon = NoticeCardIcon.Tip,
                PrimaryLabel = copy.PrimaryLabel,
                ShowSecondary = copy.ShowSecondary,
            });

            OnClick("primary", () =>
            {
                _userData?.OnConfirm?.Invoke();
                CloseSelf();
            });
        }

        public override void OnClose(bool isShutdown, object userData)
        {
            _card?.Unbind();
            _card = null;
            _userData = null;
            base.OnClose(isShutdown, userData);
        }
    }
}
