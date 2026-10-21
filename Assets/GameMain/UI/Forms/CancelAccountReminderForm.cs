using System;
using UnityEngine;
using UnityEngine.UIElements;
using UnityGameFramework.Runtime;

namespace Game
{
    /// <summary>
    /// Account cancellation reminder — shown when a cancelling account logs in.
    /// </summary>
    public class CancelAccountReminderForm : GameFormLogic
    {
        public class UserData
        {
            public string Title = "注销提醒";
            public string Body = "当前账号正在注销确认期内，登录将终止注销流程。";
            /// <summary>Login and stop the cancellation.</summary>
            public Action OnLoginAndStopCancel;
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
            if (title != null) title.text = _userData?.Title ?? "注销提醒";

            var body = Root.Q<Label>("body");
            if (body != null && _userData != null)
                body.text = _userData.Body;

            var icon = Root.Q<VisualElement>("icon");
            if (icon != null) icon.style.display = DisplayStyle.None;

            var primary = Root.Q<Button>("primary");
            if (primary != null)
            {
                var label = primary.Q<Label>("primaryLabel");
                if (label != null) label.text = "登录并终止注销";
                OnClick("primary", () =>
                {
                    _userData?.OnLoginAndStopCancel?.Invoke();
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
