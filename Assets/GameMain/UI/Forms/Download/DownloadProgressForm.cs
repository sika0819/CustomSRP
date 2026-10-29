using System;

namespace Game
{
    /// <summary>
    /// Download progress dialog. Closing the card does not stop the downloader.
    /// </summary>
    public class DownloadProgressForm : GameFormLogic
    {
        public class UserData
        {
            public Action OnCancel;
            public int Ticket;
        }

        UserData _userData;
        NoticeCardPresenter _card;

        protected override bool CloseWhenAnyButtonClicked => false;
        protected override bool SlidesUpFromBottom => false;

        public override void OnOpen(object userData)
        {
            base.OnOpen(userData);
            _userData = userData as UserData;
            int ticket = _userData != null ? _userData.Ticket : 0;
            if (UiDialogService.IsProgressRetired(ticket))
            {
                if (Root != null)
                    Root.schedule.Execute(Dismiss);
                return;
            }

            _card = new NoticeCardPresenter();
            _card.Bind(Root);
            _card.Apply(new NoticeCardContent
            {
                Title = "场景资源加载中...",
                ShowBody = false,
                Icon = NoticeCardIcon.Downloading,
                PrimaryLabel = "我知道了",
                ShowSecondary = false,
                ShowProgress = true,
            });

            OnClick("primary", () =>
            {
                _userData?.OnCancel?.Invoke();
                CloseSelf();
            });

            UiDialogService.AttachProgressForm(this, ticket);
        }

        public void Dismiss()
        {
            CloseSelfImmediate();
        }

        /// <summary>
        /// percent 为 0–100。体积未知时 downloadedMb 与 totalMb 传 0。
        /// </summary>
        public void SetProgress(float percent, float downloadedMb = 0f, float totalMb = 0f)
        {
            _card?.SetProgress(percent, downloadedMb, totalMb);
        }

        public override void OnClose(bool isShutdown, object userData)
        {
            UiDialogService.DetachProgressForm(this);
            _card?.Unbind();
            _card = null;
            _userData = null;
            base.OnClose(isShutdown, userData);
        }
    }
}
