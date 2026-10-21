using System;
using UnityEngine;
using UnityEngine.UIElements;
using UnityGameFramework.Runtime;

namespace Game
{
    /// <summary>
    /// Save changes confirmation dialog — shown when closing an edit form with unsaved changes.
    /// </summary>
    public class SaveConfirmForm : GameFormLogic
    {
        public class UserData
        {
            public Action OnSave;
            public Action OnDiscard;
        }

        UserData _userData;

        protected override bool CloseWhenAnyButtonClicked => false;
        protected override bool SlidesUpFromBottom => true;

        public override void OnOpen(object userData)
        {
            base.OnOpen(userData);
            _userData = userData as UserData;

            var title = Root.Q<Label>("title");
            if (title != null) title.text = "是否保存修改？";

            var body = Root.Q<Label>("body");
            if (body != null) body.style.display = DisplayStyle.None;

            var icon = Root.Q<VisualElement>("icon");
            if (icon != null) icon.style.display = DisplayStyle.None;

            var primary = Root.Q<Button>("primary");
            if (primary != null)
            {
                var label = primary.Q<Label>("primaryLabel");
                if (label != null) label.text = "保存退出";
                OnClick("primary", () =>
                {
                    _userData?.OnSave?.Invoke();
                    CloseSelf();
                });
            }

            var secondary = Root.Q<Button>("secondary");
            if (secondary != null)
            {
                var label = secondary.Q<Label>("secondaryLabel");
                if (label != null) label.text = "放弃";
                OnClick("secondary", () =>
                {
                    _userData?.OnDiscard?.Invoke();
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
