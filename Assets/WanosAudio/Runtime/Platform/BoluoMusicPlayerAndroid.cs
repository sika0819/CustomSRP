#if UNITY_ANDROID
using UnityEngine;
using UnityEngine.Android;

namespace WanosAudio
{
    /// <summary>
    /// Android Now Playing / MediaSession 门面（CustomSRP 精简版）。
    /// MediaSession 在 WIAEngineJNI；本类收 UnitySendMessage 并转 GlobalPlaybackIntent。
    /// </summary>
    public sealed class BoluoMusicPlayerAndroid : MonoBehaviour
    {
        public static BoluoMusicPlayerAndroid Instance { get; private set; }
        public static string ArtworkDefault = "";

        const string PostNotificationsPermission = "android.permission.POST_NOTIFICATIONS";

        static bool sIsInitialized;
        static bool sPostNotificationsRequested;

        /// <summary>NowPlaying 会话是否已开闸（可写 MediaSession）。</summary>
        public static bool IsSessionReady => Instance != null && sIsInitialized;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void OnGameStart()
        {
            EnsureExists();
        }

        /// <summary>
        /// Disable Domain Reload / UaaL 软重启后 AfterSceneLoad 不再触发，
        /// Arm / Publish 前必须能重建门面，否则 <c>!sIsInitialized</c> 永久吞掉负一屏推送。
        /// </summary>
        public static BoluoMusicPlayerAndroid EnsureExists()
        {
            if (Instance != null)
                return Instance;
            var go = new GameObject(nameof(BoluoMusicPlayerAndroid));
            Instance = go.AddComponent<BoluoMusicPlayerAndroid>();
            return Instance;
        }

        void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            KeepWithLaunch();
            // jar StreamingAssets 不能 File 打开；传 AssetManager 相对路径。
            ArtworkDefault = "WanosAudio/Artwork/boluo.png";
        }

        /// <summary>
        /// 进岛后才补建时，激活场景是 Island。挪回 Launch，退登卸岛不会把它拆掉。
        /// </summary>
        void KeepWithLaunch()
        {
            var launch = UnityEngine.SceneManagement.SceneManager.GetSceneByName("Launch");
            if (!launch.IsValid() || !launch.isLoaded || gameObject.scene == launch)
                return;
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(gameObject, launch);
        }

        public void InitializeAfterLogin(bool inBackground = false)
        {
            ArtworkDefault = "WanosAudio/Artwork/boluo.png";

            // 对称 teardownForLogout：二次进岛 / 冷启动 Awake 拆闸后重新打开 NowPlaying / FGS 闸。
            // 已 initialized 也要重开闸——GameEntry.Awake 的 Cleanup 会关闸但未必清 sIsInitialized 竞态。
            BoluoAndroidEngineNative.SetPlaybackActive(true);
            // teardown 钉的 shouldStop/userPaused 必须在此清掉。只开闸不清闩时：
            // 负一屏已 PLAYING，上滑 Recents 却因 isUserStoppedPlayback=true 走 unity_pause_preview
            // → 岛内静音、卡仍 PLAYING（日志：silenceBefore… userPaused=true shouldStop=true）。
            BoluoAndroidEngineNative.ClearLogoutStopLatch();

            bool bgDisable = BackgroundPlayPolicyCallback.GetBackgroundPlayDisable();
            // 热路径（PublishPlayingForced → EnsurePlatformSession）：禁止再 NotifyAppLifecycle。
            // AudioAppLifecycle 已同步前后台；重复 notify 会造成双重 fg enter + FGS 刷屏 + 音效卡顿。
            // 仅首次进岛 / Cleanup 后重初始化才报生命周期（加载中途已进后台也不能谎报前台）。
            if (!sIsInitialized)
            {
                BoluoAndroidEngineNative.NotifyAppLifecycle(inBackground, bgDisable);
                sIsInitialized = true;

                // 不在这里推 paused「波萝」：paused 不拉 FGS，创维等 OEM 负一屏不绑 MediaStyle。
                // 真正出卡走 PublishPlayingForced（isPlaying=true）。关后台播放才清卡。
                if (bgDisable)
                    ClearNowPlaying();

                Debug.Log("[BoluoMusicPlayerAndroid] InitializeAfterLogin"
                    + " inBackground=" + inBackground + " bgDisable=" + bgDisable);
            }
            else
            {
                Debug.Log("[BoluoAudioFg] InitializeAfterLogin already initialized"
                    + " inBackground=" + inBackground + " bgDisable=" + bgDisable);
            }

            EnsurePostNotificationsPermission();
        }

        /// <summary>
        /// Android 13+ 必须要通知权限，否则 FGS MediaStyle 被压掉，vivo/华为负一屏空白。
        /// </summary>
        static void EnsurePostNotificationsPermission()
        {
#if !UNITY_EDITOR
            if (AndroidSdkInt() < 33)
                return;

            if (Permission.HasUserAuthorizedPermission(PostNotificationsPermission))
            {
                // 权限刚在设置里打开、或冷启动前已被拒又恢复：重刷已在跑的媒体通知。
                BoluoAndroidEngineNative.RefreshMediaNotification();
                return;
            }

            if (sPostNotificationsRequested)
                return;
            sPostNotificationsRequested = true;

            var callbacks = new PermissionCallbacks();
            callbacks.PermissionGranted += name =>
            {
                if (name != PostNotificationsPermission)
                    return;
                Debug.Log("[BoluoMusicPlayerAndroid] POST_NOTIFICATIONS granted — refresh media notif");
                BoluoAndroidEngineNative.RefreshMediaNotification();
            };
            callbacks.PermissionDenied += name =>
            {
                if (name != PostNotificationsPermission)
                    return;
                Debug.LogWarning(
                    "[BoluoMusicPlayerAndroid] POST_NOTIFICATIONS denied — MediaSession 仍在，但 OEM 负一屏可能空白");
            };
            Permission.RequestUserPermission(PostNotificationsPermission, callbacks);
#endif
        }

        static int AndroidSdkInt()
        {
            try
            {
                using (var version = new AndroidJavaClass("android.os.Build$VERSION"))
                    return version.GetStatic<int>("SDK_INT");
            }
            catch (System.Exception)
            {
                return 0;
            }
        }

        public static void CleanupAfterLogout()
        {
            if (Instance == null)
            {
                Debug.Log("[BoluoAudioFg] CleanupAfterLogout no instance, teardown native");
                BoluoAndroidEngineNative.TeardownForLogout();
                return;
            }
            Instance.CleanupInternal();
        }

        void CleanupInternal()
        {
            // 先关闸并撤 native 排队推送，再清卡；禁止 MarkUserPaused(false) 把拆除态当成可续播。
            BoluoAndroidEngineNative.TeardownForLogout();
            sIsInitialized = false;
            sPostNotificationsRequested = false;
            Debug.Log("[BoluoMusicPlayerAndroid] CleanupAfterLogout");
        }

        public void UpdateNowPlayingInfo(string title, string artworkPath, bool isPlaying)
        {
            if (!sIsInitialized)
            {
                Debug.Log("[BoluoAudioFg] UpdateNowPlaying dropped not initialized"
                    + " playing=" + isPlaying);
                return;
            }

            if (BackgroundPlayPolicyCallback.GetBackgroundPlayDisable())
            {
                Debug.Log("[BoluoAudioFg] UpdateNowPlaying bg play disabled, clear card");
                ClearNowPlaying();
                return;
            }

            // 空 title+cover 会 release MediaSession。播放中禁止走清卡，兜底「波萝」。
            if (string.IsNullOrEmpty(title) && string.IsNullOrEmpty(artworkPath))
            {
                if (isPlaying)
                {
                    title = "波萝";
                    artworkPath = ArtworkDefault ?? "";
                }
                else
                {
                    Debug.Log("[BoluoAudioFg] UpdateNowPlaying empty meta, clear card");
                    ClearNowPlaying();
                    return;
                }
            }

            if (string.IsNullOrEmpty(title))
                title = "波萝";
            if (string.IsNullOrEmpty(artworkPath))
                artworkPath = ArtworkDefault ?? "";
            Debug.Log("[BoluoAudioFg] UpdateNowPlaying title=" + title
                + " playing=" + isPlaying
                + " art=" + (string.IsNullOrEmpty(artworkPath) ? "empty" : "set"));
            BoluoAndroidEngineNative.UpdateNowPlayingInfo(title, artworkPath, isPlaying);
            if (isPlaying)
                BoluoAndroidEngineNative.NoteForegroundCardPublished();
            else
                BoluoAndroidEngineNative.NoteForegroundCardCleared();
        }

        public void ClearNowPlaying()
        {
            Debug.Log("[BoluoAudioFg] ClearNowPlaying title=empty buttons=off");
            BoluoAndroidEngineNative.UpdateNowPlayingInfo("", "", false);
        }

        /// <summary>
        /// MediaSession / 通知栏 Play。
        /// reason=ui：原生 focus resume 已 Resume+unmute，只收 tip（对齐 iOS / Policy）。
        /// </summary>
        public void OnNowPlayingPlay(string reason)
        {
            if (!sIsInitialized)
            {
                Debug.Log("[BoluoAudioFg] OnNowPlayingPlay dropped not initialized reason=" + reason);
                return;
            }
            if (!BoluoNowPlayingRemoteCommandPolicy.ShouldDriveEngineResumeFromNowPlayingPlay(reason))
            {
                Debug.Log("[BoluoAudioFg] OnNowPlayingPlay ui-only refresh card reason=" + reason);
                MusicPauseUICallback.Hide();
                return;
            }
            // Java 已经合并过这一次意图。只走 Unity 续播，不再 ResumeFromUserAction。
            Debug.Log("[BoluoAudioFg] OnNowPlayingPlay drive engine RequestResumeAll reason=" + reason);
            GlobalPlaybackIntentCallback.RequestResumeAll();
        }

        public void OnNowPlayingPause(string empty)
        {
            if (!sIsInitialized)
            {
                Debug.Log("[BoluoAudioFg] OnNowPlayingPause dropped not initialized");
                return;
            }
            Debug.Log("[BoluoAudioFg] OnNowPlayingPause user pause");
            MusicPauseUICallback.Show(MusicPauseUICallback.TipKind.ProgrammaticPause);
            GlobalPlaybackIntentCallback.RequestPauseAll();
        }

        public void ShowPauseUIFromMediaControl()
        {
            Debug.Log("[BoluoAudioFg] ShowPauseUIFromMediaControl");
            MusicPauseUICallback.Show(MusicPauseUICallback.TipKind.ProgrammaticPause);
        }

        public void HidePauseUIFromMediaControl()
        {
            Debug.Log("[BoluoAudioFg] HidePauseUIFromMediaControl");
            MusicPauseUICallback.Hide();
        }

        public void OnSystemAudioMuted(string empty)
        {
            if (!BoluoAndroidEngineNative.IsFocusYieldLatched())
            {
                UnityEngine.Debug.Log("[BoluoAudioFg] drop stale OnSystemAudioMuted (not yielding)");
                return;
            }
            Debug.Log("[BoluoAudioFg] OnSystemAudioMuted interrupt not userPaused");
            WanosEngine.MarkInterrupted("android-system-mute");
            GlobalPlaybackIntentCallback.NotifySystemAudioInterrupted();
            MusicPauseUICallback.Show(MusicPauseUICallback.TipKind.TransientInterrupt);
        }

        public void OnCompetingAudioMuted(string empty)
        {
            // 后台积压的 CompetingMuted 若在 Restored 之后到达，会把刚续上的音乐再 Pause。
            // 原生已不在 focus yield：丢弃陈旧 Interrupted。
            if (!BoluoAndroidEngineNative.IsFocusYieldLatched())
            {
                UnityEngine.Debug.Log("[BoluoAudioFg] drop stale OnCompetingAudioMuted (not yielding)");
                return;
            }
            Debug.Log("[BoluoAudioFg] OnCompetingAudioMuted interrupt not userPaused");
            WanosEngine.MarkInterrupted("android-competing-mute");
            GlobalPlaybackIntentCallback.NotifySystemAudioInterrupted();
            MusicPauseUICallback.Show(MusicPauseUICallback.TipKind.CompetingLong);
        }

        /// <summary>原生 focus resume / 蓝牙路由恢复后：清 yield 并恢复音效。</summary>
        public void OnSystemAudioRestored(string empty)
        {
            Debug.Log("[BoluoAudioFg] OnSystemAudioRestored");
            GlobalPlaybackIntentCallback.NotifySystemAudioRestored();
        }

        /// <summary>系统更新弹窗：没有外来媒体，不停音乐音效，焦点也不抢回。</summary>
        public void OnSystemDialogCover(string empty)
        {
            Debug.Log("[BoluoAudioFg] OnSystemDialogCover keep playing");
            GlobalPlaybackIntentCallback.NotifySystemDialogCover();
        }

        public void OnAudioFocusDuck(string factorStr)
        {
            Debug.Log("[BoluoAudioFg] OnAudioFocusDuck factor=" + factorStr);
        }

        public void OnAudioFocusUnduck(string empty)
        {
            Debug.Log("[BoluoAudioFg] OnAudioFocusUnduck");
        }

        public void OnAudioFocusRequestFailed(string empty)
        {
            Debug.Log("[BoluoAudioFg] OnAudioFocusRequestFailed exclusive activate failed keep mute");
            WanosEngine.MarkInterrupted("android-focus-request-failed");
            GlobalPlaybackIntentCallback.NotifySystemAudioInterrupted();
        }

        public void SetUserAudioSettings(string payload)
        {
            Debug.Log("[BoluoAudioFg] SetUserAudioSettings payload=" + (payload ?? ""));
        }

        public void OnNativeTrackChanged(string payload)
        {
            Debug.Log("[BoluoAudioFg] OnNativeTrackChanged payload=" + payload);
            GlobalPlaybackIntentCallback.NotifyMediaTrackChanged();
        }

        public void OnNativePlaybackStateChanged(string value)
        {
            if (!sIsInitialized)
            {
                Debug.Log("[BoluoAudioFg] OnNativePlaybackStateChanged dropped not initialized value=" + value);
                return;
            }

            // 原生已落地控播（resumeFromMediaControl / 负一屏切歌起播 / pauseForTimerEnd 等）。
            // 禁止再进 OnNowPlayingPlay / OnNowPlayingPause：
            // play → ResumeFromUserAction 会重入 unmute 爬坡（bump generation）→ 音量一顿一顿甚至卡死；
            // pause → RequestPauseAll 会把刚恢复的播放再掐掉。
            // 播放态走 Resync（读 native paused + event），不重驱引擎。
            if (value == "1")
            {
                Debug.Log("[BoluoAudioFg] OnNativePlaybackStateChanged playing resync only");
                MusicPauseUICallback.Hide();
                GlobalPlaybackIntentCallback.NotifyMediaTrackChanged();
            }
            else
            {
                Debug.Log("[BoluoAudioFg] OnNativePlaybackStateChanged value=" + value + " resync only");
            }
        }

        public void OnMediaSkipNext(string empty)
        {
            if (!sIsInitialized)
            {
                Debug.Log("[BoluoAudioFg] OnMediaSkipNext dropped not initialized");
                return;
            }
            Debug.Log("[BoluoAudioFg] OnMediaSkipNext");
            NowPlayingSkipBridge.RequestNext();
        }

        public void OnMediaSkipPrev(string empty)
        {
            if (!sIsInitialized)
            {
                Debug.Log("[BoluoAudioFg] OnMediaSkipPrev dropped not initialized");
                return;
            }
            Debug.Log("[BoluoAudioFg] OnMediaSkipPrev");
            NowPlayingSkipBridge.RequestPrev();
        }
    }
}
#endif
