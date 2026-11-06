using UnityEngine;

namespace Game
{
    /// <summary>
    /// 下载弹窗速度 / 剩余时间展示策略：
    /// <list type="bullet">
    /// <item>进度条 / 已下 MB 可即时刷新；</item>
    /// <item>首个速度样本用短窗（约 0.15s），之后 0.5s 窗 + EMA；</item>
    /// <item>第一次读到的已下载量只做锚点，不产生速度；之后的增量才算 MB/s。</item>
    /// <item>速度与剩余约 1s 提交一次；首次有有效速度立刻提交 ETA；</item>
    /// <item>之后墙钟倒计时 + 双向限幅校准。未接近完成时最低显示「剩余2s」，但不把锚点前的体积算进速度。</item>
    /// </list>
    /// 纯状态机，不依赖 UI，便于单测。
    /// </summary>
    public sealed class DownloadProgressDisplayMeter
    {
        /// <summary>冷启动首个速度样本最短窗（越短 ETA 出得越快）。</summary>
        public const float FirstSampleInterval = 0.15f;

        /// <summary>稳态速度采样窗。</summary>
        public const float SampleInterval = 0.5f;

        /// <summary>速度 / 剩余文案提交间隔。</summary>
        public const float CommitInterval = 1f;

        /// <summary>新样本权重；越大越跟手、越小越稳。</summary>
        public const float EmaAlpha = 0.35f;

        /// <summary>单次校准相对 sticky 的最大绝对变化（秒）。</summary>
        public const float EtaMaxStepSeconds = 8f;

        /// <summary>单次校准相对 sticky 的最大相对变化。</summary>
        public const float EtaMaxStepRatio = 0.2f;

        /// <summary>仅当剩余体积低于此值（MB）时允许展示「剩余1s」。</summary>
        public const float NearDoneRemainingMb = 0.5f;

        /// <summary>仅当进度达到此百分比时允许展示「剩余1s」。</summary>
        public const float NearDonePercent = 99f;

        bool _hasSample;
        float _sampleTime;
        float _sampleMb;
        float _smoothedMbps = -1f;
        float _displayMbps = -1f;
        float _nextCommitTime = -1f;
        float _lastCommitTime = -1f;
        float _stickyEtaSec = -1f;
        int _speedSampleCount;
        bool _etaPublished;

        public float DisplaySpeedMbps => _displayMbps;

        /// <summary>是否已向界面公布过有效剩余时间。</summary>
        public bool HasPublishedEta => _etaPublished;

        public void Reset()
        {
            _hasSample = false;
            _sampleTime = 0f;
            _sampleMb = 0f;
            _smoothedMbps = -1f;
            _displayMbps = -1f;
            _nextCommitTime = -1f;
            _lastCommitTime = -1f;
            _stickyEtaSec = -1f;
            _speedSampleCount = 0;
            _etaPublished = false;
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

            // 常规按 CommitInterval；首次有速度则立刻提交 ETA。
            if (_nextCommitTime < 0f
                || now >= _nextCommitTime
                || ReadyToPublishFirstEta())
            {
                CommitSpeed(now);
                return true;
            }

            return false;
        }

        /// <summary>
        /// 用当前展示速度算剩余；未就绪返回空串（界面保持空白，不写「估算中」）。
        /// 应在 commit 帧调用：此时 <see cref="Tick"/> 已按墙钟推进 sticky。
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

            downloadedMb = Mathf.Max(0f, downloadedMb);
            float remainingMb = Mathf.Max(0f, totalMb - downloadedMb);
            float percent = downloadedMb / totalMb * 100f;
            bool nearDone = remainingMb <= NearDoneRemainingMb || percent >= NearDonePercent;
            float rawEta = remainingMb / _displayMbps;

            if (!_etaPublished)
            {
                if (_speedSampleCount < 1)
                    return string.Empty;

                _stickyEtaSec = ClampEtaFloor(rawEta, nearDone);
                _etaPublished = true;
                return FormatStickyEta(_stickyEtaSec, nearDone);
            }

            _stickyEtaSec = ClampEtaFloor(BlendEta(_stickyEtaSec, rawEta), nearDone);
            return FormatStickyEta(_stickyEtaSec, nearDone);
        }

        bool ReadyToPublishFirstEta() =>
            !_etaPublished && _speedSampleCount >= 1 && _smoothedMbps > 0.001f;

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
            float needed = _speedSampleCount == 0 ? FirstSampleInterval : SampleInterval;
            if (dt < needed)
            {
                // 首个速度样本形成前，短间隔里从 0 跳到真实已下载量只改锚点。
                // 否则打开弹窗时先写入的 0 会把打开前的体积算成 0.15s 速度，剩余被抬成 2s。
                if (_speedSampleCount == 0 && _sampleMb <= 0.0001f && downloadedMb > _sampleMb)
                {
                    _sampleTime = now;
                    _sampleMb = downloadedMb;
                }

                return;
            }

            float delta = downloadedMb - _sampleMb;
            _sampleTime = now;
            _sampleMb = downloadedMb;
            // 无增量（卡顿）不计入速度样本。
            if (delta <= 0f || dt <= 0.0001f)
                return;

            float instant = delta / dt;
            if (_smoothedMbps < 0f)
                _smoothedMbps = instant;
            else
                _smoothedMbps = Mathf.Lerp(_smoothedMbps, instant, EmaAlpha);

            _speedSampleCount++;
        }

        void CommitSpeed(float now)
        {
            if (_etaPublished && _lastCommitTime >= 0f)
            {
                float dt = now - _lastCommitTime;
                if (dt > 0f)
                    _stickyEtaSec = Mathf.Max(0f, _stickyEtaSec - dt);
            }

            _displayMbps = _smoothedMbps;
            _lastCommitTime = now;
            _nextCommitTime = now + CommitInterval;
        }

        static float BlendEta(float sticky, float raw)
        {
            float maxStep = Mathf.Max(EtaMaxStepSeconds, Mathf.Abs(sticky) * EtaMaxStepRatio);
            float delta = Mathf.Clamp(raw - sticky, -maxStep, maxStep);
            return Mathf.Max(0f, sticky + delta);
        }

        static float ClampEtaFloor(float sec, bool nearDone)
        {
            if (nearDone)
                return Mathf.Max(0f, sec);
            return Mathf.Max(2f, sec);
        }

        static string FormatStickyEta(float sec, bool nearDone)
        {
            if (sec >= 60f)
                return $"剩余{Mathf.Min(60, Mathf.CeilToInt(sec / 60f))}m";

            int shown = Mathf.CeilToInt(sec);
            if (!nearDone)
                shown = Mathf.Max(2, shown);
            else
                shown = Mathf.Max(1, shown);
            return $"剩余{shown}s";
        }
    }
}
