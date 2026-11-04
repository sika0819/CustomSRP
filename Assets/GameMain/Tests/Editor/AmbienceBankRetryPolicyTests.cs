using NUnit.Framework;

namespace Game.Tests
{
    /// <summary>边走边下 bank：落盘后站着也要立刻重评开播。</summary>
    public sealed class AmbienceBankRetryPolicyTests
    {
        [Test]
        public void Still_Skips_WhenNothingPending()
        {
            Assert.IsTrue(
                AmbienceBankRetryPolicy.ShouldSkipListenerStillEval(
                    listenerMovedEnough: false,
                    softStopping: false,
                    gainSlewPending: false,
                    hasResolvablePendingRetry: false));
        }

        [Test]
        public void Still_DoesNotSkip_WhenPendingBankResolvable()
        {
            Assert.IsFalse(
                AmbienceBankRetryPolicy.ShouldSkipListenerStillEval(
                    listenerMovedEnough: false,
                    softStopping: false,
                    gainSlewPending: false,
                    hasResolvablePendingRetry: true));
        }

        [Test]
        public void Still_DoesNotSkip_DuringForceEvalWindow()
        {
            Assert.IsFalse(
                AmbienceBankRetryPolicy.ShouldSkipListenerStillEval(
                    listenerMovedEnough: false,
                    softStopping: false,
                    gainSlewPending: false,
                    hasResolvablePendingRetry: false,
                    forceEvalWindow: true));
        }

        [Test]
        public void Moved_DoesNotSkip()
        {
            Assert.IsFalse(
                AmbienceBankRetryPolicy.ShouldSkipListenerStillEval(
                    listenerMovedEnough: true,
                    softStopping: false,
                    gainSlewPending: false,
                    hasResolvablePendingRetry: false));
        }

        [Test]
        public void RetrySignal_RequiresPendingBank()
        {
            Assert.IsFalse(AmbienceBankRetryPolicy.ShouldRetryOnBankSignal(false));
            Assert.IsTrue(AmbienceBankRetryPolicy.ShouldRetryOnBankSignal(true));
        }

        [Test]
        public void HandleReady_AcceptsCatalogBank_WithoutPending()
        {
            Assert.IsTrue(
                AmbienceBankRetryPolicy.ShouldHandleAmbienceBankReady(
                    isAmbienceCatalogBank: true,
                    anySourceUsesBank: false,
                    pendingContainsBank: false));
        }

        [Test]
        public void HandleReady_AcceptsSourceOrPending()
        {
            Assert.IsTrue(
                AmbienceBankRetryPolicy.ShouldHandleAmbienceBankReady(
                    isAmbienceCatalogBank: false,
                    anySourceUsesBank: true,
                    pendingContainsBank: false));
            Assert.IsTrue(
                AmbienceBankRetryPolicy.ShouldHandleAmbienceBankReady(
                    isAmbienceCatalogBank: false,
                    anySourceUsesBank: false,
                    pendingContainsBank: true));
        }

        [Test]
        public void HandleReady_IgnoresUnrelatedBank()
        {
            Assert.IsFalse(
                AmbienceBankRetryPolicy.ShouldHandleAmbienceBankReady(
                    isAmbienceCatalogBank: false,
                    anySourceUsesBank: false,
                    pendingContainsBank: false));
        }
    }
}
