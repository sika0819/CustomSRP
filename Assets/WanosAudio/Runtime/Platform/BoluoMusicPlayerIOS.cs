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

        [DllImport("__Internal")]
        static extern void WanosMusicPlayer_NoteUnitySkipSettled();
#endif

        /// <summary>Unity Playing / 超时：清原生 remote skip inFlight。</summary>
        public static void NoteSkipSettled()
        {
#if UNITY_IOS && !UNITY_EDITOR
            try { WanosMusicPlayer_NoteUnitySkipSettled(); }
            catch { /* missing native */ }
#endif
        }

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
            {
                Debug.Log("[BoluoAudioFg] SetNowPlayingInfo dropped not initialized");
                return;
            }

            if (BackgroundPlayPolicyCallback.GetBackgroundPlayDisable())
            {
                Debug.Log("[BoluoAudioFg] SetNowPlayingInfo bg play disabled, clear card");
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

            Debug.Log("[BoluoAudioFg] SetNowPlayingInfo title=" + title
                + " art=" + (string.IsNullOrEmpty(artworkPath) ? "empty" : "set"));
#if !UNITY_EDITOR
            BoluoMusicPlayerIOS_SetNowPlayingInfo(title, artist, album, artworkPath);
#endif
        }

        public void ClearNowPlaying()
        {
            Debug.Log("[BoluoAudioFg] ClearNowPlaying title=empty buttons=off");
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
            Debug.Log("[BoluoAudioFg] SyncExternalPlaybackState playing=" + isPlaying);
#if !UNITY_EDITOR
            try { BoluoMusicPlayerIOS_SyncExternalPlaybackState(isPlaying); }
            catch { /* missing native */ }
#endif
        }

        /// <summary>
        /// 控制中心 / 锁屏 Play。reason=ui 只刷 UI，禁止 RequestResumeAll（对齐 Policy）。
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
            Debug.Log("[BoluoAudioFg] OnNowPlayingPlay drive engine RequestResumeAll reason=" + reason);
            MusicPauseUICallback.Hide();
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
            SyncExternalPlaybackState(false);
        }

        public void OnSystemAudioMuted(string empty)
        {
            Debug.Log("[BoluoAudioFg] OnSystemAudioMuted interrupt not userPaused");
            WanosEngine.MarkInterrupted("ios-system-mute");
            GlobalPlaybackIntentCallback.NotifySystemAudioInterrupted();
            MusicPauseUICallback.Show(MusicPauseUICallback.TipKind.TransientInterrupt);
        }

        public void OnCompetingAudioMuted(string empty)
        {
            Debug.Log("[BoluoAudioFg] OnCompetingAudioMuted interrupt not userPaused");
            WanosEngine.MarkInterrupted("ios-competing-mute");
            GlobalPlaybackIntentCallback.NotifySystemAudioInterrupted();
            MusicPauseUICallback.Show(MusicPauseUICallback.TipKind.CompetingLong);
        }

        /// <summary>原生打断结束自动续播后：清 yield 并恢复音效。</summary>
        public void OnSystemAudioRestored(string empty)
        {
            Debug.Log("[BoluoAudioFg] OnSystemAudioRestored");
            GlobalPlaybackIntentCallback.NotifySystemAudioRestored();
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
            WanosEngine.MarkInterrupted("ios-focus-request-failed");
            GlobalPlaybackIntentCallback.NotifySystemAudioInterrupted();
        }

        public void SetUserAudioSettings(string payload)
        {
            Debug.Log("[BoluoAudioFg] SetUserAudioSettings payload=" + (payload ?? ""));
        }

        public void OnAudioRouteChanged(string message)
        {
            Debug.Log("[BoluoAudioFg] OnAudioRouteChanged message=" + message);
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
