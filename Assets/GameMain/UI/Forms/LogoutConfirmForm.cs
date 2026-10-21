using System;
using UnityEngine;
using UnityEngine.UIElements;
using UnityGameFramework.Runtime;

namespace Game
{
    /// <summary>
    /// Logout confirmation dialog — shown before logging out.
    /// </summary>
    public class LogoutConfirmForm : GameFormLogic
    {
        public class UserData
        {
            public Action OnConfirm;
            public Action OnCancel;
        }

        UserData _userData;

        protected override bool CloseWhenAnyButtonClicked => false;
        protected override bool SlidesUpFromBottom => true;

        public override void OnOpen(object userData)
        {
            base.OnOpen(userData);
            _userData = userData as UserData;

            var title = Root.Q<Label>("title");
            if (title != null) title.text = "确定退出登录？";

            var body = Root.Q<Label>("body");
            if (body != null) body.style.display = DisplayStyle.None;

            var icon = Root.Q<VisualElement>("icon");
            if (icon != null) icon.style.display = DisplayStyle.None;

            var primary = Root.Q<Button>("primary");
            if (primary != null)
            {
                var label = primary.Q<Label>("primaryLabel");
                if (label != null) label.text = "确定";
                OnClick("primary", () =>
                {
                    _userData?.OnConfirm?.Invoke();
                    CloseSelf();
                });
            }

            var secondary = Root.Q<Button>("secondary");
            if (secondary != null)
            {
                var label = secondary.Q<Label>("secondaryLabel");
                if (label != null) label.text = "取消";
                OnClick("secondary", () =>
                {
                    _userData?.OnCancel?.Invoke();
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
