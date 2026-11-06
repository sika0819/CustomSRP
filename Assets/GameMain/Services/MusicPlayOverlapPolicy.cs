namespace Game
{
    /// <summary>
    /// 下载完成瞬间连点：<c>Play_*</c> 已发出、引擎尚未 <c>Playing</c> 时禁止再 Post。
    /// 这段窗口里 <c>Stop_Music</c> 会被吞，再 Post 就会叠播。
    /// </summary>
    public static class MusicPlayOverlapPolicy
    {
        /// <summary>
        /// Playing 一直不来时放行，与负一屏 skip in-flight 同量级。
        /// 否则闸会把后续点歌永远卡住。
        /// </summary>
        public const int VoiceCreateTimeoutMs = 2000;

        /// <summary>闸开着则不要再 Post 下一首。</summary>
        public static bool ShouldDeferPost(bool voiceCreateInFlight) => voiceCreateInFlight;

        public enum PlayingDisposition
        {
            /// <summary>代际仍是当前目标：照常 Break / 单曲循环。</summary>
            Continue = 0,

            /// <summary>用户已停、并不想出声，或这是已经绑到别的 voice 上的迟到实例：只停，不拉起。</summary>
            StopOnly = 1,

            /// <summary>这条就是尚未 Playing 的那次 Post，点选已经前进：停掉再播当前索引。</summary>
            StopThenPlayCurrent = 2,
        }

        /// <summary>
        /// Playing 到达后怎么处理。
        /// 代际一致且就是当前 voice：照常。
        /// 用户已停或不想出声：只停。
        /// 这次 Post 的代际已经落后：停掉刚起来的实例，再播当前索引。
        /// 与当前 voice 不符、又不是这次 in-flight Post：只停这条，避免把已经绑上的另一首掐掉重播。
        /// </summary>
        public static PlayingDisposition DecidePlaying(
            bool voiceIdMatchesCurrent,
            bool isCreateInFlightVoice,
            int postedGeneration,
            int currentGeneration,
            bool stoppedByUser,
            bool playbackStillWanted)
        {
            // 系统打断 / 用户暂停：迟到的 Playing 只停，禁止再排一次 Play。
            if (stoppedByUser || !playbackStillWanted)
                return PlayingDisposition.StopOnly;

            if (voiceIdMatchesCurrent
                && postedGeneration == currentGeneration)
                return PlayingDisposition.Continue;

            if (isCreateInFlightVoice && postedGeneration != currentGeneration)
                return PlayingDisposition.StopThenPlayCurrent;

            if (voiceIdMatchesCurrent && postedGeneration != currentGeneration)
                return PlayingDisposition.StopThenPlayCurrent;

            return PlayingDisposition.StopOnly;
        }
    }
}
