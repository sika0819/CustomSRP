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
        protected override bool SlidesUpFromBottom => true;
        protected override bool CloseOnBackdropClick => true;
        protected override bool PinDocumentOrder => true;
        // 进度窗只提示；固墙拦住进区，开着仍可走开。
        protected override bool AllowsLocomotionWhileOpen => true;

        protected override void OnBackdropDismiss()
        {
            _userData?.OnCancel?.Invoke();
            CloseSelf();
        }

        public override void OnOpen(object userData)
        {
            base.OnOpen(userData);
            _userData = userData as UserData;
            int ticket = _userData != null ? _userData.Ticket : 0;
            if (UiDialogService.IsProgressRetired(ticket) || UiDialogService.IsProgressOpen(ticket))
            {
                // 票据已结束，或同一张进度窗还开着：关掉这张重复的，不要走 OnCancel。
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

            // Attach 内部若发现票已退役会 Dismiss（Close 早于异步 OnOpen）。
            UiDialogService.AttachProgressForm(this, ticket);
            if (UiDialogService.IsProgressRetired(ticket))
                return;
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
