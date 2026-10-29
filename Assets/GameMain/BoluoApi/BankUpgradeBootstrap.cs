using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using UnityGameFramework.Runtime;

namespace Game.BoluoApi
{
    /// <summary>
    /// deviceToken → userToken → upgrade/bank catalog → Critical/Rest HTTP 下载；可选灌入 Wanos。
    /// Critical 完成后可 cue 默认 BGM（不挡揭罩）。
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
        static bool _mobilePromptLatched;
        static UniTaskCompletionSource _catalogTcs = new UniTaskCompletionSource();
        static UniTaskCompletionSource _criticalTcs = new UniTaskCompletionSource();

        public static UniTask WaitCatalogAsync() => _catalogTcs.Task;
        public static UniTask WaitCriticalAsync() => _criticalTcs.Task;

        public static void ResetSession()
        {
            _started = false;
            _fatal = false;
            _silentDeferred = false;
            _mobilePromptLatched = false;
            _catalogTcs = new UniTaskCompletionSource();
            _criticalTcs = new UniTaskCompletionSource();
            AudioEnginePaths.ResetSession();
            PresetBankCatalog.Clear();
            BankDownloader.ResetSession();
        }

        /// <summary>离岛：允许下一次用户票落地后再 Start。不重跑进程级 ResetSession。</summary>
        public static void InvalidateAfterLeave()
        {
            _started = false;
            _runId++;
            _silentDeferred = false;
            _mobilePromptLatched = false;
            DownloadCellularGate.ResetForNewSession();
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
                    WanosAudio.WanosEngine.Unmute("critical-cue-retry");
                }
                else
                    WanosAudio.WanosEngine.StartEngine("critical-cue-dsp");

                if (WanosAudio.WanosEngine.IsPlaybackEnabled
                    && (WanosAudio.WanosBankStore.IsLoaded(defaultBank)
                        || WanosAudio.WanosBankStore.Load(defaultBank)
                        || TryLoadBankFromDisk(defaultBank)))
                    break;
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
                            $"[BankUpgradeBootstrap] saved bank not ready bank={cueBank} — fallback {defaultBank}");
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
            if (!AudioEnginePaths.TryFindLocalBank(bankName, out string path))
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
                if (AudioEnginePaths.TryFindLocalBank(name, out string path))
                {
                    bool ok = WanosAudio.WanosBankStore.LoadFromPath(name, path);
                    Log.Info($"[BankUpgradeBootstrap] essential bank={name} ok={ok} path={path}");
                }
                else
                    Log.Warning($"[BankUpgradeBootstrap] essential bank missing: {name}");
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

                Log.Info($"[BankUpgradeBootstrap] catalog ok banks={data.banks.Count} — Critical local, Rest silent");
                MusicCatalog.SetServerData(data.musicLists);
                if (!MusicCatalog.IsReady)
                    MusicCatalog.LoadAsync();
                if (loadIntoEngine)
                    EnsureEssentialBanksLoaded();
                _catalogTcs.TrySetResult();

                // Critical：StreamingAssets 复制/落盘即可，不静默 HTTP。
                await StreamingBankCatalog.EnsureLoadedAsync();
                ReleaseCriticalFromLocal(loadIntoEngine);

                // 与线上版本不一致 → 申请下载弹窗；用户确认后再走门禁链。
                PromptCriticalUpdateIfNeeded(loadIntoEngine);

                // Rest：Wi‑Fi / 已开闸可静默；蜂窝未开闸则巡检叠流量窗。
                await StartOrDeferRestDownload(loadIntoEngine, runId);
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
        /// Critical 与 catalog 不一致时弹申请下载；点「立即下载」后再无网→存储→流量→下。
        /// 点「返回」保留首包本地 bank，不强制更新。
        /// </summary>
        static void PromptCriticalUpdateIfNeeded(bool loadIntoEngine)
        {
            List<string> needUpdate = CollectCriticalNeedingUpdate();
            if (needUpdate.Count == 0)
            {
                Log.Info("[BankUpgradeBootstrap] Critical all local/streaming — skip HTTP");
                return;
            }

            string size = DownloadNetworkGate.FormatSizeMb(needUpdate);
            Log.Info(
                $"[BankUpgradeBootstrap] Critical need update count={needUpdate.Count} size={size}MB — show RequestDownload");
            UiDialogService.ShowRequestDownload(
                size,
                () => OnCriticalUpdateConfirmed(needUpdate, loadIntoEngine),
                onReturn: null);
        }

        static void OnCriticalUpdateConfirmed(List<string> bankNames, bool loadIntoEngine)
        {
            if (bankNames == null || bankNames.Count == 0)
                return;

            if (!DownloadNetworkGate.TryPassDownloadGates(
                    bankNames,
                    onAllowAfterMobile: () => DownloadCriticalUpdates(bankNames, loadIntoEngine).Forget(),
                    onWifiOnly: null,
                    onBlockedRetreat: null))
                return;

            DownloadCriticalUpdates(bankNames, loadIntoEngine).Forget();
        }

        static async UniTaskVoid DownloadCriticalUpdates(List<string> bankNames, bool loadIntoEngine)
        {
            var remotes = PresetBankCatalog.ResolveRemotes(bankNames);
            if (remotes.Count == 0)
                return;

            int ticket = UiDialogService.OpenDownloadProgress();
            try
            {
                BankDownloader.SetStartupParallelSlots(1);
                await BankDownloader.DownloadBanks(
                    remotes,
                    loadIntoEngine: loadIntoEngine,
                    onComplete: null,
                    onItemDone: null,
                    onItemProgress: null,
                    prioritize: true,
                    preempt: true);
                Log.Info($"[BankUpgradeBootstrap] Critical update done count={remotes.Count}");
            }
            finally
            {
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

        /// <summary>
        /// Critical 要不要 HTTP：首包版本与线上一致 → 否；首包有版本但不等 → 是；
        /// 否则看磁盘是否满足 catalog。
        /// </summary>
        static bool CriticalNeedsHttpUpdate(string name)
        {
            if (string.IsNullOrEmpty(name))
                return false;
            if (!PresetBankCatalog.TryGet(name, out BankRemoteInfo remote) || remote == null)
                return false;
            if (string.IsNullOrEmpty(remote.url))
                return false;

            if (!string.IsNullOrEmpty(remote.version)
                && StreamingBankCatalog.IsSameVersionAsStreaming(name, remote.version))
                return false;

            if (!string.IsNullOrEmpty(remote.version)
                && StreamingBankCatalog.TryGetVersion(name, out string localVer)
                && !string.IsNullOrEmpty(localVer))
                return true;

            return !BankDownloader.IsLocalReady(name);
        }

        static async UniTask StartOrDeferRestDownload(bool loadIntoEngine, int runId)
        {
            var rest = CollectRestRemotes(CriticalBankNames);
            int pending = CountPendingRemotes(rest);
            if (pending <= 0)
            {
                Log.Info("[BankUpgradeBootstrap] Rest wave empty / all local");
                return;
            }

            if (!DownloadCellularGate.CanTransmitNow())
            {
                Log.Info(
                    "[BankUpgradeBootstrap] defer silent Rest — " +
                    $"offline={DownloadCellularGate.IsOffline()} " +
                    $"needMobilePrompt={DownloadCellularGate.NeedsMobileDataPrompt()} pending={pending}");
                _silentDeferred = true;
                RunSilentRestPatrol(loadIntoEngine, runId).Forget();
                return;
            }

            await DownloadRest(loadIntoEngine);
        }

        static async UniTask DownloadRest(bool loadIntoEngine)
        {
            var rest = CollectRestRemotes(CriticalBankNames);
            if (rest.Count == 0)
                return;

            BankDownloader.SetStartupParallelSlots(1);
            await BankDownloader.DownloadBanks(
                rest,
                loadIntoEngine: loadIntoEngine,
                onComplete: null,
                onItemDone: null,
                onItemProgress: null,
                prioritize: false,
                preempt: false);
            Log.Info($"[BankUpgradeBootstrap] Rest wave finished count={rest.Count}");
        }

        /// <summary>
        /// 进岛后：仅 Rest 静默待下。蜂窝未开闸 → 叠流量窗；Wi‑Fi / 已开闸 → 开 Rest。
        /// Critical 更新只走 <see cref="PromptCriticalUpdateIfNeeded"/>，不进静默队列。
        /// </summary>
        static async UniTaskVoid RunSilentRestPatrol(bool loadIntoEngine, int runId)
        {
            const int maxPasses = 180;
            bool storagePrompted = false;
            for (int i = 0; i < maxPasses; i++)
            {
                await UniTask.Delay(1000, DelayType.UnscaledDeltaTime);
                if (runId != _runId || !_silentDeferred)
                    return;

                List<string> pending = CollectPendingRestBankNames();
                if (pending.Count == 0)
                {
                    _silentDeferred = false;
                    Log.Info("[BankUpgradeBootstrap] silent Rest patrol — nothing pending");
                    return;
                }

                if (DownloadCellularGate.IsOffline())
                    continue;

                if (!storagePrompted
                    && DownloadNetworkGate.TryBlockIfInsufficientStorage(pending))
                {
                    storagePrompted = true;
                    continue;
                }

                if (DownloadCellularGate.NeedsMobileDataPrompt())
                {
                    if (_mobilePromptLatched)
                        continue;
                    _mobilePromptLatched = true;
                    DownloadNetworkGate.TryShowMobileDataPrompt(
                        pending,
                        onAllowContinue: () =>
                        {
                            _silentDeferred = false;
                            DownloadRest(loadIntoEngine).Forget();
                        },
                        onWifiOnly: () => { _mobilePromptLatched = false; });
                    continue;
                }

                if (!DownloadCellularGate.CanTransmitNow())
                    continue;

                _silentDeferred = false;
                Log.Info("[BankUpgradeBootstrap] silent Rest patrol — transmit ok");
                await DownloadRest(loadIntoEngine);
                return;
            }

            Log.Warning("[BankUpgradeBootstrap] silent Rest patrol timed out still deferred");
        }

        static List<string> CollectPendingRestBankNames()
        {
            var names = new List<string>(64);
            var rest = CollectRestRemotes(CriticalBankNames);
            for (int i = 0; i < rest.Count; i++)
            {
                BankRemoteInfo info = rest[i];
                if (info == null || string.IsNullOrEmpty(info.bankName))
                    continue;
                if (string.IsNullOrEmpty(info.url))
                    continue;
                if (BankDownloader.IsLocalReady(info.bankName))
                    continue;
                names.Add(info.bankName);
            }

            return names;
        }

        static int CountPendingRemotes(List<BankRemoteInfo> remotes)
        {
            if (remotes == null)
                return 0;
            int n = 0;
            for (int i = 0; i < remotes.Count; i++)
            {
                BankRemoteInfo info = remotes[i];
                if (info == null || string.IsNullOrEmpty(info.bankName) || string.IsNullOrEmpty(info.url))
                    continue;
                if (BankDownloader.IsLocalReady(info.bankName))
                    continue;
                n++;
            }

            return n;
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
                    if (AudioEnginePaths.TryFindLocalBank(name, out string path))
                        WanosAudio.WanosBankStore.LoadFromPath(name, path);
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

            return rest;
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
