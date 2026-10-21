using System;
using UnityEngine;
using UnityEngine.UIElements;
using UnityGameFramework.Runtime;

namespace Game
{
    /// <summary>
    /// Download progress dialog with animated progress bar.
    /// </summary>
    public class DownloadProgressForm : GameFormLogic
    {
        public class UserData
        {
            public Action OnCancel;
        }

        UserData _userData;
        VisualElement _progressFill;
        Label _progressText;

        protected override bool CloseWhenAnyButtonClicked => false;
        protected override bool SlidesUpFromBottom => true;

        public override void OnOpen(object userData)
        {
            base.OnOpen(userData);
            _userData = userData as UserData;

            // Hide title/body/icon — progress bar is the focus.
            var title = Root.Q<Label>("title");
            if (title != null) title.style.display = DisplayStyle.None;
            var body = Root.Q<Label>("body");
            if (body != null) body.style.display = DisplayStyle.None;
            var icon = Root.Q<VisualElement>("icon");
            if (icon != null) icon.style.display = DisplayStyle.None;

            // Show progress elements.
            var track = Root.Q<VisualElement>("progress-track");
            if (track != null) track.style.display = DisplayStyle.Flex;
            _progressFill = Root.Q<VisualElement>("progress-fill");
            _progressText = Root.Q<Label>("progress-text");
            if (_progressText != null)
            {
                _progressText.style.display = DisplayStyle.Flex;
                _progressText.text = "0%";
            }

            // Hide secondary (no cancel in simple version), show primary as cancel.
            var primary = Root.Q<Button>("primary");
            if (primary != null)
            {
                var label = primary.Q<Label>("primaryLabel");
                if (label != null) label.text = "取消下载";
                OnClick("primary", () =>
                {
                    _userData?.OnCancel?.Invoke();
                    CloseSelf();
                });
            }
            var secondary = Root.Q<Button>("secondary");
            if (secondary != null) secondary.style.display = DisplayStyle.None;
        }

        /// <summary>
        /// Call this externally to update progress (0–100).
        /// </summary>
        public void SetProgress(float percent)
        {
            if (_progressFill != null)
                _progressFill.style.width = Length.Percent(Mathf.Clamp(percent, 0f, 100f));
            if (_progressText != null)
                _progressText.text = $"{(int)percent}%";
        }

        public override void OnClose(bool isShutdown, object userData)
        {
            _progressFill = null;
            _progressText = null;
            _userData = null;
            base.OnClose(isShutdown, userData);
        }
    }
}
