using System.Collections.Generic;
using Game.BoluoApi;
using NUnit.Framework;
using UnityEngine;

namespace Game.Tests
{
    /// <summary>
    /// 同意会话：窗已开时替换 continue；RunLoop Notify 后用户 EnsureReady 不丢回调。
    /// </summary>
    public sealed class DownloadConsentSessionTests
    {
        [SetUp]
        public void SetUp()
        {
            DownloadConsentSession.ResetForLeaveIsland();
            DownloadCellularGate.ResetForNewSession();
            DownloadCellularGate.EditorSimulateMobilePlatform = true;
            DownloadCellularGate.EditorReachabilityOverride =
                NetworkReachability.ReachableViaCarrierDataNetwork;
        }

        [TearDown]
        public void TearDown()
        {
            DownloadConsentSession.ResetForLeaveIsland();
            DownloadCellularGate.EditorClearReachabilityOverride();
            DownloadCellularGate.ResetForNewSession();
        }

        [Test]
        public void SecondEnsureReady_ReplacesContinue_WhileMobilePending()
        {
            int first = 0;
            int second = 0;
            var banks = new List<string> { "Music1_1" };

            DownloadConsentSession.EditorArmMobilePending(
                banks,
                onReady: () => first++);

            Assert.IsTrue(DownloadConsentSession.EditorHasPendingReady);
            Assert.AreEqual("MobileConfirm", DownloadConsentSession.EditorPhaseName);

            DownloadConsentSession.EnsureReady(
                banks,
                onReady: () => second++);

            // 仍挂着待确认；点继续应只走第二次。
            Assert.IsTrue(DownloadConsentSession.EditorHasPendingReady);
            DownloadConsentSession.EditorInvokeMobileAllow();

            Assert.AreEqual(0, first);
            Assert.AreEqual(1, second);
            Assert.IsFalse(DownloadConsentSession.EditorHasPendingReady);
        }

        [Test]
        public void NotifyThenEnsureReady_ContinueIsUserCallback()
        {
            int fromNotifyPath = 0;
            int fromUser = 0;
            var busy = new List<string> { "Ocean_Calm_Quad" };
            var userBanks = new List<string> { "Music1_1" };

            // RunLoop 风格：只通知，不带 continue。
            DownloadConsentSession.NotifyNeedCellularConsent(busy);
            Assert.AreEqual("MobileConfirm", DownloadConsentSession.EditorPhaseName);
            Assert.IsFalse(DownloadConsentSession.EditorHasPendingReady);

            // 用户点下载：替换/挂上 continue。
            DownloadConsentSession.EnsureReady(
                userBanks,
                onReady: () => fromUser++);

            Assert.IsTrue(DownloadConsentSession.EditorHasPendingReady);
            DownloadConsentSession.EditorInvokeMobileAllow();

            Assert.AreEqual(0, fromNotifyPath);
            Assert.AreEqual(1, fromUser);
        }

        [Test]
        public void BindReadyIfEmpty_DoesNotOverwriteUserReady()
        {
            int rest = 0;
            int user = 0;
            var banks = new List<string> { "A", "B" };

            DownloadConsentSession.EditorArmMobilePending(
                banks,
                onReady: () => user++);

            DownloadConsentSession.BindReadyIfEmpty(() => rest++);
            DownloadConsentSession.EditorInvokeMobileAllow();

            Assert.AreEqual(1, user);
            Assert.AreEqual(0, rest);
        }

        [Test]
        public void WifiOnly_LatchesSilentNotify_UntilUserEnsureReady()
        {
            var banks = new List<string> { "Music1_1" };
            int ready = 0;

            DownloadConsentSession.EditorArmMobilePending(
                banks,
                onReady: () => ready++);
            DownloadConsentSession.EditorInvokeMobileWifiOnly();

            Assert.AreEqual(0, ready);
            Assert.AreEqual("Idle", DownloadConsentSession.EditorPhaseName);

            // 静默 Notify 不应再进 MobileConfirm。
            DownloadConsentSession.NotifyNeedCellularConsent(banks);
            Assert.AreEqual("Idle", DownloadConsentSession.EditorPhaseName);

            // 用户再次 EnsureReady 应清闩（会走 ShowMobile，phase→MobileConfirm；
            // 无 UI 时仍进入 ShowMobileConfirm 设 phase）。
            DownloadConsentSession.EnsureReady(banks, onReady: () => ready++);
            Assert.AreEqual("MobileConfirm", DownloadConsentSession.EditorPhaseName);
        }

        [Test]
        public void WifiPass_EnsureReady_FiresSyncWithoutMobile()
        {
            DownloadCellularGate.EditorReachabilityOverride =
                NetworkReachability.ReachableViaLocalAreaNetwork;

            int ready = 0;
            DownloadConsentSession.EnsureReady(
                new List<string> { "Music1_1" },
                onReady: () => ready++);

            Assert.AreEqual(1, ready);
            Assert.AreEqual("Idle", DownloadConsentSession.EditorPhaseName);
        }
    }
}
