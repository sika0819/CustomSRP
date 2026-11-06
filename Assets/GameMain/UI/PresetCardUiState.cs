namespace Game
{
    /// <summary>预设卡下载外观：进度环只跟用户点击；失败显示重试，静默下载不转圈。</summary>
    public enum PresetDownloadVisual
    {
        NotDownloaded = 0,
        Downloading = 1,
        Retry = 2,
        Ready = 3,
    }

    public static class PresetCardUiState
    {
        /// <param name="userTriggered">用户点过这张卡，且这轮点击还没被清掉。</param>
        /// <param name="userAttemptLive">本卡仍有 bank 在用户槽排队或占用用户槽。</param>
        /// <param name="anyUnresolvedFailure">本卡有传输失败、空 url，或自动重试还没成功。</param>
        public static PresetDownloadVisual Resolve(
            bool allReady,
            bool userTriggered,
            bool userAttemptLive,
            bool anyUnresolvedFailure)
        {
            if (allReady)
                return PresetDownloadVisual.Ready;

            // 点击后、这轮还没失败：转圈。失败并离开用户槽后回到重试，即使标记还没清。
            if (userTriggered && (userAttemptLive || !anyUnresolvedFailure))
                return PresetDownloadVisual.Downloading;

            if (anyUnresolvedFailure)
                return PresetDownloadVisual.Retry;

            return PresetDownloadVisual.NotDownloaded;
        }
    }
}
