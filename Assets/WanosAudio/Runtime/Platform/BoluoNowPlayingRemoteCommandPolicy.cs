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
        /// 切歌未提交索引时忽略后续 skip。超时后放行，避免 in-flight 泄漏永久吞键。
        /// </summary>
        public const long SkipInFlightTimeoutMs = 2000L;

        /// <summary>播放/暂停始终记为 pending 并重启定时器（last-wins）。</summary>
        public static bool ShouldCoalescePlayPause() => true;

        /// <summary>
        /// 切歌防呆：in-flight（索引未提交）忽略任意方向；同方向短窗忽略；反方向允许立刻撤回。
        /// </summary>
        /// <param name="skipInFlight">上一笔 skip 尚未把 currentMusicIndex 提交。</param>
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
    }
}
