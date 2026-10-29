using UnityEngine;
using UnityEngine.UIElements;

namespace Game
{
    public enum NoticeCardIcon
    {
        Cellular = 0,
        NeedDownload,
        Downloading,
        Tip,
    }

    /// <summary>
    /// 温馨提示 / 下载弹窗共用卡片的内容。点击仍由 <see cref="GameFormLogic"/> 注册。
    /// </summary>
    public sealed class NoticeCardContent
    {
        public string Title;
        public string Body;
        public bool ShowBody = true;
        public bool ShowIcon = true;
        public NoticeCardIcon Icon = NoticeCardIcon.Cellular;
        public string PrimaryLabel = "确定";
        public string SecondaryLabel = "取消";
        public bool ShowPrimary = true;
        public bool ShowSecondary = true;
        public bool ShowProgress;
    }

    /// <summary>
    /// 绑定一张 <c>NoticeCard</c>。进度条用 scale，文案按 0.1 秒刷新。
    /// </summary>
    public sealed class NoticeCardPresenter
    {
        const float TextInterval = 0.1f;
        const float MinVisual = 0.05f;
        const string ProgressClass = "notice-card--progress";
        const string IconDownload = "notice-card__icon--download";
        const string IconDownloading = "notice-card__icon--downloading";
        const string IconTip = "notice-card__icon--tip";

        VisualElement _card;
        VisualElement _icon;
        Label _title;
        Label _body;
        Button _primary;
        Button _secondary;
        VisualElement _fill;
        Label _sentence;
        Label _eta;
        Label _downloaded;
        Label _sizeDetail;

        bool _hasSample;
        float _sampleTime;
        float _sampleMb;
        float _speedMbps = -1f;
        float _nextTextTime = -1f;
        int _shownPercent = -1;
        int _shownDownloaded = -1;
        bool _downloadedApplied;
        bool _downloadedShown;

        public void Bind(VisualElement root)
        {
            Unbind();
            if (root == null)
                return;

            _card = root.Q(className: "notice-card");
            _icon = root.Q<VisualElement>("icon");
            _title = root.Q<Label>("title");
            _body = root.Q<Label>("body");
            _primary = root.Q<Button>("primary");
            _secondary = root.Q<Button>("secondary");
            _fill = root.Q<VisualElement>("progress-fill");
            _sentence = root.Q<Label>("sentence");
            _eta = root.Q<Label>("eta");
            _downloaded = root.Q<Label>("downloaded");
            _sizeDetail = root.Q<Label>("size-detail");

            if (_fill != null)
                _fill.style.transformOrigin = new TransformOrigin(Length.Percent(0), Length.Percent(50));
        }

        public void Unbind()
        {
            _card = null;
            _icon = null;
            _title = null;
            _body = null;
            _primary = null;
            _secondary = null;
            _fill = null;
            _sentence = null;
            _eta = null;
            _downloaded = null;
            _sizeDetail = null;
            ResetSamples();
        }

        public void Apply(NoticeCardContent content)
        {
            if (content == null)
                return;

            ResetSamples();

            if (_card != null)
            {
                if (content.ShowProgress)
                    _card.AddToClassList(ProgressClass);
                else
                    _card.RemoveFromClassList(ProgressClass);
            }

            if (_title != null)
            {
                _title.text = content.Title ?? string.Empty;
                _title.style.display = string.IsNullOrEmpty(content.Title)
                    ? DisplayStyle.None
                    : DisplayStyle.Flex;
            }

            if (_body != null)
            {
                _body.text = content.Body ?? string.Empty;
                bool showBody = content.ShowBody && !string.IsNullOrEmpty(content.Body) && !content.ShowProgress;
                _body.style.display = showBody ? DisplayStyle.Flex : DisplayStyle.None;
            }

            ApplyIcon(content);
            ApplyButton(_primary, "primaryLabel", content.ShowPrimary, content.PrimaryLabel);
            ApplyButton(_secondary, "secondaryLabel", content.ShowSecondary && !content.ShowProgress, content.SecondaryLabel);

            if (content.ShowProgress)
                SetProgress(0f, 0f, 0f);
        }

        /// <summary>
        /// percent 为 0–100。downloadedMb / totalMb 为 0 时只画百分比，剩余时间显示「剩余--」。
        /// </summary>
        public void SetProgress(float percent, float downloadedMb, float totalMb)
        {
            percent = Mathf.Clamp(percent, 0f, 100f);
            downloadedMb = Mathf.Max(0f, downloadedMb);
            totalMb = Mathf.Max(0f, totalMb);

            if (_fill != null)
            {
                float t = percent / 100f;
                float visual = t >= 1f ? 1f : Mathf.Max(t, MinVisual);
                _fill.style.scale = new Scale(new Vector2(visual, 1f));
            }

            float now = Time.realtimeSinceStartup;
            if (_hasSample)
            {
                float dt = now - _sampleTime;
                if (dt >= TextInterval)
                {
                    float delta = downloadedMb - _sampleMb;
                    if (delta >= 0f && dt > 0.0001f)
                        _speedMbps = delta / dt;
                    _sampleTime = now;
                    _sampleMb = downloadedMb;
                }
            }
            else
            {
                _hasSample = true;
                _sampleTime = now;
                _sampleMb = downloadedMb;
                _speedMbps = -1f;
            }

            bool completed = percent >= 100f;
            int shownPercent = Mathf.RoundToInt(percent);
            int shownDownloaded = Mathf.RoundToInt(downloadedMb);
            bool changed = shownPercent != _shownPercent || shownDownloaded != _shownDownloaded;
            if (!completed && !changed && _nextTextTime >= 0f && now < _nextTextTime)
                return;

            _shownPercent = shownPercent;
            _shownDownloaded = shownDownloaded;
            _nextTextTime = now + TextInterval;
            WriteTexts(percent, downloadedMb, totalMb, completed);
        }

        void WriteTexts(float percent, float downloadedMb, float totalMb, bool completed)
        {
            bool hasTotal = totalMb > 0.01f;
            if (_downloaded != null && (!_downloadedApplied || _downloadedShown != hasTotal))
            {
                _downloadedApplied = true;
                _downloadedShown = hasTotal;
                _downloaded.style.display = hasTotal ? DisplayStyle.Flex : DisplayStyle.None;
            }

            if (!hasTotal)
            {
                if (_sentence != null)
                    _sentence.text = string.Empty;
                if (_eta != null)
                    _eta.text = completed ? "完成" : "剩余--";
                if (_sizeDetail != null)
                    _sizeDetail.text = $"{Mathf.RoundToInt(percent)}%";
                return;
            }

            DownloadDialogCopy.ProgressCopy copy = DownloadDialogCopy.Progress(
                downloadedMb,
                totalMb,
                _speedMbps,
                completed);

            if (_sentence != null)
                _sentence.text = copy.Sentence ?? string.Empty;
            if (_eta != null)
                _eta.text = copy.Eta ?? string.Empty;
            if (_downloaded != null)
                _downloaded.text = copy.Downloaded ?? string.Empty;
            if (_sizeDetail != null)
                _sizeDetail.text = copy.SizeDetail ?? string.Empty;
        }

        void ApplyIcon(NoticeCardContent content)
        {
            if (_icon == null)
                return;

            _icon.RemoveFromClassList(IconDownload);
            _icon.RemoveFromClassList(IconDownloading);
            _icon.RemoveFromClassList(IconTip);
            if (!content.ShowIcon)
            {
                _icon.style.display = DisplayStyle.None;
                return;
            }

            _icon.style.display = DisplayStyle.Flex;
            switch (content.Icon)
            {
                case NoticeCardIcon.NeedDownload:
                    _icon.AddToClassList(IconDownload);
                    break;
                case NoticeCardIcon.Downloading:
                    _icon.AddToClassList(IconDownloading);
                    break;
                case NoticeCardIcon.Tip:
                    _icon.AddToClassList(IconTip);
                    break;
            }
        }

        static void ApplyButton(Button button, string labelName, bool show, string label)
        {
            if (button == null)
                return;

            Label text = button.Q<Label>(labelName);
            if (text != null)
                text.text = string.IsNullOrEmpty(label) ? string.Empty : label;
            button.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
        }

        void ResetSamples()
        {
            _hasSample = false;
            _sampleTime = 0f;
            _sampleMb = 0f;
            _speedMbps = -1f;
            _nextTextTime = -1f;
            _shownPercent = -1;
            _shownDownloaded = -1;
            _downloadedApplied = false;
        }
    }
}
