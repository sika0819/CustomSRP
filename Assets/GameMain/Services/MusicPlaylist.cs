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

        /// <summary>
        /// 用户主动停过曲目（Stop）。回前台恢复时不能把停掉的曲目重新拉起来，
        /// 但「暂停」要恢复（需求：不管什么暂停状态，回前台都恢复音乐）。
        /// </summary>
        bool _stoppedByUser;

        /// <summary>当前有可恢复的曲目（选过曲且没被用户主动停掉）。</summary>
        public bool HasRestorableTrack =>
            !_stoppedByUser && CurrentAlbumIndex >= 0 && CurrentTrackIndex >= 0;

        /// <summary>
        /// 当前曲的引擎事件名（与原生传输计划里的 event 对齐）。
        /// </summary>
        public string CurrentEventName
        {
            get
            {
                var track = MusicCatalog.GetTrack(CurrentAlbumIndex, CurrentTrackIndex);
                return track?.EventName ?? "";
            }
        }

        /// <summary>
        /// 采用原生已经在播的曲目（后台负一屏切歌后回前台对账）。
        /// 只更新索引与播放态：引擎里已经是原生起播的那一轨，不能再发一次 Play 事件。
        /// </summary>
        public void AdoptTrack(int albumIndex, int trackIndex)
        {
            if (albumIndex < 0 || trackIndex < 0)
                return;
            CurrentAlbumIndex = albumIndex;
            CurrentTrackIndex = trackIndex;
            IsPlaying = true;
        }

        // -----------------------------------------------------------------------
        // Session reset (called on island leave / re-enter)
        // -----------------------------------------------------------------------

        public static void ResetSession()
        {
            if (_instance != null)
            {
                _instance._musicSerialId = -1;
                _instance.IsPlaying = false;
                _instance._stoppedByUser = false;
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
            _stoppedByUser = true;
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
            else
            {
                // serial 已丢时 PauseSound 走不到；仍发 Pause_Music，覆盖原生直控/后台传输仍在出声。
                try
                {
                    if (WanosNative.Instance != null)
                        WanosNative.Instance.PostEvent(0, "Pause_Music");
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[MusicPlaylist] Pause_Music failed: {ex.Message}");
                }
            }
            // 定时到点 / 用户暂停：音乐与音效一起停（AmbienceZone / Weather / 雨水 ticker）。
            IsPlaying = false;
            AudioInteractionBootstrap.SetContentAudioPaused(true);
            // 同步原生 userPaused，否则定时到点后焦点回归/后台续播会把歌又拉起来。
#if UNITY_ANDROID && !UNITY_EDITOR
            BoluoAndroidEngineNative.MarkUserPausedFromUnity(true);
#endif
            NowPlayingService.NoteUserPaused(true);
            NowPlayingService.PublishFromPlaylist();
        }

        public void Resume()
        {
            // 与 EnsureMusicRestored 对齐：GF ResumeSound + 显式 Resume_Music，
            // 覆盖定时到点 PauseSound / 负一屏 Pause_Music 两条停播路径。
            NowPlayingService.NoteUserPaused(false);
#if UNITY_ANDROID && !UNITY_EDITOR
            BoluoAndroidEngineNative.MarkUserPausedFromUnity(false);
#endif
            if (_musicSerialId >= 0 && GameEntry.Sound != null)
                GameEntry.Sound.ResumeSound(_musicSerialId);
            try
            {
                if (WanosNative.Instance != null)
                    WanosNative.Instance.PostEvent(0, "Resume_Music");
                else
                    WanosEngine.ResumeAll();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[MusicPlaylist] Resume_Music failed: {ex.Message}");
                WanosEngine.ResumeAll();
            }
            IsPlaying = CurrentAlbumIndex >= 0 && CurrentTrackIndex >= 0 && !_stoppedByUser;
            WanosEngine.Unmute("music-resume");
            AudioInteractionBootstrap.SetContentAudioPaused(false);
#if UNITY_ANDROID && !UNITY_EDITOR
            // 定时取消依赖此路径出声：原生 pauseForTimerEnd 已 mute，须补抢焦点 + unmute。
            BoluoAndroidEngineNative.EnsureAudibleForUnityPlayback();
#endif
            NowPlayingService.PublishFromPlaylist();
        }

        /// <summary>
        /// 定时结束弹窗「取消」/「重复定时」：续播当前曲（不 Stop+Play）。
        /// </summary>
        public void ResumeAfterTimerEnd(string reason = "timer-end")
        {
            if (!HasRestorableTrack)
            {
                // 没选过曲也要把程序化暂停旗标清掉，避免点场景误判。
                NowPlayingService.NoteUserPaused(false);
                AudioInteractionBootstrap.SetContentAudioPaused(false);
                return;
            }

            if (!WanosEngine.IsPlaybackEnabled)
                WanosEngine.EnablePlayback(reason);
            EnsureMusicRestored(reason);
        }

        /// <summary>
        /// 回前台 / 打断后恢复音乐：
        /// <para>
        /// GF agent 还在 → 只发 <c>Resume_Music</c>（续进度，不 Stop+Play）；
        /// agent 已丢（serial &lt; 0）→ 重起当前曲目。
        /// </para>
        /// 不要用原生 <c>PlaybackState</c> 决定是否硬重启：该状态在 Editor/macOS 上
        /// 经常与真实发声不同步，会把正在播的曲反复掐掉重开。
        /// 用户主动「停止」过的曲目不恢复；用户主动「暂停」的会恢复。
        /// </summary>
        public void EnsureMusicRestored(string reason)
        {
            if (!HasRestorableTrack)
                return;
            if (!WanosEngine.IsPlaybackEnabled)
                return;

            if (_musicSerialId < 0)
            {
                if (GameEntry.Sound == null)
                    return;
                // GF 侧 sound 已不在 → 没有任何可 Resume 的 voice → 重起当前曲目。
                Log.Info($"[MusicPlaylist] restore ({reason}): serial lost → replay track");
                PlayIndex(CurrentAlbumIndex, CurrentTrackIndex, restart: true);
                return;
            }

            // agent 还在 → 优先走 GF ResumeSound（前台 PauseSound 路径）；
            // 再补 Resume_Music（后台负一屏原生 Pause_Music 路径）。
            if (GameEntry.Sound != null)
                GameEntry.Sound.ResumeSound(_musicSerialId);
            try
            {
                if (WanosNative.Instance != null)
                    WanosNative.Instance.PostEvent(0, "Resume_Music");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[MusicPlaylist] Resume_Music failed: {ex.Message}");
            }
            IsPlaying = true;
            NowPlayingService.NoteUserPaused(false);
            WanosEngine.Unmute("music-restore");
            AudioInteractionBootstrap.SetContentAudioPaused(false);
            PushVolumesToEngine();
#if UNITY_ANDROID && !UNITY_EDITOR
            BoluoAndroidEngineNative.EnsureAudibleForUnityPlayback();
#endif
            Log.Info($"[MusicPlaylist] restore ({reason}): resumed track {CurrentAlbumIndex}/{CurrentTrackIndex}");
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
            // 起播新曲目 = 用户不再处于「主动停止」状态（回前台恢复可以拉回来）。
            _stoppedByUser = false;
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
            {
                WanosEngine.Unmute("music-play");
                // Push user volume again after PlaySound (agent may have written RTPC too).
                PushVolumesToEngine();
#if UNITY_ANDROID && !UNITY_EDITOR
                BoluoAndroidEngineNative.EnsureAudibleForUnityPlayback();
#endif
                // 首播/切歌成功同一帧强制 isPlaying=true，并确保已 Arm，避免负一屏空白。
                NowPlayingService.PublishPlayingForced();
            }
            else
            {
                PublishNowPlaying();
            }
            return IsPlaying;
        }

        public void PushVolumesToEngine()
        {
            // Preset iris ducks Music_Volume to 0 before Stop_Music/Play_* so retarget is silent.
            float music = MusicVolume * ContentAudioDuck.Level01;
            WanosEngine.SetVolumeParameter(WanosEngine.MusicVolumeParam, music);
            WanosEngine.SetVolumeParameter(WanosEngine.FxVolumeParam, EffectVolume);
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
