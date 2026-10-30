using System.Collections.Generic;
using System.IO;
using Game.BoluoApi;
using NUnit.Framework;
using UnityEngine;

namespace Game.Tests
{
    /// <summary>
    /// 同意会话：流量窗只由用户 EnsureReady 打开；Notify / Rest 静默。
    /// 蜂窝提示用例须用盘上不存在的 stem（Music1_1 / Ocean_* 在 StreamingAssets 已就绪）。
    /// </summary>
    public sealed class DownloadConsentSessionTests
    {
        /// <summary>故意不在 Streaming/persistent 的假名，保证 IsLocalReady=false。</summary>
        const string PendingBank = "NeedDownload_ConsentTest_A";

        [SetUp]
        public void SetUp()
        {
            DownloadConsentSession.ResetForLeaveIsland();
            DownloadCellularGate.ResetForNewSession();
            DownloadCellularGate.EditorSimulateMobilePlatform = true;
            DownloadCellularGate.EditorReachabilityOverride =
                NetworkReachability.ReachableViaCarrierDataNetwork;
            BankDownloader.InvalidateLocalReadyCache();
        }

        [TearDown]
        public void TearDown()
        {
            DownloadConsentSession.ResetForLeaveIsland();
            DownloadCellularGate.EditorClearReachabilityOverride();
            DownloadCellularGate.ResetForNewSession();
            BankDownloader.InvalidateLocalReadyCache();
        }

        [Test]
        public void Notify_NeverOpensMobile_StaysIdle()
        {
            DownloadConsentSession.NotifyNeedCellularConsent(
                new List<string> { PendingBank, "NeedDownload_ConsentTest_B" });

            Assert.AreEqual("Idle", DownloadConsentSession.EditorPhaseName);
            Assert.IsFalse(DownloadConsentSession.EditorHasPendingReady);
        }

        [Test]
        public void SecondEnsureReady_ReplacesContinue_WhileMobilePending()
        {
            int first = 0;
            int second = 0;
            var banks = new List<string> { PendingBank };

            DownloadConsentSession.EditorArmMobilePending(
                banks,
                onReady: () => first++);

            Assert.IsTrue(DownloadConsentSession.EditorHasPendingReady);
            Assert.AreEqual("MobileConfirm", DownloadConsentSession.EditorPhaseName);

            DownloadConsentSession.EnsureReady(
                banks,
                onReady: () => second++);

            Assert.IsTrue(DownloadConsentSession.EditorHasPendingReady);
            DownloadConsentSession.EditorInvokeMobileAllow();

            Assert.AreEqual(0, first);
            Assert.AreEqual(1, second);
            Assert.IsFalse(DownloadConsentSession.EditorHasPendingReady);
        }

        [Test]
        public void NotifyThenEnsureReady_OnlyUserClickOpensMobile()
        {
            int fromUser = 0;
            var busy = new List<string> { "NeedDownload_ConsentTest_Busy" };
            var userBanks = new List<string> { PendingBank };

            DownloadConsentSession.NotifyNeedCellularConsent(busy);
            Assert.AreEqual("Idle", DownloadConsentSession.EditorPhaseName);

            DownloadConsentSession.EnsureReady(
                userBanks,
                onReady: () => fromUser++);

            Assert.AreEqual("MobileConfirm", DownloadConsentSession.EditorPhaseName);
            Assert.IsTrue(DownloadConsentSession.EditorHasPendingReady);
            DownloadConsentSession.EditorInvokeMobileAllow();

            Assert.AreEqual(1, fromUser);
        }

        [Test]
        public void BindReadyIfEmpty_DoesNotOverwriteUserReady()
        {
            int rest = 0;
            int user = 0;
            var banks = new List<string> { "NeedDownload_A", "NeedDownload_B" };

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
            var banks = new List<string> { PendingBank };
            int ready = 0;

            DownloadConsentSession.EditorArmMobilePending(
                banks,
                onReady: () => ready++);
            DownloadConsentSession.EditorInvokeMobileWifiOnly();

            Assert.AreEqual(0, ready);
            Assert.AreEqual("Idle", DownloadConsentSession.EditorPhaseName);

            DownloadConsentSession.NotifyNeedCellularConsent(banks);
            Assert.AreEqual("Idle", DownloadConsentSession.EditorPhaseName);

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
                new List<string> { PendingBank },
                onReady: () => ready++);

            Assert.AreEqual(1, ready);
            Assert.AreEqual("Idle", DownloadConsentSession.EditorPhaseName);
        }

        [Test]
        public void ShowRequestFirst_NotifySilent_MobileOnlyAfterConfirm()
        {
            int ready = 0;
            var banks = new List<string> { PendingBank };

            DownloadConsentSession.EnsureReady(
                banks,
                onReady: () => ready++,
                new DownloadUiOptions { ShowRequestFirst = true });

            Assert.AreEqual("RequestPrompt", DownloadConsentSession.EditorPhaseName);

            DownloadConsentSession.NotifyNeedCellularConsent(banks);
            Assert.AreEqual("RequestPrompt", DownloadConsentSession.EditorPhaseName);
            Assert.AreEqual(0, ready);

            DownloadConsentSession.BeginRequestConfirmed();
            Assert.AreEqual("Checking", DownloadConsentSession.EditorPhaseName);
            DownloadConsentSession.EditorInvokeRequestConfirmed();
            Assert.AreEqual("MobileConfirm", DownloadConsentSession.EditorPhaseName);
            Assert.AreEqual(0, ready);

            DownloadConsentSession.EditorInvokeMobileAllow();
            Assert.AreEqual(1, ready);
            Assert.AreEqual("Idle", DownloadConsentSession.EditorPhaseName);
        }

        [Test]
        public void UserEnsureReady_Cellular_OpensMobileBeforeReady()
        {
            int ready = 0;
            DownloadConsentSession.EnsureReady(
                new List<string> { PendingBank },
                onReady: () => ready++);

            Assert.AreEqual(0, ready);
            Assert.AreEqual("MobileConfirm", DownloadConsentSession.EditorPhaseName);

            DownloadConsentSession.EditorInvokeMobileAllow();
            Assert.AreEqual(1, ready);
        }

        [Test]
        public void SecondUserDownload_Cellular_PromptsAgainAfterRevoke()
        {
            int first = 0;
            int second = 0;
            var banks = new List<string> { PendingBank };

            DownloadConsentSession.EnsureReady(banks, onReady: () => first++);
            Assert.AreEqual("MobileConfirm", DownloadConsentSession.EditorPhaseName);
            DownloadConsentSession.EditorInvokeMobileAllow();
            Assert.AreEqual(1, first);
            Assert.IsTrue(DownloadCellularGate.AllowMobileNetwork);

            // 批次结束撤销开闸（Flow finally 同款）。
            DownloadCellularGate.RevokeCellularAllow();
            Assert.IsFalse(DownloadCellularGate.AllowMobileNetwork);

            DownloadConsentSession.EnsureReady(banks, onReady: () => second++);
            Assert.AreEqual("MobileConfirm", DownloadConsentSession.EditorPhaseName);
            Assert.AreEqual(0, second);
            DownloadConsentSession.EditorInvokeMobileAllow();
            Assert.AreEqual(1, second);
        }

        [Test]
        public void EnsureReady_RevokesPriorAllow_SoNeedsPromptAgain()
        {
            DownloadCellularGate.UserAllowCellularAlways();
            Assert.IsFalse(DownloadCellularGate.NeedsMobileDataPrompt());

            int ready = 0;
            DownloadConsentSession.EnsureReady(
                new List<string> { PendingBank },
                onReady: () => ready++);

            // EnsureReady 开头 Revoke → 蜂窝仍需确认。
            Assert.AreEqual("MobileConfirm", DownloadConsentSession.EditorPhaseName);
            Assert.AreEqual(0, ready);
        }

        [Test]
        public void EnsureReady_AllLocalReady_FiresWithoutMobile_EvenOnCellular()
        {
            const string readyStem = "ConsentReady_SkipTest";
            string path = null;
            try
            {
                AudioEnginePaths.EnsureWritableDirs();
                path = Path.Combine(
                    AudioEnginePaths.PersistentBankPath,
                    readyStem + AudioEnginePaths.BankExtension);
                File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4 });
                BankDownloader.InvalidateLocalReadyCache(readyStem);
                Assert.IsTrue(BankDownloader.IsLocalReady(readyStem));

                int ready = 0;
                DownloadConsentSession.EnsureReady(
                    new List<string> { readyStem },
                    onReady: () => ready++);

                Assert.AreEqual(1, ready);
                Assert.AreEqual("Idle", DownloadConsentSession.EditorPhaseName);
            }
            finally
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    File.Delete(path);
                BankDownloader.InvalidateLocalReadyCache(readyStem);
            }
        }

        [Test]
        public void EnsureReady_Offline_AllLocalReady_StillFires()
        {
            const string readyStem = "ConsentReady_OfflineSkip";
            string path = null;
            try
            {
                DownloadCellularGate.EditorReachabilityOverride =
                    NetworkReachability.NotReachable;

                AudioEnginePaths.EnsureWritableDirs();
                path = Path.Combine(
                    AudioEnginePaths.PersistentBankPath,
                    readyStem + AudioEnginePaths.BankExtension);
                File.WriteAllBytes(path, new byte[] { 9 });
                BankDownloader.InvalidateLocalReadyCache(readyStem);

                int ready = 0;
                int cancel = 0;
                DownloadConsentSession.EnsureReady(
                    new List<string> { readyStem },
                    onReady: () => ready++,
                    new DownloadUiOptions { OnCancel = () => cancel++ });

                Assert.AreEqual(1, ready);
                Assert.AreEqual(0, cancel);
                Assert.AreEqual("Idle", DownloadConsentSession.EditorPhaseName);
            }
            finally
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    File.Delete(path);
                BankDownloader.InvalidateLocalReadyCache(readyStem);
            }
        }
    }
}
