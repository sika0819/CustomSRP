#if UNITY_ANDROID
using UnityEngine;

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

        static bool sIsInitialized;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void OnGameStart()
        {
            if (Instance != null)
                return;
            var go = new GameObject(nameof(BoluoMusicPlayerAndroid));
            Instance = go.AddComponent<BoluoMusicPlayerAndroid>();
        }

        void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            DontDestroyOnLoad(gameObject);
            // jar StreamingAssets 不能 File 打开；传 AssetManager 相对路径。
            ArtworkDefault = "WanosAudio/Artwork/boluo.png";
        }

        public void InitializeAfterLogin()
        {
            if (sIsInitialized)
                return;
            sIsInitialized = true;
            ArtworkDefault = "WanosAudio/Artwork/boluo.png";

            bool bgDisable = BackgroundPlayPolicyCallback.GetBackgroundPlayDisable();
            BoluoAndroidEngineNative.NotifyAppLifecycle(false, bgDisable);

            if (!bgDisable)
                UpdateNowPlayingInfo("波萝", ArtworkDefault, false);
            else
                ClearNowPlaying();

            Debug.Log("[BoluoMusicPlayerAndroid] InitializeAfterLogin");
        }

        public static void CleanupAfterLogout()
        {
            if (Instance == null)
                return;
            Instance.CleanupInternal();
        }

        void CleanupInternal()
        {
            ClearNowPlaying();
            BoluoAndroidEngineNative.MarkUserPausedFromUnity(false);
            sIsInitialized = false;
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
            bool playing = value == "1";
            if (playing)
            {
                OnNowPlayingPlay("");
                return;
            }

            // 后台负一屏 / 锁屏暂停由原生直接 Pause_Music，并排队这条消息。
            // Unity 解 pause 后若再走 OnNowPlayingPause → RequestPauseAll，
            // 会把 AudioAppLifecycle 刚恢复的播放又掐掉，且 _needsRestore 已清、
            // 点场景也无法再恢复。暂停态回前台由 ResyncFromNative + RestoreForegroundAudio
            //（覆盖暂停）处理，这里对 pause 通知 no-op。
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
