#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using Game.BoluoApi;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UGF = UnityGameFramework.Runtime;

namespace Game.Tests
{
    /// <summary>
    /// Play Mode 人手验证：流量窗只在用户点下载后出现（GF UI Form）。
    /// Explicit，不进 Run All。焦点切到 Game 视图，跟 Console 提示点。
    /// </summary>
    public sealed class DownloadConsentPlayModeTests
    {
        const float BootTimeoutSec = 90f;
        const float FormOpenTimeoutSec = 20f;
        const float HumanClickTimeoutSec = 300f;

        static readonly string[] DownloadFormAssets =
        {
            UiAssets.RequestDownloadForm,
            UiAssets.MobileDataConfirmForm,
            UiAssets.InsufficientStorageForm,
            UiAssets.NoNetworkForm,
            UiAssets.DownloadProgressForm,
        };

        [UnitySetUp]
        public IEnumerator UnitySetUp()
        {
            yield return EnsureUiReady();
            if (BootLoadingService.IsVisible)
                BootLoadingService.Hide();
            yield return null;
        }

        [UnityTearDown]
        public IEnumerator UnityTearDown()
        {
            CloseDownloadDialogs();
            ResetConsent();
            yield return null;
        }

        /// <summary>
        /// 进岛式静默 Notify 不得出窗；再模拟用户点下载 → 申请窗 → 立即下载 → 蜂窝 → 继续。
        /// </summary>
        [UnityTest]
        [Explicit("人手点弹窗验证，不进 Run All")]
        [NUnit.Framework.Timeout(600000)]
        [Category("DownloadConsent")]
        public IEnumerator Manual_Cellular_SilentNotifyThenUserClickShowsMobile()
        {
            ArmCellular();

            int ready = 0;
            var banks = new List<string> { "PlayMode_Consent_A" };

            // 模拟进岛 Rest / RunLoop：不得弹流量窗。
            DownloadConsentSession.NotifyNeedCellularConsent(banks);
            yield return null;
            yield return null;
            Assert.IsFalse(
                HasForm(UiAssets.MobileDataConfirmForm),
                "进岛静默 Notify 不应弹出流量窗");
            Assert.AreEqual("Idle", DownloadConsentSession.EditorPhaseName);

            DownloadConsentSession.EnsureReady(
                banks,
                onReady: () => ready++,
                new DownloadUiOptions
                {
                    ShowRequestFirst = true,
                    RequestSizeMb = DownloadNetworkGate.FormatSizeMb(banks),
                });

            yield return WaitForm(UiAssets.RequestDownloadForm, FormOpenTimeoutSec);
            Assert.IsFalse(HasForm(UiAssets.MobileDataConfirmForm));

            Prompt(
                "【蜂窝①】Game 视图点「立即下载」。\n" +
                "预期：再出「正在使用蜂窝数据」，体积按待下 bank 实算；点继续前不完成。");

            yield return WaitUntil(
                () => HasForm(UiAssets.MobileDataConfirmForm),
                HumanClickTimeoutSec,
                "你点「立即下载」后出现蜂窝窗");

            Assert.AreEqual(0, ready);
            Assert.AreEqual("MobileConfirm", DownloadConsentSession.EditorPhaseName);

            Prompt("【蜂窝②】点「继续下载」。预期：窗关，用例通过。");

            yield return WaitUntil(
                () => ready > 0,
                HumanClickTimeoutSec,
                "你点「继续下载」后 onReady");

            Assert.AreEqual(1, ready);
            Assert.IsFalse(HasForm(UiAssets.MobileDataConfirmForm));

            // 批次结束撤销后，第二次点下载仍应出流量窗。
            DownloadCellularGate.RevokeCellularAllow();
            int ready2 = 0;
            DownloadConsentSession.EnsureReady(
                banks,
                onReady: () => ready2++,
                new DownloadUiOptions
                {
                    ShowRequestFirst = true,
                    RequestSizeMb = DownloadNetworkGate.FormatSizeMb(banks),
                });
            yield return WaitForm(UiAssets.RequestDownloadForm, FormOpenTimeoutSec);
            Prompt("【蜂窝③】第二次下载：再点「立即下载」→「继续下载」，确认仍会弹流量窗。");
            yield return WaitUntil(
                () => HasForm(UiAssets.MobileDataConfirmForm),
                HumanClickTimeoutSec,
                "第二次点下载后再次出现蜂窝窗");
            yield return WaitUntil(
                () => ready2 > 0,
                HumanClickTimeoutSec,
                "第二次点「继续下载」后 onReady");
            Assert.AreEqual(1, ready2);
            Prompt("【蜂窝】通过 ✓");
        }

        /// <summary>Wi‑Fi：点「立即下载」直接过，不出蜂窝窗。</summary>
        [UnityTest]
        [Explicit("人手点弹窗验证，不进 Run All")]
        [NUnit.Framework.Timeout(600000)]
        [Category("DownloadConsent")]
        public IEnumerator Manual_Wifi_RequestThenReadyNoMobile()
        {
            ArmWifi();

            int ready = 0;
            bool sawMobile = false;
            var banks = new List<string> { "PlayMode_Consent_Wifi" };

            DownloadConsentSession.EnsureReady(
                banks,
                onReady: () => ready++,
                new DownloadUiOptions
                {
                    ShowRequestFirst = true,
                    RequestSizeMb = DownloadNetworkGate.FormatSizeMb(banks),
                });

            yield return WaitForm(UiAssets.RequestDownloadForm, FormOpenTimeoutSec);
            Prompt("【Wi‑Fi】点「立即下载」。预期：直接完成，无蜂窝窗。");

            float t0 = Time.realtimeSinceStartup;
            while (ready == 0)
            {
                if (HasForm(UiAssets.MobileDataConfirmForm))
                    sawMobile = true;
                if (Time.realtimeSinceStartup - t0 > HumanClickTimeoutSec)
                    Assert.Fail("Timeout waiting for onReady. " + Dump());
                yield return null;
            }

            Assert.AreEqual(1, ready);
            Assert.IsFalse(sawMobile, "Wi‑Fi 不应出现蜂窝窗");
            Prompt("【Wi‑Fi】通过 ✓");
        }

        /// <summary>预设/音乐风格：无申请窗，点下载等价 EnsureReady → 蜂窝 → 继续。</summary>
        [UnityTest]
        [Explicit("人手点弹窗验证，不进 Run All")]
        [NUnit.Framework.Timeout(600000)]
        [Category("DownloadConsent")]
        public IEnumerator Manual_Cellular_DirectEnsureReady_ShowsMobileWithSize()
        {
            ArmCellular();

            int ready = 0;
            // 须用不在盘上的 stem：Music1_1 已在 StreamingAssets，IsLocalReady 会直接 FireReady。
            var banks = new List<string> { "PlayMode_Consent_Direct" };

            DownloadConsentSession.EnsureReady(
                banks,
                onReady: () => ready++);

            yield return WaitForm(UiAssets.MobileDataConfirmForm, FormOpenTimeoutSec);
            Assert.AreEqual(0, ready);
            Prompt(
                "【预设/音乐】已出流量窗（体积="
                + DownloadNetworkGate.FormatSizeMb(banks)
                + "MB）。请点「继续下载」。");

            yield return WaitUntil(
                () => ready > 0,
                HumanClickTimeoutSec,
                "你点「继续下载」后 onReady");

            Assert.AreEqual(1, ready);
            Prompt("【预设/音乐】通过 ✓");
        }

        static void Prompt(string message)
        {
            Debug.LogWarning("[DownloadConsent 人手测] " + message);
        }

        static IEnumerator EnsureUiReady()
        {
            if (GameEntry.UI != null && GameEntry.Procedure != null)
            {
                CloseDownloadDialogs();
                ResetConsent();
                yield break;
            }

            UGF.GameEntry.Shutdown(UGF.ShutdownType.None);
            yield return null;
            yield return null;

            yield return ReloadLaunchScene();
            yield return WaitUntil(
                () => GameEntry.UI != null && GameEntry.Procedure != null,
                BootTimeoutSec,
                "GameEntry.UI / Procedure");
            CloseDownloadDialogs();
            ResetConsent();
        }

        static void ArmCellular()
        {
            DownloadCellularGate.EditorSimulateMobilePlatform = true;
            DownloadCellularGate.EditorReachabilityOverride =
                NetworkReachability.ReachableViaCarrierDataNetwork;
        }

        static void ArmWifi()
        {
            DownloadCellularGate.EditorSimulateMobilePlatform = true;
            DownloadCellularGate.EditorReachabilityOverride =
                NetworkReachability.ReachableViaLocalAreaNetwork;
        }

        static void ResetConsent()
        {
            DownloadConsentSession.ResetForLeaveIsland();
            DownloadCellularGate.EditorClearReachabilityOverride();
            DownloadCellularGate.ResetForNewSession();
            DownloadCellularGate.EditorSimulateMobilePlatform = false;
        }

        static void CloseDownloadDialogs()
        {
            if (GameEntry.UI == null)
                return;
            for (int i = 0; i < DownloadFormAssets.Length; i++)
                CloseIfPresent(DownloadFormAssets[i]);
        }

        static void CloseIfPresent(string assetPath)
        {
            if (GameEntry.UI == null || !GameEntry.UI.HasUIForm(assetPath))
                return;
            var form = GameEntry.UI.GetUIForm(assetPath);
            if (form != null)
                GameEntry.UI.CloseUIForm(form.SerialId);
        }

        static IEnumerator WaitForm(string assetPath, float timeoutSec)
        {
            yield return WaitUntil(
                () => HasForm(assetPath),
                timeoutSec,
                "弹出 " + assetPath);
        }

        static IEnumerator ReloadLaunchScene()
        {
            AsyncOperation op = SceneManager.LoadSceneAsync(SceneAssets.Launch, LoadSceneMode.Single);
            Assert.IsNotNull(op, "LoadSceneAsync(Launch) returned null");
            while (op != null && !op.isDone)
                yield return null;
        }

        static IEnumerator WaitUntil(Func<bool> pred, float timeoutSec, string what)
        {
            float t0 = Time.realtimeSinceStartup;
            while (!pred())
            {
                if (Time.realtimeSinceStartup - t0 > timeoutSec)
                    Assert.Fail($"Timeout after {timeoutSec:0}s waiting for {what}. " + Dump());
                yield return null;
            }
        }

        static string Dump()
        {
            return $"phase={SafePhase()} req={HasForm(UiAssets.RequestDownloadForm)} "
                + $"mobile={HasForm(UiAssets.MobileDataConfirmForm)} "
                + $"needMobile={DownloadCellularGate.NeedsMobileDataPrompt()}";
        }

        static string SafePhase()
        {
            try
            {
                return DownloadConsentSession.EditorPhaseName;
            }
            catch
            {
                return "?";
            }
        }

        static bool HasForm(string assetPath)
        {
            return GameEntry.UI != null && GameEntry.UI.HasUIForm(assetPath);
        }
    }
}
#endif
