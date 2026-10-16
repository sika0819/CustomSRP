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
        static bool _fatal;
        static UniTaskCompletionSource _catalogTcs = new UniTaskCompletionSource();
        static UniTaskCompletionSource _criticalTcs = new UniTaskCompletionSource();

        public static UniTask WaitCatalogAsync() => _catalogTcs.Task;
        public static UniTask WaitCriticalAsync() => _criticalTcs.Task;

        public static void ResetSession()
        {
            _started = false;
            _fatal = false;
            _catalogTcs = new UniTaskCompletionSource();
            _criticalTcs = new UniTaskCompletionSource();
            PresetBankCatalog.Clear();
            BankDownloader.ResetSession();
        }

        public static void Start(bool loadIntoEngine = false)
        {
            if (_started)
                return;
            _started = true;
            RunAsync(loadIntoEngine).Forget();
        }

        /// <summary>
        /// Wait Critical wave (or timeout), then cue default music on main thread.
        /// Download continues in background if timed out.
        /// </summary>
        public static async UniTask<bool> WaitCriticalThenCueAsync(float timeoutSeconds = 45f)
        {
            bool ok = await WaitCriticalWithTimeoutAsync(timeoutSeconds);
            await UniTask.SwitchToMainThread();
            if (_fatal)
            {
                Log.Warning("[BankUpgradeBootstrap] critical cue skipped — fatal");
                return false;
            }

            EnsureEssentialBanksLoaded();
            if (!MusicCatalog.IsReady)
                MusicCatalog.LoadAsync();

            // Music1_1 可能已在盘上但 Critical 波尚未 loadIntoEngine；给短窗口灌引擎 + 等播放设备。
            const string defaultBank = "Music1_1";
            float bankDeadline = UnityEngine.Time.realtimeSinceStartup + 8f;
            while (UnityEngine.Time.realtimeSinceStartup < bankDeadline)
            {
                if (!WanosAudio.WanosEngine.IsPlaybackEnabled)
                {
                    WanosAudio.WanosEngine.StartEngine("critical-cue-retry");
                    WanosAudio.WanosEngine.EnablePlayback("critical-cue-retry");
                    WanosAudio.WanosEngine.Unmute("critical-cue-retry");
                }

                if (WanosAudio.WanosEngine.IsPlaybackEnabled
                    && (WanosAudio.WanosBankStore.IsLoaded(defaultBank)
                        || WanosAudio.WanosBankStore.Load(defaultBank)))
                    break;
                await UniTask.Delay(100, DelayType.UnscaledDeltaTime);
            }

            if (GameEntry.Music != null)
            {
                GameEntry.Music.PlayIndex(0, restart: true);
                Log.Info(
                    $"[BankUpgradeBootstrap] critical cue ok={ok} " +
                    $"playback={WanosAudio.WanosEngine.IsPlaybackEnabled} " +
                    $"bankLoaded={WanosAudio.WanosBankStore.IsLoaded(defaultBank)} " +
                    $"playing={GameEntry.Music.IsPlaying}");
            }
            else
            {
                Log.Warning("[BankUpgradeBootstrap] critical cue skipped — MusicPlaylist null");
            }

            return ok;
        }

        /// <summary>Load Common_Init / Common_Music from StreamingAssets when engine is up.</summary>
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

        public static async UniTaskVoid RunAsync(bool loadIntoEngine = false)
        {
            try
            {
                SignatureLib.Init();
                BoluoSession.LoadFromPrefs();

                bool deviceOk = await BoluoDeviceLoginClient.EnsureDeviceTokenAsync(failFast: true);
                bool userOk = await BoluoDeviceLoginClient.EnsureUserTokenAsync(failFast: true);
                if (!deviceOk)
                    Log.Warning("[BankUpgradeBootstrap] deviceToken missing — upgrade/bank may fail");
                if (!userOk)
                    Log.Warning(
                        "[BankUpgradeBootstrap] userToken missing — upgrade/bank returns empty banks");

                if (!deviceOk || !userOk)
                {
                    int local = PresetBankCatalog.SetFromLocalStreaming();
                    Log.Warning(
                        $"[BankUpgradeBootstrap] tokens incomplete deviceOk={deviceOk} userOk={userOk} " +
                        $"— skip HTTP, local banks={local}");
                    ReleaseCriticalFromLocal(loadIntoEngine);
                    _catalogTcs.TrySetResult();
                    return;
                }

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

                Log.Info($"[BankUpgradeBootstrap] catalog ok banks={data.banks.Count} — Critical then Rest");
                MusicCatalog.SetServerData(data.musicLists);
                if (!MusicCatalog.IsReady)
                    MusicCatalog.LoadAsync();
                if (loadIntoEngine)
                    EnsureEssentialBanksLoaded();
                _catalogTcs.TrySetResult();

                var critical = PresetBankCatalog.ResolveRemotes(CriticalBankNames);
                // 对齐老工程 WanosDownloadManagerStandalone：单路串行，避免 3 路抢带宽把百兆 bank 拖死。
                BankDownloader.SetStartupParallelSlots(1);
                await BankDownloader.DownloadBanks(
                    critical,
                    loadIntoEngine: loadIntoEngine,
                    onComplete: null,
                    onItemDone: null,
                    onItemProgress: null,
                    prioritize: true,
                    preempt: false);

                _criticalTcs.TrySetResult();
                Log.Info($"[BankUpgradeBootstrap] Critical wave done loadIntoEngine={loadIntoEngine}");

                var rest = CollectRestRemotes(CriticalBankNames);
                if (rest.Count > 0)
                {
                    BankDownloader.SetStartupParallelSlots(1);
                    await BankDownloader.DownloadBanks(
                        rest,
                        loadIntoEngine: loadIntoEngine,
                        onComplete: null,
                        onItemDone: null,
                        onItemProgress: null,
                        prioritize: false,
                        preempt: false);
                }

                Log.Info($"[BankUpgradeBootstrap] Rest wave finished count={rest.Count}");
            }
            catch (Exception e)
            {
                _fatal = true;
                Log.Error($"[BankUpgradeBootstrap] fatal: {e.Message}");
                _criticalTcs.TrySetResult();
                _catalogTcs.TrySetResult();
            }
        }

        static void ReleaseCriticalFromLocal(bool loadIntoEngine)
        {
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
