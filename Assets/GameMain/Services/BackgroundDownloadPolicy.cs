namespace Game
{
    /// <summary>
    /// 后台下载弹窗开关 × 网络：统一策略表（纯逻辑，EditMode 可测）。
    /// AppLaunch <c>cancelAccount</c>：1 开 / 2 关 → <paramref name="wifiConfirmEnabled"/>。
    /// </summary>
    public static class BackgroundDownloadPolicy
    {
        /// <summary>关 + 可传（Wi‑Fi/已开闸）→ Bootstrap 立刻静默全量。</summary>
        public static bool ShouldBootstrapSilentAll(
            bool wifiConfirmEnabled,
            bool canTransmitNow) =>
            !wifiConfirmEnabled && canTransmitNow;

        /// <summary>开 → Bootstrap 不主动 EnsureReady / Download。</summary>
        public static bool ShouldBootstrapStayIdle(bool wifiConfirmEnabled) =>
            wifiConfirmEnabled;

        /// <summary>开 + Wi‑Fi → 用户点一次后 §4.6，确认则 KickSilentAll。</summary>
        public static bool ShouldWifiConfirmOnceThenDownloadAll(
            bool wifiConfirmEnabled,
            bool isWifiOrLan) =>
            wifiConfirmEnabled && isWifiOrLan;

        /// <summary>开 + 蜂窝 → 用户 EnsureReady 仍可弹流量窗。</summary>
        public static bool ShouldPerEntryMobilePrompt(
            bool wifiConfirmEnabled,
            bool needsMobileDataPrompt) =>
            wifiConfirmEnabled && needsMobileDataPrompt;

        /// <summary>关：蜂窝免确认，自动会话开闸。</summary>
        public static bool ShouldAutoAllowCellular(bool wifiConfirmEnabled) =>
            !wifiConfirmEnabled;

        /// <summary>
        /// 开：后台队列被蜂窝闸住且有忙活 → 弹流量窗续传。
        /// </summary>
        public static bool ShouldPromptMobileWhenBackgroundBlocked(
            bool wifiConfirmEnabled,
            bool hasBusyWork,
            bool needsMobileDataPrompt) =>
            wifiConfirmEnabled && hasBusyWork && needsMobileDataPrompt;

        /// <summary>
        /// 流量「继续」后本岛会话开闸直到回 Wi‑Fi / 离岛（开、关相同）。
        /// </summary>
        public static bool ShouldUseSessionCellularAllow(bool wifiConfirmEnabled) =>
            true;

        /// <summary>
        /// 区域门是否先弹「前方区域未下载」。
        /// 关：否。开：仅首次（未接受 §4.6 且未在下）；点过确认或队列忙 → 直接进度。
        /// </summary>
        public static bool ShouldShowGateRequestFirst(
            bool wifiConfirmEnabled,
            bool wifiAcceptedOrDownloading = false)
        {
            if (!wifiConfirmEnabled)
                return false;
            if (wifiAcceptedOrDownloading)
                return false;
            return true;
        }

        /// <summary>关：静默后台态（含巡检）；开且未接受 §4.6 则否。</summary>
        public static bool IsSilentBackgroundMode(
            bool wifiConfirmEnabled,
            bool wifiConfirmDeclinedThisIsland) =>
            !wifiConfirmEnabled || wifiConfirmDeclinedThisIsland;
    }
}
