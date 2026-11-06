using NUnit.Framework;

namespace Game.Tests
{
    public sealed class PresetCardUiStateTests
    {
        [Test]
        public void SilentDownloading_WithoutClick_IsNotDownloaded()
        {
            Assert.AreEqual(
                PresetDownloadVisual.NotDownloaded,
                PresetCardUiState.Resolve(
                    allReady: false,
                    userTriggered: false,
                    userAttemptLive: false,
                    anyUnresolvedFailure: false));
        }

        [Test]
        public void Failure_WithoutClick_IsRetry()
        {
            Assert.AreEqual(
                PresetDownloadVisual.Retry,
                PresetCardUiState.Resolve(
                    allReady: false,
                    userTriggered: false,
                    userAttemptLive: false,
                    anyUnresolvedFailure: true));
        }

        [Test]
        public void UserClick_Pending_IsDownloading()
        {
            Assert.AreEqual(
                PresetDownloadVisual.Downloading,
                PresetCardUiState.Resolve(
                    allReady: false,
                    userTriggered: true,
                    userAttemptLive: false,
                    anyUnresolvedFailure: false));
        }

        [Test]
        public void UserClick_AfterFailureLeavesUserSlot_IsRetry()
        {
            Assert.AreEqual(
                PresetDownloadVisual.Retry,
                PresetCardUiState.Resolve(
                    allReady: false,
                    userTriggered: true,
                    userAttemptLive: false,
                    anyUnresolvedFailure: true));
        }

        [Test]
        public void AllReady_IsReady()
        {
            Assert.AreEqual(
                PresetDownloadVisual.Ready,
                PresetCardUiState.Resolve(
                    allReady: true,
                    userTriggered: true,
                    userAttemptLive: false,
                    anyUnresolvedFailure: true));
        }
    }
}
