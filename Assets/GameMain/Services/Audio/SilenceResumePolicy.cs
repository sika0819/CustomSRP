namespace Game
{
    /// <summary>
    /// 为什么现在不该出声。同一时刻只有一个原因；卡片态和用户暂停闩都从这里派生。
    /// </summary>
    public enum SilenceReason
    {
        None = 0,
        /// <summary>负一屏 / 岛内 / 媒体键暂停。回前台保持静音，滑动或播放键从暂停处续。</summary>
        UserPause = 1,
        /// <summary>定时到点。只有弹窗按钮能从暂停处续。</summary>
        TimerEnd = 2,
        /// <summary>微信 / 来电 / 其它播放器。回前台或焦点回归软续一次。</summary>
        SystemInterrupt = 3,
    }

    public enum ResumeTrigger
    {
        Foreground = 0,
        SceneSwipe = 1,
        MediaPlay = 2,
        FocusRestored = 3,
        TimerDialog = 4,
    }

    public enum ResumeAction
    {
        /// <summary>保持静音，不要 Resume / Play。</summary>
        KeepSilent = 0,
        /// <summary>本来就在播，不要碰引擎。</summary>
        KeepAudible = 1,
        /// <summary>Resume_ALL + Resume_Music，不另起一首。</summary>
        SoftResume = 2,
        /// <summary>实例已经没了时重发当前曲。正常暂停走 <see cref="SoftResume"/>。</summary>
        HardReplay = 3,
    }

    /// <summary>硬续是否已经把声音发出去。没发出去之前暂停闩必须留着。</summary>
    public enum ResumeCommit
    {
        Failed = 0,
        WaitingForPost = 1,
        Done = 2,
    }

    /// <summary>硬续还在飞时，再滑一次该怎么办。</summary>
    public enum PendingReplayNudge
    {
        NotPending = 0,
        /// <summary>已经 Post，闸还在。再滑不要续发。</summary>
        LeaveWaiting = 1,
        /// <summary>已经 Post，闸没了。写成失败，这次滑动再硬续。</summary>
        RetryAfterMiss = 2,
        /// <summary>还没 Post，闸已开。现在发出去。</summary>
        PostNow = 3,
        /// <summary>还没 Post，闸还关。等上一条或超时。</summary>
        WaitForGate = 4,
    }

    /// <summary>
    /// 暂停 / 续播的纯决策。不碰引擎、不读场景，方便 Editor 测表。
    /// </summary>
    public static class SilenceResumePolicy
    {
        /// <summary>
        /// 用原生对账结果合并已有原因。
        /// <paramref name="held"/> 必须是 Resync 改写门之前的原因。
        /// 原生仍报用户暂停时，suspended 不能把它降级。
        /// <paramref name="resumeInFlight"/> 时这轮续播还没 Playing：自己清掉的原生暂停不算用户点了播放。
        /// 没有续播在飞、原生已经不暂停，才跟着播放走。
        /// </summary>
        public static SilenceReason ResolveReported(
            SilenceReason held, bool nativeUserPaused, bool nativeSuspended, bool resumeInFlight = false)
        {
            if (held == SilenceReason.TimerEnd)
                return SilenceReason.TimerEnd;
            if (resumeInFlight && held == SilenceReason.UserPause)
                return SilenceReason.UserPause;
            if (nativeUserPaused)
                return SilenceReason.UserPause;
            // 负一屏已经点了播放，而且手头没有没完成的续播。
            // 这时的 suspended 是抢焦残留，跟着播放走，不要把人按在暂停上。
            if (nativeSuspended)
                return SilenceReason.SystemInterrupt;
            return SilenceReason.None;
        }

        /// <summary>
        /// 硬续撞上 createInst：这滑还在等，不是失败。闸打开后再 Post。
        /// 没排上、也不是在等上一条，才是失败。
        /// </summary>
        public static ResumeCommit ClassifyHardReplaySchedule(bool voiceCreateInFlight, bool postScheduled)
        {
            if (voiceCreateInFlight || postScheduled)
                return ResumeCommit.WaitingForPost;
            return ResumeCommit.Failed;
        }

        /// <summary>编号到手不算响。引擎 Playing 才清暂停闩。</summary>
        public static bool ShouldCommitHardReplay(bool voiceEnteredPlaying) => voiceEnteredPlaying;

        /// <summary>
        /// 再滑时硬续还在飞。已经发出去且闸还在就别叠播；闸丢了才能再试。
        /// 还没 Post：闸开了现在发，闸关着继续等。
        /// </summary>
        public static PendingReplayNudge ClassifyPendingReplay(
            bool awaitingPlaying, bool waitingToPost, bool voiceCreateInFlight)
        {
            if (awaitingPlaying)
            {
                return voiceCreateInFlight
                    ? PendingReplayNudge.LeaveWaiting
                    : PendingReplayNudge.RetryAfterMiss;
            }

            if (waitingToPost)
            {
                return voiceCreateInFlight
                    ? PendingReplayNudge.WaitForGate
                    : PendingReplayNudge.PostNow;
            }

            return PendingReplayNudge.NotPending;
        }

        /// <summary>createInst 超时后只有本来就想出声才重发。系统打断不能把卡片点亮。</summary>
        public static bool TimeoutMayReplay(SilenceReason silence) => WantsPlayback(silence);

        /// <summary>没有播放列表不能算软续成功，否则静音原因会被清掉。</summary>
        public static bool SoftResumeAccepted(bool playlistPresent, bool softResumed) =>
            playlistPresent && softResumed;

        /// <summary>
        /// 原生暂停旗已经清掉，但丢焦 Pause / mute 还在。回前台要软续，不能当成已经在响。
        /// </summary>
        public static bool ShouldRestoreInsteadOfKeepAudible(
            SilenceReason silence, bool voicesStillNeedRestore) =>
            silence == SilenceReason.None && voicesStillNeedRestore;

        /// <summary>
        /// 前台的 CAN_DUCK，且没有外来媒体、没有通话：系统弹窗。后台仍是微信，要停。
        /// </summary>
        public static bool ShouldIgnoreCanDuckAsSystemDialog(
            bool appInBackground, bool competing, bool inVoiceCall, bool externalMusic) =>
            !appInBackground
            && IsIgnorableSystemDialogFocus(competing, inVoiceCall, externalMusic);

        public static ResumeAction Decide(SilenceReason reason, ResumeTrigger trigger)
        {
            switch (reason)
            {
                case SilenceReason.TimerEnd:
                    return trigger == ResumeTrigger.TimerDialog
                        ? ResumeAction.SoftResume
                        : ResumeAction.KeepSilent;
                case SilenceReason.UserPause:
                    return trigger == ResumeTrigger.SceneSwipe
                           || trigger == ResumeTrigger.MediaPlay
                        ? ResumeAction.SoftResume
                        : ResumeAction.KeepSilent;
                case SilenceReason.SystemInterrupt:
                    return trigger == ResumeTrigger.Foreground
                           || trigger == ResumeTrigger.FocusRestored
                           || trigger == ResumeTrigger.SceneSwipe
                           || trigger == ResumeTrigger.MediaPlay
                           || trigger == ResumeTrigger.TimerDialog
                        ? ResumeAction.SoftResume
                        : ResumeAction.KeepSilent;
                default:
                    return ResumeAction.KeepAudible;
            }
        }

        /// <summary>
        /// 软续 / 硬续都会把音乐和音效一起拉起来。只拉一边会留下一边仍 Pause。
        /// </summary>
        public static bool RestoresMusicAndEffects(ResumeAction action) =>
            action == ResumeAction.SoftResume || action == ResumeAction.HardReplay;

        /// <summary>
        /// 微信发语音取消后，原生 suspended 常已清掉，对账把
        /// <see cref="SilenceReason.SystemInterrupt"/> 擦成 <see cref="SilenceReason.None"/>。
        /// 音乐或音效只要还有一边停着，原因必须留住，回波萝才能软续两边。
        /// </summary>
        public static bool ShouldKeepSystemInterruptAfterWeChatVoiceCancel(
            SilenceReason heldBeforeResync,
            SilenceReason afterResync,
            bool musicStillPaused,
            bool effectsStillPaused)
        {
            return heldBeforeResync == SilenceReason.SystemInterrupt
                   && afterResync == SilenceReason.None
                   && (musicStillPaused || effectsStillPaused);
        }

        /// <summary>
        /// 微信发送语音取消后回到波萝。
        /// <paramref name="showInterruptInsteadOfResume"/> 为真：回前台 / 焦点回归保持静音并显示打断，
        /// 滑动或播放键才软续。否则对账擦掉打断、内容声还停着时，仍按系统打断软续。
        /// </summary>
        public static ResumeAction DecideReturnAfterWeChatVoiceCancel(
            SilenceReason heldBeforeResync,
            SilenceReason afterResync,
            bool musicStillPaused,
            bool effectsStillPaused,
            ResumeTrigger trigger,
            bool showInterruptInsteadOfResume = false)
        {
            if (showInterruptInsteadOfResume)
            {
                switch (trigger)
                {
                    case ResumeTrigger.SceneSwipe:
                    case ResumeTrigger.MediaPlay:
                    case ResumeTrigger.TimerDialog:
                        return ResumeAction.SoftResume;
                    default:
                        return ResumeAction.KeepSilent;
                }
            }

            SilenceReason reason = ShouldKeepSystemInterruptAfterWeChatVoiceCancel(
                heldBeforeResync, afterResync, musicStillPaused, effectsStillPaused)
                ? SilenceReason.SystemInterrupt
                : afterResync;
            return Decide(reason, trigger);
        }

        /// <summary>
        /// 安卓系统更新这类弹窗：瞬时丢焦，但没有外来媒体、也没有通话/语音。
        /// 不算打断，不抢回焦点。
        /// </summary>
        public static bool IsIgnorableSystemDialogFocus(
            bool competing, bool inVoiceCall, bool externalMusic)
        {
            return !competing && !inVoiceCall && !externalMusic;
        }

        public static bool WantsPlayback(SilenceReason reason) =>
            reason == SilenceReason.None;

        /// <summary>只有引擎已经 Playing 才清暂停闩。排队和失败都留着。</summary>
        public static bool ClearsLatch(ResumeCommit commit) =>
            commit == ResumeCommit.Done;
    }
}
