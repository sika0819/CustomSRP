using UnityEngine;

namespace Game
{
    /// <summary>
    /// 下载弹窗速度 / 剩余时间展示策略（对齐成熟手游）：
    /// <list type="bullet">
    /// <item>进度条 / 已下 MB 可即时刷新；</item>
    /// <item>速度按 0.5s 窗采样，再 EMA 平滑；</item>
    /// <item>速度与剩余文案约 1s 才提交一次，避免数字闪烁；</item>
    /// <item>剩余时间允许变短，上跳需超过阈值才采纳（抗网络抖动）。</item>
    /// </list>
    /// 纯状态机，不依赖 UI，便于单测。
    /// </summary>
    public sealed class DownloadProgressDisplayMeter
    {
        /// <summary>瞬时速度采样窗。</summary>
        public const float SampleInterval = 0.5f;

        /// <summary>速度 / 剩余文案提交间隔。</summary>
        public const float CommitInterval = 1f;

        /// <summary>新样本权重；越大越跟手、越小越稳。</summary>
        public const float EmaAlpha = 0.35f;

        /// <summary>剩余秒数允许上跳的最小增量（秒）。</summary>
        public const float EtaUpJumpSeconds = 5f;

        bool _hasSample;
        float _sampleTime;
        float _sampleMb;
        float _smoothedMbps = -1f;
        float _displayMbps = -1f;
        float _nextCommitTime = -1f;
        float _stickyEtaSec = -1f;

        public float DisplaySpeedMbps => _displayMbps;

        public void Reset()
        {
            _hasSample = false;
            _sampleTime = 0f;
            _sampleMb = 0f;
            _smoothedMbps = -1f;
            _displayMbps = -1f;
            _nextCommitTime = -1f;
            _stickyEtaSec = -1f;
        }

        /// <summary>
        /// 推进采样；返回本帧是否应重写速度 / 剩余文案。
        /// </summary>
        public bool Tick(float now, float downloadedMb, bool completed)
        {
            downloadedMb = Mathf.Max(0f, downloadedMb);

            if (completed)
            {
                CommitSpeed(now);
                _stickyEtaSec = -1f;
                return true;
            }

            SampleSpeed(now, downloadedMb);

            if (_nextCommitTime < 0f || now >= _nextCommitTime)
            {
                CommitSpeed(now);
                return true;
            }

            return false;
        }

        /// <summary>
        /// 用当前展示速度算剩余；无速度返回空串（界面保留上次有效文案）。
        /// </summary>
        public string ResolveEta(float downloadedMb, float totalMb, bool completed)
        {
            if (completed)
            {
                _stickyEtaSec = -1f;
                return "完成";
            }

            if (totalMb <= 0.01f || _displayMbps <= 0.001f)
                return string.Empty;

            float remainingMb = Mathf.Max(0f, totalMb - downloadedMb);
            float sec = remainingMb / _displayMbps;
            if (_stickyEtaSec < 0f || sec + 0.01f < _stickyEtaSec
                || sec >= _stickyEtaSec + EtaUpJumpSeconds)
            {
                _stickyEtaSec = sec;
            }

            return FormatStickyEta(_stickyEtaSec);
        }

        void SampleSpeed(float now, float downloadedMb)
        {
            if (!_hasSample)
            {
                _hasSample = true;
                _sampleTime = now;
                _sampleMb = downloadedMb;
                return;
            }

            float dt = now - _sampleTime;
            if (dt < SampleInterval)
                return;

            float delta = downloadedMb - _sampleMb;
            _sampleTime = now;
            _sampleMb = downloadedMb;
            if (delta < 0f || dt <= 0.0001f)
                return;

            float instant = delta / dt;
            if (_smoothedMbps < 0f)
                _smoothedMbps = instant;
            else
                _smoothedMbps = Mathf.Lerp(_smoothedMbps, instant, EmaAlpha);
        }

        void CommitSpeed(float now)
        {
            _displayMbps = _smoothedMbps;
            _nextCommitTime = now + CommitInterval;
        }

        static string FormatStickyEta(float sec)
        {
            if (sec >= 60f)
                return $"剩余{Mathf.Min(60, Mathf.CeilToInt(sec / 60f))}m";
            return $"剩余{Mathf.Max(1, Mathf.CeilToInt(sec))}s";
        }
    }
}
