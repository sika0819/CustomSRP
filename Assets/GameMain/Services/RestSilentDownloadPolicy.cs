namespace Game
{
    /// <summary>
    /// 兼容旧名：转发 <see cref="BackgroundDownloadPolicy"/>。
    /// </summary>
    public static class RestSilentDownloadPolicy
    {
        public static bool ShouldStartSilentRestNow(
            bool wifiConfirmEnabled,
            bool canTransmitNow) =>
            BackgroundDownloadPolicy.ShouldBootstrapSilentAll(
                wifiConfirmEnabled,
                canTransmitNow);

        public static bool ShouldBypassConsentPromptForSilentRest(bool wifiConfirmEnabled) =>
            !wifiConfirmEnabled;
    }
}
