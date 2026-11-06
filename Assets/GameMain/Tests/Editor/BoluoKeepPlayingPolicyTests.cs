using NUnit.Framework;
using WanosAudio;

namespace Game.Tests.Editor
{
    public sealed class BoluoKeepPlayingPolicyTests
    {
        [Test]
        public void TeardownLatch_WithPlayingCard_DoesNotBlockKeepPlaying()
        {
            // GameEntry ResetSession → teardown 双钉；随后 PublishPlayingForced 已出卡。
            Assert.IsFalse(
                BoluoKeepPlayingPolicy.IsUserStoppedForKeepPlaying(
                    userPaused: true,
                    shouldStop: true,
                    foregroundPlayingCardPublished: true));
        }

        [Test]
        public void RealUserPause_BlocksKeepPlaying()
        {
            Assert.IsTrue(
                BoluoKeepPlayingPolicy.IsUserStoppedForKeepPlaying(
                    userPaused: true,
                    shouldStop: false,
                    foregroundPlayingCardPublished: false));
            // 卡偶发仍在：真暂停只钉 userPaused。
            Assert.IsTrue(
                BoluoKeepPlayingPolicy.IsUserStoppedForKeepPlaying(
                    userPaused: true,
                    shouldStop: false,
                    foregroundPlayingCardPublished: true));
        }

        [Test]
        public void StopWithoutCard_BlocksKeepPlaying()
        {
            Assert.IsTrue(
                BoluoKeepPlayingPolicy.IsUserStoppedForKeepPlaying(
                    userPaused: false,
                    shouldStop: true,
                    foregroundPlayingCardPublished: false));
        }

        [Test]
        public void IdlePlaying_AllowsKeepPlaying()
        {
            Assert.IsFalse(
                BoluoKeepPlayingPolicy.IsUserStoppedForKeepPlaying(
                    userPaused: false,
                    shouldStop: false,
                    foregroundPlayingCardPublished: true));
        }
    }
}
