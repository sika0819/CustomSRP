namespace WanosAudio
{
    /// <summary>
    /// 后台 keep-playing 门禁（与 Android <c>BoluoWanosPauseAudioGate.isUserStoppedPlayback</c> 对齐）。
    /// 禁止把 teardown 陈旧闩当成「用户停播」，否则上滑任务栏会误静音且负一屏仍 PLAYING。
    /// </summary>
    public static class BoluoKeepPlayingPolicy
    {
        /// <summary>
        /// 是否因用户停播而禁止后台续播。
        /// </summary>
        /// <param name="userPaused">负一屏/岛内/到点用户暂停。</param>
        /// <param name="shouldStop">stop()/teardown 钉住的停播闩。</param>
        /// <param name="foregroundPlayingCardPublished">已对外推出「播放中」负一屏卡。</param>
        public static bool IsUserStoppedForKeepPlaying(
            bool userPaused,
            bool shouldStop,
            bool foregroundPlayingCardPublished)
        {
            if (foregroundPlayingCardPublished)
            {
                // teardown 双钉 userPaused∧shouldStop → 陈旧，放行 keep-playing。
                // 真暂停通常只钉 userPaused，且会清卡或推 PAUSED。
                return userPaused && !shouldStop;
            }

            return userPaused || shouldStop;
        }
    }
}
