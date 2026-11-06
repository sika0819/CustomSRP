using NUnit.Framework;

namespace Game.Tests
{
    /// <summary>
    /// 下载完成连点不叠播：Play 已发出、尚未 Playing 时的闸与 Playing 处置。
    /// </summary>
    public sealed class MusicPlayOverlapPolicyTests
    {
        [Test]
        public void GateOpen_MustDeferPost()
        {
            Assert.IsTrue(MusicPlayOverlapPolicy.ShouldDeferPost(voiceCreateInFlight: true));
            Assert.IsFalse(MusicPlayOverlapPolicy.ShouldDeferPost(voiceCreateInFlight: false));
        }

        [Test]
        public void Playing_SameGeneration_Continues()
        {
            Assert.AreEqual(
                MusicPlayOverlapPolicy.PlayingDisposition.Continue,
                MusicPlayOverlapPolicy.DecidePlaying(
                    voiceIdMatchesCurrent: true,
                    isCreateInFlightVoice: true,
                    postedGeneration: 3,
                    currentGeneration: 3,
                    stoppedByUser: false,
                    playbackStillWanted: true));
        }

        [Test]
        public void Playing_SupersededCreate_StopsThenPlaysCurrent()
        {
            Assert.AreEqual(
                MusicPlayOverlapPolicy.PlayingDisposition.StopThenPlayCurrent,
                MusicPlayOverlapPolicy.DecidePlaying(
                    voiceIdMatchesCurrent: false,
                    isCreateInFlightVoice: true,
                    postedGeneration: 1,
                    currentGeneration: 4,
                    stoppedByUser: false,
                    playbackStillWanted: true),
                "点选已前进：停掉刚起来的旧 Post，再播当前索引");
        }

        [Test]
        public void Playing_UserStopped_StopsOnly()
        {
            Assert.AreEqual(
                MusicPlayOverlapPolicy.PlayingDisposition.StopOnly,
                MusicPlayOverlapPolicy.DecidePlaying(
                    voiceIdMatchesCurrent: true,
                    isCreateInFlightVoice: true,
                    postedGeneration: 2,
                    currentGeneration: 3,
                    stoppedByUser: true,
                    playbackStillWanted: false),
                "用户已停：不得再拉起");
        }

        [Test]
        public void Playing_StaleOtherVoice_StopsOnly()
        {
            Assert.AreEqual(
                MusicPlayOverlapPolicy.PlayingDisposition.StopOnly,
                MusicPlayOverlapPolicy.DecidePlaying(
                    voiceIdMatchesCurrent: false,
                    isCreateInFlightVoice: false,
                    postedGeneration: -1,
                    currentGeneration: 5,
                    stoppedByUser: false,
                    playbackStillWanted: true),
                "已绑别的 voice：只停迟到实例，禁止 HardStop 当前曲");
        }

        [Test]
        public void VoiceCreateTimeout_MatchesSkipInFlightScale()
        {
            Assert.AreEqual(2000, MusicPlayOverlapPolicy.VoiceCreateTimeoutMs);
        }

        [Test]
        public void Playing_Silence_StopsMatchingVoiceWithoutReplay()
        {
            Assert.AreEqual(
                MusicPlayOverlapPolicy.PlayingDisposition.StopOnly,
                MusicPlayOverlapPolicy.DecidePlaying(
                    voiceIdMatchesCurrent: true,
                    isCreateInFlightVoice: true,
                    postedGeneration: 3,
                    currentGeneration: 3,
                    stoppedByUser: false,
                    playbackStillWanted: false),
                "系统打断期间迟到的 Playing 只停，不再排一次起播");
        }

        [Test]
        public void Playing_Silence_SupersededCreate_DoesNotReplay()
        {
            Assert.AreEqual(
                MusicPlayOverlapPolicy.PlayingDisposition.StopOnly,
                MusicPlayOverlapPolicy.DecidePlaying(
                    voiceIdMatchesCurrent: false,
                    isCreateInFlightVoice: true,
                    postedGeneration: 1,
                    currentGeneration: 4,
                    stoppedByUser: false,
                    playbackStillWanted: false),
                "打断期间旧 Post 起来了也只停，禁止 StopThenPlayCurrent");
        }
    }
}
