using System;

namespace Game
{
    /// <summary>
    /// No network dialog — download path uses <see cref="DownloadDialogCopy.Offline"/>;
    /// login can override title / body / button.
    /// </summary>
    public class NoNetworkForm : GameFormLogic
    {
        public class UserData
        {
            public Action OnConfirm;
            public string Title;
            public string Body;
            public string PrimaryLabel;
        }

        UserData _userData;
        NoticeCardPresenter _card;

        protected override bool CloseWhenAnyButtonClicked => false;
        protected override bool SlidesUpFromBottom => false;

        public override void OnOpen(object userData)
        {
            base.OnOpen(userData);
            _userData = userData as UserData;
            _card = new NoticeCardPresenter();
            _card.Bind(Root);

            DownloadDialogCopy.DialogCopy copy = DownloadDialogCopy.Offline();
            string title = !string.IsNullOrEmpty(_userData?.Title) ? _userData.Title : copy.Title;
            string body = !string.IsNullOrEmpty(_userData?.Body) ? _userData.Body : copy.Body;
            string primary = !string.IsNullOrEmpty(_userData?.PrimaryLabel)
                ? _userData.PrimaryLabel
                : copy.PrimaryLabel;

            _card.Apply(new NoticeCardContent
            {
                Title = title,
                Body = body,
                Icon = NoticeCardIcon.Tip,
                PrimaryLabel = primary,
                ShowSecondary = false,
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
