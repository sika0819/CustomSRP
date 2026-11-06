using UnityEngine;
using UnityGameFramework.Runtime;
using WanosAudio;

namespace Game
{
    /// <summary>
    /// App 前后台 / 音频打断的统一恢复入口。
    /// <para>
    /// 系统打断 / 禁后台播停掉的声音：回前台（以及点场景）应把音乐和音效拉回来。
    /// 定时到点、负一屏用户暂停：自动回前台保持静音并提示；定时等弹窗，其余滑动从暂停处续。
    /// </para>
    /// <para>
    /// LifecycleKind 分级：任务栏预览 / 后台续播不得用启动 unmute 门反向 HoldYield。
    /// 恢复出口：<see cref="ResumePaired"/>（从暂停处续）/ <see cref="TryPairedAutoRestore"/>（自动）。
    /// </para>
    /// </summary>
    public sealed class AudioAppLifecycle : MonoBehaviour
    {
        static AudioAppLifecycle _instance;

        /// <summary>回到前台后需要跑一次恢复。</summary>
        static bool _needsRestore;

        /// <summary>
        /// 负一屏 / 岛内用户暂停后，等点场景硬续播。
        /// KeepMute 会清 <c>_needsRestore</c>，不能只靠它；PublishPlayingForced 也不得清掉此意图。
        /// </summary>
        static bool _pendingUserResume;

        /// <summary>最近一次系统前后台。启动出声前用来避免人已经切到别的播放器还 unmute。</summary>
        static bool _inBackground;

        /// <summary>进后台时是否因 yield 停过声（回前台才需要配对恢复）。</summary>
        static bool _yieldedOnBackground;

        /// <summary>配对恢复重入闩：ReclaimFocus → UnitySendMessage 不得再套一层全量恢复。</summary>
        static bool _pairedRestoreInFlight;

        public static bool IsInBackground => _inBackground;

        public static bool PendingUserResume => _pendingUserResume;

        public static void EnsureExists()
        {
            if (_instance != null)
                return;
            var go = new GameObject(nameof(AudioAppLifecycle));
            _instance = go.AddComponent<AudioAppLifecycle>();
        }

        /// <summary>Disable Domain Reload 下静态字段不会自动清。</summary>
        public static void ResetSession()
        {
            _needsRestore = false;
            _pendingUserResume = false;
            _inBackground = false;
            _yieldedOnBackground = false;
            _pairedRestoreInFlight = false;
            IslandAudioSession.ResetStartupGate();
            PlaybackGate.ResetSession();
        }

        /// <summary>揭罩时人已在前台并且解 mute 成功：清掉加载期间留下的恢复闩。</summary>
        public static void NoteForegroundAudibleStarted()
        {
            _needsRestore = false;
            _pendingUserResume = false;
            _inBackground = false;
            _yieldedOnBackground = false;
            PlaybackGate.SetLifecycle(LifecycleKind.Foreground, "audible-started");
            PlaybackGate.NotePlaying("audible-started");
        }

        /// <summary>
        /// 用户续播已真正发出（硬起播 / MediaPlay / 定时取消）：清 pending，避免再次点场景误重起。
        /// </summary>
        public static void NoteUserResumeCommitted()
        {
            _pendingUserResume = false;
            MusicPauseUICallback.Hide();
            PlaybackGate.NotePlaying("user-resume-committed");
        }

        /// <summary>
        /// 玩家点了游戏场景：兜底跑一次恢复。
        /// 宿主壳里 Unity 的 <c>OnApplicationFocus</c> 不一定触发（Unity 窗口不一定是 key），
        /// 一次真实点击是可靠的「我回前台了」信号。
        /// 负一屏暂停（<see cref="NowPlayingService.UserManuallyPaused"/>）也在此覆盖续播，
        /// 不挂到 focus 回调上，避免通知栏抢焦把岛内主动暂停误恢复。
        /// </summary>
        public static void OnSceneInteraction()
        {
            // 定时结束弹窗未关：禁止点场景续播，等「取消/重复」。
            if (NowPlayingService.PausedForTimerEnd
                || PlaybackGate.Silence == SilenceReason.TimerEnd)
                return;
            if (PlaybackGate.Silence == SilenceReason.None
                && !NeedsRestore()
                && !_pendingUserResume)
                return;
            RestoreForegroundAudio("scene-interaction");
        }

        void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }

            _instance = this;
        }

        void OnApplicationPause(bool paused)
        {
            _inBackground = paused;
            if (paused)
            {
                EnterBackground();
                return;
            }

            LeaveBackground("app-pause-resume");
        }

#if !UNITY_EDITOR
        void OnApplicationFocus(bool focused)
        {
            // 真机：焦点回来时若已武装恢复则跑一次。
            // Editor 不挂此回调——HostView 点 Console/Inspector 会疯狂 OnFocus，
            // 不能当「回前台」；Editor 靠 pause + 场景点击兜底。
            if (!focused)
                return;
            _inBackground = false;
            LeaveBackground("app-focus-gain");
        }
#endif

        /// <summary>
        /// 进后台：先判定 <see cref="LifecycleKind"/>，再决定是否 Notify / HoldYield。
        /// 禁止先标后台再查 AllowUnmuteNow（会把任务栏预览误判成启动让出）。
        /// </summary>
        static void EnterBackground()
        {
            _needsRestore = true;
            BoluoAndroidEngineNative.WarmBackgroundEndedJni();
            BackgroundTransportSync.PublishPlan();

            LifecycleKind kind = ResolveBackgroundKind();
            PlaybackGate.SetLifecycle(kind, "enter-background");

            bool bgDisable = BackgroundPlayPolicyCallback.GetBackgroundPlayDisable();
            // Recents / KeepPlaying：仍同步原生前后台态（FGS / transport），但不停声。
            BoluoAndroidEngineNative.NotifyAppLifecycle(true, bgDisable);

            _yieldedOnBackground = false;
            if (kind == LifecycleKind.BackgroundYield
                || kind == LifecycleKind.CompetitorFocus)
            {
                IslandAudioSession.HoldYield(
                    kind == LifecycleKind.CompetitorFocus
                        ? "competitor-focus"
                        : "background-yield");
                _yieldedOnBackground = true;
                if (kind == LifecycleKind.CompetitorFocus)
                    PlaybackGate.NoteSystemSuspended("background-competitor");
            }
            else if (PlaybackGate.State == PlaybackGateState.Playing
                     || PlaybackGate.State == PlaybackGateState.Stopped)
            {
                // 任务栏 / 后台续播：保持 Playing 展示。
                if (PlaybackGate.State != PlaybackGateState.UserPaused
                    && PlaybackGate.State != PlaybackGateState.SystemSuspended)
                    PlaybackGate.NotePlaying("background-keep");
            }

            Log.Info(
                $"[AudioAppLifecycle] enter background kind={kind}"
                + $" yielded={(_yieldedOnBackground ? 1 : 0)}"
                + $" gate={PlaybackGate.State}");
        }

        /// <summary>
        /// BackgroundYieldGate：只认禁后台播 / 用户暂停 / 抢焦。
        /// 不用 !AudibleCommitted 或「已标后台后的 AllowUnmute」停已在响的声音。
        /// 启动期尚未出声且禁后台播 → Yield；允许后台播 → KeepPlaying（含冷启动后上滑任务栏）。
        /// </summary>
        static LifecycleKind ResolveBackgroundKind()
        {
            if (PlaybackGate.State == PlaybackGateState.SystemSuspended
                || WanosEngine.IsInterrupted)
                return LifecycleKind.CompetitorFocus;

            if (NowPlayingService.UserManuallyPaused
                || NowPlayingService.PausedForTimerEnd
                || PlaybackGate.Silence == SilenceReason.UserPause
                || PlaybackGate.Silence == SilenceReason.TimerEnd
                || PlaybackGate.State == PlaybackGateState.UserPaused)
            {
                // 用户已停：后台不必再 HoldYield 引擎（可能已 Pause），按 Yield 语义走 keep-mute。
                return LifecycleKind.BackgroundYield;
            }

            // 系统更新弹窗盖住活动：没有外来媒体，不停音乐音效，也不抢焦点。
            // 读到就清，避免下一次按 Home 还被当成弹窗。来晚的回调靠 OnSystemDialogCover 软续。
            if (BoluoAndroidEngineNative.IsSystemDialogCover())
            {
                BoluoAndroidEngineNative.ClearSystemDialogCover();
                return LifecycleKind.BackgroundKeepPlaying;
            }

            if (BackgroundPlayPolicyCallback.GetBackgroundPlayDisable())
                return LifecycleKind.BackgroundYield;

            // 允许后台播：Unity OnApplicationPause 在宿主里可能来自 Recents onStop 或 HOME。
            // 宿主 Recents 预览阶段引擎仍 resumed；此处统一 KeepPlaying，避免启动门误杀。
            // 真正「从未出声」的启动放弃只在禁后台播分支 Yield。
            return LifecycleKind.BackgroundKeepPlaying;
        }

        static void LeaveBackground(string reason)
        {
            if (!NeedsRestore())
                return;
            PlaybackGate.SetLifecycle(LifecycleKind.Foreground, reason);
            RestoreForegroundAudio(reason);
        }

        /// <summary>
        /// 是否需要跑一次恢复（回前台自动路径）。
        /// <para>
        /// 只认「进过后台」latch 与原生打断标记。
        /// 禁止用 <c>PlaybackState != Started</c> 当常驻条件：macOS / Editor 上该状态
        /// 经常与真实发声不同步，会导致每次焦点 / 点击都 Stop+Play 音乐与环境声。
        /// 手动暂停的点场景覆盖见 <see cref="OnSceneInteraction"/>。
        /// </para>
        /// </summary>
        static bool NeedsRestore()
        {
            if (_needsRestore)
                return true;
            if (WanosEngine.IsInterrupted)
                return true;
            if (PlaybackGate.State == PlaybackGateState.SystemSuspended)
                return true;
            return false;
        }

        /// <summary>回前台恢复：先对账，再按 <see cref="SilenceResumePolicy.Decide"/> 只走一条路。</summary>
        public static void RestoreForegroundAudio(string reason)
        {
            if (!WanosEngine.IsLoaded)
            {
                // 引擎还没起来（不在岛内）→ 没有可恢复的东西。
                // 保留 latch，避免点场景时 NeedsRestore 已灭、再也补不回来。
                return;
            }

            SilenceReason held = PlaybackGate.Silence;
            if (NowPlayingService.PausedForTimerEnd)
                held = SilenceReason.TimerEnd;

            var musicEarly = GameEntry.Music ?? MusicPlaylist.Instance;
            if (musicEarly != null)
            {
                switch (musicEarly.PeekPendingReplay())
                {
                    case PendingReplayNudge.LeaveWaiting:
                    case PendingReplayNudge.WaitForGate:
                        Log.Info($"[AudioAppLifecycle] hard replay pending, leave latch ({reason})");
                        return;
                    case PendingReplayNudge.PostNow:
                        musicEarly.ContinuePendingHardReplay();
                        Log.Info($"[AudioAppLifecycle] hard replay gate open, post now ({reason})");
                        return;
                    case PendingReplayNudge.RetryAfterMiss:
                        musicEarly.AbandonStuckHardReplay();
                        ApplyForegroundLifecycle(reason);
                        if (!ResumePaired(reason))
                        {
                            _needsRestore = true;
                            ShowPauseTipIfAllowed();
                            Log.Info($"[AudioAppLifecycle] stuck hard replay retry failed ({reason})");
                            return;
                        }

                        _needsRestore = false;
                        _yieldedOnBackground = false;
                        _pendingUserResume = false;
                        Log.Info($"[AudioAppLifecycle] stuck hard replay retried ({reason})");
                        return;
                }
            }

            // 先 Drain Push（定时 0,-1 等），再对账。Adopt 用写入前的 Silence，suspended 不降级用户暂停。
            Game.Native.NativePushReceiver.Drain();
            BackgroundTransportSync.ResyncFromNative();

            if (held == SilenceReason.TimerEnd && PlaybackGate.Silence != SilenceReason.TimerEnd)
                PlaybackGate.SetSilence(SilenceReason.TimerEnd, "restore-timer-held");

            bool nativeVoicesPaused = BoluoAndroidEngineNative.NeedsForegroundVoiceRestore();
            bool musicStillPaused = nativeVoicesPaused
                                    || (musicEarly != null
                                        && musicEarly.HasRestorableTrack
                                        && !musicEarly.IsPlaying);
            bool effectsStillPaused = nativeVoicesPaused
                                      || IslandAudioSession.IsYieldHeld
                                      || (IslandAudioSession.Instance != null
                                          && IslandAudioSession.Instance.IsContentAudioPaused);
            if (SilenceResumePolicy.ShouldKeepSystemInterruptAfterWeChatVoiceCancel(
                    held, PlaybackGate.Silence, musicStillPaused, effectsStillPaused))
                PlaybackGate.SetSilence(SilenceReason.SystemInterrupt, "wechat-voice-cancel-return");

            ResumeTrigger trigger = MapTrigger(reason);
            bool showWeChatInterrupt = HoldWeChatInterruptForSwipe();
            ResumeAction action = SilenceResumePolicy.DecideReturnAfterWeChatVoiceCancel(
                held, PlaybackGate.Silence, musicStillPaused, effectsStillPaused, trigger,
                showWeChatInterrupt);
            if (showWeChatInterrupt && action == ResumeAction.KeepSilent)
                PlaybackGate.SetSilence(SilenceReason.SystemInterrupt, "wechat-voice-cancel-return");
            Log.Info(
                $"[AudioAppLifecycle] decide silence={PlaybackGate.Silence} trigger={trigger}"
                + $" → {action} ({reason})");

            switch (action)
            {
                case ResumeAction.KeepSilent:
                    KeepManualOrTimerEndMute(reason);
                    return;
                case ResumeAction.HardReplay:
                    ApplyForegroundLifecycle(reason);
                    if (!CommitPairedHardResume(reason))
                    {
                        _needsRestore = true;
                        _pendingUserResume = PlaybackGate.Silence != SilenceReason.TimerEnd
                                             && !NowPlayingService.PausedForTimerEnd;
                        if (PlaybackGate.Silence != SilenceReason.TimerEnd
                            && !NowPlayingService.PausedForTimerEnd)
                            NowPlayingService.NoteUserPaused(true);
#if UNITY_ANDROID && !UNITY_EDITOR
                        BoluoAndroidEngineNative.MarkUserPausedFromUnity(true);
#endif
                        NowPlayingService.PublishFromGate();
                        ShowPauseTipIfAllowed();
                        Log.Info($"[AudioAppLifecycle] hard resume failed ({reason})");
                        return;
                    }

                    _needsRestore = false;
                    _yieldedOnBackground = false;
                    if (PlaybackGate.Silence == SilenceReason.None)
                        _pendingUserResume = false;
                    Log.Info($"[AudioAppLifecycle] hard resume accepted ({reason})");
                    return;
                case ResumeAction.SoftResume:
                    ApplyForegroundLifecycle(reason);
                    if (trigger == ResumeTrigger.SceneSwipe
                        || trigger == ResumeTrigger.MediaPlay
                        || trigger == ResumeTrigger.TimerDialog)
                    {
                        if (!ResumePaired(reason))
                        {
                            _needsRestore = true;
                            ShowPauseTipIfAllowed();
                            Log.Info($"[AudioAppLifecycle] paired resume failed ({reason})");
                            return;
                        }

                        _needsRestore = false;
                        _yieldedOnBackground = false;
                        _pendingUserResume = false;
                        Log.Info($"[AudioAppLifecycle] paired resume done ({reason})");
                        return;
                    }

                    if (!TryPairedAutoRestore(reason))
                    {
                        _needsRestore = true;
                        KeepManualOrTimerEndMute(reason);
                        Log.Info($"[AudioAppLifecycle] auto restore failed → keep mute ({reason})");
                        return;
                    }

                    _needsRestore = false;
                    _yieldedOnBackground = false;
                    if (PlaybackGate.Silence == SilenceReason.None)
                        _pendingUserResume = false;
                    Log.Info($"[AudioAppLifecycle] foreground restore done ({reason})");
                    return;
                default:
                    if (_yieldedOnBackground
                        || WanosEngine.IsInterrupted
                        || IslandAudioSession.IsYieldHeld)
                    {
                        ApplyForegroundLifecycle(reason);
                        if (!TryPairedAutoRestore(reason))
                        {
                            _needsRestore = true;
                            PlaybackGate.SetSilence(SilenceReason.SystemInterrupt, "yield-restore-failed");
                            KeepManualOrTimerEndMute(reason);
                            return;
                        }

                        _needsRestore = false;
                        _yieldedOnBackground = false;
                        _pendingUserResume = false;
                        Log.Info($"[AudioAppLifecycle] yield restored ({reason})");
                        return;
                    }

                    if (SilenceResumePolicy.ShouldRestoreInsteadOfKeepAudible(
                            PlaybackGate.Silence, nativeVoicesPaused))
                    {
                        ApplyForegroundLifecycle(reason);
                        if (!TryPairedAutoRestore(reason))
                        {
                            _needsRestore = true;
                            if (PlaybackGate.Silence == SilenceReason.None)
                                PlaybackGate.SetSilence(
                                    SilenceReason.SystemInterrupt, "voices-still-paused");
                            KeepManualOrTimerEndMute(reason);
                            Log.Info(
                                $"[AudioAppLifecycle] voices still paused, restore failed ({reason})");
                            return;
                        }

                        _needsRestore = false;
                        _yieldedOnBackground = false;
                        if (PlaybackGate.Silence == SilenceReason.None)
                            _pendingUserResume = false;
                        Log.Info($"[AudioAppLifecycle] voices still paused, restored ({reason})");
                        return;
                    }

                    KeepAudible(reason);
                    return;
            }
        }

        /// <summary>
        /// 微信发语音取消：原生仍停着。回前台显示打断，不要自动出声。
        /// </summary>
        static bool HoldWeChatInterruptForSwipe()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            return BoluoAndroidEngineNative.ShouldHoldWeChatInterruptOnReturn();
#elif UNITY_IOS && !UNITY_EDITOR
            return BoluoAudioSessionNative.ShouldHoldWeChatInterruptOnReturn();
#else
            return false;
#endif
        }

        static ResumeTrigger MapTrigger(string reason)
        {
            switch (reason)
            {
                case "scene-interaction":
                    return ResumeTrigger.SceneSwipe;
                case "media-play":
                case "intent-resume":
                    return ResumeTrigger.MediaPlay;
                case "timer-end":
                    return ResumeTrigger.TimerDialog;
                case "system-audio-restored":
                    return ResumeTrigger.FocusRestored;
                default:
                    return ResumeTrigger.Foreground;
            }
        }

        static void ApplyForegroundLifecycle(string reason)
        {
            PlaybackGate.SetLifecycle(LifecycleKind.Foreground, reason);
            BoluoAndroidEngineNative.NotifyAppLifecycle(
                false, BackgroundPlayPolicyCallback.GetBackgroundPlayDisable());
        }

        static void KeepAudible(string reason)
        {
            _needsRestore = false;
            _yieldedOnBackground = false;
            BoluoAndroidEngineNative.NotifyAppLifecycle(
                false, BackgroundPlayPolicyCallback.GetBackgroundPlayDisable());
            PlaybackGate.NotePlaying("keep-audible");
            NowPlayingService.PublishFromGate();
            MusicPauseUICallback.Hide();
            Log.Info($"[AudioAppLifecycle] foreground keep audible ({reason})");
        }

        /// <summary>
        /// 系统更新弹窗盖住游戏：没有外来媒体就不停声。若进后台时已经 Pause 过，这里补一次软续。
        /// </summary>
        public static void NoteSystemDialogCover()
        {
            if (PlaybackGate.Silence == SilenceReason.UserPause
                || PlaybackGate.Silence == SilenceReason.TimerEnd
                || NowPlayingService.PausedForTimerEnd)
                return;

            bool yielded = _yieldedOnBackground
                           || IslandAudioSession.IsYieldHeld
                           || PlaybackGate.Silence == SilenceReason.SystemInterrupt;
            if (PlaybackGate.Silence == SilenceReason.SystemInterrupt)
                PlaybackGate.SetSilence(SilenceReason.None, "system-dialog");
            WanosEngine.ClearInterrupted("system-dialog");
            MusicPauseUICallback.Hide();
            if (!yielded)
            {
                Log.Info("[AudioAppLifecycle] system dialog cover, already audible");
                return;
            }

            _yieldedOnBackground = false;
            _needsRestore = false;
            IslandAudioSession.ClearYieldHold();
            var music = GameEntry.Music ?? MusicPlaylist.Instance;
            if (music != null && !music.HardReplayPending)
                music.TrySoftResume("system-dialog-cover");
            IslandAudioSession.Instance?.RestoreContentAudio("system-dialog-cover");
            PlaybackGate.NotePlaying("system-dialog-cover");
            NowPlayingService.PublishFromGate();
            Log.Info("[AudioAppLifecycle] system dialog cover restored");
        }

        /// <summary>
        /// 音乐和音效一起暂停，保留曲目实例。不改静音原因。
        /// <paramref name="blockAutoResume"/> 为真时同步原生 userPaused，焦点回归不得擅自续播。
        /// </summary>
        public static void StopPaired(string reason, bool blockAutoResume)
        {
            var music = GameEntry.Music ?? MusicPlaylist.Instance;
            music?.PauseEngine();
            if (blockAutoResume)
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                BoluoAndroidEngineNative.MarkUserPausedFromUnity(true);
#endif
            }

            Log.Info($"[AudioAppLifecycle] stop paired ({reason}) blockAuto={blockAutoResume}");
        }

        /// <summary>
        /// 从暂停处续音乐和音效。实例已经没了才重发当前曲。
        /// 成功后清静音原因并收起提示。排队中返回 true。
        /// </summary>
        public static bool ResumePaired(string reason)
        {
            if (!WanosEngine.IsPlaybackEnabled)
                WanosEngine.EnablePlayback(reason);

            if (!WanosEngine.RestoreForeground(reason))
                return false;

            IslandAudioSession.ClearYieldHold();
            IslandAudioSession.NoteAudibleCommitted();
            // 先清原生暂停旗再 Resume。否则 Playing 回调会按 userPaused 把刚续上的曲子再 Pause，
            // 抢焦也会被跳过。硬续失败时下面会把旗闩回去。
#if UNITY_ANDROID && !UNITY_EDITOR
            BoluoAndroidEngineNative.MarkUserPausedFromUnity(false);
#endif
            BoluoAndroidEngineNative.ReclaimFocusOnForeground(true);

            var music = GameEntry.Music ?? MusicPlaylist.Instance;
            // 用户暂停 / 到点 / 负一屏播放键：agent 还在就 Resume_Music，从暂停处续。
            // 只有编号丢了才 Stop+Play 重发当前曲。硬续的 C# 暂停闩留到真正 Playing。
            bool softResumed = music != null && music.TrySoftResume(reason);
            if (!SilenceResumePolicy.SoftResumeAccepted(music != null, softResumed))
            {
                if (music == null)
                {
                    RelatchNativeUserPause();
                    return false;
                }
                ResumeCommit hard = music.BeginHardReplay(reason);
                if (hard == ResumeCommit.Failed)
                {
                    RelatchNativeUserPause();
                    return false;
                }
                if (hard == ResumeCommit.WaitingForPost)
                {
                    Log.Info($"[AudioAppLifecycle] resume paired waiting for post ({reason})");
                    return true;
                }
            }

            if (music != null && music.HasRestorableTrack && !music.IsPlaying && !music.HardReplayPending)
            {
                RelatchNativeUserPause();
                return false;
            }

            ClearSilenceAfterResume(reason);
            IslandAudioSession.Instance?.RestoreContentAudio(reason);
            NowPlayingService.PublishFromGate();
            MusicPauseUICallback.Hide();
            _pendingUserResume = false;
            Log.Info($"[AudioAppLifecycle] resume paired ({reason})");
            return true;
        }

        static void RelatchNativeUserPause()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            BoluoAndroidEngineNative.MarkUserPausedFromUnity(true);
#endif
        }

        /// <summary>硬续没响：下一滑还能再试。到点不走滑动。</summary>
        public static void ArmSwipeResumeAfterMissedReplay()
        {
            _needsRestore = true;
            if (PlaybackGate.Silence != SilenceReason.TimerEnd
                && !NowPlayingService.PausedForTimerEnd)
                _pendingUserResume = true;
        }

        /// <summary>旧名。显式续播从暂停处继续，不再从头 PlayIndex。</summary>
        public static bool CommitPairedHardResume(string reason) => ResumePaired(reason);

        static void ClearSilenceAfterResume(string reason)
        {
            if (PlaybackGate.Silence == SilenceReason.TimerEnd)
                NowPlayingService.NotePausedForTimerEnd(false);
            else if (PlaybackGate.Silence == SilenceReason.UserPause)
                NowPlayingService.NoteUserPaused(false);
            PlaybackGate.NotePlaying(reason);
        }

        /// <summary>
        /// 系统打断 / 后台 yield 自动路径：软续 BGM + 内容声；失败则回滚 content latch，Gate 不进 Playing。
        /// </summary>
        public static bool TryPairedAutoRestore(string reason)
        {
            if (_pairedRestoreInFlight)
            {
                Log.Info($"[AudioAppLifecycle] auto restore reentrant skip ({reason})");
                return true;
            }

            _pairedRestoreInFlight = true;
            try
            {
                if (!WanosEngine.IsPlaybackEnabled)
                    WanosEngine.EnablePlayback(reason);

                if (!WanosEngine.RestoreForeground(reason))
                    return false;

                IslandAudioSession.ClearYieldHold();
                IslandAudioSession.NoteAudibleCommitted();
                BoluoAndroidEngineNative.ReclaimFocusOnForeground();

                var music = GameEntry.Music ?? MusicPlaylist.Instance;
                bool softResumed = music != null && music.TrySoftResume(reason);
                if (!SilenceResumePolicy.SoftResumeAccepted(music != null, softResumed))
                {
                    if (music == null)
                        return false;
                    ResumeCommit hard = music.BeginHardReplay(reason);
                    if (hard == ResumeCommit.Failed)
                    {
                        IslandAudioSession.HoldYield("auto-restore-bgm-dead");
                        try { WanosEngine.PauseAll(); }
                        catch (System.Exception e)
                        {
                            Log.Warning($"[AudioAppLifecycle] rollback Pause_ALL: {e.Message}");
                        }

                        PlaybackGate.NoteSystemSuspended("auto-restore-bgm-dead");
                        return false;
                    }

                    if (hard == ResumeCommit.WaitingForPost)
                    {
                        Log.Info($"[AudioAppLifecycle] soft resume fell through to hard replay ({reason})");
                        return true;
                    }
                }

                // 软续后若仍无曲可播且本应有曲：视为失败，禁止只起音效。
                if (music != null && music.HasRestorableTrack && !music.IsPlaying && !music.HardReplayPending)
                {
                    IslandAudioSession.HoldYield("auto-restore-bgm-dead");
                    try { WanosEngine.PauseAll(); }
                    catch (System.Exception e)
                    {
                        Log.Warning($"[AudioAppLifecycle] rollback Pause_ALL: {e.Message}");
                    }
                    PlaybackGate.NoteSystemSuspended("auto-restore-bgm-dead");
                    return false;
                }

                IslandAudioSession.Instance?.RestoreContentAudio(reason);
                PlaybackGate.NotePlaying(reason);
                NowPlayingService.PublishFromGate();
                MusicPauseUICallback.Hide();
                return true;
            }
            finally
            {
                _pairedRestoreInFlight = false;
            }
        }

        /// <summary>
        /// 原生 <c>onFocusResume</c> 已落地 Resume_ALL/Music + unmute 爬坡后的 Unity 对账。
        /// 只清打断闩、内容声闸、Gate/卡片；禁止再 Unmute / Reclaim / EnsureMusic。
        /// 否则与原生爬坡叠峰，回前台音乐/音效有明显卡顿。
        /// </summary>
        public static bool SyncAfterNativeFocusResume(string reason)
        {
            if (NowPlayingService.PausedForTimerEnd
                || PlaybackGate.State == PlaybackGateState.UserPaused
                || NowPlayingService.UserManuallyPaused)
            {
                Log.Info($"[AudioAppLifecycle] native focus sync skipped user/timer ({reason})");
                return false;
            }

            if (!WanosEngine.IsLoaded || !WanosEngine.IsPlaybackEnabled)
            {
                Log.Info($"[AudioAppLifecycle] native focus sync skipped engine ({reason})");
                return false;
            }

            NowPlayingService.NoteUserPaused(false);
#if UNITY_ANDROID && !UNITY_EDITOR
            BoluoAndroidEngineNative.MarkUserPausedFromUnity(false);
#endif

            // 禁止 WanosEngine.RestoreForeground：会 TrySetMute(false)+ReapplyVolumes，
            // 与原生 guarded unmute 爬坡抢增益。
            WanosEngine.ClearInterrupted(reason);
            IslandAudioSession.ClearYieldHold();
            IslandAudioSession.NoteAudibleCommitted();

            var music = GameEntry.Music ?? MusicPlaylist.Instance;
            // 原生若只拉起了音效，这里补一次 Resume_Music。已经续过的会被短窗口吃掉。
            if (music != null && music.HasRestorableTrack && !music.IsPlaying)
            {
                bool resumed = music.TrySoftResume(reason);
                Log.Info($"[BoluoAudioFg] sync music soft-resume={resumed} ({reason})");
            }
            else
            {
                music?.ApplyNativePausedLatch(false);
            }
            IslandAudioSession.Instance?.SyncContentAudioAfterNativeResume(reason);

            PlaybackGate.NotePlaying(reason);
            NowPlayingService.PublishFromGate();
            MusicPauseUICallback.Hide();
            _needsRestore = false;
            _yieldedOnBackground = false;
            _pendingUserResume = false;
            Log.Info($"[AudioAppLifecycle] native focus sync done ({reason})");
            return true;
        }

        /// <summary>
        /// 定时到点 / 用户暂停：回前台只同步前台态并闩住内容声，禁止 Resume_ALL / 解 mute。
        /// </summary>
        static void KeepManualOrTimerEndMute(string reason)
        {
            var music = GameEntry.Music ?? MusicPlaylist.Instance;
            if (music != null && music.HardReplayPending)
            {
                Log.Info($"[AudioAppLifecycle] keep mute skipped, hard replay pending ({reason})");
                return;
            }

            _needsRestore = false;
            // 用户暂停（非到点）：武装点场景硬续；到点只等弹窗，禁止场景续。
            if (!NowPlayingService.PausedForTimerEnd
                && (NowPlayingService.UserManuallyPaused
                    || _pendingUserResume
                    || PlaybackGate.State == PlaybackGateState.UserPaused
                    || PlaybackGate.State == PlaybackGateState.SystemSuspended))
                _pendingUserResume = true;

            BoluoAndroidEngineNative.NotifyAppLifecycle(
                false, BackgroundPlayPolicyCallback.GetBackgroundPlayDisable());

            music?.ApplyNativePausedLatch(true);
            try
            {
                WanosEngine.PauseAll();
            }
            catch (System.Exception e)
            {
                Log.Warning($"[AudioAppLifecycle] timer/manual mute Pause_ALL: {e.Message}");
            }

            if (NowPlayingService.PausedForTimerEnd
                || PlaybackGate.Silence == SilenceReason.TimerEnd)
                PlaybackGate.SetSilence(SilenceReason.TimerEnd, "keep-mute");
            else if (PlaybackGate.Silence == SilenceReason.UserPause
                     || NowPlayingService.UserManuallyPaused)
                PlaybackGate.SetSilence(SilenceReason.UserPause, "keep-mute");
            else
                PlaybackGate.SetSilence(SilenceReason.SystemInterrupt, "keep-mute");

            NowPlayingService.PublishFromGate();
            ShowPauseTipIfAllowed();
            Log.Info(
                $"[AudioAppLifecycle] foreground keep mute ({reason})"
                + $" timerEnd={(NowPlayingService.PausedForTimerEnd ? 1 : 0)}"
                + $" userPaused={(NowPlayingService.UserManuallyPaused ? 1 : 0)}"
                + $" gate={PlaybackGate.State}"
                + $" pendingUserResume={(_pendingUserResume ? 1 : 0)}");
        }

        /// <summary>到点弹窗管续播：禁止滑动 tip；其余 keep-mute / 软续失败弹粘滞 tip。</summary>
        static void ShowPauseTipIfAllowed()
        {
            if (NowPlayingService.PausedForTimerEnd)
                return;
            MusicPauseUICallback.Show(MusicPauseUICallback.TipKind.ProgrammaticPause);
        }
    }
}
