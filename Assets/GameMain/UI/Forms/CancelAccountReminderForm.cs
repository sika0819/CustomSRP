#if UNITY_EDITOR
using System;
using UnityEngine.UIElements;

namespace Game
{
    /// <summary>
    /// 编辑器联调：注销确认期弹窗。资源在 Editor/UI，不进 YooAsset 发包采集。
    /// </summary>
    public class CancelAccountReminderForm : GameFormLogic
    {
        public class UserData
        {
            public string Title = "注销提醒";
            public string Body =
                "当前账号申请了注销，目前处于确认期。如果你继续登录，注销流程将自动终止。";
            /// <summary>Login and stop the cancellation.</summary>
            public Action OnLoginAndStopCancel;
            /// <summary>Exit: clear session, stay on login.</summary>
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
                // 对齐原生 CancelConfirmDialog / HomeLoginCancelDialogView：「退出」清会话回登录。
                if (label != null) label.text = "退出";
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
#endif
