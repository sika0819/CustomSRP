using System;
using Cysharp.Threading.Tasks;
using UnityGameFramework.Runtime;

namespace Game.BoluoApi
{
    /// <summary>
    /// deviceToken → upgrade/bank catalog → HTTP 下载落盘。不挡进岛；不灌 Wwise。
    /// </summary>
    public static class BankUpgradeBootstrap
    {
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

        public static async UniTaskVoid RunAsync(bool loadIntoEngine = false)
        {
            try
            {
                SignatureLib.Init();
                BoluoSession.LoadFromPrefs();

                var data = await FetchCatalogFailFastAsync();
                if (data == null)
                {
                    int local = PresetBankCatalog.SetFromLocalStreaming();
                    Log.Warning($"[BankUpgradeBootstrap] upgrade/bank failed — local banks={local}");
                    _criticalTcs.TrySetResult();
                    _catalogTcs.TrySetResult();
                    return;
                }

                if (!PresetBankCatalog.TryApplyUpgrade(data))
                {
                    Log.Warning(
                        $"[BankUpgradeBootstrap] banks empty bankVersion={data.bankVersion} " +
                        $"builder={data.builderAccountId}");
                    PresetBankCatalog.SetFromLocalStreaming();
                    _criticalTcs.TrySetResult();
                    _catalogTcs.TrySetResult();
                    return;
                }

                Log.Info($"[BankUpgradeBootstrap] catalog ok banks={data.banks.Count} — enqueue downloads");
                _catalogTcs.TrySetResult();

                var remotes = PresetBankCatalog.AllRemotes();
                await BankDownloader.DownloadBanks(
                    remotes,
                    loadIntoEngine: false,
                    onComplete: null,
                    onItemDone: null,
                    onItemProgress: null,
                    prioritize: true,
                    preempt: false);

                _criticalTcs.TrySetResult();
                Log.Info("[BankUpgradeBootstrap] download wave finished (no Wwise load)");
            }
            catch (Exception e)
            {
                _fatal = true;
                Log.Error($"[BankUpgradeBootstrap] fatal: {e.Message}");
                _criticalTcs.TrySetResult();
                _catalogTcs.TrySetResult();
            }
        }

        static async UniTask<BankUpgradeData> FetchCatalogFailFastAsync()
        {
            bool tokenOk = await BoluoDeviceLoginClient.EnsureDeviceTokenAsync(failFast: true);
            if (!tokenOk)
                Log.Warning("[BankUpgradeBootstrap] deviceToken missing — upgrade/bank may fail");

            bool userOk = await BoluoDeviceLoginClient.EnsureUserTokenAsync(failFast: true);
            if (!userOk)
                Log.Warning("[BankUpgradeBootstrap] userToken missing — upgrade/bank may return empty banks");

            return await BankUpgradeClient.RequestUpgradeBank(failFast: true);
        }
    }
}
