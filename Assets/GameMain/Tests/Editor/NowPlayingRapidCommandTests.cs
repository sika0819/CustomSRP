using NUnit.Framework;
using WanosAudio;

namespace Game.Tests
{
    /// <summary>
    /// 负一屏连点播放/暂停、连点切歌后的播放态与曲目。
    /// 合并窗口内不得逐下翻转；切歌被丢掉时曲目不变，接受的切歌保持播放。
    /// </summary>
    public sealed class NowPlayingRapidCommandTests
    {
        const long CoalesceMs = BoluoNowPlayingRemoteCommandPolicy.PlayPauseCoalesceMs;
        const long SkipDebounceMs = BoluoNowPlayingRemoteCommandPolicy.SkipDebounceMs;
        const long SkipTimeoutMs = BoluoNowPlayingRemoteCommandPolicy.SkipInFlightTimeoutMs;

        static BoluoNowPlayingRemoteSession Playing(int index = 0) =>
            new BoluoNowPlayingRemoteSession(4, isPlaying: true, userManuallyPaused: false, trackIndex: index);

        static BoluoNowPlayingRemoteSession Paused(int index = 0) =>
            new BoluoNowPlayingRemoteSession(4, isPlaying: false, userManuallyPaused: true, trackIndex: index);

        /// <summary>原生已停、Unity 仍以为在播：出声标志与用户暂停闩不一致。</summary>
        static BoluoNowPlayingRemoteSession AudibleButLatched(int index = 0) =>
            new BoluoNowPlayingRemoteSession(4, isPlaying: true, userManuallyPaused: true, trackIndex: index);

        static void AssertSettledPlayback(BoluoNowPlayingRemoteSession session, bool playing)
        {
            Assert.AreEqual(playing, session.IsPlaying);
            Assert.AreEqual(playing, session.CardShowsPlaying);
            Assert.AreEqual(!playing, session.UserManuallyPaused);
        }

        [Test]
        public void RapidPlayPause_LastPause_DoesNotFlipUntilWindowEnds()
        {
            var session = Playing();
            session.PressPause(0);
            session.PressPlay(40);
            session.PressPause(90);
            session.PressPlay(140);
            session.PressPause(180);

            session.Tick(180 + CoalesceMs - 1);
            Assert.IsTrue(session.IsPlaying, "合并窗口内仍是点之前的播放态");
            Assert.IsTrue(session.CardShowsPlaying);
            Assert.AreEqual(0, session.EnginePauseCount);
            Assert.AreEqual(0, session.EngineResumeCount);
            Assert.IsTrue(session.HasPendingPlayPause);

            session.Tick(180 + CoalesceMs);
            Assert.IsFalse(session.IsPlaying);
            Assert.IsTrue(session.UserManuallyPaused);
            Assert.IsFalse(session.CardShowsPlaying);
            Assert.AreEqual(1, session.EnginePauseCount, "连点只落地最后一次暂停");
            Assert.AreEqual(0, session.EngineResumeCount, "中间的播放不得先发给引擎");
            Assert.AreEqual(0, session.TrackIndex);
        }

        [Test]
        public void RapidPlayPause_LastPlay_LandsPlayingOnce()
        {
            var session = Paused();
            session.PressPlay(0);
            session.PressPause(30);
            session.PressPlay(70);
            session.PressPause(110);
            session.PressPlay(160);

            session.Tick(160 + CoalesceMs - 1);
            Assert.IsFalse(session.IsPlaying);
            Assert.AreEqual(0, session.EngineResumeCount);

            session.Tick(160 + CoalesceMs);
            Assert.IsTrue(session.IsPlaying);
            Assert.IsFalse(session.UserManuallyPaused);
            Assert.IsTrue(session.CardShowsPlaying);
            Assert.AreEqual(1, session.EngineResumeCount);
            Assert.AreEqual(0, session.EnginePauseCount);
        }

        [Test]
        public void PauseThenPlayJustBeforeWindow_StaysPlaying()
        {
            var session = Playing();
            session.PressPause(0);
            session.PressPlay(CoalesceMs - 1);

            session.Tick(CoalesceMs);
            Assert.IsTrue(session.IsPlaying, "窗口结束前改按播放，不得先落成暂停");
            Assert.AreEqual(0, session.EnginePauseCount);

            session.Tick(CoalesceMs - 1 + CoalesceMs);
            Assert.IsTrue(session.IsPlaying);
            Assert.IsFalse(session.UserManuallyPaused);
            Assert.AreEqual(0, session.EnginePauseCount);
            Assert.AreEqual(0, session.EngineResumeCount, "本来就在播，最后一次播放不碰设备");
        }

        [Test]
        public void SpacedPauseAndPlay_EachIntentLands()
        {
            var session = Playing();
            session.PressPause(0);
            session.Tick(CoalesceMs);
            Assert.IsFalse(session.IsPlaying);
            Assert.IsTrue(session.UserManuallyPaused);

            session.PressPlay(CoalesceMs);
            session.Tick(CoalesceMs + CoalesceMs);
            Assert.IsTrue(session.IsPlaying);
            Assert.IsFalse(session.UserManuallyPaused);
            Assert.AreEqual(1, session.EnginePauseCount);
            Assert.AreEqual(1, session.EngineResumeCount);
        }

        [Test]
        public void ColdStartMuted_PauseStillLands()
        {
            var session = new BoluoNowPlayingRemoteSession(
                4, isPlaying: false, userManuallyPaused: false);
            session.PressPause(0);
            session.Tick(CoalesceMs);
            Assert.IsFalse(session.IsPlaying);
            Assert.IsTrue(session.UserManuallyPaused);
            Assert.IsFalse(session.CardShowsPlaying);
            Assert.AreEqual(1, session.EnginePauseCount, "isMuted 初值不能把负一屏暂停吞掉");
        }

        [Test]
        public void FocusResumeUiPlay_DoesNotChangePlayback()
        {
            var session = Paused();
            session.PressPlay(0, "ui");
            session.PressPlay(20, "ui");
            session.Tick(CoalesceMs + 20);
            Assert.IsFalse(session.IsPlaying);
            Assert.IsTrue(session.UserManuallyPaused);
            Assert.AreEqual(0, session.EngineResumeCount);
            Assert.IsFalse(session.HasPendingPlayPause);
        }

        [Test]
        public void RapidNext_OnlyFirstChangesTrack_StaysPlaying()
        {
            var session = Playing();
            Assert.IsTrue(session.PressNext(0));
            Assert.IsFalse(session.PressNext(30));
            Assert.IsFalse(session.PressNext(80));
            Assert.IsFalse(session.PressNext(150));

            Assert.AreEqual(1, session.TrackIndex);
            Assert.AreEqual(1, session.AcceptedSkipCount);
            Assert.AreEqual(3, session.IgnoredSkipCount);
            Assert.IsTrue(session.SkipInFlight);
            Assert.IsTrue(session.IsPlaying);
            Assert.IsTrue(session.CardShowsPlaying);
            Assert.AreEqual(0, session.ResyncCount, "连切不得被滞后原生索引盖回第 0 首");
            Assert.AreEqual(0, session.EnginePauseCount);
        }

        [Test]
        public void RapidNext_AfterSettle_SameDirectionStillDebounced()
        {
            var session = Playing();
            session.PressNext(0);
            session.NoteSkipSettled();

            Assert.IsFalse(session.PressNext(SkipDebounceMs - 1));
            Assert.AreEqual(1, session.TrackIndex);
            Assert.IsFalse(session.SkipInFlight);

            Assert.IsTrue(session.PressNext(SkipDebounceMs));
            Assert.AreEqual(2, session.TrackIndex);
            Assert.IsTrue(session.IsPlaying);
            Assert.IsTrue(session.CardShowsPlaying);
        }

        [Test]
        public void RapidNextPrev_ReverseWhileInFlight_KeepsNewTrack()
        {
            var session = Playing();
            session.PressNext(0);
            Assert.IsFalse(session.PressPrev(80));
            Assert.IsFalse(session.PressNext(120));
            Assert.AreEqual(1, session.TrackIndex);
            Assert.AreEqual(1, session.AcceptedSkipCount);
            Assert.IsTrue(session.IsPlaying);
        }

        [Test]
        public void NextThenPrev_AfterSettle_RewindsImmediately()
        {
            var session = Playing(index: 2);
            session.PressNext(0);
            session.NoteSkipSettled();
            Assert.IsTrue(session.PressPrev(100));
            Assert.AreEqual(2, session.TrackIndex, "非 in-flight 的反方向立刻撤回，不受同向防抖限制");
            Assert.IsTrue(session.IsPlaying);
            Assert.AreEqual(0, session.ResyncCount);
        }

        [Test]
        public void PrevFromFirstTrack_WrapsAndStaysPlaying()
        {
            var session = Playing(index: 0);
            Assert.IsTrue(session.PressPrev(0));
            Assert.AreEqual(3, session.TrackIndex);
            Assert.IsTrue(session.IsPlaying);
            Assert.IsFalse(session.UserManuallyPaused);
        }

        [Test]
        public void InFlight_JustBeforeTimeout_SecondNextIgnored()
        {
            var session = Playing();
            session.PressNext(0);
            Assert.IsFalse(session.PressNext(SkipTimeoutMs - 1));
            Assert.AreEqual(1, session.TrackIndex);
            Assert.IsTrue(session.IsPlaying);
            Assert.IsTrue(session.SkipInFlight);
        }

        [Test]
        public void InFlight_AtTimeout_SecondNextAdvances()
        {
            var session = Playing();
            session.PressNext(0);
            Assert.IsTrue(session.PressNext(SkipTimeoutMs));
            Assert.AreEqual(2, session.TrackIndex);
            Assert.IsTrue(session.IsPlaying);
            Assert.IsTrue(session.CardShowsPlaying);
            Assert.AreEqual(0, session.ResyncCount);
        }

        [Test]
        public void PauseThenImmediateNext_NewTrackStaysPlaying()
        {
            var session = Playing();
            session.PressPause(0);
            Assert.IsTrue(session.PressNext(80));

            session.Tick(CoalesceMs + 80);
            Assert.AreEqual(1, session.TrackIndex);
            Assert.IsTrue(session.IsPlaying, "先暂停再切歌，合并到期不得把新曲静音");
            Assert.IsFalse(session.UserManuallyPaused);
            Assert.IsTrue(session.CardShowsPlaying);
            Assert.IsFalse(session.HasPendingPlayPause);
            Assert.AreEqual(0, session.EnginePauseCount);
            Assert.AreEqual(0, session.ResyncCount);
        }

        [Test]
        public void AlreadyPaused_NextStartsPlayingNewTrack()
        {
            var session = Paused();
            Assert.IsTrue(session.PressNext(0));
            session.Tick(SkipTimeoutMs);
            Assert.AreEqual(1, session.TrackIndex);
            Assert.IsTrue(session.IsPlaying);
            Assert.IsFalse(session.UserManuallyPaused);
            Assert.AreEqual(0, session.EnginePauseCount);
            Assert.AreEqual(0, session.EngineResumeCount, "切歌自己起播，不走播放合并");
        }

        [Test]
        public void NextThenPause_LandsPausedOnNewTrack()
        {
            var session = Playing();
            session.PressNext(0);
            session.PressPause(50);
            session.Tick(50 + CoalesceMs);
            Assert.AreEqual(1, session.TrackIndex);
            Assert.IsFalse(session.IsPlaying);
            Assert.IsTrue(session.UserManuallyPaused);
            Assert.IsFalse(session.CardShowsPlaying);
            Assert.AreEqual(1, session.EnginePauseCount);
        }

        [Test]
        public void PlayThenImmediateNext_PendingPlayDoesNotPauseLater()
        {
            var session = Paused();
            session.PressPlay(0);
            session.PressNext(40);
            session.Tick(CoalesceMs + 40);
            Assert.AreEqual(1, session.TrackIndex);
            Assert.IsTrue(session.IsPlaying);
            Assert.IsFalse(session.UserManuallyPaused);
            Assert.AreEqual(0, session.EnginePauseCount);
            Assert.IsFalse(session.HasPendingPlayPause);
        }

        [Test]
        public void TimerEnd_SkipChangesTrackButStaysPaused()
        {
            var session = Playing();
            session.NotePausedForTimerEnd(true);
            Assert.IsTrue(session.PressNext(0));
            session.Tick(CoalesceMs);
            Assert.AreEqual(1, session.TrackIndex);
            Assert.IsFalse(session.IsPlaying);
            Assert.IsTrue(session.UserManuallyPaused);
            Assert.IsFalse(session.CardShowsPlaying);
        }

        [Test]
        public void MixedBurst_PausePlayPauseThenDoubleNext_EndsPlayingNextTrack()
        {
            var session = Playing();
            session.PressPause(0);
            session.PressPlay(40);
            session.PressPause(90);
            Assert.IsTrue(session.PressNext(120));
            Assert.IsFalse(session.PressNext(160));
            session.Tick(1000);

            Assert.AreEqual(1, session.TrackIndex);
            Assert.IsTrue(session.IsPlaying);
            Assert.IsTrue(session.CardShowsPlaying);
            Assert.IsFalse(session.UserManuallyPaused);
            Assert.AreEqual(1, session.AcceptedSkipCount);
            Assert.AreEqual(1, session.IgnoredSkipCount);
            Assert.AreEqual(0, session.EnginePauseCount);
            Assert.AreEqual(0, session.EngineResumeCount);
            Assert.AreEqual(0, session.ResyncCount);
        }

        [Test]
        public void DesyncedLatch_RapidPlayPause_LastPlay_ResumesOnce()
        {
            var session = AudibleButLatched();
            session.PressPause(0);
            session.PressPlay(40);
            session.PressPause(90);
            session.PressPlay(140);

            session.Tick(140 + CoalesceMs - 1);
            Assert.IsTrue(session.IsPlaying, "合并窗口内不随连点翻转");
            Assert.AreEqual(0, session.EngineResumeCount);
            Assert.AreEqual(0, session.EnginePauseCount);

            session.Tick(140 + CoalesceMs);
            AssertSettledPlayback(session, playing: true);
            Assert.AreEqual(1, session.EngineResumeCount, "未静音但暂停闩仍在，播放不能被吞掉");
            Assert.AreEqual(0, session.EnginePauseCount);
            Assert.AreEqual(0, session.TrackIndex);
        }

        [Test]
        public void DesyncedLatch_RapidPlayPause_LastPause_PausesOnce()
        {
            var session = AudibleButLatched();
            session.PressPlay(0);
            session.PressPause(40);
            session.PressPlay(90);
            session.PressPause(140);

            session.Tick(140 + CoalesceMs - 1);
            Assert.IsTrue(session.IsPlaying, "合并窗口内不随连点翻转");
            Assert.AreEqual(0, session.EnginePauseCount);
            Assert.AreEqual(0, session.EngineResumeCount);

            session.Tick(140 + CoalesceMs);
            AssertSettledPlayback(session, playing: false);
            Assert.AreEqual(1, session.EnginePauseCount, "连点只落地最后一次暂停");
            Assert.AreEqual(0, session.EngineResumeCount);
            Assert.AreEqual(0, session.TrackIndex);
        }

        [Test]
        public void NextThenPause_IgnoredSkipsDoNotCancelPause()
        {
            var session = Playing();
            Assert.IsTrue(session.PressNext(0));
            session.PressPause(50);
            Assert.IsFalse(session.PressNext(80));
            Assert.IsFalse(session.PressPrev(120));

            session.Tick(50 + CoalesceMs - 1);
            Assert.IsTrue(session.IsPlaying, "暂停尚未落地");
            Assert.AreEqual(1, session.TrackIndex);
            Assert.IsTrue(session.HasPendingPlayPause);

            session.Tick(50 + CoalesceMs);
            Assert.AreEqual(1, session.TrackIndex);
            AssertSettledPlayback(session, playing: false);
            Assert.AreEqual(1, session.EnginePauseCount);
            Assert.AreEqual(0, session.EngineResumeCount);
            Assert.AreEqual(1, session.AcceptedSkipCount);
            Assert.AreEqual(2, session.IgnoredSkipCount);
        }

        [Test]
        public void AlreadyPaused_RapidNext_OnlyFirstStartsPlaying()
        {
            var session = Paused();
            Assert.IsTrue(session.PressNext(0));
            Assert.IsFalse(session.PressNext(40));
            Assert.IsFalse(session.PressNext(90));

            Assert.AreEqual(1, session.TrackIndex);
            AssertSettledPlayback(session, playing: true);
            Assert.AreEqual(1, session.AcceptedSkipCount);
            Assert.AreEqual(2, session.IgnoredSkipCount);
            Assert.AreEqual(0, session.EnginePauseCount);
            Assert.AreEqual(0, session.EngineResumeCount, "切歌自己起播，不走播放合并");
        }

        [Test]
        public void RapidNext_FromLastTrack_WrapsAndStaysPlaying()
        {
            var session = Playing(index: 3);
            Assert.IsTrue(session.PressNext(0));
            Assert.IsFalse(session.PressNext(50));

            Assert.AreEqual(0, session.TrackIndex);
            AssertSettledPlayback(session, playing: true);
            Assert.AreEqual(1, session.AcceptedSkipCount);
            Assert.AreEqual(1, session.IgnoredSkipCount);
            Assert.AreEqual(0, session.EnginePauseCount);
        }

        [Test]
        public void StalePlay_DoesNotOverrideNewerPauseOrSkip()
        {
            Assert.IsTrue(BoluoNowPlayingRemoteCommandPolicy.ShouldCommitAudible(3, 3, userPaused: false));
            Assert.IsFalse(
                BoluoNowPlayingRemoteCommandPolicy.ShouldCommitAudible(3, 3, userPaused: true),
                "暂停已经落地，晚到的播放不得再出声");
            Assert.IsFalse(
                BoluoNowPlayingRemoteCommandPolicy.ShouldCommitAudible(3, 4, userPaused: false),
                "更新的播放或切歌已经占了代际，旧的播放不得改卡片");
        }

        [Test]
        public void TimerEnd_DropsPendingPlay_AndIgnoresLaterPlay()
        {
            var session = Paused();
            session.PressPlay(0);
            Assert.IsTrue(session.HasPendingPlayPause);

            session.NotePausedForTimerEnd(true);
            session.Tick(CoalesceMs);
            Assert.AreEqual(0, session.EngineResumeCount, "到点后合并窗口不得把声音拉起来");
            Assert.IsTrue(session.PausedForTimerEnd);
            Assert.IsFalse(session.HasPendingPlayPause);

            session.PressPlay(CoalesceMs + 10);
            session.Tick(CoalesceMs + 10 + CoalesceMs);
            Assert.AreEqual(0, session.EngineResumeCount, "到点后的播放只等弹窗");
            Assert.IsTrue(session.UserManuallyPaused);
        }

        [Test]
        public void SingleTrack_RapidNext_StaysOnSameTrackPlaying()
        {
            var session = new BoluoNowPlayingRemoteSession(
                1, isPlaying: true, userManuallyPaused: false);
            Assert.IsTrue(session.PressNext(0));
            Assert.IsFalse(session.PressNext(40));
            Assert.IsFalse(session.PressPrev(80));

            Assert.AreEqual(0, session.TrackIndex);
            AssertSettledPlayback(session, playing: true);
            Assert.AreEqual(1, session.AcceptedSkipCount);
            Assert.AreEqual(2, session.IgnoredSkipCount);
            Assert.AreEqual(0, session.EnginePauseCount);
        }
    }
}
