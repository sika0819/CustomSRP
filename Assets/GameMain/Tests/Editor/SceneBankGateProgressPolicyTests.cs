using Game.BoluoApi;
using NUnit.Framework;

namespace Game.Tests
{
    /// <summary>区域门进度条偶现消失 / 再进不弹：开窗状态与再开判定。</summary>
    public sealed class SceneBankGateProgressPolicyTests
    {
        [TearDown]
        public void TearDown()
        {
            BoluoAppLaunchCache.EditorClearOverride();
            BankUpgradeBootstrap.EditorSetRestWifiConfirmDeclined(false);
        }

        [Test]
        public void ShowRequestFirst_WhenBackgroundConfirmOn()
        {
            Assert.IsTrue(SceneBankGateProgressPolicy.ShouldShowRequestFirst(silentBackgroundRest: false));
        }

        [Test]
        public void SkipRequestFirst_WhenSilentBackgroundRest()
        {
            // 后台弹窗关 / 已关 §4.6：进区域门直接优先下 + 进度，不弹「前方区域未下载」。
            Assert.IsFalse(SceneBankGateProgressPolicy.ShouldShowRequestFirst(silentBackgroundRest: true));
        }

        [Test]
        public void SilentBackgroundRest_FeatureOff()
        {
            BoluoAppLaunchCache.EditorCancelAccountOverride = BoluoAppLaunchCache.FeatureOff;
            BankUpgradeBootstrap.EditorSetRestWifiConfirmDeclined(false);
            Assert.IsTrue(BankUpgradeBootstrap.IsSilentBackgroundRest);
        }

        [Test]
        public void SilentBackgroundRest_DeclinedWhileFeatureOn()
        {
            BoluoAppLaunchCache.EditorCancelAccountOverride = BoluoAppLaunchCache.FeatureOn;
            BankUpgradeBootstrap.EditorSetRestWifiConfirmDeclined(true);
            Assert.IsTrue(BankUpgradeBootstrap.IsSilentBackgroundRest);
            // 未接受 §4.6 / 未在下：首次仍可申请窗。
            Assert.IsTrue(
                BackgroundDownloadPolicy.ShouldShowGateRequestFirst(
                    wifiConfirmEnabled: true,
                    wifiAcceptedOrDownloading: false));
        }

        [Test]
        public void NotSilent_FeatureOn_NotDeclined()
        {
            BoluoAppLaunchCache.EditorCancelAccountOverride = BoluoAppLaunchCache.FeatureOn;
            BankUpgradeBootstrap.EditorSetRestWifiConfirmDeclined(false);
            Assert.IsFalse(BankUpgradeBootstrap.IsSilentBackgroundRest);
            Assert.IsTrue(
                BackgroundDownloadPolicy.ShouldShowGateRequestFirst(
                    wifiConfirmEnabled: true,
                    wifiAcceptedOrDownloading: false));
        }

        [Test]
        public void GateRequestFirst_FeatureOn_AfterConfirm_Skipped()
        {
            Assert.IsFalse(
                BackgroundDownloadPolicy.ShouldShowGateRequestFirst(
                    wifiConfirmEnabled: true,
                    wifiAcceptedOrDownloading: true));
        }

        [Test]
        public void GateRequestFirst_FeatureOff_Skipped()
        {
            Assert.IsFalse(
                BackgroundDownloadPolicy.ShouldShowGateRequestFirst(wifiConfirmEnabled: false));
            Assert.IsFalse(
                SceneBankGateProgressPolicy.ShouldShowRequestFirst(silentBackgroundRest: true));
        }

        [Test]
        public void OrphanedOpen_Clears_WhenUiGone()
        {
            Assert.IsTrue(
                SceneBankGateProgressPolicy.ShouldClearOrphanedProgressOpen(
                    progressOpenFlag: true,
                    uiOpenForTicket: false,
                    anyActiveProgressForm: false,
                    hasOtherPopup: false));
        }

        [Test]
        public void OrphanedOpen_Keeps_WhenConsentPopupUp()
        {
            Assert.IsFalse(
                SceneBankGateProgressPolicy.ShouldClearOrphanedProgressOpen(
                    progressOpenFlag: true,
                    uiOpenForTicket: false,
                    anyActiveProgressForm: false,
                    hasOtherPopup: true));
        }

        [Test]
        public void Reopen_WhenInsideAndUiMissing()
        {
            Assert.IsTrue(
                SceneBankGateProgressPolicy.ShouldAttemptReopen(
                    playerInsideBlockedGate: true,
                    progressOpenFlag: false,
                    uiOpenForTicket: false,
                    ticketValidAndNotRetired: true,
                    hasOtherPopup: false));
        }

        [Test]
        public void Reopen_Blocked_WhileFlagSaysOpen()
        {
            Assert.IsFalse(
                SceneBankGateProgressPolicy.ShouldAttemptReopen(
                    playerInsideBlockedGate: true,
                    progressOpenFlag: true,
                    uiOpenForTicket: false,
                    ticketValidAndNotRetired: true,
                    hasOtherPopup: false));
        }

        [Test]
        public void CloseTicket_DoesNotRetireOther()
        {
            Assert.IsTrue(SceneBankGateProgressPolicy.ClosingShouldRetireOnlySelf(2, 1));
            Assert.IsFalse(SceneBankGateProgressPolicy.ClosingShouldRetireOnlySelf(1, 1));
        }

        [Test]
        public void PreferReopen_WhenSpawnerBusy()
        {
            Assert.IsTrue(
                SceneBankGateProgressPolicy.ShouldPreferReopenOverNewPrompt(
                    spawnerBusy: true,
                    flowBusy: false,
                    activeProgressTicket: 0));
        }

        [Test]
        public void PreferReopen_WhenFlowBusyWithTicket()
        {
            Assert.IsTrue(
                SceneBankGateProgressPolicy.ShouldPreferReopenOverNewPrompt(
                    spawnerBusy: false,
                    flowBusy: true,
                    activeProgressTicket: 3));
        }

        [Test]
        public void PreferNewPrompt_WhenFlowBusySuppressNoTicket()
        {
            // 环境音/预设 SuppressProgress：IsBusy 但无票 → 门口仍要走申请+进度。
            Assert.IsFalse(
                SceneBankGateProgressPolicy.ShouldPreferReopenOverNewPrompt(
                    spawnerBusy: false,
                    flowBusy: true,
                    activeProgressTicket: 0));
        }

        [Test]
        public void ClearOrphanBusy_WhenSpawnerBusyFlowIdleNoTicket()
        {
            // 流量切 Wi‑Fi → §4.6 后 Progress 未开票：须清 busy，否则 PreferReopen 空转。
            Assert.IsTrue(
                SceneBankGateProgressPolicy.ShouldClearOrphanedSpawnerBusy(
                    spawnerBusy: true,
                    flowBusy: false,
                    activeProgressTicket: 0,
                    hasOtherPopup: false,
                    consentPromptActive: false));
        }

        [Test]
        public void KeepBusy_WhileFlowBusyOrTicketOrPopupOrConsent()
        {
            Assert.IsFalse(
                SceneBankGateProgressPolicy.ShouldClearOrphanedSpawnerBusy(
                    spawnerBusy: true,
                    flowBusy: true,
                    activeProgressTicket: 0,
                    hasOtherPopup: false));
            Assert.IsFalse(
                SceneBankGateProgressPolicy.ShouldClearOrphanedSpawnerBusy(
                    spawnerBusy: true,
                    flowBusy: false,
                    activeProgressTicket: 3,
                    hasOtherPopup: false));
            Assert.IsFalse(
                SceneBankGateProgressPolicy.ShouldClearOrphanedSpawnerBusy(
                    spawnerBusy: true,
                    flowBusy: false,
                    activeProgressTicket: 0,
                    hasOtherPopup: true));
            // 异步 Open §4.6 / 申请窗期间 HasPopup 可能仍 false，不能清 busy。
            Assert.IsFalse(
                SceneBankGateProgressPolicy.ShouldClearOrphanedSpawnerBusy(
                    spawnerBusy: true,
                    flowBusy: false,
                    activeProgressTicket: 0,
                    hasOtherPopup: false,
                    consentPromptActive: true));
        }
    }
}
