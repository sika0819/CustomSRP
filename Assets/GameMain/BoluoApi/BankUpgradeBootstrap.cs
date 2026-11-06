using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using UnityGameFramework.Runtime;

namespace Game.BoluoApi
{
    /// <summary>
    /// deviceToken → userToken → upgrade/bank（musicLists + banks，等同 MusicLists / ProjectMetadata）
    /// 先落目录供揭罩前 UI；揭罩起播后再走 Critical/Rest 静默下载。
    /// </summary>
    public static class BankUpgradeBootstrap
    {
        /// <summary>
        /// StreamingAssets 内置基础 bank（事件路由依赖）。进引擎后立刻灌入，不走 HTTP。
        /// </summary>
        public static readonly string[] EssentialLocalBankNames =
        {
            "Common_Init",
            "Common_Music",
        };

        /// <summary>Default BGM + spawn ambience (aligned with MusicPlaylist / AmbienceEventCatalog).</summary>
        public static readonly string[] CriticalBankNames =
        {
            "Music1_1",
            "Ocean_Calm_Quad",
            "River_Quad",
            "River_ADD",
            "Boluodao_Forest",
            "Boluodao_Grassland",
            "Boluodao_Plateau",
            "Lake_Quad",
            "Campfire",
            "Waterfall_Big_Quad",
            "Waterfall_Small_Quad",
            "Wind",
            "Rain",
            "Rain_Water",
        };

        static bool _started;
        static int _runId;
        static bool _fatal;
        static bool _silentDeferred;
        /// <summary>
        /// 后台 Rest §4.6 已被用户关掉（「稍后」/ 蒙层）：本岛不再重弹，改为静默 Rest。
        /// 未关过则开关开时必须先弹出确认（禁止未关窗就静默）。
        /// </summary>
        static bool _restWifiConfirmDeclined;
        static UniTaskCompletionSource _catalogTcs = new UniTaskCompletionSource();
        static UniTaskCompletionSource _criticalTcs = new UniTaskCompletionSource();
        /// <summary>揭罩起播后再放行静默 bank 下载；目录更新不挡在这道门后。</summary>
        static UniTaskCompletionSource _afterUncoverTcs = new UniTaskCompletionSource();

        public static UniTask WaitCatalogAsync() => _catalogTcs.Task;
        public static UniTask WaitCriticalAsync() => _criticalTcs.Task;

        /// <summary>
        /// 等 upgrade/bank 把 banks / musicLists 落到目录（或失败回落本地）。
        /// 揭罩 / 音乐列表前应 await，避免先显示旧本地 JSON。
        /// </summary>
        public static async UniTask WaitCatalogWithTimeoutAsync(float timeoutSeconds = 20f)
        {
            UniTask catalog = _catalogTcs.Task;
            if (catalog.Status.IsCompleted())
                return;
            if (timeoutSeconds <= 0f)
            {
                await catalog;
                return;
            }

            await UniTask.WhenAny(
                catalog,
                UniTask.Delay(TimeSpan.FromSeconds(timeoutSeconds), ignoreTimeScale: true));
        }

        /// <summary>
        /// 揭罩并完成起播门禁后调用：放行与以往相同的静默 Rest / Critical bank 下载。
        /// </summary>
        public static void NotifyAfterUncover()
        {
            _afterUncoverTcs.TrySetResult();
        }

        static void ResetAfterUncoverGate()
        {
            _afterUncoverTcs.TrySetResult();
            _afterUncoverTcs = new UniTaskCompletionSource();
        }

        static async UniTask WaitAfterUncoverAsync(int runId)
        {
            UniTask gate = _afterUncoverTcs.Task;
            if (!gate.Status.IsCompleted())
            {
                Log.Info("[BankUpgradeBootstrap] wait after uncover before bank downloads");
                await gate;
            }

            if (runId >= 0 && runId != _runId)
                return;
        }

        /// <summary>
        /// 后台弹窗关（或本岛已关 §4.6）：区域门跳过申请窗、优先插队。
        /// </summary>
        public static bool IsSilentBackgroundRest =>
            Game.BackgroundDownloadPolicy.IsSilentBackgroundMode(
                BoluoAppLaunchCache.IsWifiDownloadConfirmEnabled(),
                _restWifiConfirmDeclined);

#if UNITY_EDITOR
        /// <summary>单测：模拟本岛已关过 §4.6。</summary>
        public static void EditorSetRestWifiConfirmDeclined(bool declined) =>
            _restWifiConfirmDeclined = declined;
#endif

        public static void ResetSession()
        {
            _started = false;
            _fatal = false;
            _silentDeferred = false;
            _restWifiConfirmDeclined = false;
            _catalogTcs = new UniTaskCompletionSource();
            _criticalTcs = new UniTaskCompletionSource();
            ResetAfterUncoverGate();
            AudioEnginePaths.ResetSession();
            PresetBankCatalog.Clear();
            BankDownloader.ResetSession();
            Game.DownloadConsentSession.ResetForLeaveIsland();
        }

        /// <summary>离岛：允许下一次用户票落地后再 Start。不重跑进程级 ResetSession。</summary>
        public static void InvalidateAfterLeave()
        {
            _started = false;
            _runId++;
            _silentDeferred = false;
            _restWifiConfirmDeclined = false;
            ResetAfterUncoverGate();
            DownloadCellularGate.ResetForNewSession();
            Game.DownloadConsentSession.ResetForLeaveIsland();
        }

        public static void Start(bool loadIntoEngine = false)
        {
            if (_started)
                return;

            BoluoSession.LoadFromPrefs();
            if (string.IsNullOrEmpty(BoluoSession.UserToken))
            {
                Log.Info("[BankUpgradeBootstrap] skip start — userToken not ready");
                return;
            }

            _started = true;
            int run = _runId;
            RunAsync(loadIntoEngine, run).Forget();
        }

        /// <summary>
        /// Cue default <c>Music1_1</c> as soon as bank + catalog are ready.
        /// Does <b>not</b> wait for the full Critical wave (Wind/Rain/…) — that blocked
        /// Editor BGM for tens of seconds while Music1_1 was already on disk.
        /// </summary>
        public static async UniTask<bool> WaitCriticalThenCueAsync(float timeoutSeconds = 45f)
        {
            await UniTask.SwitchToMainThread();
            if (_fatal)
            {
                Log.Warning("[BankUpgradeBootstrap] critical cue skipped — fatal");
                return false;
            }

            await EnsureLocalBanksReadyAsync();
            // banks + musicLists 先齐，再 cue / 展示列表。
            await WaitCatalogWithTimeoutAsync(20f);
            await MusicCatalog.EnsureReadyAsync(8f);

            const string defaultBank = "Music1_1";
            float bankDeadline = UnityEngine.Time.realtimeSinceStartup + 8f;
            while (UnityEngine.Time.realtimeSinceStartup < bankDeadline)
            {
                // Device open first, then DSP (macOS wrapper requires playback device).
                if (!WanosAudio.WanosEngine.IsPlaybackEnabled)
                {
                    WanosAudio.WanosEngine.EnablePlayback("critical-cue-retry");
                    WanosAudio.WanosEngine.StartEngine("critical-cue-retry");
                    if (!IslandAudioSession.IsYieldHeld
                        && WanosAudio.WanosEngine.Unmute("critical-cue-retry"))
                        IslandAudioSession.NoteAudibleCommitted();
                }
                else
                    WanosAudio.WanosEngine.StartEngine("critical-cue-dsp");

                if (WanosAudio.WanosEngine.IsPlaybackEnabled
                    && (WanosAudio.WanosBankStore.IsLoaded(defaultBank)
                        || WanosAudio.WanosBankStore.Load(defaultBank)
                        || TryLoadBankFromDisk(defaultBank)))
                    break;
                if (!string.IsNullOrEmpty(BankLoadGate.LastBlockReason))
                {
                    Log.Warning(
                        $"[BankUpgradeBootstrap] default bank skipped bank={defaultBank} " +
                        $"reason={BankLoadGate.LastBlockReason}");
                    break;
                }
                await UniTask.Delay(100, DelayType.UnscaledDeltaTime);
            }

            if (GameEntry.Music != null)
            {
                // 退登 Cleanup 已 Disarm：禁止再 PlayIndex → EnsureAudible / PublishPlayingForced
                // 在 LoadScene(Login) 窗口重开闸拉 FGS（ForegroundServiceDidNotStartInTimeException）。
                if (!NowPlayingService.IsArmed)
                {
                    Log.Warning("[BankUpgradeBootstrap] critical cue skipped — NowPlaying not armed");
                    return false;
                }

                if (!MusicCatalog.IsReady || MusicCatalog.AlbumCount == 0)
                {
                    Log.Warning(
                        "[BankUpgradeBootstrap] critical cue skipped — MusicCatalog empty " +
                        $"(ready={MusicCatalog.IsReady} albums={MusicCatalog.AlbumCount})");
                }
                else
                {
                    // 正式账号有曲目存档则恢复；否则默认 Music1_1（勿用扁平索引）。
                    string cueBank = defaultBank;
                    int album = 0;
                    int track = 0;
                    bool restored = false;
                    if (BoluoGameSave.TryGetSavedMusicTrack(out string savedBank, out string savedEvent)
                        && !string.IsNullOrEmpty(savedBank))
                    {
                        if (!string.IsNullOrEmpty(savedEvent)
                            && MusicCatalog.TryFindByEventName(savedEvent, out album, out track))
                        {
                            cueBank = savedBank;
                            restored = true;
                        }
                        else if (MusicCatalog.TryFindByBankName(savedBank, out album, out track))
                        {
                            cueBank = savedBank;
                            restored = true;
                        }
                    }

                    if (!restored)
                    {
                        if (MusicCatalog.TryFindByBankName(defaultBank, out album, out track))
                            cueBank = defaultBank;
                        else
                        {
                            album = 0;
                            track = 0;
                        }
                    }

                    // 确保存档曲 bank 已加载再 Play
                    if (!string.Equals(cueBank, defaultBank, System.StringComparison.OrdinalIgnoreCase)
                        && !WanosAudio.WanosBankStore.IsLoaded(cueBank)
                        && !WanosAudio.WanosBankStore.Load(cueBank)
                        && !TryLoadBankFromDisk(cueBank))
                    {
                        Log.Warning(
                            $"[BankUpgradeBootstrap] saved bank not ready bank={cueBank} " +
                            $"reason={BankLoadGate.LastBlockReason ?? WanosAudio.WanosBankStore.LastLoadDetail} " +
                            $"— fallback {defaultBank}");
                        cueBank = defaultBank;
                        if (!MusicCatalog.TryFindByBankName(defaultBank, out album, out track))
                        {
                            album = 0;
                            track = 0;
                        }
                    }

                    GameEntry.Music.PlayIndex(album, track, restart: true);
                    Log.Info(
                        $"[BankUpgradeBootstrap] cue restored={restored} bank={cueBank} " +
                        $"album={album} track={track}");
                }

                bool criticalDone = _criticalTcs.Task.Status.IsCompleted();
                Log.Info(
                    $"[BankUpgradeBootstrap] critical cue criticalDone={criticalDone} " +
                    $"playback={WanosAudio.WanosEngine.IsPlaybackEnabled} " +
                    $"bankLoaded={WanosAudio.WanosBankStore.IsLoaded(defaultBank)} " +
                    $"albums={MusicCatalog.AlbumCount} " +
                    $"playing={GameEntry.Music.IsPlaying}");
            }
            else
            {
                Log.Warning("[BankUpgradeBootstrap] critical cue skipped — MusicPlaylist null");
            }

            // Optional: still observe Critical for callers; do not gate the cue on it.
            return await WaitCriticalWithTimeoutAsync(timeoutSeconds);
        }

        static bool TryLoadBankFromDisk(string bankName)
        {
            if (!BankLoadGate.TryResolveForEngine(bankName, out string path))
                return false;
            return WanosAudio.WanosBankStore.LoadFromPath(bankName, path);
        }

        /// <summary>Load Common_Init / Common_Music from disk when engine is up.</summary>
        public static void EnsureEssentialBanksLoaded()
        {
            if (!WanosAudio.WanosNative.Instance.IsCreated)
                return;
            for (int i = 0; i < EssentialLocalBankNames.Length; i++)
            {
                string name = EssentialLocalBankNames[i];
                if (string.IsNullOrEmpty(name) || WanosAudio.WanosBankStore.IsLoaded(name))
                    continue;
                if (BankLoadGate.TryResolveForEngine(name, out string path))
                {
                    bool ok = WanosAudio.WanosBankStore.LoadFromPath(name, path);
                    Log.Info($"[BankUpgradeBootstrap] essential bank={name} ok={ok} path={path}");
                }
                else
                {
                    Log.Warning(
                        $"[BankUpgradeBootstrap] essential bank skipped: {name} " +
                        $"reason={BankLoadGate.LastBlockReason}");
                }
            }
        }

        /// <summary>
        /// Android：先把 StreamingAssets/WanosBank 解到 persistent，再灌 essential。
        /// GuestLogin / Critical cue 前应 await。
        /// </summary>
        public static async UniTask EnsureLocalBanksReadyAsync()
        {
            await AudioEnginePaths.EnsureStreamingBanksExtractedAsync();
            // productName 改名后复用旧 persistent 里已下完的 bank，避免进度卡在重下。
            await AudioEnginePaths.EnsureLegacyBanksMigratedAsync();
            await BankLoadGate.PrepareForBootAsync();
            EnsureEssentialBanksLoaded();
        }

        static async UniTask<bool> WaitCriticalWithTimeoutAsync(float timeoutSeconds)
        {
            if (_fatal)
                return false;

            var critical = _criticalTcs.Task;
            if (critical.Status.IsCompleted())
                return !_fatal;

            if (timeoutSeconds <= 0f)
            {
                await critical;
                return !_fatal;
            }

            int win = await UniTask.WhenAny(
                critical,
                UniTask.Delay(TimeSpan.FromSeconds(timeoutSeconds), ignoreTimeScale: true));
            return win == 0 && !_fatal;
        }

        public static async UniTaskVoid RunAsync(bool loadIntoEngine = false, int runId = -1)
        {
            try
            {
                SignatureLib.Init();
                BoluoSession.LoadFromPrefs();

                // 真机 jar 内 bank 须先落到 persistent；再迁入旧 product 目录已下完的 bank。
                await AudioEnginePaths.EnsureStreamingBanksExtractedAsync();
                await AudioEnginePaths.EnsureLegacyBanksMigratedAsync();
                await BankLoadGate.PrepareForBootAsync();
                if (loadIntoEngine)
                    EnsureEssentialBanksLoaded();

                bool deviceOk = await BoluoDeviceLoginClient.EnsureDeviceTokenAsync(failFast: true);
                bool userOk = await BoluoDeviceLoginClient.EnsureUserTokenAsync(failFast: true);
                if (runId >= 0 && runId != _runId)
                    return;
                if (!userOk)
                {
                    // 只清这一轮。离岛或新一轮 Start 已经换了 runId 时不要把新的 _started 打掉。
                    if (runId < 0 || runId == _runId)
                        _started = false;
                    int local = PresetBankCatalog.SetFromLocalStreaming();
                    Log.Warning(
                        $"[BankUpgradeBootstrap] userToken missing deviceOk={deviceOk} " +
                        $"— skip HTTP, local banks={local}");
                    ReleaseCriticalFromLocal(loadIntoEngine);
                    _catalogTcs.TrySetResult();
                    return;
                }

                if (!deviceOk)
                {
                    Log.Warning("[BankUpgradeBootstrap] deviceToken missing — upgrade/bank may fail");
                }

#if UNITY_EDITOR
                // Play Mode 壳占位票（pm_device_tok_keep 等）不是服务端签发的。
                // 拿去打 upgrade/bank 会得到「deviceToken 鉴权失败」并 Log.Error，测试直接失败。
                if (Game.Native.EditorNativeHost.UsesLocalShellTickets)
                {
                    int local = PresetBankCatalog.SetFromLocalStreaming();
                    Log.Info(
                        $"[BankUpgradeBootstrap] local shell tickets — skip upgrade/bank, local banks={local}");
                    ReleaseCriticalFromLocal(loadIntoEngine);
                    _catalogTcs.TrySetResult();
                    return;
                }
#endif

                var data = await BankUpgradeClient.RequestUpgradeBank(failFast: true);
                if (data == null)
                {
                    int local = PresetBankCatalog.SetFromLocalStreaming();
                    Log.Warning($"[BankUpgradeBootstrap] upgrade/bank failed — local banks={local}");
                    ReleaseCriticalFromLocal(loadIntoEngine);
                    _catalogTcs.TrySetResult();
                    return;
                }

                if (!PresetBankCatalog.TryApplyUpgrade(data))
                {
                    Log.Warning(
                        $"[BankUpgradeBootstrap] banks empty bankVersion={data.bankVersion} " +
                        $"builder={data.builderAccountId} — not treating as success");
                    int local = PresetBankCatalog.SetFromLocalStreaming();
                    if (local == 0)
                        Log.Warning("[BankUpgradeBootstrap] no local banks either — audio will stay silent");
                    ReleaseCriticalFromLocal(loadIntoEngine);
                    _catalogTcs.TrySetResult();
                    return;
                }

                // musicLists ≈ MusicLists.json；banks ≈ ProjectMetadata banks[]。
                // 揭罩前只更新目录，让未落盘项显示「未下载」；bank HTTP 等揭罩后。
                Log.Info(
                    $"[BankUpgradeBootstrap] catalog ok banks={data.banks.Count} " +
                    "musicLists=" + (data.musicLists?.Count ?? 0) +
                    " — apply before uncover, defer bank HTTP");
                MusicCatalog.SetServerData(data.musicLists);
                if (!MusicCatalog.IsReady)
                    MusicCatalog.LoadAsync();
                // 新目录下旧正向 ready 缓存作废，UI 按新 version/md5 显示未下载。
                BankDownloader.InvalidateLocalReadyCache();
                if (loadIntoEngine)
                    EnsureEssentialBanksLoaded();
                await StreamingBankCatalog.EnsureLoadedAsync();
                ReleaseCriticalFromLocal(loadIntoEngine);
                _catalogTcs.TrySetResult();

                await WaitAfterUncoverAsync(runId);
                if (runId >= 0 && runId != _runId)
                    return;

                // 揭罩起播后：与以往相同的静默全量 / 流量窗 / FeatureOn 空转链路。
                await StartBackgroundDownloads(loadIntoEngine, runId);
            }
            catch (Exception e)
            {
                _fatal = true;
                Log.Error($"[BankUpgradeBootstrap] fatal: {e.Message}");
                UiDialogService.ShowRequestFailure("升级失败：" + e.Message);
                _criticalTcs.TrySetResult();
                _catalogTcs.TrySetResult();
            }
        }

        /// <summary>
        /// Bootstrap 后台策略入口（见 <see cref="Game.BackgroundDownloadPolicy"/>）。
        /// </summary>
        static async UniTask StartBackgroundDownloads(bool loadIntoEngine, int runId)
        {
            bool wifiConfirm = BoluoAppLaunchCache.IsWifiDownloadConfirmEnabled();
            if (Game.BackgroundDownloadPolicy.ShouldBootstrapStayIdle(wifiConfirm))
            {
                Log.Info(
                    "[BankUpgradeBootstrap] FeatureOn — bootstrap idle (no auto Critical/Rest)");
                await UniTask.CompletedTask;
                return;
            }

            List<string> allPending = Game.BackgroundDownloadCoordinator.CollectAllPendingBankNames();
            if (allPending.Count == 0)
            {
                Log.Info("[BankUpgradeBootstrap] all banks local — skip background download");
                await UniTask.CompletedTask;
                return;
            }

            // FeatureOff 蜂窝：零弹窗自动开闸后再判 CanTransmit。
            DownloadCellularGate.EnsureFeatureOffCellularAutoAllow();
            bool canTransmit = DownloadCellularGate.CanTransmitNow();
            Log.Info(
                "[BankUpgradeBootstrap] background decide pending=" + allPending.Count +
                " wifiConfirm=" + wifiConfirm +
                " canTransmit=" + canTransmit);

            if (Game.BackgroundDownloadPolicy.ShouldBootstrapSilentAll(wifiConfirm, canTransmit))
            {
                Log.Info(
                    "[BankUpgradeBootstrap] FeatureOff — silent all count=" + allPending.Count);
                // 静默全量禁止带着「下完切预设」意图；切预设只许用户点 / 健康推荐。
                Game.PendingDownloadIntent.Clear();
                KickSilentAllRemaining(loadIntoEngine);
                await UniTask.CompletedTask;
                return;
            }

            Log.Info(
                "[BankUpgradeBootstrap] FeatureOff defer — waiting transmit " +
                $"offline={DownloadCellularGate.IsOffline()}");
            _silentDeferred = true;
            RunSilentRestPatrol(loadIntoEngine, runId).Forget();
            await UniTask.CompletedTask;
        }

        /// <summary>
        /// 静默全量：先整波 Music*，再 Critical/Rest 非音乐。
        /// FeatureOff Bootstrap / FeatureOn+WiFi 确认后共用。
        /// </summary>
        public static void KickSilentAllRemaining(bool loadIntoEngine = true)
        {
            List<string> pending = Game.BackgroundDownloadCoordinator.CollectAllPendingBankNames();
            if (pending.Count == 0)
            {
                Log.Info("[BankUpgradeBootstrap] KickSilentAllRemaining — nothing pending");
                return;
            }

            Log.Info(
                "[BankUpgradeBootstrap] KickSilentAllRemaining count=" + pending.Count);
            DownloadAllSilent(loadIntoEngine);
        }

        /// <summary>
        /// 静默全量：必须先下完曲目列表，再下预设/氛围。
        /// 禁止 Critical 与 Rest Forget 并行——氛围会抢在 Music* 入队前占满槽，列表看起来「完全没开始」。
        /// </summary>
        static void DownloadAllSilent(bool loadIntoEngine)
        {
            DownloadSilentOrdered(loadIntoEngine).Forget();
        }

        static async UniTaskVoid DownloadSilentOrdered(bool loadIntoEngine)
        {
            List<BankRemoteInfo> music = CollectPendingMusicRemotes();
            if (music.Count > 0)
            {
                Log.Info(
                    "[BankUpgradeBootstrap] silent Music wave first count=" + music.Count);
                await BankDownloader.DownloadBanks(
                    music,
                    loadIntoEngine: loadIntoEngine,
                    onComplete: null,
                    onItemDone: null,
                    onItemProgress: null,
                    prioritize: false,
                    preempt: false);
                Log.Info(
                    "[BankUpgradeBootstrap] silent Music wave done count=" + music.Count);
            }

            List<string> criticalNonMusic = CollectCriticalNeedingUpdateNonMusic();
            if (criticalNonMusic.Count > 0)
            {
                Log.Info(
                    "[BankUpgradeBootstrap] silent Critical (non-Music) count=" +
                    criticalNonMusic.Count);
                await DownloadCriticalUpdates(
                    criticalNonMusic,
                    loadIntoEngine,
                    showProgressDialog: false);
            }

            await DownloadRestNonMusic(loadIntoEngine);
        }

        /// <summary>静默补 Critical 非音乐（FeatureOff 巡检；Music* 已在 Music wave）。</summary>
        static void TrySilentCriticalUpdates(bool loadIntoEngine)
        {
            List<string> needUpdate = CollectCriticalNeedingUpdateNonMusic();
            if (needUpdate.Count == 0)
                return;
            Log.Info(
                $"[BankUpgradeBootstrap] silent Critical kick count={needUpdate.Count}");
            DownloadCriticalUpdates(needUpdate, loadIntoEngine, showProgressDialog: false).Forget();
        }

        static async UniTask DownloadCriticalUpdates(
            List<string> bankNames,
            bool loadIntoEngine,
            bool showProgressDialog)
        {
            var remotes = PresetBankCatalog.ResolveRemotes(bankNames);
            if (remotes.Count == 0)
                return;

            // 静默路径不开进度窗；用户申请路径仍开（区域门忙时也不叠第二张）。
            int ticket = 0;
            bool showProgress = showProgressDialog && !Game.UserBankDownloadFlow.IsBusy;
            if (showProgress)
                ticket = UiDialogService.OpenDownloadProgress();
            try
            {
                // 静默不走 Startup：非 Music 若 prioritize，会排在 Normal 里的曲目前面。
                // 用户进度窗路径仍 preempt 进用户槽。出队闸门保证静默时 Music* 先于预设。
                await BankDownloader.DownloadBanks(
                    remotes,
                    loadIntoEngine: loadIntoEngine,
                    onComplete: null,
                    onItemDone: null,
                    onItemProgress: null,
                    prioritize: false,
                    preempt: showProgressDialog);
                Log.Info($"[BankUpgradeBootstrap] Critical update done count={remotes.Count}");
            }
            finally
            {
                if (ticket > 0)
                    UiDialogService.CloseDownloadProgress(ticket);
            }
        }

        static List<string> CollectCriticalNeedingUpdate()
        {
            var names = new List<string>(CriticalBankNames.Length);
            for (int i = 0; i < CriticalBankNames.Length; i++)
            {
                string name = CriticalBankNames[i];
                if (CriticalNeedsHttpUpdate(name))
                    names.Add(name);
            }

            return names;
        }

        /// <summary>Critical 待下且非 Music*（Music1_1 归 Music wave）。</summary>
        static List<string> CollectCriticalNeedingUpdateNonMusic()
        {
            List<string> all = CollectCriticalNeedingUpdate();
            var nonMusic = new List<string>(all.Count);
            for (int i = 0; i < all.Count; i++)
            {
                if (!IsMusicBankName(all[i]))
                    nonMusic.Add(all[i]);
            }

            return nonMusic;
        }

        /// <summary>catalog 内全部待下的 Music*（含 Critical 的 Music1_1）。</summary>
        static List<BankRemoteInfo> CollectPendingMusicRemotes()
        {
            var all = PresetBankCatalog.AllRemotes();
            var music = new List<BankRemoteInfo>(all.Count);
            for (int i = 0; i < all.Count; i++)
            {
                BankRemoteInfo info = all[i];
                if (info == null || string.IsNullOrEmpty(info.bankName))
                    continue;
                if (string.IsNullOrEmpty(info.url))
                    continue;
                if (!IsMusicBankName(info.bankName))
                    continue;
                if (BankDownloader.IsLocalReady(info.bankName))
                    continue;
                music.Add(info);
            }

            return music;
        }

        /// <summary>
        /// Critical 要不要 HTTP：catalog 有 url 且盘上未就绪 → 要。
        /// 就绪须服务端 version 与 md5 都对上；只对上首包 version 或体积不算。
        /// 禁止无落盘就跳过——否则 Rest 排除 Critical 名后预设氛围包永久不下。
        /// </summary>
        static bool CriticalNeedsHttpUpdate(string name)
        {
            if (string.IsNullOrEmpty(name))
                return false;
            if (!PresetBankCatalog.TryGet(name, out BankRemoteInfo remote) || remote == null)
                return false;
            if (string.IsNullOrEmpty(remote.url) || string.IsNullOrEmpty(remote.md5))
                return false;

            return !BankDownloader.IsLocalReady(name);
        }

#if UNITY_EDITOR
        /// <summary>单测：Critical 是否仍需 HTTP / 申请窗。</summary>
        public static bool EditorCriticalNeedsHttpUpdate(string name) =>
            CriticalNeedsHttpUpdate(name);

        /// <summary>单测：Rest 收集 + 音乐优先排序（排除 Critical 名）。</summary>
        public static List<BankRemoteInfo> EditorCollectRestRemotes(IList<string> criticalNames) =>
            CollectRestRemotes(criticalNames);

        /// <summary>单测：静默 Music wave 收集。</summary>
        public static List<BankRemoteInfo> EditorCollectPendingMusicRemotes() =>
            CollectPendingMusicRemotes();

        /// <summary>单测：Music* 是否排在非 Music 前。</summary>
        public static int EditorCompareRestRemoteMusicFirst(BankRemoteInfo a, BankRemoteInfo b) =>
            CompareRestRemoteMusicFirst(a, b);
#endif

        /// <summary>Rest 非音乐：Music* 已在 Music wave；Critical 名仍排除。</summary>
        static async UniTask DownloadRestNonMusic(bool loadIntoEngine)
        {
            var rest = CollectRestRemotes(CriticalBankNames);
            var nonMusic = new List<BankRemoteInfo>(rest.Count);
            for (int i = 0; i < rest.Count; i++)
            {
                BankRemoteInfo info = rest[i];
                if (info == null || IsMusicBankName(info.bankName))
                    continue;
                if (string.IsNullOrEmpty(info.url))
                    continue;
                if (BankDownloader.IsLocalReady(info.bankName))
                    continue;
                nonMusic.Add(info);
            }

            if (nonMusic.Count == 0)
                return;

            await BankDownloader.DownloadBanks(
                nonMusic,
                loadIntoEngine: loadIntoEngine,
                onComplete: null,
                onItemDone: null,
                onItemProgress: null,
                prioritize: false,
                preempt: false);
            Log.Info($"[BankUpgradeBootstrap] Rest non-Music wave finished count={nonMusic.Count}");
        }

        static async UniTask DownloadRest(bool loadIntoEngine) =>
            await DownloadRestNonMusic(loadIntoEngine);

        /// <summary>
        /// FeatureOff：等可传后静默全量（蜂窝自动开闸，无弹窗）。
        /// FeatureOn：不应进入本巡检（Bootstrap 空转）。
        /// </summary>
        static async UniTaskVoid RunSilentRestPatrol(bool loadIntoEngine, int runId)
        {
            const int maxPasses = 180;
            for (int i = 0; i < maxPasses; i++)
            {
                await UniTask.Delay(1000, DelayType.UnscaledDeltaTime);
                if (runId != _runId || !_silentDeferred)
                    return;

                bool wifiConfirm = BoluoAppLaunchCache.IsWifiDownloadConfirmEnabled();
                if (Game.BackgroundDownloadPolicy.ShouldBootstrapStayIdle(wifiConfirm))
                {
                    _silentDeferred = false;
                    Log.Info("[BankUpgradeBootstrap] patrol stop — FeatureOn idle");
                    return;
                }

                List<string> allPending =
                    Game.BackgroundDownloadCoordinator.CollectAllPendingBankNames();
                if (allPending.Count == 0)
                {
                    _silentDeferred = false;
                    Log.Info("[BankUpgradeBootstrap] silent patrol — nothing pending");
                    return;
                }

                DownloadCellularGate.EnsureFeatureOffCellularAutoAllow();
                bool canTransmit = DownloadCellularGate.CanTransmitNow();

                if (Game.BackgroundDownloadPolicy.ShouldBootstrapSilentAll(
                        wifiConfirm, canTransmit))
                {
                    _silentDeferred = false;
                    Log.Info(
                        "[BankUpgradeBootstrap] patrol — FeatureOff transmit ok → silent all");
                    KickSilentAllRemaining(loadIntoEngine);
                    return;
                }
            }

            Log.Warning("[BankUpgradeBootstrap] silent Rest patrol timed out still deferred");
        }

        static void ReleaseCriticalFromLocal(bool loadIntoEngine)
        {
            // Extract already awaited at RunAsync start; load what we have on disk.
            if (loadIntoEngine)
            {
                EnsureEssentialBanksLoaded();
                for (int i = 0; i < CriticalBankNames.Length; i++)
                {
                    string name = CriticalBankNames[i];
                    if (string.IsNullOrEmpty(name))
                        continue;
                    if (BankLoadGate.TryResolveForEngine(name, out string path))
                        WanosAudio.WanosBankStore.LoadFromPath(name, path);
                    else
                    {
                        Log.Warning(
                            $"[BankUpgradeBootstrap] critical bank skipped: {name} " +
                            $"reason={BankLoadGate.LastBlockReason}");
                    }
                }
            }

            if (!MusicCatalog.IsReady)
                MusicCatalog.LoadAsync();
            _criticalTcs.TrySetResult();
        }

        static List<BankRemoteInfo> CollectRestRemotes(IList<string> criticalNames)
        {
            var all = PresetBankCatalog.AllRemotes();
            var rest = new List<BankRemoteInfo>(all.Count);
            for (int i = 0; i < all.Count; i++)
            {
                var info = all[i];
                if (info == null || string.IsNullOrEmpty(info.bankName))
                    continue;
                if (IsCriticalName(info.bankName, criticalNames))
                    continue;
                rest.Add(info);
            }

            // Music 前置：静默 Rest 先下完曲目列表，再下预设/氛围（≤3 窗口内填满音乐）。
            rest.Sort(CompareRestRemoteMusicFirst);
            return rest;
        }

        static int CompareRestRemoteMusicFirst(BankRemoteInfo a, BankRemoteInfo b)
        {
            bool aMusic = IsMusicBankName(a?.bankName);
            bool bMusic = IsMusicBankName(b?.bankName);
            if (aMusic == bMusic)
                return 0;
            return aMusic ? -1 : 1;
        }

        static bool IsMusicBankName(string bankName)
        {
            string stem = AudioEnginePaths.StripBankExtension(bankName);
            return !string.IsNullOrEmpty(stem)
                   && stem.StartsWith("Music", StringComparison.OrdinalIgnoreCase);
        }

        static bool IsCriticalName(string bankName, IList<string> criticalNames)
        {
            string stem = AudioEnginePaths.StripBankExtension(bankName);
            for (int i = 0; i < criticalNames.Count; i++)
            {
                if (string.Equals(stem, criticalNames[i], StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }
    }
}
