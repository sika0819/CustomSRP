namespace Game
{
    /// <summary>
    /// 播放展示 / 续播意图单一权威（负一屏卡、岛内图标、自动/硬续策略都读这里）。
    /// 可听现实（引擎 mute）由原生 / HoldYield 落地；本门只表达「对外呈现什么 + 谁有权续」。
    /// </summary>
    public enum PlaybackGateState
    {
        /// <summary>无会话 / 已清卡。</summary>
        Stopped = 0,
        /// <summary>持焦可听，或允许后台续播中（含任务栏预览）。</summary>
        Playing = 1,
        /// <summary>负一屏 / 岛内 / 到点 — 等显式续播。</summary>
        UserPaused = 2,
        /// <summary>抢焦 / 来电 / duck — 可自动软续。</summary>
        SystemSuspended = 3,
    }

    /// <summary>
    /// 前后台分级：禁止把 OnApplicationPause 当成单一「进后台停声」。
    /// </summary>
    public enum LifecycleKind
    {
        /// <summary>前台。</summary>
        Foreground = 0,
        /// <summary>上滑任务栏预览：保持 Playing，禁止 HoldYield / 占位卡让出。</summary>
        RecentsTransient = 1,
        /// <summary>HOME / onStop 且允许后台播。</summary>
        BackgroundKeepPlaying = 2,
        /// <summary>禁后台播，或启动期尚未出声且进程已离开。</summary>
        BackgroundYield = 3,
        /// <summary>对方媒体抢焦。</summary>
        CompetitorFocus = 4,
    }

    /// <summary>
    /// C# 侧 Gate 镜像。原生 MediaSession 写卡也应同栈跟随；Publish 的 isPlaying 只派生自本门。
    /// </summary>
    public static class PlaybackGate
    {
        static PlaybackGateState _state = PlaybackGateState.Stopped;
        static LifecycleKind _lifecycle = LifecycleKind.Foreground;
        static SilenceReason _silence = SilenceReason.None;

        public static PlaybackGateState State => _state;
        public static LifecycleKind Lifecycle => _lifecycle;

        /// <summary>当前静音原因。卡片态由它派生，调用方不要再另写一套暂停布尔。</summary>
        public static SilenceReason Silence => _silence;

        public static bool CardShouldShowPlaying => _state == PlaybackGateState.Playing;

        public static bool IsUserPausedLike =>
            _state == PlaybackGateState.UserPaused;

        public static bool IsSystemSuspended =>
            _state == PlaybackGateState.SystemSuspended;

        public static void ResetSession()
        {
            _state = PlaybackGateState.Stopped;
            _lifecycle = LifecycleKind.Foreground;
            _silence = SilenceReason.None;
        }

        /// <summary>写入唯一静音原因，并同步卡片态。</summary>
        public static void SetSilence(SilenceReason reason, string why)
        {
            _silence = reason;
            PlaybackGateState state;
            switch (reason)
            {
                case SilenceReason.UserPause:
                case SilenceReason.TimerEnd:
                    state = PlaybackGateState.UserPaused;
                    break;
                case SilenceReason.SystemInterrupt:
                    state = PlaybackGateState.SystemSuspended;
                    break;
                default:
                    state = _state == PlaybackGateState.Stopped
                        ? PlaybackGateState.Stopped
                        : PlaybackGateState.Playing;
                    break;
            }

            Set(state, why);
        }

        public static void Set(PlaybackGateState state, string reason)
        {
            if (_state == state)
                return;
            var prev = _state;
            _state = state;
            UnityGameFramework.Runtime.Log.Info(
                $"[PlaybackGate] {prev} → {state} ({reason})");
        }

        public static void SetLifecycle(LifecycleKind kind, string reason)
        {
            if (_lifecycle == kind)
                return;
            var prev = _lifecycle;
            _lifecycle = kind;
            UnityGameFramework.Runtime.Log.Info(
                $"[PlaybackGate] lifecycle {prev} → {kind} ({reason})");
        }

        /// <summary>
        /// 用户暂停闩：仅在 UserPaused ↔ Playing 间切换。
        /// SystemSuspended 由 <see cref="AdoptFromNative"/> / 配对恢复清，禁止 NoteUserPaused(false) 误清抢焦挂起。
        /// </summary>
        public static void NoteUserPaused(bool paused, string reason)
        {
            if (paused)
            {
                // 到点只能由弹窗清掉。停引擎不得把 TimerEnd 写成用户暂停。
                if (_silence == SilenceReason.TimerEnd)
                    return;
                SetSilence(SilenceReason.UserPause, reason);
            }
            else if (_silence == SilenceReason.UserPause)
                SetSilence(SilenceReason.None, reason);
        }

        public static void NoteSystemSuspended(string reason) =>
            SetSilence(SilenceReason.SystemInterrupt, reason);

        public static void NotePlaying(string reason)
        {
            // 用户暂停 / 到点只能由显式续播清掉，刷卡不得把滑动续播闩擦掉。
            if (_silence == SilenceReason.UserPause || _silence == SilenceReason.TimerEnd)
                return;
            if (_silence == SilenceReason.SystemInterrupt)
                _silence = SilenceReason.None;
            Set(PlaybackGateState.Playing, reason);
        }

        public static void NoteStopped(string reason)
        {
            _silence = SilenceReason.None;
            Set(PlaybackGateState.Stopped, reason);
        }

        /// <summary>
        /// Resync / 原生对账。用户暂停优先于 suspended，避免负一屏暂停被抢焦标志降级。
        /// </summary>
        public static void AdoptFromNative(
            bool userPaused, bool suspended, string reason, bool resumeInFlight = false)
        {
            // _silence 在这次写入前就是 Resync 要抄住的原因。
            SilenceReason next = SilenceResumePolicy.ResolveReported(
                _silence, userPaused, suspended, resumeInFlight);
            SetSilence(next, reason);
        }
    }
}
