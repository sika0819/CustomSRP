using System;
using UnityEngine;
using UnityEngine.UIElements;
using UnityGameFramework.Runtime;

namespace Game
{
    /// <summary>
    /// Pause tip dialog — shown when audio is interrupted by phone call etc.
    /// Supports two modes: brief (auto-hide) and long (requires dismiss).
    /// </summary>
    public class PauseTipForm : GameFormLogic
    {
        public enum TipMode
        {
            /// <summary>Brief interruption — auto-hides after a few seconds.</summary>
            Brief,
            /// <summary>Long-term audio lock — requires user dismiss.</summary>
            LongTerm,
        }

        public class UserData
        {
            public TipMode Mode = TipMode.Brief;
            public string Title = "音频中断";
            public string Body = "音频被其它应用占用，滑动或点击空白可继续播放。";
            public Action OnResume;
        }

        UserData _userData;
        float _autoHideSeconds = 5f;
        IVisualElementScheduledItem _autoHide;

        protected override bool CloseWhenAnyButtonClicked => false;
        protected override bool SlidesUpFromBottom => true;

        public override void OnOpen(object userData)
        {
            base.OnOpen(userData);
            _userData = userData as UserData;

            var title = Root.Q<Label>("title");
            if (title != null) title.text = _userData?.Title ?? "音频中断";

            var body = Root.Q<Label>("body");
            if (body != null)
                body.text = _userData?.Body ?? "音频被其它应用占用，滑动或点击空白可继续播放。";

            var icon = Root.Q<VisualElement>("icon");
            if (icon != null) icon.style.display = DisplayStyle.None;

            // Hide both buttons — Brief mode auto-hides; LongTerm uses backdrop to dismiss.
            var primary = Root.Q<Button>("primary");
            if (primary != null) primary.style.display = DisplayStyle.None;
            var secondary = Root.Q<Button>("secondary");
            if (secondary != null) secondary.style.display = DisplayStyle.None;

            if (_userData?.Mode == TipMode.Brief)
            {
                // Auto-hide after delay.
                _autoHide = Root.schedule.Execute(() =>
                {
                    _userData?.OnResume?.Invoke();
                    CloseSelf();
                }).StartingIn((long)(_autoHideSeconds * 1000f));
            }
        }

        public override void OnClose(bool isShutdown, object userData)
        {
            _autoHide?.Pause();
            _autoHide = null;
            _userData = null;
            base.OnClose(isShutdown, userData);
        }
    }
}
