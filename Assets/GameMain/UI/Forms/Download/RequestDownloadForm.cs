using System;

namespace Game
{
    /// <summary>
    /// Download request dialog: shows resource size and download / return buttons.
    /// </summary>
    public class RequestDownloadForm : GameFormLogic
    {
        public class UserData
        {
            public string ResourceSize;
            public Action OnDownload;
            public Action OnReturn;
        }

        UserData _userData;
        NoticeCardPresenter _card;

        protected override bool CloseWhenAnyButtonClicked => false;
        // 尺寸写在 Instance 上。外面包一层没尺寸的盒子时，绝对定位的卡片会塌成 0，窗开着但看不见，摇杆也被锁住。
        protected override bool SlidesUpFromBottom => false;

        public override void OnOpen(object userData)
        {
            base.OnOpen(userData);
            _userData = userData as UserData;
            _card = new NoticeCardPresenter();
            _card.Bind(Root);

            DownloadDialogCopy.DialogCopy copy = DownloadDialogCopy.Request(
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
                _userData?.OnDownload?.Invoke();
                CloseSelf();
            });
            OnClick("secondary", () =>
            {
                _userData?.OnReturn?.Invoke();
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
