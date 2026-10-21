#if UNITY_IOS
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;

namespace WanosAudio
{
    /// <summary>
    /// iOS Now Playing / RemoteCommand 门面（CustomSRP 精简版）。
    /// 系统 API 在 BoluoMusicPlayerIOS.mm；本类收 UnitySendMessage 并转 GlobalPlaybackIntent。
    /// </summary>
    public sealed class BoluoMusicPlayerIOS : MonoBehaviour
    {
        public static BoluoMusicPlayerIOS Instance { get; private set; }
        public static string ArtworkDefault = "";

        static bool sIsInitialized;

#if !UNITY_EDITOR
        [DllImport("__Internal")]
        static extern void BoluoMusicPlayerIOS_Init(
            int category, uint options, bool autoActivate, bool notifyOthersOnDeactivate,
            string unityGameObjectName);

        [DllImport("__Internal")]
        static extern void BoluoMusicPlayerIOS_SetNowPlayingInfo(
            string title, string artist, string album, string artworkPath);

        [DllImport("__Internal")]
        static extern void BoluoMusicPlayerIOS_RemoveRemoteCommands();

        [DllImport("__Internal")]
        static extern void BoluoMusicPlayerIOS_SetInitialized(bool initialized);

        [DllImport("__Internal")]
        static extern void BoluoMusicPlayerIOS_MarkStoppedForLogout();

        [DllImport("__Internal")]
        static extern bool BoluoMusicPlayerIOS_IsUserPaused();

        [DllImport("__Internal")]
        static extern void BoluoMusicPlayerIOS_SyncExternalPlaybackState(bool isPlaying);

        [DllImport("__Internal")]
        static extern void BoluoMusicPlayerIOS_ApplyUserPauseFromExternal();

        [DllImport("__Internal")]
        static extern void BoluoMusicPlayerIOS_ApplyUserResumeFromExternal();
#endif

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void OnGameStart()
        {
            if (Instance != null)
                return;
            var go = new GameObject(nameof(BoluoMusicPlayerIOS));
            Instance = go.AddComponent<BoluoMusicPlayerIOS>();
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

        /// <summary>进岛 / 登录后 Arm 会话并允许推 NowPlaying。</summary>
        public void InitializeAfterLogin()
        {
            if (sIsInitialized)
                return;
            sIsInitialized = true;
            ArtworkDefault = Path.Combine(
                Application.streamingAssetsPath, "WanosAudio", "Artwork", "boluo.png");

#if !UNITY_EDITOR
            // Playback=4，options=0，autoActivate=true
            BoluoMusicPlayerIOS_Init(4, 0, true, false, gameObject.name);
            BoluoMusicPlayerIOS_SetInitialized(true);
#endif
            BoluoAudioSessionNative.ArmSessionForIslandPlayback();

            if (!BackgroundPlayPolicyCallback.GetBackgroundPlayDisable())
                SetNowPlayingInfo("波萝", "", "", ArtworkDefault);
            else
                ClearNowPlaying();

            Debug.Log("[BoluoMusicPlayerIOS] InitializeAfterLogin");
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
#if !UNITY_EDITOR
            try
            {
                BoluoMusicPlayerIOS_MarkStoppedForLogout();
                BoluoMusicPlayerIOS_SetInitialized(false);
            }
            catch { /* missing native */ }
#endif
            BoluoAudioSessionNative.SilenceForLogout();
            sIsInitialized = false;
            Debug.Log("[BoluoMusicPlayerIOS] CleanupAfterLogout");
        }

        public void SetNowPlayingInfo(string title, string artist, string album, string artworkPath)
        {
            if (!sIsInitialized)
                return;

            if (BackgroundPlayPolicyCallback.GetBackgroundPlayDisable())
            {
                ClearNowPlaying();
                return;
            }

            bool clear = string.IsNullOrEmpty(title)
                         && string.IsNullOrEmpty(artist)
                         && string.IsNullOrEmpty(album)
                         && string.IsNullOrEmpty(artworkPath);
            if (clear)
            {
                ClearNowPlaying();
                return;
            }

            title = string.IsNullOrEmpty(title) ? "波萝" : title;
            artist ??= "";
            album ??= "";
            if (string.IsNullOrEmpty(artworkPath))
                artworkPath = ArtworkDefault ?? "";

#if !UNITY_EDITOR
            BoluoMusicPlayerIOS_SetNowPlayingInfo(title, artist, album, artworkPath);
#endif
        }

        public void ClearNowPlaying()
        {
#if !UNITY_EDITOR
            try
            {
                BoluoMusicPlayerIOS_RemoveRemoteCommands();
                BoluoMusicPlayerIOS_SetNowPlayingInfo("", "", "", "");
            }
            catch { /* missing native */ }
#endif
        }

        public bool IsUserPaused()
        {
#if !UNITY_EDITOR
            try { return BoluoMusicPlayerIOS_IsUserPaused(); }
            catch { return false; }
#else
            return false;
#endif
        }

        public void SyncExternalPlaybackState(bool isPlaying)
        {
#if !UNITY_EDITOR
            try { BoluoMusicPlayerIOS_SyncExternalPlaybackState(isPlaying); }
            catch { /* missing native */ }
#endif
        }

        /// <summary>
        /// 控制中心 / 锁屏 Play。reason=ui 只刷 UI，禁止 RequestResumeAll。
        /// </summary>
        public void OnNowPlayingPlay(string reason)
        {
            if (!sIsInitialized)
                return;
            MusicPauseUICallback.Hide();
            if (string.Equals(reason, "ui", System.StringComparison.Ordinal))
                return;
            GlobalPlaybackIntentCallback.RequestResumeAll();
        }

        public void OnNowPlayingPause(string empty)
        {
            if (!sIsInitialized)
                return;
            MusicPauseUICallback.Show(MusicPauseUICallback.TipKind.ProgrammaticPause);
            GlobalPlaybackIntentCallback.RequestPauseAll();
            SyncExternalPlaybackState(false);
        }

        public void OnSystemAudioMuted(string empty)
        {
            WanosEngine.MarkInterrupted("ios-system-mute");
            MusicPauseUICallback.Show(MusicPauseUICallback.TipKind.TransientInterrupt);
        }

        public void OnCompetingAudioMuted(string empty)
        {
            WanosEngine.MarkInterrupted("ios-competing-mute");
            MusicPauseUICallback.Show(MusicPauseUICallback.TipKind.CompetingLong);
        }

        public void OnAudioFocusDuck(string factorStr) { }
        public void OnAudioFocusUnduck(string empty) { }
        public void OnAudioFocusRequestFailed(string empty)
        {
            WanosEngine.MarkInterrupted("ios-focus-request-failed");
        }
        public void SetUserAudioSettings(string payload) { }
        public void OnAudioRouteChanged(string message) { }

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
