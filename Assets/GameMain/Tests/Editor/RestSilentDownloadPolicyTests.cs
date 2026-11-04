using NUnit.Framework;

namespace Game.Tests
{
    /// <summary>后台弹窗关 + Wi‑Fi：Rest 立刻静默，不等用户、不堵 Critical。</summary>
    public sealed class RestSilentDownloadPolicyTests
    {
        [Test]
        public void FeatureOff_Wifi_StartsSilentNow()
        {
            Assert.IsTrue(
                RestSilentDownloadPolicy.ShouldStartSilentRestNow(
                    wifiConfirmEnabled: false,
                    canTransmitNow: true));
        }

        [Test]
        public void FeatureOff_NoTransmit_DoesNotStartYet()
        {
            Assert.IsFalse(
                RestSilentDownloadPolicy.ShouldStartSilentRestNow(
                    wifiConfirmEnabled: false,
                    canTransmitNow: false));
        }

        [Test]
        public void FeatureOn_Wifi_DoesNotAutoSilent()
        {
            // 开关开须先弹 §4.6（或等用户关窗），不可因可传就静默。
            Assert.IsFalse(
                RestSilentDownloadPolicy.ShouldStartSilentRestNow(
                    wifiConfirmEnabled: true,
                    canTransmitNow: true));
        }

        [Test]
        public void FeatureOff_BypassesConsentPromptWait()
        {
            Assert.IsTrue(
                RestSilentDownloadPolicy.ShouldBypassConsentPromptForSilentRest(
                    wifiConfirmEnabled: false));
            Assert.IsFalse(
                RestSilentDownloadPolicy.ShouldBypassConsentPromptForSilentRest(
                    wifiConfirmEnabled: true));
        }

        [Test]
        public void FeatureOff_SilentGate_AppliesToCriticalAndRest()
        {
            // 同一闸：弹窗关+可传 → Critical（预设氛围）与 Rest（音乐）都静默。
            Assert.IsTrue(
                RestSilentDownloadPolicy.ShouldStartSilentRestNow(
                    wifiConfirmEnabled: false,
                    canTransmitNow: true),
                "Critical/Rest 共用静默闸，否则预设等人点申请窗");
        }
    }
}
