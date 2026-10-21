using System;
using UnityEngine;
using UnityEngine.UIElements;
using UnityGameFramework.Runtime;

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
            /// <summary>Resource size string, e.g. "500MB".</summary>
            public string ResourceSize = "500MB";
            public Action OnAllow;
            public Action OnWifiOnly;
        }

        UserData _userData;

        protected override bool CloseWhenAnyButtonClicked => false;
        protected override bool SlidesUpFromBottom => true;

        public override void OnOpen(object userData)
        {
            base.OnOpen(userData);
            _userData = userData as UserData;

            var title = Root.Q<Label>("title");
            if (title != null) title.text = "使用蜂窝数据";

            var body = Root.Q<Label>("body");
            if (body != null && _userData != null)
                body.text = $"需要下载约 {_userData.ResourceSize} 音频资源，当前使用移动数据，下载可能产生流量费用。";

            var icon = Root.Q<VisualElement>("icon");
            if (icon != null) icon.style.display = DisplayStyle.None;

            var primary = Root.Q<Button>("primary");
            if (primary != null)
            {
                var label = primary.Q<Label>("primaryLabel");
                if (label != null) label.text = "继续用流量下载";
                OnClick("primary", () =>
                {
                    _userData?.OnAllow?.Invoke();
                    CloseSelf();
                });
            }

            var secondary = Root.Q<Button>("secondary");
            if (secondary != null)
            {
                var label = secondary.Q<Label>("secondaryLabel");
                if (label != null) label.text = "仅 Wi-Fi";
                OnClick("secondary", () =>
                {
                    _userData?.OnWifiOnly?.Invoke();
                    CloseSelf();
                });
            }
        }

        public override void OnClose(bool isShutdown, object userData)
        {
            _userData = null;
            base.OnClose(isShutdown, userData);
        }
    }
}
