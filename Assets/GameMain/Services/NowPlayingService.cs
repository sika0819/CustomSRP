using System.IO;
using Game.BoluoApi.Music;
using Game.Native;
using UnityEngine;
using UnityGameFramework.Runtime;
using WanosAudio;

namespace Game
{
    /// <summary>
    /// 负一屏元数据权威入口 + 远程控播收口（对齐旧 GameData.UpdateNowPlayingInfo）。
    /// 系统卡片由 Unity 插件写；IMP 105 仅镜像宿主。
    /// </summary>
    public static class NowPlayingService
    {
        public const string ArtworkRelative = "WanosAudio/Artwork/boluo.png";
        const string BackgroundPlayDisableKey = "Boluo.BackgroundPlayDisable";

        static bool _handlersBound;
        static bool _armed;

        /// <summary>
        /// 负一屏 / IMP 播放、暂停、切歌的唯一防呆会话。
        /// 曲目下标仍由 <see cref="MusicPlaylist"/> 推进；这里只决定忽略、合并和到点丢播放。
        /// </summary>
        static readonly BoluoNowPlayingRemoteSession Commands =
            new BoluoNowPlayingRemoteSession(64, isPlaying: false, userManuallyPaused: false);

        public static bool UserManuallyPaused =>
            PlaybackGate.Silence == SilenceReason.UserPause
            || PlaybackGate.Silence == SilenceReason.TimerEnd;

        /// <summary>定时到点。回前台和滑动都不续，只有弹窗。</summary>
        public static bool PausedForTimerEnd =>
            PlaybackGate.Silence == SilenceReason.TimerEnd;

        public static bool IsArmed => _armed;

        public static void NoteUserPaused(bool paused)
        {
            PlaybackGate.NoteUserPaused(paused, paused ? "note-user-paused" : "note-user-resumed");
            Commands.AdoptPlayback(PlaybackGate.CardShouldShowPlaying, UserManuallyPaused);
        }

        /// <summary>Resync 写完 <see cref="PlaybackGate.Silence"/> 后，把远程会话对齐过去。</summary>
        public static void MirrorSilenceLatch()
        {
            if (PausedForTimerEnd)
            {
                Commands.NotePausedForTimerEnd(true);
                BoluoAndroidEngineNative.NotePausedForTimerEnd(true);
            }
            else
                Commands.AdoptPlayback(PlaybackGate.CardShouldShowPlaying, UserManuallyPaused);
        }

        /// <summary>定时到点 / 弹窗续播：抬或清到点停播闩，并丢掉尚未落地的播放。</summary>
        public static void NotePausedForTimerEnd(bool paused)
        {
            if (paused)
            {
                PlaybackGate.SetSilence(SilenceReason.TimerEnd, "timer-end");
                Commands.NotePausedForTimerEnd(true);
                BoluoAndroidEngineNative.NotePausedForTimerEnd(true);
            }
            else if (PlaybackGate.Silence == SilenceReason.TimerEnd)
            {
                Commands.NotePausedForTimerEnd(false);
                BoluoAndroidEngineNative.NotePausedForTimerEnd(false);
                PlaybackGate.SetSilence(SilenceReason.None, "timer-end-clear");
                Commands.AdoptPlayback(PlaybackGate.CardShouldShowPlaying, false);
            }
        }

        /// <summary>
        /// Android StreamingAssets 是 jar: URL，传相对 asset 路径给 MediaSession 解码；
        /// iOS 传 Data/Raw 绝对路径（原生还有 bundle fallback）。
        /// </summary>
        public static string ArtworkPath
        {
            get
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                return ArtworkRelative;
#else
                return Path.Combine(Application.streamingAssetsPath, ArtworkRelative);
#endif
            }
        }

        public static void Init()
        {
            if (_handlersBound)
                return;
            _handlersBound = true;

            BackgroundPlayPolicyCallback.SetBackgroundPlayDisableCallback(LoadBackgroundPlayDisable);
            GlobalPlaybackIntentCallback.SetHandlers(OnIntentPauseAll, OnIntentResumeAll);
            GlobalPlaybackIntentCallback.SetMediaTrackChangedHandler(OnMediaTrackChanged);
            GlobalPlaybackIntentCallback.SetSystemAudioInterruptedHandler(OnSystemAudioInterrupted);
            GlobalPlaybackIntentCallback.SetSystemAudioRestoredHandler(OnSystemAudioRestored);
            GlobalPlaybackIntentCallback.SetSystemDialogCoverHandler(AudioAppLifecycle.NoteSystemDialogCover);
            NowPlayingSkipBridge.SetHandlers(OnSkipNext, OnSkipPrev);

            // tip/toast 同一全局气泡；按 TipKind 粘滞/瞬态；到点不弹（KeepMute 侧拦截）。
            MusicPauseUICallback.SetCallbacks(
                () => UiDialogService.ShowAudioPauseTip(MusicPauseUICallback.TipKind.ProgrammaticPause),
                UiDialogService.DismissToast);
            MusicPauseUICallback.SetShowWithKindCallback(UiDialogService.ShowAudioPauseTip);
            MusicPauseUICallback.SetBlankAreaGestureCallback(AudioAppLifecycle.OnSceneInteraction);

            if (GameEntry.Event != null)
                GameEntry.Event.Subscribe(IslandWorldReadyEventArgs.EventId, OnWorldReady);

            Log.Info("[NowPlaying] Init");
        }

        public static void ResetSession()
        {
            CleanupAfterLeaveIsland();
            if (_handlersBound && GameEntry.Event != null)
                GameEntry.Event.Unsubscribe(IslandWorldReadyEventArgs.EventId, OnWorldReady);
            GlobalPlaybackIntentCallback.ClearHandlers();
            NowPlayingSkipBridge.Clear();
            BackgroundPlayPolicyCallback.ClearCallback();
            _handlersBound = false;
            Commands.CancelPendingPlayPause();
            Commands.NoteSkipSettled();
        }

        static void OnSkipNext()
        {
            var music = GameEntry.Music ?? MusicPlaylist.Instance;
            AcceptSkip(+1, () => music?.SkipAdjacentInAlbum(+1));
        }

        static void OnSkipPrev()
        {
            var music = GameEntry.Music ?? MusicPlaylist.Instance;
            AcceptSkip(-1, () => music?.SkipAdjacentInAlbum(-1));
        }

        public static void SetBackgroundPlayDisable(bool disable)
        {
            if (GameEntry.Setting != null)
            {
                GameEntry.Setting.SetBool(BackgroundPlayDisableKey, disable);
                GameEntry.Setting.Save();
            }
#if UNITY_IOS && !UNITY_EDITOR
            // Host also dlsym BoluoWanosLifecycle_SetBackgroundPlayDisabled.
#endif
            if (disable && _armed)
                ClearCard();
        }

        static bool LoadBackgroundPlayDisable()
        {
            if (GameEntry.Setting != null && GameEntry.Setting.HasSetting(BackgroundPlayDisableKey))
                return GameEntry.Setting.GetBool(BackgroundPlayDisableKey, false);
            return false;
        }

        static void OnWorldReady(object sender, GameFramework.Event.GameEventArgs e)
        {
            ArmSession();
        }

        /// <summary>进岛后允许 NowPlaying / RemoteCommand。</summary>
        public static void ArmSession()
        {
            bool firstArm = !_armed;
            _armed = true;
            Commands.AdoptPlayback(false, false);

            // 每次 Arm 都确保平台门面 + 开闸：冷启动 Cleanup 会关 sPlaybackActive；
            // 若只在 !_armed 时 Init，已 Arm 但未 Init 会永久吞掉负一屏推送。
            EnsurePlatformSession();

            if (firstArm)
                PublishFromPlaylist();
            Log.Info("[NowPlaying] ArmSession");
        }

        /// <summary>
        /// 进岛当下就出负一屏，不等 bank / 引擎 / 揭罩。
        /// 人已经不在前台则不出，避免占住抖音 / QQ 的卡片。
        /// </summary>
        public static void ShowCardIfForeground()
        {
            if (AudioAppLifecycle.IsInBackground)
            {
                Log.Info("[NowPlaying] ShowCardIfForeground skipped — unity background");
                return;
            }

            if (BackgroundPlayPolicyCallback.GetBackgroundPlayDisable())
            {
                Log.Info("[NowPlaying] ShowCardIfForeground skipped — background play disabled");
                return;
            }

            bool wasArmed = _armed;
            ArmSession();
            bool nativeForeground = BoluoAudioSessionNative.AllowUnmuteNow()
                && BoluoAndroidEngineNative.AllowUnmuteNow();
            if (!nativeForeground)
            {
                // Arm 的首刷会写成暂停并 setActive。人已经切走时把这次占位拆掉。
                if (!wasArmed)
                    ClearCard();
                Log.Info("[NowPlaying] ShowCardIfForeground skipped — not foreground owner");
                return;
            }

            PublishPlayingForced();
            Log.Info("[NowPlaying] ShowCardIfForeground");
        }

        static void EnsurePlatformSession()
        {
#if UNITY_IOS && !UNITY_EDITOR
            if (BoluoMusicPlayerIOS.Instance != null)
                BoluoMusicPlayerIOS.Instance.InitializeAfterLogin();
#elif UNITY_ANDROID && !UNITY_EDITOR
            var android = BoluoMusicPlayerAndroid.EnsureExists();
            android?.InitializeAfterLogin(AudioAppLifecycle.IsInBackground);
#endif
        }

        public static void CleanupAfterLeaveIsland()
        {
            var music = GameEntry.Music ?? MusicPlaylist.Instance;
            music?.Stop(publish: false);
            music?.DisarmAfterLeave();
            Commands.CancelPendingPlayPause();
            Commands.NoteSkipSettled();
            Commands.NotePausedForTimerEnd(false);
            BoluoAndroidEngineNative.NotePausedForTimerEnd(false);
            PlaybackGate.NoteStopped("leave-island");
            // 先 Disarm，堵住拆除窗口里 PublishFromPlaylist 再推非空 title。
            _armed = false;

#if UNITY_IOS && !UNITY_EDITOR
            BoluoMusicPlayerIOS.CleanupAfterLogout();
#elif UNITY_ANDROID && !UNITY_EDITOR
            // 须在 LoadScene(Login) 前：关闸 + 撤 native 排队 NowPlaying，避免误拉 FGS 超时杀进程。
            BoluoMusicPlayerAndroid.CleanupAfterLogout();
#endif
            ClearCard();
            // 原生侧的后台传输计划也作废：曲子已经停了，别让原生再切。
            BackgroundTransportSync.ClearPlan();
            Log.Info("[NowPlaying] CleanupAfterLeaveIsland");
        }

        /// <summary>
        /// 首播 / 切歌成功：请求 Gate=Playing 并刷卡。
        /// 必须已 <see cref="ArmSession"/>：禁止在 <c>CleanupAfterLeaveIsland</c> 后自动重 Arm。
        /// 不在此清静音原因；真正续播由 <see cref="AudioAppLifecycle.ResumePaired"/> 清闩。
        /// 若仍处用户暂停 / 到点闩，只推 meta，playing 位仍跟 Gate。
        /// </summary>
        public static void PublishPlayingForced()
        {
            if (!_armed)
            {
                Log.Info("[NowPlaying] PublishPlayingForced skipped — not armed (teardown guard)");
                return;
            }

            EnsurePlatformSession();
            if (!UserManuallyPaused
                && PlaybackGate.State != PlaybackGateState.UserPaused
                && PlaybackGate.State != PlaybackGateState.SystemSuspended)
            {
                PlaybackGate.NotePlaying("publish-forced");
            }

            PublishFromGate();
            Log.Info($"[NowPlaying] PublishPlayingForced gate={PlaybackGate.State}");
        }

        /// <summary>按当前 playlist meta + <see cref="PlaybackGate"/> 写卡（playing 只派生自 Gate）。</summary>
        public static void PublishFromGate() => PublishFromPlaylist(forcePlaying: false);

        public static void PublishFromPlaylist(bool forcePlaying = false)
        {
            var music = GameEntry.Music ?? MusicPlaylist.Instance;
            if (music == null)
            {
                // 无 playlist 时勿拆 MediaSession：清卡只走 CleanupAfterLeaveIsland / ClearCard。
                if (_armed)
                    Log.Warning("[NowPlaying] PublishFromPlaylist skipped — MusicPlaylist null");
                return;
            }

            var track = MusicCatalog.GetTrack(music.CurrentAlbumIndex, music.CurrentTrackIndex);
            string title = track?.MusicName ?? "";
            string album = MusicCatalog.GetAlbum(music.CurrentAlbumIndex)?.Name ?? "";
            // forcePlaying 仅用于 Gate 尚未 NotePlaying 的首帧兜底；有暂停/挂起闩时仍跟 Gate。
            if (forcePlaying
                && !UserManuallyPaused
                && PlaybackGate.State != PlaybackGateState.UserPaused
                && PlaybackGate.State != PlaybackGateState.SystemSuspended)
            {
                PlaybackGate.NotePlaying("publish-force-legacy");
            }

            bool playing = PlaybackGate.CardShouldShowPlaying;
            // 负一屏点了播放、Post 还没落地：卡片先显示播放。更新的暂停会清掉 HardReplayPending。
            if (!playing
                && music.HardReplayPending
                && !PausedForTimerEnd)
                playing = true;
            if (string.IsNullOrEmpty(title))
            {
                // 未 Arm：直接跳过，禁止 Publish("",…) → ClearCard。
                if (!_armed)
                    return;
                title = "波萝";
            }

            Publish(title, album, playing);

            // 后台传输计划：Unity 在后台会被宿主 pause，负一屏的暂停/切歌要靠原生执行，
            // 所以每次发布卡片都把当前列表同步给原生。
            BackgroundTransportSync.PublishPlan();
        }

        public static void Publish(string title, string album, bool isPlaying)
        {
            if (BackgroundPlayPolicyCallback.GetBackgroundPlayDisable())
            {
                ClearCard();
                return;
            }

            // 未进岛：只跳过，禁止 ClearCard。否则 Arm 前 / 竞态 Publish 会把 MediaSession 拆掉，
            // 首播成功后再也补不回来（CustomSRP Java playlist 空，引擎回调不补卡）。
            if (!_armed)
            {
                Log.Info($"[NowPlaying] Publish skipped — not armed title={title} playing={isPlaying}");
                return;
            }

            // 空 title 会触发原生 release MediaSession；播放中 / 岛内占位一律兜底「波萝」。
            if (string.IsNullOrEmpty(title))
                title = "波萝";

            string artwork = ArtworkPath;

            // IMP 105 镜像（宿主不写系统会话）
            NativeBridgeFacade.PublishNowPlaying(
                isPlaying, 0, 0, title, album ?? "", 0, 0);

#if UNITY_IOS && !UNITY_EDITOR
            if (BoluoMusicPlayerIOS.Instance != null)
            {
                BoluoMusicPlayerIOS.Instance.SetNowPlayingInfo(
                    title, album ?? "", album ?? "", artwork);
                BoluoMusicPlayerIOS.Instance.SyncExternalPlaybackState(isPlaying);
            }
#elif UNITY_ANDROID && !UNITY_EDITOR
            if (BoluoMusicPlayerAndroid.Instance != null)
            {
                BoluoMusicPlayerAndroid.Instance.UpdateNowPlayingInfo(
                    title, artwork, isPlaying);
            }
#else
            // Editor: IMP only
#endif
        }

        public static void ClearCard()
        {
            NativeBridgeFacade.PublishNowPlaying(false, 0, 0, "", "", 0, 0);
#if UNITY_IOS && !UNITY_EDITOR
            BoluoMusicPlayerIOS.Instance?.ClearNowPlaying();
#elif UNITY_ANDROID && !UNITY_EDITOR
            BoluoMusicPlayerAndroid.Instance?.ClearNowPlaying();
#endif
        }

        // ----- Intent handlers (from GlobalPlaybackIntent / MediaCommand) -----

        static void OnIntentPauseAll()
        {
            if (PlaybackGate.Silence != SilenceReason.TimerEnd)
                NoteUserPaused(true);
            AudioAppLifecycle.StopPaired("intent-pause", blockAutoResume: true);
            PublishFromGate();
        }

        static void OnIntentResumeAll()
        {
            if (PausedForTimerEnd)
                return;
            var music = GameEntry.Music ?? MusicPlaylist.Instance;
            if (PlaybackGate.Silence == SilenceReason.None
                && music != null
                && music.IsPlaying)
                return;

            if (!AudioAppLifecycle.ResumePaired("intent-resume"))
            {
                if (PlaybackGate.Silence != SilenceReason.TimerEnd)
                    NoteUserPaused(true);
                PublishFromGate();
                UiDialogService.ShowAudioPauseTip(MusicPauseUICallback.TipKind.ProgrammaticPause);
                return;
            }

            PublishFromGate();
        }

        /// <summary>
        /// 其它播放器抢焦点：音乐和音效一起停。不写成用户暂停，回前台能自动续。
        /// </summary>
        static void OnSystemAudioInterrupted()
        {
            PlaybackGate.NoteSystemSuspended("system-audio-interrupted");
            IslandAudioSession.HoldYield("system-audio-interrupted");
            AudioAppLifecycle.StopPaired("system-audio-interrupted", blockAutoResume: false);
            PublishFromGate();
            Log.Info("[NowPlaying] system audio interrupted — gate=SystemSuspended");
        }

        /// <summary>
        /// 原生短暂打断结束：原生已 Resume+unmute，Unity 只对账闩/Gate/内容声。
        /// 禁止再走全量 TryPairedAutoRestore（会与 unmute 爬坡叠峰 → 卡顿）。
        /// 定时结束弹窗未关时禁止拉回（与回前台 keep mute 同口径）。
        /// </summary>
        static void OnSystemAudioRestored()
        {
            if (PausedForTimerEnd || PlaybackGate.State == PlaybackGateState.UserPaused)
            {
                Log.Info("[NowPlaying] system audio restored skipped — user/timer mute");
                return;
            }

            if (!AudioAppLifecycle.SyncAfterNativeFocusResume("system-audio-restored"))
            {
                UiDialogService.ShowAudioPauseTip(MusicPauseUICallback.TipKind.CompetingLong);
                PublishFromGate();
                Log.Info("[NowPlaying] system audio restored failed — tip armed");
                return;
            }

            Log.Info("[NowPlaying] system audio restored — sync ok");
        }

        /// <summary>
        /// 原生已改曲（后台负一屏 skip）：对账索引 + Gate。
        /// 禁止挂在 Unity 自有 Next/Prev 后面——那时原生仍是旧曲，Resync 会盖回。
        /// </summary>
        static void OnMediaTrackChanged()
        {
            Commands.CancelPendingPlayPause();
            BackgroundTransportSync.ResyncFromNative();
            if (!PausedForTimerEnd
                && !UserManuallyPaused
                && PlaybackGate.State != PlaybackGateState.UserPaused
                && PlaybackGate.State != PlaybackGateState.SystemSuspended
                && PlaybackGate.State != PlaybackGateState.Stopped)
            {
                PlaybackGate.NotePlaying("native-track-changed");
            }

            PublishFromGate();
        }

        /// <summary>IMP 104 / 合并后的远程控播。播放和暂停都进同一会话。</summary>
        public static void HandleMediaCommand(int cmd)
        {
            if (!_armed)
                return;

            var music = GameEntry.Music ?? MusicPlaylist.Instance;
            if (music == null)
                return;

            switch (cmd)
            {
                case MsgType.MediaPause:
                    SchedulePlayPause(wantMute: true);
                    break;
                case MsgType.MediaPlay:
                    SchedulePlayPause(wantMute: false);
                    break;
                case MsgType.MediaNext:
                    AcceptSkip(+1, () => music.SkipAdjacentInAlbum(+1));
                    break;
                case MsgType.MediaPrev:
                    AcceptSkip(-1, () => music.SkipAdjacentInAlbum(-1));
                    break;
            }
        }

        /// <summary>
        /// Unity 前台切歌（负一屏转发 / IMP MediaNext·Prev）。
        /// 防呆在 <see cref="BoluoNowPlayingRemoteSession"/>。定时到点不因切歌清闩。
        /// </summary>
        static void AcceptSkip(int direction, System.Action skip)
        {
            long nowMs = NowMs();
            bool accepted = direction < 0
                ? Commands.PressPrev(nowMs)
                : Commands.PressNext(nowMs);
            if (!accepted)
            {
                Log.Info(
                    $"[NowPlaying] skip ignored dir={direction} inFlight={Commands.SkipInFlight}");
                return;
            }

            if (!PausedForTimerEnd)
            {
                if (PlaybackGate.Silence == SilenceReason.UserPause)
                    PlaybackGate.SetSilence(SilenceReason.None, "media-skip");
                PlaybackGate.NotePlaying("media-skip");
                Commands.AdoptPlayback(true, false);
#if UNITY_ANDROID && !UNITY_EDITOR
                BoluoAndroidEngineNative.MarkUserPausedFromUnity(false);
#endif
            }

            skip?.Invoke();
            if (BoluoNowPlayingRemoteCommandPolicy.ShouldResyncAfterUnityOwnedSkip())
                GlobalPlaybackIntentCallback.NotifyMediaTrackChanged();
            else
                PublishFromGate();
        }

        /// <summary>引擎 Playing / 超时：放开下一笔远程 skip，并同步原生 inFlight。</summary>
        public static void NoteSkipSettled()
        {
            if (!Commands.SkipInFlight)
                return;
            Commands.NoteSkipSettled();
#if UNITY_ANDROID && !UNITY_EDITOR
            BoluoAndroidEngineNative.NoteSkipSettled();
#elif UNITY_IOS && !UNITY_EDITOR
            BoluoMusicPlayerIOS.NoteSkipSettled();
#endif
        }

        static long NowMs() => (long)(Time.realtimeSinceStartupAsDouble * 1000.0);

        static void SchedulePlayPause(bool wantMute)
        {
            var music = GameEntry.Music ?? MusicPlaylist.Instance;
            Commands.SeedAndPress(
                music != null && music.IsPlaying,
                UserManuallyPaused,
                wantMute,
                NowMs());
        }

        /// <summary>由 BoluoNativeBridge 或 MusicPlaylist.Tick 泵送。</summary>
        public static void Tick()
        {
            bool skipWasInFlight = Commands.SkipInFlight;
            int pauses = Commands.EnginePauseCount;
            int resumes = Commands.EngineResumeCount;
            Commands.Tick(NowMs());
            if (skipWasInFlight && !Commands.SkipInFlight)
                NotifyNativeSkipSettled();

            if (Commands.EnginePauseCount != pauses)
                GlobalPlaybackIntentCallback.RequestPauseAll();
            else if (Commands.EngineResumeCount != resumes)
                GlobalPlaybackIntentCallback.RequestResumeAll();
        }

        static void NotifyNativeSkipSettled()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            BoluoAndroidEngineNative.NoteSkipSettled();
#elif UNITY_IOS && !UNITY_EDITOR
            BoluoMusicPlayerIOS.NoteSkipSettled();
#endif
        }

        /// <summary>
        /// 插件侧 OnNowPlayingPlay：ui 只刷卡；remote 进会话合并。
        /// Java 已经合并过的前台意图走 <see cref="OnIntentResumeAll"/>，不要再进这里。
        /// </summary>
        public static void OnRemotePlay(string reason)
        {
            if (!BoluoNowPlayingRemoteCommandPolicy.ShouldDriveEngineResumeFromNowPlayingPlay(reason))
            {
                PublishFromGate();
                return;
            }
            SchedulePlayPause(wantMute: false);
        }

        public static void OnRemotePause()
        {
            SchedulePlayPause(wantMute: true);
        }
    }
}
