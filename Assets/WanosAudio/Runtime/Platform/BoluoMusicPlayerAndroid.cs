#if UNITY_ANDROID
using System.IO;
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
            ArtworkDefault = Path.Combine(
                Application.streamingAssetsPath, "WanosAudio", "Artwork", "boluo.png");
        }

        public void InitializeAfterLogin()
        {
            if (sIsInitialized)
                return;
            sIsInitialized = true;
            ArtworkDefault = Path.Combine(
                Application.streamingAssetsPath, "WanosAudio", "Artwork", "boluo.png");

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

            if (string.IsNullOrEmpty(title) && string.IsNullOrEmpty(artworkPath))
            {
                ClearNowPlaying();
                return;
            }

            if (string.IsNullOrEmpty(artworkPath))
                artworkPath = ArtworkDefault ?? "";
            BoluoAndroidEngineNative.UpdateNowPlayingInfo(title ?? "", artworkPath, isPlaying);
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
            MusicPauseUICallback.Show(MusicPauseUICallback.TipKind.TransientInterrupt);
        }

        public void OnCompetingAudioMuted(string empty)
        {
            MusicPauseUICallback.Show(MusicPauseUICallback.TipKind.CompetingLong);
        }

        public void OnAudioFocusDuck(string factorStr) { }
        public void OnAudioFocusUnduck(string empty) { }
        public void OnAudioFocusRequestFailed(string empty) { }
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
                OnNowPlayingPlay("");
            else
                OnNowPlayingPause("");
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
