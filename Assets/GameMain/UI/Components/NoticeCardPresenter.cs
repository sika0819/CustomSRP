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
    /// 绑定一张 <c>NoticeCard</c>。进度条即时刷新；速度 / 剩余走
    /// <see cref="DownloadProgressDisplayMeter"/>（短窗首样 + 约 1s 提交 + EMA）。
    /// </summary>
    public sealed class NoticeCardPresenter
    {
        /// <summary>有真实进度后给 9-slice 的最小可视宽度（准备态为 0）。</summary>
        const float MinVisualAfterProgress = 0.02f;
        const float PreparingPercent = 0.5f;
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

        readonly DownloadProgressDisplayMeter _meter = new DownloadProgressDisplayMeter();
        int _shownPercent = -1;
        int _shownDownloaded = -1;
        int _shownTotal = -1;
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
            {
                // 未出速度前留空。不要用 0MB 去 Tick：那会把锚点钉死，
                // 紧接着的真实已下载量会被算进 0.15s，剩余每次打开都是「剩余2s」。
                // 第一次真实 SetProgress 才是速度锚点。
                if (_eta != null)
                    _eta.text = string.Empty;
                if (_sentence != null)
                    _sentence.text = string.Empty;
                if (_downloaded != null)
                {
                    _downloaded.text = string.Empty;
                    _downloaded.style.display = DisplayStyle.None;
                }
                if (_sizeDetail != null)
                    _sizeDetail.text = string.Empty;
                if (_fill != null)
                    _fill.style.width = Length.Percent(0f);
            }
        }

        /// <summary>
        /// percent 为 0–100。进度条 / MB 可随推送更新；
        /// 速度始终展示（未知为「速度--MB/秒」），与剩余约 1s 提交一次；
        /// 无速度时 ETA 留空，已公布后无速度则保持上次剩余。
        /// </summary>
        public void SetProgress(float percent, float downloadedMb, float totalMb)
        {
            percent = Mathf.Clamp(percent, 0f, 100f);
            downloadedMb = Mathf.Max(0f, downloadedMb);
            totalMb = Mathf.Max(0f, totalMb);

            bool completed = percent >= 100f;
            bool commitSpeedEta = _meter.Tick(Time.realtimeSinceStartup, downloadedMb, completed);
            bool preparing = IsPreparing(percent);

            if (_fill != null)
            {
                float t = percent / 100f;
                float visual;
                if (t >= 1f)
                    visual = 1f;
                else if (preparing || t <= 0f)
                    visual = 0f;
                else
                    visual = Mathf.Max(t, MinVisualAfterProgress);
                // 宽度配合 9-slice，圆角不会被压扁。单条进度，和音量条一样写 width。
                _fill.style.width = Length.Percent(visual * 100f);
            }

            int shownPercent = Mathf.RoundToInt(percent);
            int shownDownloaded = Mathf.RoundToInt(downloadedMb);
            int shownTotal = Mathf.RoundToInt(totalMb);
            bool sizeChanged = shownPercent != _shownPercent
                || shownDownloaded != _shownDownloaded
                || shownTotal != _shownTotal;
            if (!completed && !sizeChanged && !commitSpeedEta)
                return;

            _shownPercent = shownPercent;
            _shownDownloaded = shownDownloaded;
            _shownTotal = shownTotal;
            WriteTexts(percent, downloadedMb, totalMb, completed, commitSpeedEta);
        }

        bool IsPreparing(float percent) =>
            percent < PreparingPercent && _meter.DisplaySpeedMbps <= 0.001f;

        void WriteTexts(
            float percent,
            float downloadedMb,
            float totalMb,
            bool completed,
            bool commitSpeedEta)
        {
            float speedMbps = _meter.DisplaySpeedMbps;
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
                {
                    if (completed)
                        _eta.text = "完成";
                    else if (!_meter.HasPublishedEta)
                        _eta.text = string.Empty;
                }

                if (_sizeDetail != null)
                {
                    // DisplaySpeed 只在 commit 时变；百分比可跟手。
                    string speedText = speedMbps > 0.001f
                        ? speedMbps.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)
                        : "--";
                    _sizeDetail.text = $"{Mathf.RoundToInt(percent)}%  速度{speedText}MB/秒";
                }
                return;
            }

            // DisplaySpeed 只在 commit 时更新，MB 跟手时速度数字保持不动。
            DownloadDialogCopy.ProgressCopy copy = DownloadDialogCopy.Progress(
                downloadedMb,
                totalMb,
                speedMbps,
                completed);

            if (_sentence != null)
                _sentence.text = copy.Prefix ?? string.Empty;
            if (_downloaded != null)
                _downloaded.text = copy.Downloaded ?? string.Empty;
            if (_sizeDetail != null)
                _sizeDetail.text = copy.SizeDetail ?? string.Empty;

            if (_eta == null)
                return;

            if (completed)
            {
                _eta.text = "完成";
                return;
            }

            // 尚未公布且非 commit 帧：留空。commit 帧问 Meter；空串且已公布则保留上次。
            if (!_meter.HasPublishedEta && !commitSpeedEta)
            {
                _eta.text = string.Empty;
                return;
            }

            if (!commitSpeedEta)
                return;

            string eta = _meter.ResolveEta(downloadedMb, totalMb, completed);
            if (!string.IsNullOrEmpty(eta))
                _eta.text = eta;
            else if (!_meter.HasPublishedEta)
                _eta.text = string.Empty;
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
            {
                text.text = string.IsNullOrEmpty(label) ? string.Empty : label;
                // 字在按钮上面。字自己吃点击时，父按钮的按下收不到。
                text.pickingMode = PickingMode.Ignore;
            }

            button.pickingMode = PickingMode.Position;
            button.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
        }

        void ResetSamples()
        {
            _meter.Reset();
            _shownPercent = -1;
            _shownDownloaded = -1;
            _shownTotal = -1;
            _downloadedApplied = false;
            _downloadedShown = false;
        }
    }
}
