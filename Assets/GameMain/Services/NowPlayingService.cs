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
        static bool _userManuallyPaused;
        static float _pendingPlayPauseAt = -1f;
        static bool _pendingWantMute;
        static bool _skipCancelPending;

        public static bool UserManuallyPaused => _userManuallyPaused;
        public static bool IsArmed => _armed;

        public static void NoteUserPaused(bool paused) => _userManuallyPaused = paused;

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
            NowPlayingSkipBridge.SetHandlers(OnSkipNext, OnSkipPrev);

            MusicPauseUICallback.SetCallbacks(
                () => { /* IslandMainForm polls Music.IsPlaying */ },
                () => { });

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
            _pendingPlayPauseAt = -1f;
        }

        static void OnSkipNext()
        {
            var music = GameEntry.Music ?? MusicPlaylist.Instance;
            AcceptSkip(() => music?.Next());
        }

        static void OnSkipPrev()
        {
            var music = GameEntry.Music ?? MusicPlaylist.Instance;
            AcceptSkip(() => music?.Prev());
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
            _userManuallyPaused = false;

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
            _userManuallyPaused = false;
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
        /// 首播 / 切歌成功同一帧强制刷「播放中」卡片。
        /// 必须已 <see cref="ArmSession"/>：禁止在 <c>CleanupAfterLeaveIsland</c> 后自动重 Arm，
        /// 否则 LoadScene(Login) 主线程拥堵窗口会 <c>setPlaybackActive(true)</c> + 拉 FGS →
        /// ForegroundServiceDidNotStartInTimeException（退登杀进程）。
        /// </summary>
        public static void PublishPlayingForced()
        {
            if (!_armed)
            {
                Log.Info("[NowPlaying] PublishPlayingForced skipped — not armed (teardown guard)");
                return;
            }

            // 已 Arm：补 Init / 开闸（冷启动 Cleanup 可能关过 sPlaybackActive）。
            EnsurePlatformSession();
            _userManuallyPaused = false;
            PublishFromPlaylist(forcePlaying: true);
            Log.Info("[NowPlaying] PublishPlayingForced");
        }

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
            bool playing = forcePlaying || (music.IsPlaying && !_userManuallyPaused);
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
            _userManuallyPaused = true;
            var music = GameEntry.Music ?? MusicPlaylist.Instance;
            music?.Pause();
            PublishFromPlaylist();
        }

        static void OnIntentResumeAll()
        {
            _userManuallyPaused = false;
            var music = GameEntry.Music ?? MusicPlaylist.Instance;
            music?.Resume();
            PublishFromPlaylist();
        }

        static void OnMediaTrackChanged()
        {
            _userManuallyPaused = false;
            _skipCancelPending = true;
            _pendingPlayPauseAt = -1f;
            // 原生在后台切过歌时也会走这里（Unity 恢复后补投递的消息）：
            // 先对账把当前曲同步过来，再发卡片，避免把旧曲名写回负一屏。
            BackgroundTransportSync.ResyncFromNative();
            PublishFromPlaylist();
        }

        /// <summary>IMP 104 / 合并后的远程控播。</summary>
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
                    // 定时结束弹窗「取消/重复」：立刻 ResumeAfterTimerEnd（音乐+音效闸+EnsureAudible），
                    // 跳过 coalesce/skip，与原生 resumeFromUserAction 双保险。
                    _userManuallyPaused = false;
                    _pendingPlayPauseAt = -1f;
                    _skipCancelPending = false;
                    music.ResumeAfterTimerEnd("media-play");
                    PublishFromPlaylist();
                    break;
                case MsgType.MediaNext:
                    AcceptSkip(() => music.Next());
                    break;
                case MsgType.MediaPrev:
                    AcceptSkip(() => music.Prev());
                    break;
            }
        }

        static void AcceptSkip(System.Action skip)
        {
            if (BoluoNowPlayingRemoteCommandPolicy.ShouldCancelPendingPlayPauseOnSkip())
            {
                _skipCancelPending = true;
                _pendingPlayPauseAt = -1f;
            }
            skip?.Invoke();
            GlobalPlaybackIntentCallback.NotifyMediaTrackChanged();
        }

        static void SchedulePlayPause(bool wantMute)
        {
            _pendingWantMute = wantMute;
            _skipCancelPending = false;
            if (BoluoNowPlayingRemoteCommandPolicy.ShouldCoalescePlayPause())
            {
                _pendingPlayPauseAt = Time.realtimeSinceStartup
                    + BoluoNowPlayingRemoteCommandPolicy.PlayPauseCoalesceMs / 1000f;
            }
            else
            {
                FlushPlayPause(wantMute);
            }
        }

        /// <summary>由 BoluoNativeBridge 或 MusicPlaylist.Tick 泵送。</summary>
        public static void Tick()
        {
            if (_pendingPlayPauseAt < 0f)
                return;
            if (Time.realtimeSinceStartup < _pendingPlayPauseAt)
                return;
            if (_skipCancelPending)
            {
                _pendingPlayPauseAt = -1f;
                _skipCancelPending = false;
                return;
            }
            bool want = _pendingWantMute;
            _pendingPlayPauseAt = -1f;
            FlushPlayPause(want);
        }

        static void FlushPlayPause(bool wantMute)
        {
            var music = GameEntry.Music ?? MusicPlaylist.Instance;
            bool isPlaying = music != null && music.IsPlaying;
            bool already = BoluoNowPlayingRemoteCommandPolicy.ShouldSkipFlushBecauseAlreadyConsistent(
                wantMute, !isPlaying, _userManuallyPaused);
            if (already)
            {
                PublishFromPlaylist();
                return;
            }

            if (wantMute)
                GlobalPlaybackIntentCallback.RequestPauseAll();
            else
                GlobalPlaybackIntentCallback.RequestResumeAll();
        }

        /// <summary>
        /// 插件侧 OnNowPlayingPlay：ui 只刷卡；remote 走 Resume。
        /// </summary>
        public static void OnRemotePlay(string reason)
        {
            if (string.Equals(reason, "ui", System.StringComparison.Ordinal))
            {
                _userManuallyPaused = false;
                PublishFromPlaylist();
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
