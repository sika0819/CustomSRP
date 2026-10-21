using System;
using UnityEngine;
using UnityEngine.UIElements;
using UnityGameFramework.Runtime;

namespace Game
{
    /// <summary>
    /// Login agreement / privacy dialog.
    /// Shows agreement text with links and Agree / Decline buttons.
    /// </summary>
    public class AgreementForm : GameFormLogic
    {
        public class UserData
        {
            public string Title = "用户协议及隐私保护";
            public string Body = "登录即表示您已阅读并同意《用户协议》和《隐私政策》";
            /// <summary>Called when user taps "我已阅读并同意".</summary>
            public Action OnAgree;
            /// <summary>Called when user taps "不同意" or closes the dialog.</summary>
            public Action OnDecline;
            /// <summary>Called when user taps the user agreement link.</summary>
            public Action OnOpenUserAgreement;
            /// <summary>Called when user taps the privacy policy link.</summary>
            public Action OnOpenPrivacyPolicy;
        }

        UserData _userData;

        protected override bool CloseWhenAnyButtonClicked => false;
        protected override bool SlidesUpFromBottom => false;

        public override void OnOpen(object userData)
        {
            base.OnOpen(userData);
            _userData = userData as UserData;

            var title = Root.Q<Label>("title");
            if (title != null) title.text = _userData?.Title ?? "用户协议及隐私保护";

            var body = Root.Q<Label>("body");
            if (body != null && _userData != null)
                body.text = _userData.Body;

            var icon = Root.Q<VisualElement>("icon");
            if (icon != null) icon.style.display = DisplayStyle.None;

            var primary = Root.Q<Button>("primary");
            if (primary != null)
            {
                var label = primary.Q<Label>("primaryLabel");
                if (label != null) label.text = "我已阅读并同意";
                OnClick("primary", () =>
                {
                    _userData?.OnAgree?.Invoke();
                    CloseSelf();
                });
            }

            var secondary = Root.Q<Button>("secondary");
            if (secondary != null)
            {
                var label = secondary.Q<Label>("secondaryLabel");
                if (label != null) label.text = "不同意";
                OnClick("secondary", () =>
                {
                    _userData?.OnDecline?.Invoke();
                    CloseSelf();
                });
            }

            // Backdrop click = decline.
            var rootEl = Root.Q("root") ?? Root;
            rootEl.RegisterCallback<ClickEvent>(evt =>
            {
                _userData?.OnDecline?.Invoke();
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
