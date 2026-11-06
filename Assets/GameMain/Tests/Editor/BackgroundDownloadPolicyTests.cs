using NUnit.Framework;

namespace Game.Tests
{
    /// <summary>后台弹窗关/开 × Wi‑Fi/流量：策略矩阵。</summary>
    public sealed class BackgroundDownloadPolicyTests
    {
        [Test]
        public void FeatureOff_Wifi_BootstrapSilentAll()
        {
            Assert.IsTrue(
                BackgroundDownloadPolicy.ShouldBootstrapSilentAll(
                    wifiConfirmEnabled: false,
                    canTransmitNow: true));
            Assert.IsFalse(
                BackgroundDownloadPolicy.ShouldBootstrapStayIdle(false));
            Assert.IsFalse(
                BackgroundDownloadPolicy.ShouldShowGateRequestFirst(false));
            Assert.IsTrue(BackgroundDownloadPolicy.ShouldAutoAllowCellular(false));
        }

        [Test]
        public void FeatureOff_Cellular_NoMobileTotalPrompt_AutoAllow()
        {
            Assert.IsTrue(BackgroundDownloadPolicy.ShouldAutoAllowCellular(false));
            Assert.IsTrue(
                BackgroundDownloadPolicy.ShouldUseSessionCellularAllow(false));
        }

        [Test]
        public void FeatureOn_BootstrapIdle()
        {
            Assert.IsTrue(BackgroundDownloadPolicy.ShouldBootstrapStayIdle(true));
            Assert.IsFalse(
                BackgroundDownloadPolicy.ShouldBootstrapSilentAll(
                    wifiConfirmEnabled: true,
                    canTransmitNow: true));
            Assert.IsFalse(BackgroundDownloadPolicy.ShouldAutoAllowCellular(true));
            Assert.IsTrue(
                BackgroundDownloadPolicy.ShouldShowGateRequestFirst(
                    wifiConfirmEnabled: true,
                    wifiAcceptedOrDownloading: false));
        }

        [Test]
        public void FeatureOn_AfterConfirm_SkipGateRequest()
        {
            Assert.IsFalse(
                BackgroundDownloadPolicy.ShouldShowGateRequestFirst(
                    wifiConfirmEnabled: true,
                    wifiAcceptedOrDownloading: true));
        }

        [Test]
        public void FeatureOn_Wifi_UserOnceThenSilentAll()
        {
            Assert.IsTrue(
                BackgroundDownloadPolicy.ShouldWifiConfirmOnceThenDownloadAll(
                    wifiConfirmEnabled: true,
                    isWifiOrLan: true));
            Assert.IsTrue(
                BackgroundDownloadPolicy.ShouldUseSessionCellularAllow(true));
        }

        [Test]
        public void FeatureOn_BackgroundBlocked_PromptsMobile()
        {
            Assert.IsTrue(
                BackgroundDownloadPolicy.ShouldPromptMobileWhenBackgroundBlocked(
                    wifiConfirmEnabled: true,
                    hasBusyWork: true,
                    needsMobileDataPrompt: true));
            Assert.IsFalse(
                BackgroundDownloadPolicy.ShouldPromptMobileWhenBackgroundBlocked(
                    wifiConfirmEnabled: false,
                    hasBusyWork: true,
                    needsMobileDataPrompt: true));
            Assert.IsFalse(
                BackgroundDownloadPolicy.ShouldPromptMobileWhenBackgroundBlocked(
                    wifiConfirmEnabled: true,
                    hasBusyWork: false,
                    needsMobileDataPrompt: true));
        }

        [Test]
        public void FeatureOn_Cellular_UserEntryStillNeedsMobile()
        {
            Assert.IsTrue(
                BackgroundDownloadPolicy.ShouldPerEntryMobilePrompt(
                    wifiConfirmEnabled: true,
                    needsMobileDataPrompt: true));
            Assert.IsFalse(
                BackgroundDownloadPolicy.ShouldWifiConfirmOnceThenDownloadAll(
                    wifiConfirmEnabled: true,
                    isWifiOrLan: false));
        }

        [Test]
        public void SilentBackgroundMode_FeatureOffOrDeclined()
        {
            Assert.IsTrue(
                BackgroundDownloadPolicy.IsSilentBackgroundMode(
                    wifiConfirmEnabled: false,
                    wifiConfirmDeclinedThisIsland: false));
            Assert.IsTrue(
                BackgroundDownloadPolicy.IsSilentBackgroundMode(
                    wifiConfirmEnabled: true,
                    wifiConfirmDeclinedThisIsland: true));
            Assert.IsFalse(
                BackgroundDownloadPolicy.IsSilentBackgroundMode(
                    wifiConfirmEnabled: true,
                    wifiConfirmDeclinedThisIsland: false));
        }
    }
}
