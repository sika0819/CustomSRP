using Game.BoluoApi;
using NUnit.Framework;
using UnityEngine;

namespace Game.Tests
{
    /// <summary>FeatureOn+WiFi：同意链体积可全量，下载列表不扩全量。</summary>
    public sealed class BackgroundDownloadCoordinatorTests
    {
        [SetUp]
        public void SetUp()
        {
            DownloadConsentSession.ResetForLeaveIsland();
            DownloadCellularGate.ResetForNewSession();
            DownloadCellularGate.EditorSimulateMobilePlatform = true;
            BoluoAppLaunchCache.EditorClearOverride();
        }

        [TearDown]
        public void TearDown()
        {
            DownloadConsentSession.ResetForLeaveIsland();
            DownloadCellularGate.EditorClearReachabilityOverride();
            DownloadCellularGate.ResetForNewSession();
            BoluoAppLaunchCache.EditorClearOverride();
        }

        [Test]
        public void BanksForWifiConsentSize_FeatureOff_ReturnsClicked()
        {
            BoluoAppLaunchCache.EditorCancelAccountOverride = BoluoAppLaunchCache.FeatureOff;
            DownloadCellularGate.EditorReachabilityOverride =
                NetworkReachability.ReachableViaLocalAreaNetwork;

            string[] clicked = { "Zone_A", "Zone_B" };
            string[] result = BackgroundDownloadCoordinator.BanksForWifiConsentSize(clicked);
            Assert.AreSame(clicked, result);
        }

        [Test]
        public void BanksForWifiConsentSize_FeatureOnWifi_BeforeAccept_PrefersAllPendingWhenCatalogHasAny()
        {
            BoluoAppLaunchCache.EditorCancelAccountOverride = BoluoAppLaunchCache.FeatureOn;
            DownloadCellularGate.EditorReachabilityOverride =
                NetworkReachability.ReachableViaLocalAreaNetwork;
            Assert.IsFalse(DownloadConsentSession.WifiConfirmAccepted);

            string[] clicked = { "OnlyClicked_ConsentSize" };
            // catalog 空时 CollectAllPending 为空 → 回退 clicked（单测无 catalog 注入）。
            string[] result = BackgroundDownloadCoordinator.BanksForWifiConsentSize(clicked);
            Assert.AreEqual(1, result.Length);
            Assert.AreEqual(clicked[0], result[0]);
        }

        [Test]
        public void BanksForWifiConsentSize_FeatureOnWifi_AfterAccept_ReturnsClicked()
        {
            BoluoAppLaunchCache.EditorCancelAccountOverride = BoluoAppLaunchCache.FeatureOn;
            DownloadCellularGate.EditorReachabilityOverride =
                NetworkReachability.ReachableViaLocalAreaNetwork;

            DownloadConsentSession.EnsureReady(
                new[] { "NeedDownload_WifiSize_A" },
                onReady: () => { });
            Assert.AreEqual("WifiConfirm", DownloadConsentSession.EditorPhaseName);
            DownloadConsentSession.EditorInvokeWifiConfirm();
            Assert.IsTrue(DownloadConsentSession.WifiConfirmAccepted);

            string[] clicked = { "Zone_Only" };
            string[] result = BackgroundDownloadCoordinator.BanksForWifiConsentSize(clicked);
            Assert.AreSame(clicked, result);
        }

        [Test]
        public void BanksForWifiConsentSize_FeatureOnCellular_ReturnsClicked()
        {
            BoluoAppLaunchCache.EditorCancelAccountOverride = BoluoAppLaunchCache.FeatureOn;
            DownloadCellularGate.EditorReachabilityOverride =
                NetworkReachability.ReachableViaCarrierDataNetwork;

            string[] clicked = { "Mob_A" };
            string[] result = BackgroundDownloadCoordinator.BanksForWifiConsentSize(clicked);
            Assert.AreSame(clicked, result);
        }

        [Test]
        public void ShouldWifiConfirmOnceThenDownloadAll_MeansSilentKickNotUserFlowExpand()
        {
            // 策略语义：开+WiFi → 确认后 KickSilentAll；下载列表仍是点击 pending。
            Assert.IsTrue(
                BackgroundDownloadPolicy.ShouldWifiConfirmOnceThenDownloadAll(
                    wifiConfirmEnabled: true,
                    isWifiOrLan: true));
        }
    }
}
