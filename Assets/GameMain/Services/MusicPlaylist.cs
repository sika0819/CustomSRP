using System;
using Game.BoluoApi.Music;
using UnityEngine;
using UnityGameFramework.Runtime;
using WanosAudio;

namespace Game
{
    /// <summary>
    /// Music playback backed by <see cref="MusicCatalog"/>.
    /// All track data (names, banks, events) comes from <c>StreamingAssets/WanosAudio/MusicLists.json</c>.
    /// </summary>
    public sealed class MusicPlaylist
    {
        public const string MusicGroup = "Music";
        public const string SoundGroup = "Sound";
        public const string AmbientGroup = "Ambient";
        public const string PlayModeKey = "Music.PlayMode";

        static MusicPlaylist _instance;

        public static MusicPlaylist Instance => _instance ??= new MusicPlaylist();

        // -----------------------------------------------------------------------
        // State
        // -----------------------------------------------------------------------

        /// <summary>Current album index in <see cref="MusicCatalog"/> (-1 = none).</summary>
        public int CurrentAlbumIndex { get; private set; } = -1;

        /// <summary>Current track index within the current album (-1 = none).</summary>
        public int CurrentTrackIndex { get; private set; } = -1;

        /// <summary>True when a track is currently playing.</summary>
        public bool IsPlaying { get; private set; }

        public float MusicVolume { get; private set; } = 1f;
        public float EffectVolume { get; private set; } = 1f;
        public int PlayMode { get; private set; } = MusicPlayMode.Single;

        int _musicSerialId = -1;

        // -----------------------------------------------------------------------
        // Session reset (called on island leave / re-enter)
        // -----------------------------------------------------------------------

        public static void ResetSession()
        {
            if (_instance != null)
            {
                _instance._musicSerialId = -1;
                _instance.IsPlaying = false;
            }
            NowPlayingService.CleanupAfterLeaveIsland();
        }

        // -----------------------------------------------------------------------
        // Volume / PlayMode
        // -----------------------------------------------------------------------

        public void ApplySavedVolumes()
        {
            BoluoGameSave.EnsureLoaded();
            MusicVolume = BoluoGameSave.MusicVolume01;
            EffectVolume = BoluoGameSave.EffectVolume01;
            PlayMode = LoadPlayMode();
            PushVolumesToEngine();
            ApplyGroupVolumes();
        }

        public void SetMusicVolume(float value)
        {
            MusicVolume = Mathf.Clamp01(value);
            PushVolumesToEngine();
            ApplyGroupVolumes();
        }

        public void SetEffectVolume(float value)
        {
            EffectVolume = Mathf.Clamp01(value);
            PushVolumesToEngine();
            ApplyGroupVolumes();
        }

        public void SetPlayMode(int mode)
        {
            if (mode < MusicPlayMode.Single || mode > MusicPlayMode.Shuffle)
                mode = MusicPlayMode.Single;
            PlayMode = mode;
            if (GameEntry.Setting == null)
                return;
            GameEntry.Setting.SetInt(PlayModeKey, PlayMode);
            GameEntry.Setting.Save();
        }

        public int CyclePlayMode()
        {
            SetPlayMode(MusicPlayMode.Next(PlayMode));
            return PlayMode;
        }

        // -----------------------------------------------------------------------
        // Playback — album + track indexed
        // -----------------------------------------------------------------------

        /// <summary>
        /// Play a specific track. Always stops any previous BGM first (one song at a time).
        /// </summary>
        public void PlayIndex(int albumIndex, int trackIndex, bool restart = false)
        {
            var track = MusicCatalog.GetTrack(albumIndex, trackIndex);
            if (track == null)
            {
                Debug.LogWarning($"[MusicPlaylist] PlayIndex out-of-range: album={albumIndex} track={trackIndex}");
                return;
            }

            bool same = albumIndex == CurrentAlbumIndex && trackIndex == CurrentTrackIndex && IsPlaying;
            CurrentAlbumIndex = albumIndex;
            CurrentTrackIndex = trackIndex;

            if (same && !restart)
                return;

            // Hard-stop previous voice before starting the next (Wanos can stack PostEvents).
            Stop(publish: false);
            TryStartTrack(track.Value);
        }

        /// <summary>
        /// Stop current BGM (GF agent + engine <c>Stop_Music</c>). Used by track switch / user stop.
        /// Preset apply must use <see cref="ForceRetargetAtLanding"/> — do not stop BGM across iris.
        /// </summary>
        public void Stop(bool publish = true)
        {
            StopCurrentMusic();
            // Always post even if serialId was lost — prevents overlapping Play_Music* voices.
            try
            {
                if (WanosNative.Instance != null)
                    WanosNative.Instance.PostEvent(0, "Stop_Music");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[MusicPlaylist] Stop_Music failed: {ex.Message}");
            }

            IsPlaying = false;
            if (publish)
                NowPlayingService.PublishFromPlaylist();
        }

        /// <summary>
        /// Preset landing: switch BGM to <paramref name="preferredBankName"/> (or hard-restart
        /// the current track when null). Always goes through <see cref="PlayIndex"/> so prior
        /// <c>Stop_Music</c> runs before the new <c>Play_*</c> — no stacked voices, no silent gap.
        /// </summary>
        public void ForceRetargetAtLanding(string preferredBankName = null)
        {
            if (!MusicCatalog.IsReady)
                return;

            if (!string.IsNullOrEmpty(preferredBankName)
                && MusicCatalog.TryFindByBankName(preferredBankName, out int album, out int track))
            {
                PlayIndex(album, track, restart: true);
                return;
            }

            if (CurrentAlbumIndex < 0 || CurrentTrackIndex < 0)
                return;
            PlayIndex(CurrentAlbumIndex, CurrentTrackIndex, restart: true);
        }

        /// <summary>
        /// Compatibility shim: maps a flat <paramref name="trackIndex"/> across all albums.
        /// Use <see cref="PlayIndex(int, int)"/> with proper album+track indices instead.
        /// </summary>
        public void PlayIndex(int trackIndex, bool restart = false)
        {
            // Flat index: find album/track via the catalog.
            int total = 0;
            for (int a = 0; a < MusicCatalog.AlbumCount; a++)
            {
                int count = MusicCatalog.GetTracks(a).Count;
                if (trackIndex < total + count)
                {
                    PlayIndex(a, trackIndex - total, restart);
                    return;
                }
                total += count;
            }

            // Fallback: try to find by current album only (legacy behaviour).
            var album = MusicCatalog.GetAlbum(CurrentAlbumIndex);
            if (album != null && trackIndex >= 0 && trackIndex < album.TrackCount)
                PlayIndex(CurrentAlbumIndex, trackIndex, restart);
        }

        // -----------------------------------------------------------------------
        // Navigation
        // -----------------------------------------------------------------------

        public void Next()
        {
            if (!MusicCatalog.IsReady || MusicCatalog.AlbumCount == 0)
                return;

            if (PlayMode == MusicPlayMode.Shuffle)
            {
                PlayIndex(PickShuffleAlbum(), PickShuffleTrack(), restart: true);
                return;
            }

            // Advance within the current album.
            var tracks = MusicCatalog.GetTracks(CurrentAlbumIndex);
            if (tracks.Count == 0)
            {
                // Try next non-empty album.
                for (int i = 1; i < MusicCatalog.AlbumCount; i++)
                {
                    int idx = (CurrentAlbumIndex + i) % MusicCatalog.AlbumCount;
                    if (MusicCatalog.GetTracks(idx).Count > 0)
                    {
                        PlayIndex(idx, 0, restart: true);
                        return;
                    }
                }
                return;
            }

            int next = CurrentTrackIndex + 1;
            if (next >= tracks.Count)
                next = PlayMode == MusicPlayMode.Loop ? 0 : tracks.Count - 1;
            PlayIndex(CurrentAlbumIndex, next, restart: true);
        }

        public void Prev()
        {
            if (!MusicCatalog.IsReady || MusicCatalog.AlbumCount == 0)
                return;

            if (PlayMode == MusicPlayMode.Shuffle)
            {
                PlayIndex(PickShuffleAlbum(), PickShuffleTrack(), restart: true);
                return;
            }

            var tracks = MusicCatalog.GetTracks(CurrentAlbumIndex);
            int prev = CurrentTrackIndex - 1;
            if (prev < 0)
                prev = PlayMode == MusicPlayMode.Loop ? tracks.Count - 1 : 0;
            PlayIndex(CurrentAlbumIndex, prev, restart: true);
        }

        int PickShuffleAlbum()
        {
            if (MusicCatalog.AlbumCount <= 1)
                return 0;
            int next;
            do { next = UnityEngine.Random.Range(0, MusicCatalog.AlbumCount); }
            while (next == CurrentAlbumIndex);
            return next;
        }

        int PickShuffleTrack()
        {
            var tracks = MusicCatalog.GetTracks(CurrentAlbumIndex);
            if (tracks.Count == 0)
                return 0;
            return UnityEngine.Random.Range(0, tracks.Count);
        }

        // -----------------------------------------------------------------------
        // Pause / Resume
        // -----------------------------------------------------------------------

        public void Pause()
        {
            if (_musicSerialId >= 0 && GameEntry.Sound != null)
            {
                GameEntry.Sound.PauseSound(_musicSerialId);
            }
            // BGM 不在播就别动 Wanos 引擎 —— 否则会把 AmbientZone / Weather / SFX
            // 一起锁死,而工程内没有自动 Resume 路径。
            // 同样,SetContentAudioPaused(true) 会让 AmbienceZone / WeatherAudio
            // 立刻 StopAllPlaying() 并停 ticker;只在 BGM 真在播时才有意义。
            IsPlaying = false;
            if (_musicSerialId >= 0)
            {
                AudioInteractionBootstrap.SetContentAudioPaused(true);
            }
            NowPlayingService.NoteUserPaused(true);
            NowPlayingService.PublishFromPlaylist();
        }

        public void Resume()
        {
            NowPlayingService.NoteUserPaused(false);
            if (_musicSerialId >= 0 && GameEntry.Sound != null)
                GameEntry.Sound.ResumeSound(_musicSerialId);
            else
                WanosEngine.ResumeAll();
            IsPlaying = true;
            WanosEngine.Unmute("music-resume");
            AudioInteractionBootstrap.SetContentAudioPaused(false);
            NowPlayingService.PublishFromPlaylist();
        }

        // -----------------------------------------------------------------------
        // Internal helpers
        // -----------------------------------------------------------------------

        void StopCurrentMusic()
        {
            if (_musicSerialId >= 0 && GameEntry.Sound != null)
            {
                GameEntry.Sound.StopSound(_musicSerialId);
                _musicSerialId = -1;
            }
            IsPlaying = false;
        }

        bool TryStartTrack(MusicTrack track)
        {
            string bankName = track.BankName;
            string eventName = track.EventName;
            if (string.IsNullOrEmpty(bankName))
            {
                Debug.LogWarning("[MusicPlaylist] Track has no BankName; skipping.");
                PublishNowPlaying();
                return false;
            }

            // Build full asset path: "BankName/EventName" or fall back to "Play_BankName".
            string asset = !string.IsNullOrEmpty(eventName)
                ? $"{bankName}/{eventName}"
                : $"Play_{bankName}";

            if (!WanosBankStore.IsLoaded(bankName) && !WanosBankStore.Load(bankName))
            {
                Debug.LogWarning($"[MusicPlaylist] skip play — bank not ready bank={bankName}");
                PublishNowPlaying();
                return false;
            }

            if (!WanosEngine.IsPlaybackEnabled)
            {
                Debug.LogWarning("[MusicPlaylist] skip play — playback not enabled yet");
                return false;
            }

            _musicSerialId = GameEntry.Sound.PlaySound(asset, MusicGroup);
            IsPlaying = _musicSerialId >= 0;
            if (IsPlaying)
                WanosEngine.Unmute("music-play");
            PublishNowPlaying();
            return IsPlaying;
        }

        void PushVolumesToEngine()
        {
            WanosEngine.SetVolumeParameter("Music_Volume", MusicVolume);
            WanosEngine.SetVolumeParameter("SFX_Volume", EffectVolume);
        }

        void ApplyGroupVolumes()
        {
            if (GameEntry.Sound == null)
                return;
            SetGroupVolume(MusicGroup, MusicVolume);
            SetGroupVolume(SoundGroup, EffectVolume);
            SetGroupVolume(AmbientGroup, EffectVolume);
        }

        static void SetGroupVolume(string group, float volume)
        {
            var g = GameEntry.Sound.GetSoundGroup(group);
            if (g != null)
                g.Volume = Mathf.Clamp01(volume);
        }

        void PublishNowPlaying()
        {
            NowPlayingService.PublishFromPlaylist();
        }

        static int LoadPlayMode()
        {
            if (GameEntry.Setting != null && GameEntry.Setting.HasSetting(PlayModeKey))
            {
                int mode = GameEntry.Setting.GetInt(PlayModeKey, MusicPlayMode.Single);
                if (mode >= MusicPlayMode.Single && mode <= MusicPlayMode.Shuffle)
                    return mode;
            }
            return MusicPlayMode.Single;
        }
    }
}
