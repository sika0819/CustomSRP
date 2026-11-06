using NUnit.Framework;
using WanosAudio;

namespace Game.Tests
{
    /// <summary>
    /// 负一屏 / 前台切歌所有权与防呆（对齐 BoluoNowPlayingRemoteCommandPolicy）。
    /// </summary>
    public sealed class NowPlayingSkipOwnershipTests
    {
        [Test]
        public void UnityOwnedSkip_MustNotResyncImmediately()
        {
            Assert.IsFalse(
                BoluoNowPlayingRemoteCommandPolicy.ShouldResyncAfterUnityOwnedSkip(),
                "AcceptSkip 后 Resync 会用滞后原生索引盖回旧曲");
        }

        [Test]
        public void Skip_StillCancelsPendingPlayPause()
        {
            Assert.IsTrue(BoluoNowPlayingRemoteCommandPolicy.ShouldCancelPendingPlayPauseOnSkip());
        }

        [Test]
        public void InFlight_At1400ms_MustIgnoreAnyDirection()
        {
            Assert.IsTrue(
                BoluoNowPlayingRemoteCommandPolicy.ShouldIgnoreSkip(
                    skipInFlight: true, sameDirectionAsLast: true, elapsedSinceLastAcceptedSkipMs: 1400),
                "连切撞 Sequence createInst：inFlight 须挡住 ~1.4s 第二笔");
            Assert.IsTrue(
                BoluoNowPlayingRemoteCommandPolicy.ShouldIgnoreSkip(
                    skipInFlight: true, sameDirectionAsLast: false, elapsedSinceLastAcceptedSkipMs: 1400),
                "inFlight 期间反方向也忽略");
        }

        [Test]
        public void InFlight_AtTimeout_MustAllow()
        {
            Assert.IsFalse(
                BoluoNowPlayingRemoteCommandPolicy.ShouldIgnoreSkip(
                    skipInFlight: true,
                    sameDirectionAsLast: true,
                    elapsedSinceLastAcceptedSkipMs: BoluoNowPlayingRemoteCommandPolicy.SkipInFlightTimeoutMs),
                "超时后必须放行，避免永久吞键");
            Assert.IsFalse(
                BoluoNowPlayingRemoteCommandPolicy.ShouldIgnoreSkip(
                    skipInFlight: true, sameDirectionAsLast: false, elapsedSinceLastAcceptedSkipMs: 2500));
        }

        [Test]
        public void SameDirection_WithinDebounce_MustIgnore()
        {
            Assert.IsTrue(
                BoluoNowPlayingRemoteCommandPolicy.ShouldIgnoreSkip(
                    skipInFlight: false, sameDirectionAsLast: true, elapsedSinceLastAcceptedSkipMs: 200));
        }

        [Test]
        public void ReverseDirection_WhenNotInFlight_MustAllow()
        {
            Assert.IsFalse(
                BoluoNowPlayingRemoteCommandPolicy.ShouldIgnoreSkip(
                    skipInFlight: false, sameDirectionAsLast: false, elapsedSinceLastAcceptedSkipMs: 100),
                "非 inFlight 反方向立刻撤回");
        }

        [Test]
        public void PlayPause_StillCoalesces()
        {
            Assert.IsTrue(
                BoluoNowPlayingRemoteCommandPolicy.ShouldCoalescePlayPause(),
                "连点暂停/播放 last-wins，避免 mute/unmute 拉锯");
        }

        [Test]
        public void Flush_MutedWithoutUserPause_MustNotSkip()
        {
            Assert.IsFalse(
                BoluoNowPlayingRemoteCommandPolicy.ShouldSkipFlushBecauseAlreadyConsistent(
                    wantMute: true, isMuted: true, userManuallyPaused: false),
                "冷启动 isMuted 初值不能把负一屏暂停吞掉");
        }

        [Test]
        public void Flush_AudibleWhileUserPaused_MustNotSkipPlay()
        {
            Assert.IsFalse(
                BoluoNowPlayingRemoteCommandPolicy.ShouldSkipFlushBecauseAlreadyConsistent(
                    wantMute: false, isMuted: false, userManuallyPaused: true),
                "未静音但暂停闩仍在，负一屏播放必须驱引擎");
        }

        [Test]
        public void Flush_AlreadyConsistent_SkipsDevice()
        {
            Assert.IsTrue(
                BoluoNowPlayingRemoteCommandPolicy.ShouldSkipFlushBecauseAlreadyConsistent(
                    wantMute: true, isMuted: true, userManuallyPaused: true),
                "已静音且用户暂停闩已抬，不再碰设备");
            Assert.IsTrue(
                BoluoNowPlayingRemoteCommandPolicy.ShouldSkipFlushBecauseAlreadyConsistent(
                    wantMute: false, isMuted: false, userManuallyPaused: false),
                "已在播且暂停闩已清，不再碰设备");
        }

        [Test]
        public void NowPlayingPlay_UiReason_MustNotDriveEngine()
        {
            Assert.IsFalse(
                BoluoNowPlayingRemoteCommandPolicy.ShouldDriveEngineResumeFromNowPlayingPlay("ui"),
                "focus resume 的 ui 通知禁止再 Resume/unmute，否则与原生爬坡叠峰卡顿");
            Assert.IsTrue(
                BoluoNowPlayingRemoteCommandPolicy.ShouldDriveEngineResumeFromNowPlayingPlay(""),
                "真实远程 Play 须驱引擎");
            Assert.IsTrue(
                BoluoNowPlayingRemoteCommandPolicy.ShouldDriveEngineResumeFromNowPlayingPlay(null),
                "空 reason 视为远程 Play");
        }
    }
}
