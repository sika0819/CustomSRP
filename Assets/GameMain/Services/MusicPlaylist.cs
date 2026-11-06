using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Game.BoluoApi;
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

        /// <summary>
        /// <c>Play_*</c> 已 Post、引擎尚未 Playing。此窗口 Stop 会被吞，不能再 Post 下一首。
        /// </summary>
        bool _voiceCreateInFlight;

        int _voiceCreateGeneration;
        ulong _voiceCreateId;

        /// <summary>Break_Music already posted for this voice (ListLoop / Shuffle).</summary>
        bool _breakPostedForVoice;

        /// <summary>
        /// 用户主动停过曲目（Stop）。回前台恢复时不能把停掉的曲目重新拉起来，
        /// 但「暂停」要恢复（需求：不管什么暂停状态，回前台都恢复音乐）。
        /// </summary>
        bool _stoppedByUser;

        /// <summary>硬续已排队、Playing 还没到。这段时间回前台不得再 Pause_ALL。</summary>
        bool _hardReplayScheduled;

        /// <summary>硬续进行中：静音原因留到这次 voice 进入 Playing。</summary>
        bool _holdSilenceUntilPost;

        /// <summary>PlaySound 已返回编号，还在等引擎 Playing。编号不算已经出声。</summary>
        bool _hardReplayAwaitingPlaying;

        /// <summary>这轮硬续开始时的静音原因。失败时写回去，不把到点改成用户暂停。</summary>
        SilenceReason _hardReplayReason = SilenceReason.UserPause;

        /// <summary>硬续已排上、声音还没进入 Playing。</summary>
        public bool HardReplayPending =>
            _hardReplayScheduled || _holdSilenceUntilPost || _hardReplayAwaitingPlaying;

        /// <summary>再滑时看这轮硬续该等、该发，还是闸丢了要重试。</summary>
        public PendingReplayNudge PeekPendingReplay()
        {
            bool waitingToPost = !_hardReplayAwaitingPlaying
                                 && (_hardReplayScheduled || _holdSilenceUntilPost);
            return SilenceResumePolicy.ClassifyPendingReplay(
                _hardReplayAwaitingPlaying, waitingToPost, _voiceCreateInFlight);
        }

        /// <summary>闸已经开、硬续还没 Post：现在发出去。不走对账。</summary>
        public void ContinuePendingHardReplay() => ContinueHardReplayAfterGate();

        /// <summary>等 Playing 但闸没了：写回静音原因，让这次滑动再硬续。</summary>
        public void AbandonStuckHardReplay() => NoteHardReplayMissed("gate-lost");

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
        /// 只更新索引：引擎里已经是原生起播的那一轨，不能再发一次 Play 事件（避免中途掐断）。
        /// <paramref name="paused"/> 为 true 时不得强行 <see cref="IsPlaying"/>=true（对齐 PlaybackGate）。
        /// </summary>
        public void AdoptTrack(int albumIndex, int trackIndex, bool paused = false)
        {
            if (albumIndex < 0 || trackIndex < 0)
                return;

            bool albumChanged = albumIndex != CurrentAlbumIndex;
            bool trackChanged = albumChanged || trackIndex != CurrentTrackIndex;
            bool playingChanged = IsPlaying != (!paused && !_stoppedByUser);

            // 后台负一屏切过来的曲也算已听（对齐 Boluo MarkCurrentMusicPlayedIfAny）。
            MarkTrackHeard(albumIndex, trackIndex);
            CurrentAlbumIndex = albumIndex;
            CurrentTrackIndex = trackIndex;
            IsPlaying = !paused && !_stoppedByUser;
            if (!paused)
                _stoppedByUser = false;

            BoluoPlatformMusicBridge.NoteUnityMusicContext(
                PlayMode, CurrentAlbumIndex, CurrentTrackIndex);

            EnsureVoiceHook();
            if (WanosSoundAgentHelper.TryGetActiveMusicVoice(out ulong agentVoice) && agentVoice != 0)
            {
                AttachMusicVoice(agentVoice);
                Log.Info(
                    $"[MusicPlaylist] AdoptTrack album={albumIndex} track={trackIndex}"
                    + $" paused={(paused ? 1 : 0)} rebind voice={agentVoice} mode={PlayMode}");
            }
            else if (_musicVoiceId != 0 && IsListAdvanceMode(PlayMode) && !_breakPostedForVoice)
            {
                TryPostBreak("adopt-keep-voice");
            }
            else
            {
                Log.Info(
                    $"[MusicPlaylist] AdoptTrack album={albumIndex} track={trackIndex}"
                    + $" paused={(paused ? 1 : 0)} voice={_musicVoiceId} mode={PlayMode}");
            }

            // 与 PlayIndex 同通道：列表高亮 / 播放栏曲名必须跟会话，禁止静默改索引。
            if (trackChanged || playingChanged)
                TrackChanged?.Invoke();
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
                _instance.ClearVoiceCreateGate();
                _instance.IsPlaying = false;
                _instance._stoppedByUser = false;
            }
            NowPlayingService.CleanupAfterLeaveIsland();
        }

        /// <summary>
        /// 离岛：解绑 voice、作废进行中的切歌兜底，并保持「用户已停」。
        /// 不要走 <see cref="ResetSession"/>：那里会把 <c>_stoppedByUser</c> 抬回 false，
        /// 回前台 <c>TrySoftResume</c> 会把曲目拉起来。
        /// </summary>
        public void DisarmAfterLeave()
        {
            UnhookVoiceStates();
            _playGeneration++;
            ClearVoiceCreateGate();
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
        /// Play a specific track. One song at a time.
        /// 上一首 <c>Play_*</c> 已发出但引擎还没 Playing 时，只记下这次点选，不再 Post。
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

            // 作废在途延帧起播。createInst 窗口内 Stop 会被吞，不能再 HardStop+Post。
            // 硬续撞上这个窗口：保持已排队，等闸打开再 Post，不要当成失败。
            bool holdSilence = _holdSilenceUntilPost;
            if (!holdSilence)
                _hardReplayScheduled = false;
            int gen = ++_playGeneration;
            _stoppedByUser = false;
            if (!holdSilence && SilenceResumePolicy.WantsPlayback(PlaybackGate.Silence))
                IsPlaying = true;
            if (MusicPlayOverlapPolicy.ShouldDeferPost(_voiceCreateInFlight))
            {
                if (holdSilence)
                    _hardReplayScheduled = true;
                Log.Info(
                    $"[MusicPlaylist] defer post until Playing album={albumIndex} track={trackIndex} gen={gen}");
                TrackChanged?.Invoke();
                NowPlayingService.PublishFromPlaylist();
                return;
            }

            HardStopMusicVoices(markStoppedByUser: false);
            // HardStop 会把 IsPlaying 拉回 false。普通点歌先乐观标记，避免列表闪回「未播放」。
            // 硬续要等真正 Post 成功才算在播，否则滑动续播闩会被提前清掉。
            _stoppedByUser = false;
            if (!holdSilence && SilenceResumePolicy.WantsPlayback(PlaybackGate.Silence))
                IsPlaying = true;
            if (holdSilence)
                _hardReplayScheduled = true;
            StartTrackDeferred(gen, track.Value).Forget();
            TrackChanged?.Invoke();
            // 索引已提交：先刷负一屏曲名/计划，等 TryStartTrack 再 PublishPlayingForced 钉播放态。
            NowPlayingService.PublishFromPlaylist();
        }

        /// <summary>
        /// Stop current BGM (GF agent + engine <c>Stop_Music</c>). Used by track switch / user stop.
        /// Preset apply must use <see cref="ForceRetargetAtLanding"/> — do not stop BGM across iris.
        /// </summary>
        public void Stop(bool publish = true)
        {
            // 先闩用户停，避免 Playing 回调在 HardStop 前把曲子又拉起来。
            _holdSilenceUntilPost = false;
            _hardReplayScheduled = false;
            _hardReplayAwaitingPlaying = false;
            _stoppedByUser = true;
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
            // 停播即放行闸，避免超时兜底把刚停的曲子又拉起来。
            ClearVoiceCreateGate();
            // agent.Stop 会 Post 一次 Stop_Music；有 serial 时此处只再补 Break，避免双 Stop。
            bool stopPostedByAgent = _musicSerialId >= 0 && GameEntry.Sound != null;
            StopCurrentMusic();
            WanosSoundAgentHelper.SilenceAllMusicAgentsLocal();
            _musicVoiceId = 0;
            _breakPostedForVoice = false;
            _voiceGeneration++;
            if (markStoppedByUser)
                _stoppedByUser = true;
            try
            {
                if (!stopPostedByAgent)
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
            // 对齐原生 forceStop 后 ~50ms，让引擎消化 Stop/Break 再建 Sequence。
            await UniTask.Yield(PlayerLoopTiming.Update);
            if (generation != _playGeneration)
                return;
            await UniTask.Delay(50, DelayType.Realtime);
            if (generation != _playGeneration)
                return;
            if (!TryStartTrack(track, generation))
            {
                if (generation == _playGeneration)
                    NoteHardReplayMissed("post-skipped");
            }
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

        /// <summary>
        /// <b>仅</b>负一屏 / remote 手动切歌（<see cref="NowPlayingService"/>）。
        /// 本专辑 ±1 取模；邻接未下载完 → 重播当前；已就绪 → 切过去。
        /// 禁止给音乐列表用：列表未下载点行仍走 <c>MusicForm.TriggerMusicDownload</c>→下完
        /// <see cref="PlayIndex"/>，岛内按钮走 <see cref="Next"/>/<see cref="Prev"/>。
        /// </summary>
        public void SkipAdjacentInAlbum(int direction)
        {
            if (!MusicCatalog.IsReady || MusicCatalog.AlbumCount == 0)
                return;
            if (direction == 0)
                direction = 1;

            var tracks = MusicCatalog.GetTracks(CurrentAlbumIndex);
            int next = MusicAlbumNav.ResolveManualSkipIndex(
                tracks.Count,
                CurrentTrackIndex,
                direction,
                i => IsTrackBankLocallyReady(tracks[i]));
            if (next < 0)
                return;
            PlayIndex(CurrentAlbumIndex, next, restart: true);
        }

        /// <summary>与列表 / 传输计划一致：盘上 <see cref="BankDownloader.IsLocalReady"/> 才可切播。</summary>
        static bool IsTrackBankLocallyReady(MusicTrack track)
        {
            if (string.IsNullOrEmpty(track.BankName))
                return false;
            return BankDownloader.IsLocalReady(track.BankName);
        }

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

            // Advance within the current album（列表环绕；单曲到边界停；单曲专辑重播）。
            var tracks = MusicCatalog.GetTracks(CurrentAlbumIndex);
            if (tracks.Count == 0)
                return;

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
            if (tracks.Count == 0)
                return;

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
            if (instanceId == 0)
                return;

            if (state == WanosInstanceState.Playing)
            {
                HandleVoicePlaying(instanceId);
                return;
            }

            if (instanceId != _musicVoiceId)
                return;

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

        void HandleVoicePlaying(ulong instanceId)
        {
            bool matches = instanceId == _musicVoiceId;
            // voiceId 尚未回填时只认当前绑定；禁止把任意 Playing 都当成 in-flight。
            bool isCreateVoice = _voiceCreateInFlight
                && (_voiceCreateId != 0 ? _voiceCreateId == instanceId : matches);
            int postedGen = isCreateVoice
                ? _voiceCreateGeneration
                : (matches ? _playGeneration : -1);
            // 这次硬续的 voice 真正 Playing 了，才清暂停闩。编号到手之前不算。
            if (_hardReplayAwaitingPlaying
                && isCreateVoice
                && postedGen == _playGeneration
                && SilenceResumePolicy.ShouldCommitHardReplay(voiceEnteredPlaying: true))
            {
                CommitHardReplayPosted();
            }

            bool wantPlay = !_stoppedByUser
                && CurrentAlbumIndex >= 0
                && CurrentTrackIndex >= 0
                && SilenceResumePolicy.WantsPlayback(PlaybackGate.Silence);

            var disposition = MusicPlayOverlapPolicy.DecidePlaying(
                matches,
                isCreateVoice,
                postedGen,
                _playGeneration,
                _stoppedByUser,
                wantPlay);

            if (disposition == MusicPlayOverlapPolicy.PlayingDisposition.Continue)
            {
                ClearVoiceCreateGate();
                // Sequence createInst 已落地：放开远程 skip in-flight。
                NowPlayingService.NoteSkipSettled();
                // ListLoop / Shuffle: Break → 本曲播完进 Ended → Next（不中途掐断）。
                // Single: 不 Break → 引擎自循环。
                if (IsListAdvanceMode(PlayMode))
                    TryPostBreak("Playing");
                return;
            }

            if (disposition == MusicPlayOverlapPolicy.PlayingDisposition.StopThenPlayCurrent)
            {
                Log.Info(
                    $"[MusicPlaylist] Playing superseded voice={instanceId} → stop then play"
                    + $" {CurrentAlbumIndex}/{CurrentTrackIndex}");
                ClearVoiceCreateGate();
                HardStopMusicVoices(markStoppedByUser: false);
                if (_stoppedByUser || !SilenceResumePolicy.WantsPlayback(PlaybackGate.Silence))
                    return;
                var track = MusicCatalog.GetTrack(CurrentAlbumIndex, CurrentTrackIndex);
                if (track == null)
                    return;
                int gen = ++_playGeneration;
                _stoppedByUser = false;
                IsPlaying = true;
                StartTrackDeferred(gen, track.Value).Forget();
                TrackChanged?.Invoke();
                NowPlayingService.PublishFromPlaylist();
                return;
            }

            // 硬续还占着闸：不是这次 voice 的 Playing 只停那一条，不能清闸。
            // 清掉之后超时会直接退出，等待标志却还在，再滑就永远被挡死。
            if (HardReplayPending && _voiceCreateInFlight && !isCreateVoice)
            {
                StopStaleVoice(instanceId);
                return;
            }

            // StopOnly：用户已停，或迟到 Playing 不属于当前会话。
            ClearVoiceCreateGate();
            if (matches || isCreateVoice)
                HardStopMusicVoices(markStoppedByUser: _stoppedByUser);
            else
                StopStaleVoice(instanceId);

            ContinueHardReplayAfterGate();
        }

        void StopStaleVoice(ulong instanceId)
        {
            try
            {
                WanosInteraction.StopVoice(instanceId, 0);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[MusicPlaylist] StopVoice stale Playing failed: {ex.Message}");
            }
        }

        /// <summary>上一条 createInst 结束或超时后，把还在等的硬续发出去。</summary>
        void ContinueHardReplayAfterGate()
        {
            if (_hardReplayAwaitingPlaying)
                return;
            if (!_hardReplayScheduled && !_holdSilenceUntilPost)
                return;
            if (MusicPlayOverlapPolicy.ShouldDeferPost(_voiceCreateInFlight))
                return;
            if (CurrentAlbumIndex < 0 || CurrentTrackIndex < 0)
                return;
            PlayIndex(CurrentAlbumIndex, CurrentTrackIndex, restart: true);
        }

        void NoteVoiceCreateInFlight(int generation, ulong voiceId)
        {
            _voiceCreateInFlight = true;
            _voiceCreateGeneration = generation;
            _voiceCreateId = voiceId;
            ScheduleVoiceCreateTimeout(generation, voiceId).Forget();
        }

        void ClearVoiceCreateGate()
        {
            _voiceCreateInFlight = false;
            _voiceCreateGeneration = 0;
            _voiceCreateId = 0;
        }

        async UniTaskVoid ScheduleVoiceCreateTimeout(int generation, ulong voiceId)
        {
            try
            {
                await UniTask.Delay(
                    MusicPlayOverlapPolicy.VoiceCreateTimeoutMs,
                    DelayType.Realtime);
            }
            catch
            {
                return;
            }

            if (!_voiceCreateInFlight)
                return;
            if (_voiceCreateGeneration != generation)
                return;
            if (_voiceCreateId != 0 && voiceId != 0 && _voiceCreateId != voiceId)
                return;

            Log.Warning(
                $"[MusicPlaylist] Playing missing after {MusicPlayOverlapPolicy.VoiceCreateTimeoutMs}ms"
                + $" — release gate gen={generation} voice={voiceId}");
            bool awaitingThis = _hardReplayAwaitingPlaying;
            bool waitingToPost = _hardReplayScheduled && !_hardReplayAwaitingPlaying;
            ClearVoiceCreateGate();
            if (awaitingThis)
            {
                NoteHardReplayMissed("playing-timeout");
                return;
            }

            if (waitingToPost)
            {
                ContinueHardReplayAfterGate();
                return;
            }

            if (_stoppedByUser
                || !SilenceResumePolicy.TimeoutMayReplay(PlaybackGate.Silence))
                return;
            if (CurrentAlbumIndex < 0 || CurrentTrackIndex < 0)
                return;

            // 闸失效后硬切当前索引，避免永远 defer；若原 voice 仍在，HardStop 此刻已可停。
            PlayIndex(CurrentAlbumIndex, CurrentTrackIndex, restart: true);
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
            PauseEngine();
            // 调用方已经写好静音原因。这里只拦住原生自动续播，不再把 TimerEnd 改成用户暂停。
#if UNITY_ANDROID && !UNITY_EDITOR
            BoluoAndroidEngineNative.MarkUserPausedFromUnity(true);
#endif
            NowPlayingService.PublishFromPlaylist();
        }

        /// <summary>
        /// 音乐和音效一起暂停，保留曲目实例。不改 <see cref="PlaybackGate"/>，也不写原生 userPaused。
        /// </summary>
        public void PauseEngine()
        {
            _holdSilenceUntilPost = false;
            _hardReplayScheduled = false;
            _hardReplayAwaitingPlaying = false;
            _playGeneration++;
            if (_musicSerialId >= 0 && GameEntry.Sound != null)
            {
                GameEntry.Sound.PauseSound(_musicSerialId);
            }
            else
            {
                try
                {
                    WanosInteraction.PostMusicEvent("Pause_Music");
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[MusicPlaylist] Pause_Music failed: {ex.Message}");
                }
            }

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
        }

        public void Resume()
        {
            // 只发引擎续播，不改暂停闩、不解 mute。谁能出声由 AudioAppLifecycle 决定。
            TrySoftResume("resume");
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

        /// <summary>用户暂停后续播：从暂停处续音乐和音效，不从头再播。</summary>
        public void ResumeFromUserPause(string reason) => AudioAppLifecycle.ResumePaired(reason);

        /// <summary>
        /// 硬起重播当前曲。返回 <see cref="ResumeCommit.WaitingForPost"/> 时闩还在，下一滑可再试。
        /// </summary>
        public ResumeCommit BeginHardReplay(string reason)
        {
            if (_hardReplayScheduled || _holdSilenceUntilPost || _hardReplayAwaitingPlaying)
                return ResumeCommit.WaitingForPost;

            _hardReplayReason = PlaybackGate.Silence == SilenceReason.TimerEnd
                ? SilenceReason.TimerEnd
                : PlaybackGate.Silence == SilenceReason.None
                    ? SilenceReason.UserPause
                    : PlaybackGate.Silence;

            try
            {
                WanosEngine.ResumeAll();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[MusicPlaylist] Resume_ALL failed: {ex.Message}");
            }

            IslandAudioSession.ClearYieldHold();
            PushVolumesToEngine();

            if (!HasRestorableTrack)
            {
                Log.Info($"[MusicPlaylist] hard replay ({reason}): no track, SFX gate only");
                CommitHardReplayPosted();
#if UNITY_ANDROID && !UNITY_EDITOR
                BoluoAndroidEngineNative.EnsureAudibleForUnityPlayback();
#endif
                NowPlayingService.PublishFromGate();
                return ResumeCommit.Done;
            }

            if (!WanosEngine.IsPlaybackEnabled)
                WanosEngine.EnablePlayback(reason);

            _holdSilenceUntilPost = true;
            Log.Info($"[MusicPlaylist] hard replay ({reason}): schedule {CurrentAlbumIndex}/{CurrentTrackIndex}");
            PlayIndex(CurrentAlbumIndex, CurrentTrackIndex, restart: true);
            bool createBlocked = MusicPlayOverlapPolicy.ShouldDeferPost(_voiceCreateInFlight);
            if (createBlocked && !_hardReplayScheduled)
                _hardReplayScheduled = true;
            ResumeCommit commit = SilenceResumePolicy.ClassifyHardReplaySchedule(
                createBlocked, _hardReplayScheduled);
            if (commit != ResumeCommit.WaitingForPost)
            {
                NoteHardReplayMissed("not-scheduled");
                return ResumeCommit.Failed;
            }

            return ResumeCommit.WaitingForPost;
        }

        /// <summary>定时结束弹窗「取消」/「重复定时」/关窗：从暂停处续音乐和音效。</summary>
        public void ResumeAfterTimerEnd(string reason = "timer-end")
        {
            AudioAppLifecycle.ResumePaired(reason);
        }

        /// <summary>
        /// 系统打断后的软续：agent 还在就 Resume_Music，不 Stop+Play，也不改暂停闩。
        /// agent 已丢返回 false，调用方再硬起一次。
        /// </summary>
        public bool TrySoftResume(string reason)
        {
            if (!HasRestorableTrack)
                return true;
            if (!WanosEngine.IsPlaybackEnabled)
                return false;
            if (_musicSerialId < 0 || GameEntry.Sound == null)
                return false;

            try
            {
                WanosEngine.ResumeAll();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[MusicPlaylist] Resume_ALL failed: {ex.Message}");
            }

            GameEntry.Sound.ResumeSound(_musicSerialId);
            try
            {
                // 与原生 resumePaired 共用短窗口。第二发 Resume_Music 打在已续上的曲子上会 Ended。
                if (!BoluoAndroidEngineNative.ClaimVoiceResume("Resume_Music"))
                {
                    Log.Info($"[BoluoAudioFg] Resume_Music coalesced ({reason})");
                }
                else
                {
                    WanosInteraction.PostMusicEvent("Resume_Music");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[MusicPlaylist] Resume_Music failed: {ex.Message}");
            }

            IsPlaying = true;
            Log.Info($"[MusicPlaylist] soft resume ({reason}): track {CurrentAlbumIndex}/{CurrentTrackIndex}");
            return true;
        }

        void CommitHardReplayPosted()
        {
            _holdSilenceUntilPost = false;
            _hardReplayScheduled = false;
            _hardReplayAwaitingPlaying = false;
            NowPlayingService.NotePausedForTimerEnd(false);
            NowPlayingService.NoteUserPaused(false);
            IslandAudioSession.Instance?.RestoreContentAudio("hard-replay");
            AudioAppLifecycle.NoteUserResumeCommitted();
        }

        void NoteHardReplayMissed(string why)
        {
            if (!_holdSilenceUntilPost && !_hardReplayScheduled && !_hardReplayAwaitingPlaying)
                return;
            SilenceReason reason = _hardReplayReason;
            _holdSilenceUntilPost = false;
            _hardReplayScheduled = false;
            _hardReplayAwaitingPlaying = false;
            IsPlaying = false;
            IslandAudioSession.Instance?.SetContentAudioPaused(true);
            if (reason == SilenceReason.TimerEnd || NowPlayingService.PausedForTimerEnd)
                PlaybackGate.SetSilence(SilenceReason.TimerEnd, "hard-replay-miss");
            else if (reason == SilenceReason.SystemInterrupt)
                PlaybackGate.SetSilence(SilenceReason.SystemInterrupt, "hard-replay-miss");
            else
                NowPlayingService.NoteUserPaused(true);
            if (PlaybackGate.Silence == SilenceReason.UserPause)
                AudioAppLifecycle.ArmSwipeResumeAfterMissedReplay();
#if UNITY_ANDROID && !UNITY_EDITOR
            BoluoAndroidEngineNative.MarkUserPausedFromUnity(true);
#endif
            Log.Info($"[MusicPlaylist] hard replay not posted ({why}) — keep silence");
            NowPlayingService.PublishFromGate();
        }

        // -----------------------------------------------------------------------
        // Internal helpers
        // -----------------------------------------------------------------------

        void StopCurrentMusic()
        {
            if (_musicSerialId >= 0 && GameEntry.Sound != null)
            {
                // StopSound → agent.Stop 会 Post Stop_Music；HardStop 有 serial 时只再补 Break。
                GameEntry.Sound.StopSound(_musicSerialId);
                _musicSerialId = -1;
            }
            IsPlaying = false;
        }

        bool TryStartTrack(MusicTrack track, int generation = -1)
        {
            if (generation >= 0 && generation != _playGeneration)
                return false;
            // 上一首 Play 还在 createInst：这次延帧起播作废，等 Playing / 超时再播当前索引。
            if (MusicPlayOverlapPolicy.ShouldDeferPost(_voiceCreateInFlight))
                return false;

            // 系统打断 / 用户暂停：禁止再 Post、禁止 Unmute。硬续自己会先抬 _holdSilenceUntilPost。
            if (!_holdSilenceUntilPost
                && !SilenceResumePolicy.WantsPlayback(PlaybackGate.Silence))
            {
                Debug.Log("[MusicPlaylist] skip play — silenced");
                PublishNowPlaying();
                return false;
            }

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

            if (_holdSilenceUntilPost)
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                BoluoAndroidEngineNative.MarkUserPausedFromUnity(false);
#endif
            }

            _musicSerialId = GameEntry.Sound.PlaySound(asset, MusicGroup);
            ulong postedVoice = _musicVoiceId;
            if (generation >= 0 && generation != _playGeneration)
            {
                // 连点抢先：刚起的 agent 立刻清掉。Stop 若被吞，记下 voice，Playing 时再停。
                StopCurrentMusic();
                WanosSoundAgentHelper.SilenceAllMusicAgentsLocal();
                NoteVoiceCreateInFlight(generation, postedVoice);
                return false;
            }

            IsPlaying = _musicSerialId >= 0;
            bool replaying = _holdSilenceUntilPost;
            if (replaying)
            {
                if (!IsPlaying
                    || !SilenceResumePolicy.ShouldCommitHardReplay(voiceEnteredPlaying: false))
                {
                    if (!IsPlaying)
                    {
                        NoteHardReplayMissed("no-serial");
                        return false;
                    }

                    // 编号只表示排上了。暂停闩留到这次 voice 进入 Playing。
                    _hardReplayAwaitingPlaying = true;
                }
                else
                {
                    CommitHardReplayPosted();
                }
            }

            if (IsPlaying)
            {
                NoteVoiceCreateInFlight(generation >= 0 ? generation : _playGeneration, postedVoice);
                bool needEnsureAudible = replaying
                    || !IslandAudioSession.AudibleCommitted
                    || IslandAudioSession.IsYieldHeld;
                bool allowAudible = replaying
                    || SilenceResumePolicy.WantsPlayback(PlaybackGate.Silence);
                if (allowAudible && WanosEngine.Unmute("music-play"))
                    IslandAudioSession.NoteAudibleCommitted();
                else if (!allowAudible)
                    Debug.Log("[MusicPlaylist] skip unmute — silenced");
                else
                {
                    // 切歌 Unmute 失败只记日志，不 HoldYield 闩住整岛声景（雨/环境）。
                    Debug.LogWarning("[MusicPlaylist] unmute denied on play — keep content audio");
                }
                // Push user volume again after PlaySound (agent may have written RTPC too).
                PushVolumesToEngine();
#if UNITY_ANDROID && !UNITY_EDITOR
                // 已在前台出声时的切歌不刷 EnsureAudible，避免连点取消半截 unmute ramp。
                if (needEnsureAudible && allowAudible)
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
