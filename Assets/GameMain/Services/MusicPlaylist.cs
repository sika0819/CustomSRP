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

        /// <summary>
        /// 用户音乐音量 ≤ 此值视为静音。引擎对近零 <c>Music_Volume</c> 会虚杀掉 voice，
        /// 若当成曲终会 Advance/重播；调音量不得触发重播。
        /// </summary>
        const float SilentMusicVolume01 = 0.001f;

        /// <summary>
        /// 用户拉到 0 时仍推给引擎的保活地板（经 ×100 后 RTPC≈0.1），避免虚杀；
        /// 虹膜 Duck=0 时仍推绝对 0，保留转场静音切歌。
        /// </summary>
        const float MusicVolumeKeepAlive01 = 0.001f;

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

        /// <summary>Default matches <see cref="BoluoSaveData.bgmVolume"/> (80).</summary>
        public float MusicVolume { get; private set; } = 0.8f;

        /// <summary>Default matches <see cref="BoluoSaveData.voiceVolume"/> (80).</summary>
        public float EffectVolume { get; private set; } = 0.8f;
        public int PlayMode { get; private set; } = MusicPlayMode.Single;

        int _musicSerialId = -1;

        /// <summary>Current BGM voice from <see cref="WanosSoundAgentHelper"/> (0 = none).</summary>
        ulong _musicVoiceId;

        /// <summary>Bumped on attach/detach so stale Ended after Stop/skip is ignored.</summary>
        int _voiceGeneration;

        /// <summary>Bumped on PlayIndex / user Stop so superseded deferred Play is dropped.</summary>
        int _playGeneration;

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
            // 后台负一屏切过来的曲也算已听（对齐 Boluo MarkCurrentMusicPlayedIfAny）。
            MarkTrackHeard(albumIndex, trackIndex);
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
                _instance._playGeneration++;
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
            _playGeneration++;
            if (_musicSerialId >= 0 && GameEntry.Sound != null)
            {
                GameEntry.Sound.StopSound(_musicSerialId);
                _musicSerialId = -1;
            }

            WanosSoundAgentHelper.SilenceAllMusicAgentsLocal();
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
            BoluoPlatformMusicBridge.NoteUnityMusicVoice(voiceId);
            BoluoPlatformMusicBridge.NoteUnityMusicContext(
                PlayMode, CurrentAlbumIndex, CurrentTrackIndex);

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
            BoluoPlatformMusicBridge.ClearUnityMusicVoice();
        }

        // -----------------------------------------------------------------------
        // Volume / PlayMode
        // -----------------------------------------------------------------------

        public void ApplySavedVolumes()
        {
            BoluoGameSave.EnsureLoaded();
            // Setting 键是设备级音量常量（不依赖账号进度盘）；无键时回退主存档。
            MusicVolume = ResolveVolume01(VolumeForm.MusicVolumeKey, BoluoGameSave.MusicVolume01);
            EffectVolume = ResolveVolume01(VolumeForm.EffectVolumeKey, BoluoGameSave.EffectVolume01);
            PersistVolumeConstants();
            PlayMode = LoadPlayMode();
            PushVolumesToEngine();
            ApplyGroupVolumes();
        }

        public void SetMusicVolume(float value)
        {
            float prev = MusicVolume;
            MusicVolume = Mathf.Clamp01(value);
            PushVolumesToEngine();
            ApplyGroupVolumes();
            // 调音量禁止 PlayIndex 重播。静音用 RTPC 保活（见 PushVolumesToEngine）；
            // 若引擎仍虚杀掉 voice，离开静音时仅在 voice 已丢时软恢复。
            MaybeRestoreMusicAfterLeavingSilence(prev, MusicVolume);
        }

        void MaybeRestoreMusicAfterLeavingSilence(float prev01, float next01)
        {
            if (prev01 > SilentMusicVolume01 || next01 <= SilentMusicVolume01)
                return;
            if (!IsPlaying || !HasRestorableTrack)
                return;
            if (_musicVoiceId != 0)
                return; // 仍挂着：只靠 RTPC 抬音量，不重 Post。
            if (NowPlayingService.UserManuallyPaused)
                return;
            if (!WanosEngine.IsPlaybackEnabled || IslandAudioSession.IsYieldHeld)
                return;

            // 静音期仍被虚杀 → Ended 已忽略切歌，但 voice 没了；Resume 拉不活，只能重 Post。
            // 保活 RTPC 正常时走不到这里（_musicVoiceId 仍在，上面已 return）。
            Log.Info(
                $"[MusicPlaylist] volume {prev01:F3}→{next01:F3} after mute virt-kill — restore " +
                $"{CurrentAlbumIndex}/{CurrentTrackIndex}");
            PlayIndex(CurrentAlbumIndex, CurrentTrackIndex, restart: true);
        }

        public void SetEffectVolume(float value)
        {
            EffectVolume = Mathf.Clamp01(value);
            PushVolumesToEngine();
            ApplyGroupVolumes();
        }

        /// <summary>松手 / 启动：把当前音量写入 Setting 常量（重启还原 UI）。</summary>
        public void PersistVolumeConstants()
        {
            PersistVolumeConstants(MusicVolume, EffectVolume);
        }

        static float ResolveVolume01(string settingKey, float fallback01)
        {
            if (GameEntry.Setting != null && GameEntry.Setting.HasSetting(settingKey))
                return Mathf.Clamp01(GameEntry.Setting.GetFloat(settingKey, fallback01));
            return Mathf.Clamp01(fallback01);
        }

        static void PersistVolumeConstants(float music01, float effect01)
        {
            music01 = Mathf.Clamp01(music01);
            effect01 = Mathf.Clamp01(effect01);
            if (GameEntry.Setting != null)
            {
                GameEntry.Setting.SetFloat(VolumeForm.MusicVolumeKey, music01);
                GameEntry.Setting.SetFloat(VolumeForm.EffectVolumeKey, effect01);
                GameEntry.Setting.Save();
            }

            BoluoGameSave.SetMusicVolume01(music01);
            BoluoGameSave.SetEffectVolume01(effect01);
        }

        public void SetPlayMode(int mode)
        {
            if (mode < MusicPlayMode.Single || mode > MusicPlayMode.Shuffle)
                mode = MusicPlayMode.Single;
            if (PlayMode == mode)
                return;

            PlayMode = mode;
            BoluoPlatformMusicBridge.NoteUnityMusicContext(
                PlayMode, CurrentAlbumIndex, CurrentTrackIndex);

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

            // 对齐 Boluo MusicPlaybackService：点歌 / 上一首下一首 / 循环随机自动切
            // 一律在起播时记已听。只靠曲终 Mark 会在 Ended 丢包、后台原生切歌时红点回潮。
            MarkTrackHeard(albumIndex, trackIndex);

            bool same = albumIndex == CurrentAlbumIndex && trackIndex == CurrentTrackIndex && IsPlaying;
            CurrentAlbumIndex = albumIndex;
            CurrentTrackIndex = trackIndex;
            BoluoPlatformMusicBridge.NoteUnityMusicContext(
                PlayMode, CurrentAlbumIndex, CurrentTrackIndex);

            if (same && !restart)
                return;

            // 作废在途延帧起播；HardStop 对齐原生 Stop_Music+Break_Music，下一帧再 Play。
            int gen = ++_playGeneration;
            HardStopMusicVoices(markStoppedByUser: false);
            // 延帧起播前先乐观标记，避免 RefreshAll 一帧闪回「未播放」。
            _stoppedByUser = false;
            IsPlaying = true;
            StartTrackDeferred(gen, track.Value).Forget();
            TrackChanged?.Invoke();
        }

        /// <summary>
        /// Stop current BGM (GF agent + engine <c>Stop_Music</c>). Used by track switch / user stop.
        /// Preset apply must use <see cref="ForceRetargetAtLanding"/> — do not stop BGM across iris.
        /// </summary>
        public void Stop(bool publish = true)
        {
            _playGeneration++;
            HardStopMusicVoices(markStoppedByUser: true);
            if (publish)
                NowPlayingService.PublishFromPlaylist();
        }

        /// <summary>
        /// Stop every Music agent + engine music voices (native-aligned Stop+Break).
        /// </summary>
        void HardStopMusicVoices(bool markStoppedByUser)
        {
            StopCurrentMusic();
            WanosSoundAgentHelper.SilenceAllMusicAgentsLocal();
            _musicVoiceId = 0;
            _breakPostedForVoice = false;
            _voiceGeneration++;
            if (markStoppedByUser)
                _stoppedByUser = true;
            // Always post even if serialId was lost — prevents overlapping Play_Music* voices.
            try
            {
                WanosInteraction.PostMusicEvent("Stop_Music");
                WanosInteraction.PostMusicEvent("Break_Music");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[MusicPlaylist] Stop_Music/Break_Music failed: {ex.Message}");
            }

            IsPlaying = false;
            // Stop/Break 后重推用户音量，避免半截原生增益 / duck 态被当成「音量变了」。
            PushVolumesToEngine();
        }

        async UniTaskVoid StartTrackDeferred(int generation, MusicTrack track)
        {
            // 让引擎消化 Stop/Break（对齐原生 ~50ms，这里用一帧避免主线程 sleep）。
            await UniTask.Yield(PlayerLoopTiming.Update);
            if (generation != _playGeneration)
                return;
            TryStartTrack(track, generation);
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
            // 空专辑，或 Loop 下仅 1 首（服务器目录裁薄）：跨专辑，否则负一屏/下一首原地打转。
            if (tracks.Count == 0
                || (PlayMode == MusicPlayMode.Loop && tracks.Count == 1))
            {
                if (TryPlayAdjacentAlbum(+1))
                    return;
                if (tracks.Count == 1)
                    PlayIndex(CurrentAlbumIndex, 0, restart: true);
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
            if (tracks.Count == 0
                || (PlayMode == MusicPlayMode.Loop && tracks.Count == 1))
            {
                if (TryPlayAdjacentAlbum(-1))
                    return;
                if (tracks.Count == 1)
                    PlayIndex(CurrentAlbumIndex, 0, restart: true);
                return;
            }

            int prev = CurrentTrackIndex - 1;
            if (prev < 0)
                prev = PlayMode == MusicPlayMode.Loop ? tracks.Count - 1 : 0;
            PlayIndex(CurrentAlbumIndex, prev, restart: true);
        }

        /// <summary>Loop 跨专辑：找相邻非空专辑的首/末曲。</summary>
        bool TryPlayAdjacentAlbum(int direction)
        {
            if (MusicCatalog.AlbumCount <= 1)
                return false;
            for (int i = 1; i < MusicCatalog.AlbumCount; i++)
            {
                int idx = (CurrentAlbumIndex + direction * i) % MusicCatalog.AlbumCount;
                if (idx < 0)
                    idx += MusicCatalog.AlbumCount;
                var t = MusicCatalog.GetTracks(idx);
                if (t.Count <= 0)
                    continue;
                int track = direction > 0 ? 0 : t.Count - 1;
                PlayIndex(idx, track, restart: true);
                return true;
            }
            return false;
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

        static bool IsListAdvanceMode(int mode) => MusicPlayMode.IsListAdvanceMode(mode);

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

            // 用户音量≈0：引擎虚杀近静音 voice，不是曲终。丢掉 ownership，禁止 Advance/重播。
            if (MusicVolume <= SilentMusicVolume01)
            {
                Log.Info(
                    $"[MusicPlaylist] Ended ignored — music volume silent ({MusicVolume:F3}) voice={instanceId}");
                _musicVoiceId = 0;
                _breakPostedForVoice = false;
                _voiceGeneration++;
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

            // 曲终兜底再记一次（PlayIndex 起播已记；Ended 丢包时仍可清当前行）。
            MarkTrackHeard(CurrentAlbumIndex, CurrentTrackIndex);

            if (PlayMode == MusicPlayMode.Single)
            {
                // Should rarely hit (Single usually never Ends). Restart same track.
                if (CurrentAlbumIndex >= 0 && CurrentTrackIndex >= 0)
                    PlayIndex(CurrentAlbumIndex, CurrentTrackIndex, restart: true);
                return;
            }

            Next();
        }

        /// <summary>
        /// 与点歌同一套 key；起播 / 曲终 / 原生对账都走这里，红点此后不再亮。
        /// </summary>
        static void MarkTrackHeard(int albumIndex, int trackIndex)
        {
            if (albumIndex < 0 || trackIndex < 0)
                return;

            var album = MusicCatalog.GetAlbum(albumIndex);
            var track = MusicCatalog.GetTrack(albumIndex, trackIndex);
            if (album == null || track == null)
                return;

            DownloadedUnplayedManager.MarkPlayed(
                DownloadedUnplayedManager.MusicKey(album.Name, track.Value.MusicName));
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
            // 定时到点 / 负一屏 / 用户暂停：音乐与音效一起停。
            // Pause_Music 停 BGM；Pause_ALL 兜住引擎侧仍在响的环境/天气 voice
            // （对齐原生 handleTransportInBackground / pauseForTimerEnd）。
            // SetContentAudioPaused 停 C# 侧 Ambience/Weather/雨水，并禁止 Evaluate 重 Post。
            try
            {
                WanosEngine.PauseAll();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[MusicPlaylist] Pause_ALL failed: {ex.Message}");
            }
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
            // 先 Resume_ALL（内容声），再 Resume_Music；曲已 Pause_Music 非 Started，
            // 不会踩「活实例 + Started 禁 Resume_ALL」那条 Ended 切歌坑。
            try
            {
                WanosEngine.ResumeAll();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[MusicPlaylist] Resume_ALL failed: {ex.Message}");
            }
            if (_musicSerialId >= 0 && GameEntry.Sound != null)
                GameEntry.Sound.ResumeSound(_musicSerialId);
            try
            {
                WanosInteraction.PostMusicEvent("Resume_Music");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[MusicPlaylist] Resume_Music failed: {ex.Message}");
            }
            IsPlaying = CurrentAlbumIndex >= 0 && CurrentTrackIndex >= 0 && !_stoppedByUser;
            if (WanosEngine.Unmute("music-resume"))
            {
                IslandAudioSession.ClearYieldHold();
                IslandAudioSession.NoteAudibleCommitted();
                IslandAudioSession.Instance?.SetContentAudioPaused(false);
            }
            else
                Debug.LogWarning("[MusicPlaylist] unmute denied on resume — keep content audio");
#if UNITY_ANDROID && !UNITY_EDITOR
            // 定时取消依赖此路径出声：原生 pauseForTimerEnd 已 mute，须补抢焦点 + unmute。
            BoluoAndroidEngineNative.EnsureAudibleForUnityPlayback();
#endif
            NowPlayingService.PublishFromPlaylist();
        }

        /// <summary>
        /// 后台负一对账：原生已 Pause_Music(+Pause_ALL[/mute])，只闩 C# 播放态与内容声闸，
        /// 禁止再 Post（避免与原生双发）。Unity 醒着时 Ambience Evaluate 不得重起音效。
        /// </summary>
        public void ApplyNativePausedLatch(bool paused)
        {
            if (paused)
            {
                IsPlaying = false;
                IslandAudioSession.Instance?.SetContentAudioPaused(true);
                return;
            }

            IslandAudioSession.Instance?.SetContentAudioPaused(false);
            IsPlaying = CurrentAlbumIndex >= 0 && CurrentTrackIndex >= 0 && !_stoppedByUser;
        }

        /// <summary>
        /// 定时结束弹窗「取消」/「重复定时」/关窗：恢复音效闸并硬起重播当前曲。
        /// <para>
        /// 真机到点走 Pause_Music+Pause_ALL+mute 后，Resume_Music 经常拉不起 BGM
        /// （音效 Resume_ALL / ContentAudio 却正常）——不赌 Pause 态，直接 PlayIndex restart。
        /// 禁止改 Music/Effect 用户音量：到点只 Pause，此处只 Resume + 重推原音量。
        /// </para>
        /// </summary>
        public void ResumeAfterTimerEnd(string reason = "timer-end")
        {
            NowPlayingService.NotePausedForTimerEnd(false);
            NowPlayingService.NoteUserPaused(false);
#if UNITY_ANDROID && !UNITY_EDITOR
            BoluoAndroidEngineNative.MarkUserPausedFromUnity(false);
#endif
            try
            {
                WanosEngine.ResumeAll();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[MusicPlaylist] Resume_ALL failed: {ex.Message}");
            }
            IslandAudioSession.ClearYieldHold();
            IslandAudioSession.Instance?.SetContentAudioPaused(false);
            // 到点曾 mute：重推用户音量，避免引擎 RTPC 停留在护栏衰减/默认值被听成「音量被调了」。
            PushVolumesToEngine();

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
#if UNITY_ANDROID && !UNITY_EDITOR
            // PlayIndex 在 AudibleCommitted 时会跳过 EnsureAudible；定时到点 mute 后必须再抢一次。
            BoluoAndroidEngineNative.EnsureAudibleForUnityPlayback();
#endif
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

            // agent 还在 → Resume_ALL（负一屏/到点 Pause_ALL）+ GF ResumeSound + Resume_Music。
            try
            {
                WanosEngine.ResumeAll();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[MusicPlaylist] Resume_ALL failed: {ex.Message}");
            }
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
            if (WanosEngine.Unmute("music-restore"))
            {
                IslandAudioSession.ClearYieldHold();
                IslandAudioSession.NoteAudibleCommitted();
                IslandAudioSession.Instance?.SetContentAudioPaused(false);
            }
            else
                Debug.LogWarning("[MusicPlaylist] unmute denied on restore — keep content audio");
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
                // StopSound → agent.Stop 也会 Post Stop_Music；随后 HardStop 再统一发一次。
                GameEntry.Sound.StopSound(_musicSerialId);
                _musicSerialId = -1;
            }
            IsPlaying = false;
        }

        bool TryStartTrack(MusicTrack track, int generation = -1)
        {
            if (generation >= 0 && generation != _playGeneration)
                return false;

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

            if (IslandAudioSession.IsYieldHeld)
            {
                Debug.Log("[MusicPlaylist] skip play — audio yielded");
                PublishNowPlaying();
                return false;
            }

            if (generation >= 0 && generation != _playGeneration)
                return false;

            _musicSerialId = GameEntry.Sound.PlaySound(asset, MusicGroup);
            if (generation >= 0 && generation != _playGeneration)
            {
                // 连点抢先：刚起的 agent 立刻清掉，避免叠在下一首上。
                StopCurrentMusic();
                WanosSoundAgentHelper.SilenceAllMusicAgentsLocal();
                return false;
            }

            IsPlaying = _musicSerialId >= 0;
            if (IsPlaying)
            {
                // 在 NoteAudibleCommitted 之前采样：首播仍要 EnsureAudible，切歌跳过。
                bool needEnsureAudible = !IslandAudioSession.AudibleCommitted
                    || IslandAudioSession.IsYieldHeld;
                if (WanosEngine.Unmute("music-play"))
                    IslandAudioSession.NoteAudibleCommitted();
                else
                {
                    // 切歌 Unmute 失败只记日志，不 HoldYield 闩住整岛声景（雨/环境）。
                    Debug.LogWarning("[MusicPlaylist] unmute denied on play — keep content audio");
                }
                // Push user volume again after PlaySound (agent may have written RTPC too).
                PushVolumesToEngine();
#if UNITY_ANDROID && !UNITY_EDITOR
                // 已在前台出声时的切歌不刷 EnsureAudible，避免连点取消半截 unmute ramp。
                if (needEnsureAudible)
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
            // 用户滑条到 0：不要把 RTPC 打成绝对 0（虚杀 → Ended → 重播）。虹膜 Duck 仍推 0。
            if (MusicVolume <= SilentMusicVolume01
                && IslandAudioSession.DuckLevel01 > SilentMusicVolume01
                && music < MusicVolumeKeepAlive01)
            {
                music = MusicVolumeKeepAlive01;
            }

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
