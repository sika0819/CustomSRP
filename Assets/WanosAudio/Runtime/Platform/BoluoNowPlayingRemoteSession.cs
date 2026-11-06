namespace WanosAudio
{
    /// <summary>
    /// 负一屏播放 / 暂停 / 切歌的纯状态机（时钟由调用方注入）。
    /// 对齐 <see cref="BoluoNowPlayingRemoteCommandPolicy"/>，以及
    /// NowPlayingService 的 SchedulePlayPause / Tick / Flush / AcceptSkip：
    /// 连点只落地最后一次播放或暂停；切歌在 in-flight 与同向防抖内不改曲目，
    /// 接受切歌则作废尚未落地的暂停，新曲保持播放。
    /// </summary>
    public sealed class BoluoNowPlayingRemoteSession
    {
        readonly int _trackCount;
        int _unityIndex;
        int _nativeIndex;
        int _lastSkipDirection;
        long _lastAcceptedSkipAtMs = -1L;
        long _pendingAtMs = -1L;
        bool _pendingWantMute;

        public BoluoNowPlayingRemoteSession(
            int trackCount,
            bool isPlaying,
            bool userManuallyPaused,
            int trackIndex = 0)
        {
            if (trackCount <= 0)
                throw new System.ArgumentOutOfRangeException(nameof(trackCount));
            _trackCount = trackCount;
            if (trackIndex < 0 || trackIndex >= trackCount)
                trackIndex = 0;
            _unityIndex = trackIndex;
            _nativeIndex = trackIndex;
            IsPlaying = isPlaying;
            UserManuallyPaused = userManuallyPaused;
        }

        /// <summary>引擎是否在出声。合并窗口内不随每次点击翻转。</summary>
        public bool IsPlaying { get; private set; }

        /// <summary>用户暂停闩。负一屏卡片播放态为 <c>!UserManuallyPaused</c>（忽略系统挂起）。</summary>
        public bool UserManuallyPaused { get; private set; }

        public bool PausedForTimerEnd { get; private set; }

        /// <summary>负一屏当前曲。Unity 自有切歌后不得被滞后的原生索引盖回。</summary>
        public int TrackIndex => _unityIndex;

        public bool SkipInFlight { get; private set; }

        public bool HasPendingPlayPause => _pendingAtMs >= 0;

        /// <summary>真正发给引擎的暂停次数。连点合并后应远小于点击次数。</summary>
        public int EnginePauseCount { get; private set; }

        /// <summary>真正发给引擎的续播次数。</summary>
        public int EngineResumeCount { get; private set; }

        public int AcceptedSkipCount { get; private set; }

        public int IgnoredSkipCount { get; private set; }

        /// <summary>Unity 自有切歌后误走 Resync 的次数。正常路径必须为 0。</summary>
        public int ResyncCount { get; private set; }

        public bool CardShowsPlaying => !UserManuallyPaused;

        public void NotePausedForTimerEnd(bool paused)
        {
            PausedForTimerEnd = paused;
            if (!paused)
                return;
            UserManuallyPaused = true;
            IsPlaying = false;
            CancelPendingPlayPause();
        }

        /// <summary>外部路径改过出声或暂停闩之后调用。不碰 pending 和切歌 in-flight。</summary>
        public void AdoptPlayback(bool audible, bool manuallyPaused)
        {
            IsPlaying = audible;
            UserManuallyPaused = manuallyPaused;
        }

        public void CancelPendingPlayPause() => _pendingAtMs = -1L;

        /// <param name="reason">空串为负一屏真实 Play；<c>ui</c> 只刷卡，不改播放态。</param>
        public void PressPlay(long nowMs, string reason = "")
        {
            if (!BoluoNowPlayingRemoteCommandPolicy.ShouldDriveEngineResumeFromNowPlayingPlay(reason))
                return;
            // 到点只等弹窗。合并窗口里的播放也由 NotePausedForTimerEnd 丢掉。
            if (PausedForTimerEnd)
                return;
            SchedulePlayPause(wantMute: false, nowMs);
        }

        public void PressPause(long nowMs) => SchedulePlayPause(wantMute: true, nowMs);

        /// <summary>
        /// 用调用方看到的引擎状态播种后再记播放/暂停。连点播种不取消上一笔 pending，只改最终意图。
        /// 到点后的播放整笔忽略。
        /// </summary>
        public void SeedAndPress(bool audible, bool manuallyPaused, bool wantPause, long nowMs, string playReason = "")
        {
            if (!wantPause
                && !BoluoNowPlayingRemoteCommandPolicy.ShouldDriveEngineResumeFromNowPlayingPlay(playReason))
                return;
            if (!wantPause && PausedForTimerEnd)
                return;
            IsPlaying = audible;
            UserManuallyPaused = manuallyPaused;
            SchedulePlayPause(wantPause, nowMs);
        }

        public bool PressNext(long nowMs) => AcceptSkip(+1, nowMs);

        public bool PressPrev(long nowMs) => AcceptSkip(-1, nowMs);

        public void NoteSkipSettled()
        {
            if (!SkipInFlight)
                return;
            SkipInFlight = false;
            _nativeIndex = _unityIndex;
        }

        public void Tick(long nowMs)
        {
            if (SkipInFlight && _lastAcceptedSkipAtMs >= 0
                && nowMs - _lastAcceptedSkipAtMs >= BoluoNowPlayingRemoteCommandPolicy.SkipInFlightTimeoutMs)
            {
                NoteSkipSettled();
            }

            if (_pendingAtMs < 0 || nowMs < _pendingAtMs)
                return;

            bool wantMute = _pendingWantMute;
            _pendingAtMs = -1L;
            FlushPlayPause(wantMute);
        }

        void SchedulePlayPause(bool wantMute, long nowMs)
        {
            _pendingWantMute = wantMute;
            if (BoluoNowPlayingRemoteCommandPolicy.ShouldCoalescePlayPause())
                _pendingAtMs = nowMs + BoluoNowPlayingRemoteCommandPolicy.PlayPauseCoalesceMs;
            else
                FlushPlayPause(wantMute);
        }

        void FlushPlayPause(bool wantMute)
        {
            bool already = BoluoNowPlayingRemoteCommandPolicy.ShouldSkipFlushBecauseAlreadyConsistent(
                wantMute, isMuted: !IsPlaying, userManuallyPaused: UserManuallyPaused);
            if (already)
                return;

            if (wantMute)
            {
                UserManuallyPaused = true;
                IsPlaying = false;
                EnginePauseCount++;
                return;
            }

            if (PausedForTimerEnd)
                return;

            PausedForTimerEnd = false;
            UserManuallyPaused = false;
            IsPlaying = true;
            EngineResumeCount++;
        }

        bool AcceptSkip(int direction, long nowMs)
        {
            if (direction == 0)
                direction = 1;

            long elapsed = _lastAcceptedSkipAtMs < 0
                ? long.MaxValue
                : nowMs - _lastAcceptedSkipAtMs;
            bool sameDir = _lastSkipDirection != 0 && _lastSkipDirection == direction;
            if (SkipInFlight && elapsed >= BoluoNowPlayingRemoteCommandPolicy.SkipInFlightTimeoutMs)
                NoteSkipSettled();

            if (BoluoNowPlayingRemoteCommandPolicy.ShouldIgnoreSkip(SkipInFlight, sameDir, elapsed))
            {
                IgnoredSkipCount++;
                return false;
            }

            if (BoluoNowPlayingRemoteCommandPolicy.ShouldCancelPendingPlayPauseOnSkip())
                _pendingAtMs = -1L;

            SkipInFlight = true;
            _lastSkipDirection = direction;
            _lastAcceptedSkipAtMs = nowMs;
            AcceptedSkipCount++;

            int laggedNative = _nativeIndex;
            _unityIndex = Wrap(_unityIndex + direction);
            if (BoluoNowPlayingRemoteCommandPolicy.ShouldResyncAfterUnityOwnedSkip())
            {
                _unityIndex = laggedNative;
                ResyncCount++;
            }

            if (!PausedForTimerEnd)
            {
                UserManuallyPaused = false;
                IsPlaying = true;
            }

            return true;
        }

        int Wrap(int index)
        {
            if (_trackCount == 1)
                return 0;
            return ((index % _trackCount) + _trackCount) % _trackCount;
        }
    }
}
