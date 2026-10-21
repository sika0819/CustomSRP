using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game
{
    /// <summary>
    /// Download request dialog: shows resource size and download / return buttons.
    /// </summary>
    public class RequestDownloadForm : GameFormLogic
    {
        public class UserData
        {
            public string ResourceSize;  // e.g. "约 500MB"
            public Action OnDownload;
            public Action OnReturn;
        }

        UserData _userData;

        protected override bool CloseWhenAnyButtonClicked => false;
        protected override bool SlidesUpFromBottom => true;

        public override void OnOpen(object userData)
        {
            base.OnOpen(userData);
            _userData = userData as UserData;

            var body = Root.Q<Label>("body");
            if (body != null && _userData != null)
                body.text = $"需要下载约 {_userData.ResourceSize} 音频资源，当前使用移动数据，下载可能产生流量费用。";

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
            _userData = null;
            base.OnClose(isShutdown, userData);
        }
    }
}
