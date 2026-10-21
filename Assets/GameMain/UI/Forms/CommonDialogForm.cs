using System;
using UnityEngine;
using UnityEngine.UIElements;
using UnityGameFramework.Runtime;

namespace Game
{
    /// <summary>
    /// Parameterized dialog form backed by CommonDialog.uxml.
    /// Supports: title, body, optional icon, optional primary/secondary buttons,
    /// optional progress bar.
    /// </summary>
    public class CommonDialogForm : GameFormLogic
    {
        public class UserData
        {
            public string Title;
            public string Body;
            public string IconPath;
            public bool ShowIcon;
            public string PrimaryLabel;
            public string SecondaryLabel;
            public bool ShowPrimary;
            public bool ShowSecondary;
            public bool ShowProgress;
            public Action OnPrimary;
            public Action OnSecondary;
        }

        VisualElement _icon;
        Label _title;
        Label _body;
        Button _primary;
        Button _secondary;
        VisualElement _progressTrack;
        VisualElement _progressFill;
        Label _progressText;

        UserData _userData;

        protected override bool CloseWhenAnyButtonClicked => false;
        protected override bool SlidesUpFromBottom => true;

        public override void OnOpen(object userData)
        {
            base.OnOpen(userData);

            _userData = userData as UserData;

            _icon = Root.Q<VisualElement>("icon");
            _title = Root.Q<Label>("title");
            _body = Root.Q<Label>("body");
            _primary = Root.Q<Button>("primary");
            _secondary = Root.Q<Button>("secondary");
            _progressTrack = Root.Q<VisualElement>("progress-track");
            _progressFill = Root.Q<VisualElement>("progress-fill");
            _progressText = Root.Q<Label>("progress-text");

            if (_userData != null)
                ApplyUserData(_userData);
        }

        void ApplyUserData(UserData data)
        {
            if (_title != null)
            {
                _title.text = data.Title ?? string.Empty;
                _title.style.display = string.IsNullOrEmpty(data.Title)
                    ? DisplayStyle.None : DisplayStyle.Flex;
            }

            if (_body != null)
            {
                _body.text = data.Body ?? string.Empty;
                _body.style.display = string.IsNullOrEmpty(data.Body)
                    ? DisplayStyle.None : DisplayStyle.Flex;
            }

            if (_icon != null)
            {
                if (data.ShowIcon && !string.IsNullOrEmpty(data.IconPath))
                {
                    _icon.style.backgroundImage = new StyleBackground(
                        Resources.Load<Texture2D>(data.IconPath));
                    _icon.style.display = DisplayStyle.Flex;
                }
                else
                {
                    _icon.style.display = DisplayStyle.None;
                }
            }

            if (_primary != null)
            {
                var label = _primary.Q<Label>("primaryLabel");
                if (label != null)
                    label.text = data.PrimaryLabel ?? "确定";

                if (data.ShowPrimary)
                {
                    _primary.style.display = DisplayStyle.Flex;
                    OnClick("primary", () =>
                    {
                        _userData?.OnPrimary?.Invoke();
                        CloseSelf();
                    });
                }
                else
                {
                    _primary.style.display = DisplayStyle.None;
                }
            }

            if (_secondary != null)
            {
                var label = _secondary.Q<Label>("secondaryLabel");
                if (label != null)
                    label.text = data.SecondaryLabel ?? "取消";

                if (data.ShowSecondary)
                {
                    _secondary.style.display = DisplayStyle.Flex;
                    OnClick("secondary", () =>
                    {
                        _userData?.OnSecondary?.Invoke();
                        CloseSelf();
                    });
                }
                else
                {
                    _secondary.style.display = DisplayStyle.None;
                }
            }

            if (_progressTrack != null)
                _progressTrack.style.display = data.ShowProgress ? DisplayStyle.Flex : DisplayStyle.None;
            if (_progressText != null)
                _progressText.style.display = data.ShowProgress ? DisplayStyle.Flex : DisplayStyle.None;
        }

        /// <summary>
        /// Update download progress (0–100). Call from outside while the form is open.
        /// </summary>
        public void SetProgress(float percent, string text = null)
        {
            if (_progressFill == null)
                return;
            _progressFill.style.width = Length.Percent(Mathf.Clamp(percent, 0f, 100f));
            if (_progressText != null && text != null)
                _progressText.text = text;
        }

        public override void OnClose(bool isShutdown, object userData)
        {
            _icon = null;
            _title = null;
            _body = null;
            _primary = null;
            _secondary = null;
            _progressTrack = null;
            _progressFill = null;
            _progressText = null;
            _userData = null;
            base.OnClose(isShutdown, userData);
        }
    }
}
