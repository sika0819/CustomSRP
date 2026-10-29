using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
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

        /// <summary>Playing 丢失时补发 Break 的等待（仍等本曲播完，不中途 Stop）。</summary>
        const int BreakFallbackDelayMs = 1500;

        /// <summary>Ended 丢失时（macOS Editor 常见）按曲长兜底切歌的余量。</summary>
        const int EndedFallbackSlackMs = 2000;

        /// <summary>无法从 bank 估时长时的 Ended 兜底（仍避免永远不切）。</summary>
        const int EndedFallbackDefaultMs = 180_000;

        static MusicPlaylist _instance;

        public static MusicPlaylist Instance => _instance ??= new MusicPlaylist();

        static readonly List<(int album, int track)> ShuffleScratch = new List<(int, int)>(64);

        // -----------------------------------------------------------------------
        // State
        // -----------------------------------------------------------------------

        /// <summary>Current album index in <see cref="MusicCatalog"/> (-1 = none).</summary>
        public int CurrentAlbumIndex { get; private set; } = -1;

        /// <summary>Current track index within the current album (-1 = none).</summary>
        public int CurrentTrackIndex { get; private set; } = -1;

        /// <summary>True when a track is currently playing.</summary>
        public bool IsPlaying { get; private set; }

        /// <summary>
        /// 当前曲目已提交并尝试起播之后（含循环/随机自动切歌）。
        /// 打开中的播放列表据此重绑行上的播放标记。
        /// </summary>
        public event Action TrackChanged;

        public float MusicVolume { get; private set; } = 1f;
        public float EffectVolume { get; private set; } = 1f;
        public int PlayMode { get; private set; } = MusicPlayMode.Single;

        int _musicSerialId = -1;

        /// <summary>Current BGM voice from <see cref="WanosSoundAgentHelper"/> (0 = none).</summary>
        ulong _musicVoiceId;

        /// <summary>Bumped on attach/detach so stale Ended after Stop/skip is ignored.</summary>
        int _voiceGeneration;

        /// <summary>Break_Music already posted for this voice (ListLoop / Shuffle).</summary>
        bool _breakPostedForVoice;

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

        /// <summary>当前曲目的 bank。沉浸/切预设回收时保留，避免把正在听的曲子卸掉。</summary>
        public string CurrentBankName
        {
            get
            {
                var track = MusicCatalog.GetTrack(CurrentAlbumIndex, CurrentTrackIndex);
                return track?.BankName;
            }
        }

        /// <summary>
        /// 采用原生已经在播的曲目（后台负一屏切歌后回前台对账）。
        /// 只更新索引与播放态：引擎里已经是原生起播的那一轨，不能再发一次 Play 事件（避免中途掐断）。
        /// Loop/Shuffle 时尽量重挂 voice + Break，等本曲 Ended 再自动下一首。
        /// </summary>
        public void AdoptTrack(int albumIndex, int trackIndex)
        {
            if (albumIndex < 0 || trackIndex < 0)
                return;
            CurrentAlbumIndex = albumIndex;
            CurrentTrackIndex = trackIndex;
            IsPlaying = true;
            _stoppedByUser = false;

            EnsureVoiceHook();
            if (WanosSoundAgentHelper.TryGetActiveMusicVoice(out ulong agentVoice) && agentVoice != 0)
            {
                AttachMusicVoice(agentVoice);
                Log.Info($"[MusicPlaylist] AdoptTrack album={albumIndex} track={trackIndex} rebind voice={agentVoice} mode={PlayMode}");
                return;
            }

            if (_musicVoiceId != 0 && IsListAdvanceMode(PlayMode) && !_breakPostedForVoice)
                TryPostBreak("adopt-keep-voice");
            else
                Log.Info($"[MusicPlaylist] AdoptTrack album={albumIndex} track={trackIndex} voice={_musicVoiceId} mode={PlayMode}");
        }

        // -----------------------------------------------------------------------
        // Session reset (called on island leave / re-enter)
        // -----------------------------------------------------------------------

        public static void ResetSession()
        {
            if (_instance != null)
            {
                _instance.UnhookVoiceStates();
                _instance._musicSerialId = -1;
                _instance._musicVoiceId = 0;
                _instance._breakPostedForVoice = false;
                _instance._voiceGeneration++;
                _instance.IsPlaying = false;
                _instance._stoppedByUser = false;
            }
            NowPlayingService.CleanupAfterLeaveIsland();
        }

        /// <summary>
        /// 离岛：解绑 voice、作废进行中的切歌兜底，并保持「用户已停」。
        /// 不要走 <see cref="ResetSession"/>：那里会把 <c>_stoppedByUser</c> 抬回 false，
        /// 回前台 <c>EnsureMusicRestored</c> 会把曲目拉起来。
        /// </summary>
        public void DisarmAfterLeave()
        {
            UnhookVoiceStates();
            if (_musicSerialId >= 0 && GameEntry.Sound != null)
            {
                GameEntry.Sound.StopSound(_musicSerialId);
                _musicSerialId = -1;
            }

            _musicVoiceId = 0;
            _breakPostedForVoice = false;
            _voiceGeneration++;
            IsPlaying = false;
            _stoppedByUser = true;
        }

        /// <summary>
        /// Called by <see cref="WanosSoundAgentHelper"/> after PostEvent with callback.
        /// </summary>
        public void AttachMusicVoice(ulong voiceId)
        {
            EnsureVoiceHook();
            _musicVoiceId = voiceId;
            _breakPostedForVoice = false;
            int gen = ++_voiceGeneration;

            // 不在 Attach 立刻 Break：引擎可能尚未 Playing，过早 Break 会被吞，
            // 且 _breakPostedForVoice 会挡住真正的 Playing→Break。
            // Playing 正常路径发 Break；丢包时由 fallback 补发（仍等本曲播完）。
            if (IsListAdvanceMode(PlayMode))
                ScheduleBreakFallback(gen).Forget();
        }

        /// <summary>
        /// Called before Stop_Music so Ended from stop does not advance the playlist.
        /// </summary>
        public void DetachMusicVoice(ulong voiceId)
        {
            if (voiceId != 0 && voiceId == _musicVoiceId)
                _musicVoiceId = 0;
            _breakPostedForVoice = false;
            _voiceGeneration++;
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
            if (PlayMode == mode)
                return;

            PlayMode = mode;

            // Loop/Shuffle：只发 Break，等本曲播完再 Ended→Next（不中途 Stop）。
            // Single：不 Break，引擎继续自循环。voice 还没挂上时由 Attach 后的 fallback 补 Break。
            if (IsPlaying && IsListAdvanceMode(mode))
            {
                if (_musicVoiceId == 0
                    && WanosSoundAgentHelper.TryGetActiveMusicVoice(out ulong agentVoice)
                    && agentVoice != 0)
                {
                    AttachMusicVoice(agentVoice);
                }

                if (_musicVoiceId != 0)
                    TryPostBreak("SetPlayMode");
            }

            if (GameEntry.Setting != null)
            {
                GameEntry.Setting.SetInt(PlayModeKey, PlayMode);
                GameEntry.Setting.Save();
            }
            // Android 负一屏 transport plan 带 mode；切模式后立刻重推，避免仍用旧模式切歌。
            NowPlayingService.PublishFromPlaylist();
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
            TrackChanged?.Invoke();
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
                WanosInteraction.PostMusicEvent("Stop_Music");
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
                PickShuffle(out int album, out int track);
                PlayIndex(album, track, restart: true);
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
                PickShuffle(out int album, out int track);
                PlayIndex(album, track, restart: true);
                return;
            }

            var tracks = MusicCatalog.GetTracks(CurrentAlbumIndex);
            int prev = CurrentTrackIndex - 1;
            if (prev < 0)
                prev = PlayMode == MusicPlayMode.Loop ? tracks.Count - 1 : 0;
            PlayIndex(CurrentAlbumIndex, prev, restart: true);
        }

        void PickShuffle(out int albumIndex, out int trackIndex)
        {
            albumIndex = CurrentAlbumIndex;
            trackIndex = CurrentTrackIndex;
            if (MusicCatalog.AlbumCount <= 0)
                return;

            // 全专辑扁平随机，排除当前同一首（全库仅 1 首时才允许同曲）。
            ShuffleScratch.Clear();
            for (int a = 0; a < MusicCatalog.AlbumCount; a++)
            {
                var tracks = MusicCatalog.GetTracks(a);
                for (int t = 0; t < tracks.Count; t++)
                {
                    if (a == CurrentAlbumIndex && t == CurrentTrackIndex)
                        continue;
                    ShuffleScratch.Add((a, t));
                }
            }

            if (ShuffleScratch.Count == 0)
                return;

            var pick = ShuffleScratch[UnityEngine.Random.Range(0, ShuffleScratch.Count)];
            albumIndex = pick.album;
            trackIndex = pick.track;
        }

        // -----------------------------------------------------------------------
        // Voice Playing / Ended → Break + auto-next (Boluo WanosMusicPlayer.PlayState)
        // -----------------------------------------------------------------------

        static bool IsListAdvanceMode(int mode) =>
            mode == MusicPlayMode.Loop || mode == MusicPlayMode.Shuffle;

        void EnsureVoiceHook()
        {
            // 先卸再挂：WanosEventCallbackQueue.ResetSession 可能把 StateChanged 置空，
            // 短路“已经挂过”会永久收不到 Playing/Ended。
            WanosEventCallbackQueue.StateChanged -= OnMusicVoiceState;
            WanosEventCallbackQueue.StateChanged += OnMusicVoiceState;
        }

        void UnhookVoiceStates()
        {
            WanosEventCallbackQueue.StateChanged -= OnMusicVoiceState;
        }

        void TryPostBreak(string reason)
        {
            if (_breakPostedForVoice)
                return;
            _breakPostedForVoice = true;
            try
            {
                WanosInteraction.PostMusicEvent("Break_Music");
                Log.Info($"[MusicPlaylist] Break_Music ({reason}) voice={_musicVoiceId} mode={PlayMode}");
                // macOS Editor：Playing 能到、Ended 经常丢 → 按曲长兜底 Advance（Ended 先到则 gen 变、兜底作废）。
                ScheduleEndedFallback(_voiceGeneration).Forget();
            }
            catch (Exception ex)
            {
                _breakPostedForVoice = false;
                Debug.LogWarning($"[MusicPlaylist] Break_Music ({reason}) failed: {ex.Message}");
            }
        }

        async UniTaskVoid ScheduleBreakFallback(int generation)
        {
            try
            {
                await UniTask.Delay(BreakFallbackDelayMs, DelayType.UnscaledDeltaTime);
            }
            catch
            {
                return;
            }

            if (generation != _voiceGeneration)
                return;
            if (_breakPostedForVoice || _musicVoiceId == 0)
                return;
            if (!IsListAdvanceMode(PlayMode))
                return;
            TryPostBreak("Playing-fallback");
        }

        async UniTaskVoid ScheduleEndedFallback(int generation)
        {
            int delayMs = EndedFallbackDefaultMs;
            var track = MusicCatalog.GetTrack(CurrentAlbumIndex, CurrentTrackIndex);
            if (track != null
                && !string.IsNullOrEmpty(track.Value.BankName)
                && WanosBankStore.TryEstimateDurationMs(track.Value.BankName, out int estimatedMs))
            {
                delayMs = estimatedMs + EndedFallbackSlackMs;
            }

            try
            {
                await UniTask.Delay(delayMs, DelayType.UnscaledDeltaTime);
            }
            catch
            {
                return;
            }

            if (generation != _voiceGeneration)
                return;
            if (_musicVoiceId == 0)
                return;
            if (!IsListAdvanceMode(PlayMode))
                return;
            if (_stoppedByUser || NowPlayingService.UserManuallyPaused)
                return;

            Log.Warning(
                $"[MusicPlaylist] Ended missing after {delayMs}ms — fallback advance {CurrentAlbumIndex}/{CurrentTrackIndex} mode={PlayMode}");
            _musicVoiceId = 0;
            _breakPostedForVoice = false;
            _voiceGeneration++;
            AdvanceAfterTrackEnded();
        }

        void OnMusicVoiceState(ulong instanceId, int state)
        {
            if (instanceId == 0 || instanceId != _musicVoiceId)
                return;

            if (state == WanosInstanceState.Playing)
            {
                // ListLoop / Shuffle: Break → 本曲播完进 Ended → Next（不中途掐断）。
                // Single: 不 Break → 引擎自循环。
                if (IsListAdvanceMode(PlayMode))
                    TryPostBreak("Playing");
                return;
            }

            if (state != WanosInstanceState.Ended)
                return;

            if (_stoppedByUser || NowPlayingService.UserManuallyPaused)
            {
                Log.Info($"[MusicPlaylist] Ended ignored stopped={_stoppedByUser} paused={NowPlayingService.UserManuallyPaused}");
                return;
            }

            Log.Info($"[MusicPlaylist] Ended voice={instanceId} mode={PlayMode} → advance {CurrentAlbumIndex}/{CurrentTrackIndex}");
            // Drop ownership before Advance (PlayIndex → Stop detaches again safely).
            _musicVoiceId = 0;
            _breakPostedForVoice = false;
            _voiceGeneration++;
            AdvanceAfterTrackEnded();
        }

        void AdvanceAfterTrackEnded()
        {
            if (!MusicCatalog.IsReady)
                return;

            if (PlayMode == MusicPlayMode.Single)
            {
                // Should rarely hit (Single usually never Ends). Restart same track.
                if (CurrentAlbumIndex >= 0 && CurrentTrackIndex >= 0)
                    PlayIndex(CurrentAlbumIndex, CurrentTrackIndex, restart: true);
                return;
            }

            Next();
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
                    WanosInteraction.PostMusicEvent("Pause_Music");
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[MusicPlaylist] Pause_Music failed: {ex.Message}");
                }
            }
            // 定时到点 / 用户暂停：音乐与音效一起停（AmbienceZone / Weather / 雨水 ticker）。
            IsPlaying = false;
            IslandAudioSession.Instance?.SetContentAudioPaused(true);
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
                WanosInteraction.PostMusicEvent("Resume_Music");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[MusicPlaylist] Resume_Music failed: {ex.Message}");
                WanosEngine.ResumeAll();
            }
            IsPlaying = CurrentAlbumIndex >= 0 && CurrentTrackIndex >= 0 && !_stoppedByUser;
            WanosEngine.Unmute("music-resume");
            IslandAudioSession.Instance?.SetContentAudioPaused(false);
#if UNITY_ANDROID && !UNITY_EDITOR
            // 定时取消依赖此路径出声：原生 pauseForTimerEnd 已 mute，须补抢焦点 + unmute。
            BoluoAndroidEngineNative.EnsureAudibleForUnityPlayback();
#endif
            NowPlayingService.PublishFromPlaylist();
        }

        /// <summary>
        /// 定时结束弹窗「取消」/「重复定时」：恢复音效闸并硬起重播当前曲。
        /// <para>
        /// 真机到点走 Pause_Music+Pause_ALL+mute 后，Resume_Music 经常拉不起 BGM
        /// （音效 Resume_ALL / ContentAudio 却正常）——不赌 Pause 态，直接 PlayIndex restart。
        /// </para>
        /// </summary>
        public void ResumeAfterTimerEnd(string reason = "timer-end")
        {
            NowPlayingService.NoteUserPaused(false);
#if UNITY_ANDROID && !UNITY_EDITOR
            BoluoAndroidEngineNative.MarkUserPausedFromUnity(false);
#endif
            IslandAudioSession.Instance?.SetContentAudioPaused(false);

            if (!HasRestorableTrack)
            {
                Log.Info($"[MusicPlaylist] ResumeAfterTimerEnd ({reason}): no track, SFX gate only");
#if UNITY_ANDROID && !UNITY_EDITOR
                BoluoAndroidEngineNative.EnsureAudibleForUnityPlayback();
#endif
                return;
            }

            if (!WanosEngine.IsPlaybackEnabled)
                WanosEngine.EnablePlayback(reason);

            // 硬起播：Stop_Music + Play_*，绕过失效的 Resume_Music。
            Log.Info($"[MusicPlaylist] ResumeAfterTimerEnd ({reason}): replay {CurrentAlbumIndex}/{CurrentTrackIndex}");
            PlayIndex(CurrentAlbumIndex, CurrentTrackIndex, restart: true);
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
                WanosInteraction.PostMusicEvent("Resume_Music");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[MusicPlaylist] Resume_Music failed: {ex.Message}");
            }
            IsPlaying = true;
            NowPlayingService.NoteUserPaused(false);
            WanosEngine.Unmute("music-restore");
            IslandAudioSession.Instance?.SetContentAudioPaused(false);
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
            float music = MusicVolume * IslandAudioSession.DuckLevel01;
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
