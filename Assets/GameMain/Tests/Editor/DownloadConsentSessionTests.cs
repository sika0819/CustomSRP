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
            BoluoAppLaunchCache.EditorClearOverride();
            BankDownloader.InvalidateLocalReadyCache();
        }

        [TearDown]
        public void TearDown()
        {
            DownloadConsentSession.ResetForLeaveIsland();
            DownloadCellularGate.EditorClearReachabilityOverride();
            DownloadCellularGate.ResetForNewSession();
            BoluoAppLaunchCache.EditorClearOverride();
            BankDownloader.InvalidateLocalReadyCache();
        }

        [Test]
        public void Notify_FeatureOff_NeverOpensMobile_StaysIdle()
        {
            BoluoAppLaunchCache.EditorCancelAccountOverride = BoluoAppLaunchCache.FeatureOff;
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
        public void NotifyThenEnsureReady_FeatureOff_NotifyStaysIdle_UserEnsureAutoAllows()
        {
            BoluoAppLaunchCache.EditorCancelAccountOverride = BoluoAppLaunchCache.FeatureOff;
            int fromUser = 0;
            var busy = new List<string> { "NeedDownload_ConsentTest_Busy" };
            var userBanks = new List<string> { PendingBank };

            DownloadConsentSession.NotifyNeedCellularConsent(busy);
            Assert.AreEqual("Idle", DownloadConsentSession.EditorPhaseName);

            DownloadConsentSession.EnsureReady(
                userBanks,
                onReady: () => fromUser++);

            // FeatureOff：EnsureReady 自动开闸，不弹流量窗。
            Assert.AreEqual(1, fromUser);
            Assert.AreEqual("Idle", DownloadConsentSession.EditorPhaseName);
            Assert.IsTrue(DownloadCellularGate.AllowMobileNetwork);
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
            BoluoAppLaunchCache.EditorCancelAccountOverride = BoluoAppLaunchCache.FeatureOn;
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
            BoluoAppLaunchCache.EditorCancelAccountOverride = BoluoAppLaunchCache.FeatureOn;
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
        public void FeatureOff_Cellular_AutoAllow_NoMobilePrompt()
        {
            // FeatureOff：蜂窝零弹窗，自动开闸后直接 Ready。
            BoluoAppLaunchCache.EditorCancelAccountOverride = BoluoAppLaunchCache.FeatureOff;
            DownloadCellularGate.EditorReachabilityOverride =
                NetworkReachability.ReachableViaCarrierDataNetwork;

            int ready = 0;
            DownloadConsentSession.EnsureReady(
                new List<string> { PendingBank },
                onReady: () => ready++);

            Assert.AreEqual(1, ready);
            Assert.AreEqual("Idle", DownloadConsentSession.EditorPhaseName);
            Assert.IsTrue(DownloadCellularGate.AllowMobileNetwork);
        }

        [Test]
        public void SecondUserDownload_Cellular_SkipsPromptWhileSessionAllowed()
        {
            // FeatureOn：流量确认后会话开闸，后续不重弹。
            BoluoAppLaunchCache.EditorCancelAccountOverride = BoluoAppLaunchCache.FeatureOn;
            int first = 0;
            int second = 0;
            var banks = new List<string> { PendingBank };

            DownloadConsentSession.EnsureReady(banks, onReady: () => first++);
            Assert.AreEqual("MobileConfirm", DownloadConsentSession.EditorPhaseName);
            DownloadConsentSession.EditorInvokeMobileAllow();
            Assert.AreEqual(1, first);
            Assert.IsTrue(DownloadCellularGate.AllowMobileNetwork);

            DownloadConsentSession.EnsureReady(banks, onReady: () => second++);
            Assert.AreEqual(1, second);
            Assert.AreEqual("Idle", DownloadConsentSession.EditorPhaseName);
            Assert.IsTrue(DownloadCellularGate.AllowMobileNetwork);
        }

        [Test]
        public void FeatureOn_Cellular_SessionAllow_SkipsSecondPrompt_UntilWifiRevoke()
        {
            // FeatureOn：流量确认后会话开闸，回 Wi‑Fi 前不再弹。
            BoluoAppLaunchCache.EditorCancelAccountOverride = BoluoAppLaunchCache.FeatureOn;
            DownloadCellularGate.EditorReachabilityOverride =
                NetworkReachability.ReachableViaCarrierDataNetwork;

            int first = 0;
            int second = 0;
            var banks = new List<string> { PendingBank };

            DownloadConsentSession.EnsureReady(banks, onReady: () => first++);
            Assert.AreEqual("MobileConfirm", DownloadConsentSession.EditorPhaseName);
            DownloadConsentSession.EditorInvokeMobileAllow();
            Assert.AreEqual(1, first);
            Assert.IsTrue(DownloadCellularGate.AllowMobileNetwork);

            DownloadConsentSession.EnsureReady(banks, onReady: () => second++);
            Assert.AreEqual(1, second);
            Assert.AreEqual("Idle", DownloadConsentSession.EditorPhaseName);
            Assert.IsTrue(DownloadCellularGate.AllowMobileNetwork);
        }

        [Test]
        public void FeatureOn_NotifyNeedCellular_OpensMobileWhenBusy()
        {
            BoluoAppLaunchCache.EditorCancelAccountOverride = BoluoAppLaunchCache.FeatureOn;
            DownloadCellularGate.EditorReachabilityOverride =
                NetworkReachability.ReachableViaCarrierDataNetwork;

            DownloadConsentSession.NotifyNeedCellularConsent(
                new List<string> { PendingBank });

            Assert.AreEqual("MobileConfirm", DownloadConsentSession.EditorPhaseName);
            DownloadConsentSession.EditorInvokeMobileAllow();
            Assert.IsTrue(DownloadCellularGate.AllowMobileNetwork);
            Assert.AreEqual("Idle", DownloadConsentSession.EditorPhaseName);
        }

        [Test]
        public void EnsureReady_KeepsPriorAllow_NoSecondMobilePrompt()
        {
            DownloadCellularGate.UserAllowCellularAlways();
            Assert.IsFalse(DownloadCellularGate.NeedsMobileDataPrompt());

            int ready = 0;
            DownloadConsentSession.EnsureReady(
                new List<string> { PendingBank },
                onReady: () => ready++);

            Assert.AreEqual(1, ready);
            Assert.AreEqual("Idle", DownloadConsentSession.EditorPhaseName);
        }

        [Test]
        public void EnsureReady_Wifi_FeatureOn_ShowsWifiConfirm()
        {
            BoluoAppLaunchCache.EditorCancelAccountOverride = BoluoAppLaunchCache.FeatureOn;
            DownloadCellularGate.EditorReachabilityOverride =
                NetworkReachability.ReachableViaLocalAreaNetwork;

            int ready = 0;
            int later = 0;
            DownloadConsentSession.EnsureReady(
                new List<string> { PendingBank },
                onReady: () => ready++,
                new DownloadUiOptions { OnWifiConfirmLater = () => later++ });

            Assert.AreEqual(0, ready);
            Assert.AreEqual("WifiConfirm", DownloadConsentSession.EditorPhaseName);
            DownloadConsentSession.EditorInvokeWifiConfirm();
            Assert.AreEqual(1, ready);
            Assert.IsTrue(DownloadConsentSession.EditorWifiConfirmAccepted);

            // 本会话已确认：再次 EnsureReady 直接过。
            int second = 0;
            DownloadConsentSession.EnsureReady(
                new List<string> { PendingBank },
                onReady: () => second++);
            Assert.AreEqual(1, second);
            Assert.AreEqual(0, later);
        }

        [Test]
        public void BeginWifiConfirmed_BeforeClose_LatchesAccepted_SurvivesNotifyClose()
        {
            // 模拟 WifiConfirmForm：先 BeginWifiConfirmed → Close → OnDownload(FireReady)。
            BoluoAppLaunchCache.EditorCancelAccountOverride = BoluoAppLaunchCache.FeatureOn;
            DownloadCellularGate.EditorReachabilityOverride =
                NetworkReachability.ReachableViaLocalAreaNetwork;

            int ready = 0;
            DownloadConsentSession.EnsureReady(
                new List<string> { PendingBank },
                onReady: () => ready++);

            Assert.AreEqual("WifiConfirm", DownloadConsentSession.EditorPhaseName);
            DownloadConsentSession.BeginWifiConfirmed();
            Assert.IsTrue(DownloadConsentSession.EditorWifiConfirmAccepted);
            Assert.AreEqual("Checking", DownloadConsentSession.EditorPhaseName);

            // Form.OnClose → NotifyWifiDialogClosed 不得把 phase 打回 Idle 冲掉 Ready。
            DownloadConsentSession.NotifyWifiDialogClosed();
            Assert.AreEqual("Checking", DownloadConsentSession.EditorPhaseName);

            DownloadConsentSession.EditorInvokeWifiConfirm();
            Assert.AreEqual(1, ready);
            Assert.AreEqual("Idle", DownloadConsentSession.EditorPhaseName);
        }

        [Test]
        public void CarrierToWifi_RegionRequest_ThenWifiConfirm_FiresReady()
        {
            // 流量 → Wi‑Fi 后再进区域门：申请窗 → §4.6 → Ready（进度窗由 Flow 开）。
            BoluoAppLaunchCache.EditorCancelAccountOverride = BoluoAppLaunchCache.FeatureOn;
            DownloadCellularGate.EditorReachabilityOverride =
                NetworkReachability.ReachableViaCarrierDataNetwork;
            DownloadCellularGate.EditorReachabilityOverride =
                NetworkReachability.ReachableViaLocalAreaNetwork;

            int ready = 0;
            DownloadConsentSession.EnsureReady(
                new List<string> { PendingBank },
                onReady: () => ready++,
                new DownloadUiOptions { ShowRequestFirst = true });

            Assert.AreEqual("RequestPrompt", DownloadConsentSession.EditorPhaseName);
            DownloadConsentSession.BeginRequestConfirmed();
            DownloadConsentSession.EditorInvokeRequestConfirmed();
            Assert.AreEqual("WifiConfirm", DownloadConsentSession.EditorPhaseName);

            DownloadConsentSession.BeginWifiConfirmed();
            DownloadConsentSession.NotifyWifiDialogClosed();
            DownloadConsentSession.EditorInvokeWifiConfirm();
            Assert.AreEqual(1, ready);
            Assert.IsTrue(DownloadConsentSession.EditorWifiConfirmAccepted);
        }

        [Test]
        public void EnsureReady_Wifi_FeatureOn_Later_Cancels()
        {
            BoluoAppLaunchCache.EditorCancelAccountOverride = BoluoAppLaunchCache.FeatureOn;
            DownloadCellularGate.EditorReachabilityOverride =
                NetworkReachability.ReachableViaLocalAreaNetwork;

            int ready = 0;
            int later = 0;
            int cancel = 0;
            DownloadConsentSession.EnsureReady(
                new List<string> { PendingBank },
                onReady: () => ready++,
                new DownloadUiOptions
                {
                    OnCancel = () => cancel++,
                    OnWifiConfirmLater = () => later++,
                });

            Assert.AreEqual("WifiConfirm", DownloadConsentSession.EditorPhaseName);
            Assert.IsTrue(DownloadConsentSession.IsPromptActive);
            DownloadConsentSession.EditorInvokeWifiLater();
            Assert.AreEqual(0, ready);
            Assert.AreEqual(1, later);
            Assert.AreEqual(1, cancel);
            Assert.IsFalse(DownloadConsentSession.EditorWifiConfirmAccepted);
            Assert.IsFalse(DownloadConsentSession.IsPromptActive);
        }

        [Test]
        public void ShowRequestFirst_Return_FiresOnCancel_ClearsPromptActive()
        {
            DownloadCellularGate.EditorReachabilityOverride =
                NetworkReachability.ReachableViaLocalAreaNetwork;

            int cancel = 0;
            DownloadConsentSession.EnsureReady(
                new List<string> { PendingBank },
                onReady: () => { },
                new DownloadUiOptions
                {
                    ShowRequestFirst = true,
                    OnCancel = () => cancel++,
                });

            Assert.AreEqual("RequestPrompt", DownloadConsentSession.EditorPhaseName);
            Assert.IsTrue(DownloadConsentSession.IsPromptActive);
            DownloadConsentSession.EditorInvokeRequestReturn();
            Assert.AreEqual(1, cancel);
            Assert.IsFalse(DownloadConsentSession.IsPromptActive);
        }

        [Test]
        public void EnsureReady_Wifi_FeatureOff_FiresReady()
        {
            BoluoAppLaunchCache.EditorCancelAccountOverride = BoluoAppLaunchCache.FeatureOff;
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
        public void FeatureOff_Cellular_NoMobileConfirm_AutoAllow()
        {
            // 关弹窗：蜂窝零确认窗，自动开闸。
            BoluoAppLaunchCache.EditorCancelAccountOverride = BoluoAppLaunchCache.FeatureOff;
            DownloadCellularGate.EditorReachabilityOverride =
                NetworkReachability.ReachableViaCarrierDataNetwork;

            int ready = 0;
            DownloadConsentSession.EnsureReady(
                new List<string> { PendingBank },
                onReady: () => ready++);

            Assert.AreEqual(1, ready);
            Assert.AreEqual("Idle", DownloadConsentSession.EditorPhaseName);
            Assert.IsTrue(DownloadCellularGate.AllowMobileNetwork);
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
        public void ShowRequestFirst_AllLocalReady_SkipsRequestPrompt()
        {
            // Critical 申请窗路径：盘上已齐时不得再弹「前方区域未下载」。
            const string readyStem = "ConsentReady_RequestSkip";
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
                    onReady: () => ready++,
                    new DownloadUiOptions { ShowRequestFirst = true, RequestSizeMb = "99" });

                Assert.AreEqual(1, ready);
                Assert.AreEqual("Idle", DownloadConsentSession.EditorPhaseName);
                Assert.IsFalse(DownloadConsentSession.IsPromptActive);
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
