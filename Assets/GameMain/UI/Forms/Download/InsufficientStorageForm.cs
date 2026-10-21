using System;
using UnityEngine;
using UnityEngine.UIElements;
using UnityGameFramework.Runtime;

namespace Game
{
    /// <summary>
    /// Insufficient storage dialog — shown when device storage is too low.
    /// </summary>
    public class InsufficientStorageForm : GameFormLogic
    {
        public class UserData
        {
            /// <summary>Required space in MB, e.g. "500MB".</summary>
            public string RequiredSpace = "500MB";
            public Action OnConfirm;
        }

        UserData _userData;

        protected override bool CloseWhenAnyButtonClicked => false;
        protected override bool SlidesUpFromBottom => true;

        public override void OnOpen(object userData)
        {
            base.OnOpen(userData);
            _userData = userData as UserData;

            var title = Root.Q<Label>("title");
            if (title != null) title.text = "存储空间不足";

            var body = Root.Q<Label>("body");
            if (body != null && _userData != null)
                body.text = $"需要预留至少 {_userData.RequiredSpace} 存储空间，请清理后重试。";

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

            // Hide secondary — single OK button.
            var secondary = Root.Q<Button>("secondary");
            if (secondary != null) secondary.style.display = DisplayStyle.None;
        }

        public override void OnClose(bool isShutdown, object userData)
        {
            _userData = null;
            base.OnClose(isShutdown, userData);
        }
    }
}
