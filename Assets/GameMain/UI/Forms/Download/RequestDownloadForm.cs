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
        // 尺寸必须写在模板实例上。再套一层盒子时，中间的 TemplateContainer 没有尺寸，绝对定位的卡片会塌成 0，白底 panel 消失，摇杆也被锁住。
        protected override bool SlidesUpFromBottom => false;
        protected override bool CloseOnBackdropClick => true;
        protected override bool PinDocumentOrder => true;

        protected override void OnBackdropDismiss()
        {
            _userData?.OnReturn?.Invoke();
            CloseSelf();
        }

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
