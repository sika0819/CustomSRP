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

            bool bgDisable = BackgroundPlayPolicyCallback.GetBackgroundPlayDisable();
            // 加载中途已进后台时不能谎报前台，否则随后抢焦会在抖音 / QQ 音乐之上出声。
            BoluoAndroidEngineNative.NotifyAppLifecycle(inBackground, bgDisable);

            EnsurePostNotificationsPermission();

            if (sIsInitialized)
                return;
            sIsInitialized = true;

            // 不在这里推 paused「波萝」：paused 不拉 FGS，创维等 OEM 负一屏不绑 MediaStyle。
            // 真正出卡走 PublishPlayingForced（isPlaying=true）。关后台播放才清卡。
            if (bgDisable)
                ClearNowPlaying();

            Debug.Log("[BoluoMusicPlayerAndroid] InitializeAfterLogin");
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
                return;

            if (BackgroundPlayPolicyCallback.GetBackgroundPlayDisable())
            {
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
                    ClearNowPlaying();
                    return;
                }
            }

            if (string.IsNullOrEmpty(title))
                title = "波萝";
            if (string.IsNullOrEmpty(artworkPath))
                artworkPath = ArtworkDefault ?? "";
            BoluoAndroidEngineNative.UpdateNowPlayingInfo(title, artworkPath, isPlaying);
            if (isPlaying)
                BoluoAndroidEngineNative.NoteForegroundCardPublished();
            else
                BoluoAndroidEngineNative.NoteForegroundCardCleared();
        }

        public void ClearNowPlaying()
        {
            BoluoAndroidEngineNative.UpdateNowPlayingInfo("", "", false);
        }

        /// <summary>MediaSession / 通知栏 Play。</summary>
        public void OnNowPlayingPlay(string reason)
        {
            if (!sIsInitialized)
                return;
            MusicPauseUICallback.Hide();
            BoluoAndroidEngineNative.ResumeFromUserAction();
            if (string.Equals(reason, "ui", System.StringComparison.Ordinal))
                return;
            GlobalPlaybackIntentCallback.RequestResumeAll();
        }

        public void OnNowPlayingPause(string empty)
        {
            if (!sIsInitialized)
                return;
            MusicPauseUICallback.Show(MusicPauseUICallback.TipKind.ProgrammaticPause);
            BoluoAndroidEngineNative.MarkUserPausedFromUnity(true);
            GlobalPlaybackIntentCallback.RequestPauseAll();
        }

        public void ShowPauseUIFromMediaControl()
        {
            MusicPauseUICallback.Show(MusicPauseUICallback.TipKind.ProgrammaticPause);
        }

        public void HidePauseUIFromMediaControl()
        {
            MusicPauseUICallback.Hide();
        }

        public void OnSystemAudioMuted(string empty)
        {
            WanosEngine.MarkInterrupted("android-system-mute");
            MusicPauseUICallback.Show(MusicPauseUICallback.TipKind.TransientInterrupt);
        }

        public void OnCompetingAudioMuted(string empty)
        {
            WanosEngine.MarkInterrupted("android-competing-mute");
            MusicPauseUICallback.Show(MusicPauseUICallback.TipKind.CompetingLong);
        }

        public void OnAudioFocusDuck(string factorStr) { }
        public void OnAudioFocusUnduck(string empty) { }
        public void OnAudioFocusRequestFailed(string empty)
        {
            WanosEngine.MarkInterrupted("android-focus-request-failed");
        }
        public void SetUserAudioSettings(string payload) { }

        public void OnNativeTrackChanged(string payload)
        {
            GlobalPlaybackIntentCallback.NotifyMediaTrackChanged();
        }

        public void OnNativePlaybackStateChanged(string value)
        {
            if (!sIsInitialized)
                return;

            // 原生已落地控播（resumeFromMediaControl / pauseForTimerEnd 等）后的状态回传。
            // 禁止再进 OnNowPlayingPlay / OnNowPlayingPause：
            // play → ResumeFromUserAction 会重入 unmute 爬坡（bump generation）→ 音量一顿一顿甚至卡死；
            // pause → RequestPauseAll 会把刚恢复的播放再掐掉。
            // Unity 播放态由 MediaCommand / MusicPlaylist 对账；此处只收起暂停 tip。
            if (value == "1")
                MusicPauseUICallback.Hide();
        }

        public void OnMediaSkipNext(string empty)
        {
            if (!sIsInitialized)
                return;
            NowPlayingSkipBridge.RequestNext();
        }

        public void OnMediaSkipPrev(string empty)
        {
            if (!sIsInitialized)
                return;
            NowPlayingSkipBridge.RequestPrev();
        }
    }
}
#endif
