namespace WanosAudio
{
    /// <summary>
    /// 负一屏 / 锁屏 / 控制中心远程键防呆（纯逻辑）。
    /// iOS <c>BoluoMusicPlayerIOS.mm</c> + <c>WanosMusicPlayer.mm</c>、
    /// Android <c>WanosMusicPlayerNative.java</c> 语义对齐本类。
    /// </summary>
    public static class BoluoNowPlayingRemoteCommandPolicy
    {
        /// <summary>播放/暂停 last-wins 合并窗口。连点只落地最终意图，避免 mute/unmute 拉锯。</summary>
        public const long PlayPauseCoalesceMs = 250L;

        /// <summary>同方向切歌防抖。挡住 MediaStyle+Session 连发，以及索引未更新时连跳同一曲。</summary>
        public const long SkipDebounceMs = 400L;

        /// <summary>
        /// 引擎切歌未 settle（Playing / 超时）时忽略后续 skip。
        /// 超时后放行，避免 in-flight 泄漏永久吞键。
        /// </summary>
        public const long SkipInFlightTimeoutMs = 2000L;

        /// <summary>播放/暂停始终记为 pending 并重启定时器（last-wins）。</summary>
        public static bool ShouldCoalescePlayPause() => true;

        /// <summary>
        /// 切歌防呆：in-flight（引擎未 settle）忽略任意方向；同方向短窗忽略；反方向在非 in-flight 时立刻撤回。
        /// </summary>
        /// <param name="skipInFlight">上一笔 skip 尚未 Playing / 超时 settle。</param>
        /// <param name="sameDirectionAsLast">与上一笔已接受 skip 同向（+1 或 -1）。</param>
        /// <param name="elapsedSinceLastAcceptedSkipMs">距上一笔已接受 skip 的毫秒；从未 skip 传极大值。</param>
        public static bool ShouldIgnoreSkip(
            bool skipInFlight,
            bool sameDirectionAsLast,
            long elapsedSinceLastAcceptedSkipMs,
            long skipDebounceMs = SkipDebounceMs,
            long skipInFlightTimeoutMs = SkipInFlightTimeoutMs)
        {
            if (elapsedSinceLastAcceptedSkipMs < 0)
                elapsedSinceLastAcceptedSkipMs = long.MaxValue;
            if (skipInFlight && elapsedSinceLastAcceptedSkipMs < skipInFlightTimeoutMs)
                return true;
            return sameDirectionAsLast && elapsedSinceLastAcceptedSkipMs < skipDebounceMs;
        }

        /// <summary>
        /// skip 一旦接受，必须作废尚未 flush 的 play/pause。
        /// 否则「先点暂停再切歌」会在合并窗口到期后把新曲又静音。
        /// </summary>
        public static bool ShouldCancelPendingPlayPauseOnSkip() => true;

        /// <summary>
        /// Unity 自有 Next/Prev（AcceptSkip）后禁止立刻 Resync。
        /// 此时原生传输计划索引仍滞后，AdoptTrack 会把会话盖回旧曲；
        /// 仅原生已改曲（OnNativeTrackChanged / 后台 bg_skip）才应 Resync。
        /// </summary>
        public static bool ShouldResyncAfterUnityOwnedSkip() => false;

        /// <summary>
        /// play/pause flush 时状态已一致则只补 NowPlaying UI，不碰设备。
        /// mute 方向须同时 userManuallyPaused：冷启动 isMuted 初值 YES 不能当成已暂停。
        /// play 方向也须 !userManuallyPaused：定时到点原生已 Pause_Music 但 Unity 尚未 Drain
        /// 时 IsPlaying 仍可能为 true，若只看 !isMuted 会把 MediaPlay 续播吞掉。
        /// </summary>
        public static bool ShouldSkipFlushBecauseAlreadyConsistent(
            bool wantMute,
            bool isMuted,
            bool userManuallyPaused)
            => wantMute ? (isMuted && userManuallyPaused) : (!isMuted && !userManuallyPaused);

        /// <summary>
        /// 原生 focus resume 程序化通知必须传 <c>ui</c>：只收 tip / 刷卡，禁止再驱引擎。
        /// 否则与原生 Resume + unmute 爬坡叠峰 → 回前台音乐/音效听感卡顿。
        /// 控制中心 / 负一屏真实 Play 传空串或其它 reason，才走 Resume。
        /// </summary>
        public static bool ShouldDriveEngineResumeFromNowPlayingPlay(string reason)
            => !string.Equals(reason, "ui", System.StringComparison.Ordinal);

        /// <summary>
        /// 播放 / 暂停 / 切歌 settle 之后才能出声。
        /// 代际已经往后走，或用户已经暂停，就禁止再 Post、再把卡片改成播放。
        /// </summary>
        public static bool ShouldCommitAudible(long generationAtStart, long generationNow, bool userPaused)
            => generationAtStart == generationNow && !userPaused;
    }
}
