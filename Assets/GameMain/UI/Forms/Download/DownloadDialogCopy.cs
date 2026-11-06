using System.Globalization;

namespace Game
{
    /// <summary>
    /// 岛内下载弹层文案。不依赖 UI，方便单元测试锁住整句。
    /// </summary>
    public static class DownloadDialogCopy
    {
        /// <summary>岛内无网下载提示（toast，非弹窗）。</summary>
        public const string OfflineToast = "网络异常";

        public sealed class DialogCopy
        {
            public string Title;
            public string Body;
            public string PrimaryLabel;
            public string SecondaryLabel;
            public bool ShowSecondary;
        }

        public sealed class ProgressCopy
        {
            public string Prefix;
            public string Sentence;
            public string Eta;
            public string Downloaded;
            public string SizeDetail;
        }

        public static DialogCopy Request(string size)
        {
            size = NormalizeSize(size);
            return new DialogCopy
            {
                Title = "前方区域未下载",
                Body = $"此区域需要额外下载资源包（约{size}MB），下载后即可进入并体验完整内容。",
                PrimaryLabel = "立即下载",
                SecondaryLabel = "返回",
                ShowSecondary = true,
            };
        }

        public static DialogCopy Mobile(string size)
        {
            size = NormalizeSize(size, fallback: "500");
            return new DialogCopy
            {
                Title = "正在使用蜂窝数据",
                Body = $"需要下载约 {size}MB 音频资源，当前使用移动数据，下载可能产生流量费用。",
                PrimaryLabel = "继续下载",
                SecondaryLabel = "切换Wi-Fi后下载",
                ShowSecondary = true,
            };
        }

        /// <summary>
        /// Wi‑Fi 下载确认（AppLaunch <c>feature.cancelAccount == 1</c>）。
        /// </summary>
        public static DialogCopy Wifi(string size)
        {
            size = NormalizeSize(size, fallback: "若干");
            return new DialogCopy
            {
                Title = "温馨提示",
                Body = $"需要下载约 {size} MB音频资源，当前已连接Wi-Fi，可放心下载",
                PrimaryLabel = "立即下载",
                SecondaryLabel = "稍后",
                ShowSecondary = true,
            };
        }

        public static DialogCopy Storage(string size)
        {
            size = NormalizeSize(size, fallback: "500");
            return new DialogCopy
            {
                Title = "温馨提示",
                Body = $"设备存储空间不足，需至少预留{size}MB空间才能下载，建议清理存储空间后重试",
                PrimaryLabel = "我知道了",
                ShowSecondary = false,
            };
        }

        public static DialogCopy Offline()
        {
            return new DialogCopy
            {
                Title = "无网络连接",
                Body = "当前设备无网络，无法下载资源包，请开启网络后继续",
                PrimaryLabel = "我知道了",
                ShowSecondary = false,
            };
        }

        /// <summary>
        /// 前缀 + 已下载数字 + 体积/速度，拼成整句。
        /// 速度用 0.#（一位小数，少闪），未知时为「速度--MB/秒」；无速度时 Eta 为空；
        /// 完成显示「完成」；超过 60 秒用「剩余Nm」，封顶 60。
        /// 弹窗实际剩余以 <see cref="DownloadProgressDisplayMeter"/> 为准。
        /// </summary>
        public static ProgressCopy Progress(
            float downloadedMb,
            float totalMb,
            float speedMbps,
            bool completed = false)
        {
            int downloaded = UnityEngine.Mathf.RoundToInt(UnityEngine.Mathf.Max(0f, downloadedMb));
            int total = UnityEngine.Mathf.RoundToInt(UnityEngine.Mathf.Max(0f, totalMb));
            string speedText = speedMbps > 0.001f
                ? speedMbps.ToString("0.#", CultureInfo.InvariantCulture)
                : "--";

            const string Lead = "正在为你下载资源：";
            string prefix;
            string sizeDetail;
            string sentence;
            if (total <= 0)
            {
                prefix = string.Empty;
                sizeDetail = string.Empty;
                sentence = string.Empty;
            }
            else
            {
                prefix = Lead;
                sizeDetail = $"MB/{total}MB  速度{speedText}MB/秒";
                sentence = prefix + downloaded + sizeDetail;
            }

            return new ProgressCopy
            {
                Prefix = prefix,
                Sentence = sentence,
                Eta = FormatEta(downloadedMb, totalMb, speedMbps, completed),
                Downloaded = downloaded.ToString(),
                SizeDetail = sizeDetail,
            };
        }

        public static string FormatEta(
            float downloadedMb,
            float totalMb,
            float speedMbps,
            bool completed)
        {
            if (completed)
                return "完成";
            // 无速度 / 无总量：留空（尽快由 Meter 写出剩余，避免「估算中」占位）。
            if (totalMb <= 0.01f || speedMbps <= 0.001f)
                return string.Empty;

            float remaining = UnityEngine.Mathf.Max(0f, totalMb - downloadedMb);
            float percent = downloadedMb / totalMb * 100f;
            bool nearDone = remaining <= DownloadProgressDisplayMeter.NearDoneRemainingMb
                || percent >= DownloadProgressDisplayMeter.NearDonePercent;
            float sec = remaining / speedMbps;
            if (sec >= 60f)
                return $"剩余{UnityEngine.Mathf.Min(60, UnityEngine.Mathf.CeilToInt(sec / 60f))}m";

            int shown = UnityEngine.Mathf.CeilToInt(sec);
            if (!nearDone)
                shown = UnityEngine.Mathf.Max(2, shown);
            else
                shown = UnityEngine.Mathf.Max(1, shown);
            return $"剩余{shown}s";
        }

        static string NormalizeSize(string size, string fallback = "若干")
        {
            if (string.IsNullOrWhiteSpace(size))
                return fallback;

            string trimmed = size.Trim();
            if (trimmed.EndsWith("MB", System.StringComparison.OrdinalIgnoreCase)
                || trimmed.EndsWith("KB", System.StringComparison.OrdinalIgnoreCase))
                trimmed = trimmed.Substring(0, trimmed.Length - 2).Trim();

            return string.IsNullOrEmpty(trimmed) ? fallback : trimmed;
        }
    }
}
