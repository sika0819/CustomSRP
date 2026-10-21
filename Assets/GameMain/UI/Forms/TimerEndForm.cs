using System;
using UnityEngine;
using UnityEngine.UIElements;
using UnityGameFramework.Runtime;

namespace Game
{
    /// <summary>
    /// Timer end dialog — shown when the island timer reaches zero.
    /// Cancel: resume playback without re-arming timer.
    /// Repeat: re-arm timer with the same duration.
    /// </summary>
    public class TimerEndForm : GameFormLogic
    {
        public class UserData
        {
            public Action OnCancel;
            public Action OnRepeat;
        }

        UserData _userData;

        protected override bool CloseWhenAnyButtonClicked => false;
        protected override bool SlidesUpFromBottom => true;

        public override void OnOpen(object userData)
        {
            base.OnOpen(userData);
            _userData = userData as UserData;

            var title = Root.Q<Label>("title");
            if (title != null) title.text = "定时时间到了~";

            var body = Root.Q<Label>("body");
            if (body != null) body.style.display = DisplayStyle.None;

            var icon = Root.Q<VisualElement>("icon");
            if (icon != null) icon.style.display = DisplayStyle.None;

            var primary = Root.Q<Button>("primary");
            if (primary != null)
            {
                var label = primary.Q<Label>("primaryLabel");
                if (label != null) label.text = "重复定时";
                OnClick("primary", () =>
                {
                    _userData?.OnRepeat?.Invoke();
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
