using NUnit.Framework;

namespace Game.Tests
{
    /// <summary>
    /// 暂停原因只有一个。负一屏暂停加抢焦不能被降级；滑动从暂停处续；系统弹窗不算打断。
    /// 微信发语音取消后回到波萝：音乐和音效一起软续。
    /// </summary>
    public sealed class SilenceResumePolicyTests
    {
        [Test]
        public void UserPause_PlusSuspended_StaysUserPause()
        {
            SilenceReason reason = SilenceResumePolicy.ResolveReported(
                SilenceReason.UserPause, nativeUserPaused: true, nativeSuspended: true);

            Assert.AreEqual(SilenceReason.UserPause, reason);
            Assert.AreEqual(
                ResumeAction.KeepSilent,
                SilenceResumePolicy.Decide(reason, ResumeTrigger.Foreground));
            Assert.AreEqual(
                ResumeAction.KeepSilent,
                SilenceResumePolicy.Decide(reason, ResumeTrigger.FocusRestored));
            Assert.AreEqual(
                ResumeAction.SoftResume,
                SilenceResumePolicy.Decide(reason, ResumeTrigger.SceneSwipe));
            Assert.AreEqual(
                ResumeAction.SoftResume,
                SilenceResumePolicy.Decide(reason, ResumeTrigger.MediaPlay));
        }

        [Test]
        public void NativePlay_WithStickySuspend_DoesNotStayUserPause()
        {
            SilenceReason reason = SilenceResumePolicy.ResolveReported(
                SilenceReason.UserPause, nativeUserPaused: false, nativeSuspended: true);

            Assert.AreEqual(SilenceReason.SystemInterrupt, reason);
            Assert.AreEqual(
                ResumeAction.SoftResume,
                SilenceResumePolicy.Decide(reason, ResumeTrigger.Foreground));
        }

        [Test]
        public void NativeClear_DropsUserPause()
        {
            SilenceReason reason = SilenceResumePolicy.ResolveReported(
                SilenceReason.UserPause, nativeUserPaused: false, nativeSuspended: false);

            Assert.AreEqual(SilenceReason.None, reason);
            Assert.AreEqual(
                ResumeAction.KeepAudible,
                SilenceResumePolicy.Decide(reason, ResumeTrigger.Foreground));
        }

        [Test]
        public void TimerEnd_OnlyDialogResumesFromPause()
        {
            Assert.AreEqual(
                ResumeAction.KeepSilent,
                SilenceResumePolicy.Decide(SilenceReason.TimerEnd, ResumeTrigger.SceneSwipe));
            Assert.AreEqual(
                ResumeAction.SoftResume,
                SilenceResumePolicy.Decide(SilenceReason.TimerEnd, ResumeTrigger.TimerDialog));
            Assert.AreEqual(
                SilenceReason.TimerEnd,
                SilenceResumePolicy.ResolveReported(
                    SilenceReason.TimerEnd, nativeUserPaused: false, nativeSuspended: true));
        }

        [Test]
        public void SystemInterrupt_ForegroundAndSwipe_ContinueTogether()
        {
            Assert.AreEqual(
                ResumeAction.SoftResume,
                SilenceResumePolicy.Decide(SilenceReason.SystemInterrupt, ResumeTrigger.Foreground));
            Assert.AreEqual(
                ResumeAction.SoftResume,
                SilenceResumePolicy.Decide(SilenceReason.SystemInterrupt, ResumeTrigger.FocusRestored));
            Assert.AreEqual(
                ResumeAction.SoftResume,
                SilenceResumePolicy.Decide(SilenceReason.SystemInterrupt, ResumeTrigger.SceneSwipe));
            Assert.IsTrue(SilenceResumePolicy.RestoresMusicAndEffects(
                SilenceResumePolicy.Decide(SilenceReason.SystemInterrupt, ResumeTrigger.SceneSwipe)));
            Assert.IsTrue(SilenceResumePolicy.RestoresMusicAndEffects(
                SilenceResumePolicy.Decide(SilenceReason.UserPause, ResumeTrigger.MediaPlay)));
        }

        [Test]
        public void FocusReturn_DoesNotClearUserPause()
        {
            Assert.AreEqual(
                ResumeAction.KeepSilent,
                SilenceResumePolicy.Decide(SilenceReason.UserPause, ResumeTrigger.FocusRestored));
            Assert.IsTrue(SilenceResumePolicy.WantsPlayback(SilenceReason.None));
            Assert.IsFalse(SilenceResumePolicy.WantsPlayback(SilenceReason.SystemInterrupt));
            Assert.IsFalse(SilenceResumePolicy.WantsPlayback(SilenceReason.UserPause));
        }

        [Test]
        public void ResumeInFlight_StickySuspend_StaysUserPause()
        {
            SilenceReason reason = SilenceResumePolicy.ResolveReported(
                SilenceReason.UserPause,
                nativeUserPaused: false,
                nativeSuspended: true,
                resumeInFlight: true);

            Assert.AreEqual(SilenceReason.UserPause, reason);
            Assert.AreEqual(
                ResumeAction.KeepSilent,
                SilenceResumePolicy.Decide(reason, ResumeTrigger.Foreground));
        }

        [Test]
        public void HardReplay_WithoutPlaying_KeepsLatch()
        {
            Assert.IsFalse(SilenceResumePolicy.ShouldCommitHardReplay(voiceEnteredPlaying: false));
            Assert.IsTrue(SilenceResumePolicy.ShouldCommitHardReplay(voiceEnteredPlaying: true));
            Assert.IsFalse(SilenceResumePolicy.ClearsLatch(ResumeCommit.WaitingForPost));
            Assert.IsFalse(SilenceResumePolicy.ClearsLatch(ResumeCommit.Failed));
            Assert.IsTrue(SilenceResumePolicy.ClearsLatch(ResumeCommit.Done));
        }

        [Test]
        public void CreateInFlight_HardReplay_WaitsInsteadOfFailing()
        {
            Assert.AreEqual(
                ResumeCommit.WaitingForPost,
                SilenceResumePolicy.ClassifyHardReplaySchedule(
                    voiceCreateInFlight: true, postScheduled: false));
            Assert.AreEqual(
                ResumeCommit.WaitingForPost,
                SilenceResumePolicy.ClassifyHardReplaySchedule(
                    voiceCreateInFlight: true, postScheduled: true));
            Assert.AreEqual(
                ResumeCommit.Failed,
                SilenceResumePolicy.ClassifyHardReplaySchedule(
                    voiceCreateInFlight: false, postScheduled: false));
        }

        [Test]
        public void HardReplay_KeepsLatchUntilPosted()
        {
            Assert.IsFalse(SilenceResumePolicy.ClearsLatch(ResumeCommit.Failed));
            Assert.IsFalse(SilenceResumePolicy.ClearsLatch(ResumeCommit.WaitingForPost));
            Assert.IsTrue(SilenceResumePolicy.ClearsLatch(ResumeCommit.Done));
        }

        [Test]
        public void WeChatVoiceSendCancelled_ReturnShowsInterrupt_SwipeResumes()
        {
            Assert.IsFalse(SilenceResumePolicy.IsIgnorableSystemDialogFocus(
                competing: false, inVoiceCall: true, externalMusic: false));

            ResumeAction foreground = SilenceResumePolicy.DecideReturnAfterWeChatVoiceCancel(
                heldBeforeResync: SilenceReason.SystemInterrupt,
                afterResync: SilenceReason.None,
                musicStillPaused: true,
                effectsStillPaused: true,
                ResumeTrigger.Foreground,
                showInterruptInsteadOfResume: true);
            Assert.AreEqual(ResumeAction.KeepSilent, foreground);
            Assert.IsFalse(SilenceResumePolicy.RestoresMusicAndEffects(foreground));

            ResumeAction focus = SilenceResumePolicy.DecideReturnAfterWeChatVoiceCancel(
                SilenceReason.SystemInterrupt, SilenceReason.None,
                musicStillPaused: true, effectsStillPaused: false,
                ResumeTrigger.FocusRestored,
                showInterruptInsteadOfResume: true);
            Assert.AreEqual(ResumeAction.KeepSilent, focus);

            ResumeAction swipe = SilenceResumePolicy.DecideReturnAfterWeChatVoiceCancel(
                SilenceReason.SystemInterrupt, SilenceReason.None,
                musicStillPaused: false, effectsStillPaused: true,
                ResumeTrigger.SceneSwipe,
                showInterruptInsteadOfResume: true);
            Assert.AreEqual(ResumeAction.SoftResume, swipe);
            Assert.IsTrue(SilenceResumePolicy.RestoresMusicAndEffects(swipe));

            Assert.IsTrue(SilenceResumePolicy.ShouldKeepSystemInterruptAfterWeChatVoiceCancel(
                SilenceReason.SystemInterrupt, SilenceReason.None,
                musicStillPaused: true, effectsStillPaused: true));
        }

        [Test]
        public void WeChatVoiceSendCancelled_UserPauseOrAlreadyAudible_StaysPut()
        {
            ResumeAction userPaused = SilenceResumePolicy.DecideReturnAfterWeChatVoiceCancel(
                SilenceReason.UserPause, SilenceReason.UserPause,
                musicStillPaused: true, effectsStillPaused: true,
                ResumeTrigger.Foreground);
            Assert.AreEqual(ResumeAction.KeepSilent, userPaused);
            Assert.IsFalse(SilenceResumePolicy.RestoresMusicAndEffects(userPaused));

            Assert.AreEqual(
                ResumeAction.KeepSilent,
                SilenceResumePolicy.DecideReturnAfterWeChatVoiceCancel(
                    SilenceReason.TimerEnd, SilenceReason.TimerEnd,
                    musicStillPaused: true, effectsStillPaused: true,
                    ResumeTrigger.Foreground));

            ResumeAction alreadyAudible = SilenceResumePolicy.DecideReturnAfterWeChatVoiceCancel(
                SilenceReason.SystemInterrupt, SilenceReason.None,
                musicStillPaused: false, effectsStillPaused: false,
                ResumeTrigger.Foreground);
            Assert.AreEqual(ResumeAction.KeepAudible, alreadyAudible);
            Assert.IsFalse(SilenceResumePolicy.RestoresMusicAndEffects(alreadyAudible));
            Assert.IsFalse(SilenceResumePolicy.ShouldKeepSystemInterruptAfterWeChatVoiceCancel(
                SilenceReason.SystemInterrupt, SilenceReason.None,
                musicStillPaused: false, effectsStillPaused: false));

            Assert.AreEqual(
                ResumeAction.KeepAudible,
                SilenceResumePolicy.DecideReturnAfterWeChatVoiceCancel(
                    SilenceReason.None, SilenceReason.None,
                    musicStillPaused: true, effectsStillPaused: true,
                    ResumeTrigger.Foreground));
        }

        [Test]
        public void TimerEnd_EnginePauseDoesNotDowngradeReason()
        {
            PlaybackGate.ResetSession();
            PlaybackGate.SetSilence(SilenceReason.TimerEnd, "timer-end");
            PlaybackGate.NoteUserPaused(true, "pause-engine");
            Assert.AreEqual(SilenceReason.TimerEnd, PlaybackGate.Silence);
            Assert.AreEqual(
                ResumeAction.KeepSilent,
                SilenceResumePolicy.Decide(PlaybackGate.Silence, ResumeTrigger.Foreground));
            Assert.AreEqual(
                ResumeAction.KeepSilent,
                SilenceResumePolicy.Decide(PlaybackGate.Silence, ResumeTrigger.SceneSwipe));
            Assert.AreEqual(
                ResumeAction.SoftResume,
                SilenceResumePolicy.Decide(PlaybackGate.Silence, ResumeTrigger.TimerDialog));
            PlaybackGate.ResetSession();
        }

        [Test]
        public void SystemDialog_WithoutForeignAudio_IsIgnored()
        {
            Assert.IsTrue(SilenceResumePolicy.IsIgnorableSystemDialogFocus(
                competing: false, inVoiceCall: false, externalMusic: false));
            Assert.IsFalse(SilenceResumePolicy.IsIgnorableSystemDialogFocus(
                competing: true, inVoiceCall: false, externalMusic: false));
            Assert.IsFalse(SilenceResumePolicy.IsIgnorableSystemDialogFocus(
                competing: false, inVoiceCall: true, externalMusic: false));
            Assert.IsFalse(SilenceResumePolicy.IsIgnorableSystemDialogFocus(
                competing: false, inVoiceCall: false, externalMusic: true));
        }

        [Test]
        public void PendingReplay_AwaitingWithGate_DoesNotRepost()
        {
            Assert.AreEqual(
                PendingReplayNudge.LeaveWaiting,
                SilenceResumePolicy.ClassifyPendingReplay(
                    awaitingPlaying: true, waitingToPost: false, voiceCreateInFlight: true));
            Assert.AreEqual(
                PendingReplayNudge.RetryAfterMiss,
                SilenceResumePolicy.ClassifyPendingReplay(
                    awaitingPlaying: true, waitingToPost: false, voiceCreateInFlight: false));
        }

        [Test]
        public void PendingReplay_WaitingToPost_PostsOnlyWhenGateOpen()
        {
            Assert.AreEqual(
                PendingReplayNudge.PostNow,
                SilenceResumePolicy.ClassifyPendingReplay(
                    awaitingPlaying: false, waitingToPost: true, voiceCreateInFlight: false));
            Assert.AreEqual(
                PendingReplayNudge.WaitForGate,
                SilenceResumePolicy.ClassifyPendingReplay(
                    awaitingPlaying: false, waitingToPost: true, voiceCreateInFlight: true));
        }

        [Test]
        public void Timeout_SystemInterrupt_DoesNotReplay()
        {
            Assert.IsFalse(SilenceResumePolicy.TimeoutMayReplay(SilenceReason.SystemInterrupt));
            Assert.IsFalse(SilenceResumePolicy.TimeoutMayReplay(SilenceReason.UserPause));
            Assert.IsFalse(SilenceResumePolicy.TimeoutMayReplay(SilenceReason.TimerEnd));
            Assert.IsTrue(SilenceResumePolicy.TimeoutMayReplay(SilenceReason.None));
        }

        [Test]
        public void MissingPlaylist_IsNotSoftSuccess()
        {
            Assert.IsFalse(SilenceResumePolicy.SoftResumeAccepted(
                playlistPresent: false, softResumed: false));
            Assert.IsFalse(SilenceResumePolicy.SoftResumeAccepted(
                playlistPresent: false, softResumed: true));
            Assert.IsFalse(SilenceResumePolicy.SoftResumeAccepted(
                playlistPresent: true, softResumed: false));
            Assert.IsTrue(SilenceResumePolicy.SoftResumeAccepted(
                playlistPresent: true, softResumed: true));
        }

        [Test]
        public void VoicesStillPaused_ForegroundDoesNotKeepAudible()
        {
            SilenceReason reason = SilenceResumePolicy.ResolveReported(
                SilenceReason.UserPause, nativeUserPaused: false, nativeSuspended: false);

            Assert.AreEqual(SilenceReason.None, reason);
            Assert.AreEqual(
                ResumeAction.KeepAudible,
                SilenceResumePolicy.Decide(reason, ResumeTrigger.Foreground));
            Assert.IsTrue(SilenceResumePolicy.ShouldRestoreInsteadOfKeepAudible(
                reason, voicesStillNeedRestore: true));
            Assert.IsFalse(SilenceResumePolicy.ShouldRestoreInsteadOfKeepAudible(
                reason, voicesStillNeedRestore: false));
        }

        [Test]
        public void CanDuck_OnlyForegroundWithoutCompetitor_IsSystemDialog()
        {
            Assert.IsTrue(SilenceResumePolicy.ShouldIgnoreCanDuckAsSystemDialog(
                appInBackground: false, competing: false, inVoiceCall: false, externalMusic: false));
            Assert.IsFalse(SilenceResumePolicy.ShouldIgnoreCanDuckAsSystemDialog(
                appInBackground: true, competing: false, inVoiceCall: false, externalMusic: false));
            Assert.IsFalse(SilenceResumePolicy.ShouldIgnoreCanDuckAsSystemDialog(
                appInBackground: false, competing: false, inVoiceCall: true, externalMusic: false));
        }
    }
}
