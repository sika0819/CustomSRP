using System;
using Cysharp.Threading.Tasks;
using Game.BoluoApi;
using UnityEngine;
using UnityGameFramework.Runtime;

namespace Game
{
    /// <summary>
    /// 切预设唯一入口（用户点卡 / iOS 健康智慧推荐）：
    /// 未齐 → 下载 → 齐后 RequestTransition；已齐 → 立刻切。
    /// 静默 KickSilentAll 不得调用本类。
    /// </summary>
    public static class PresetEnterCoordinator
    {
        public static void RequestEnter(
            int presetId,
            PendingDownloadIntent.EnterSource source)
        {
            if (source != PendingDownloadIntent.EnterSource.UserClick
                && source != PendingDownloadIntent.EnterSource.HealthRecommend)
            {
                Log.Warning(
                    "[PresetEnter] refuse enter source=" + source + " preset=" + presetId);
                return;
            }

            IslandPresetAsset preset = GameEntry.Presets?.GetById(presetId);
            if (preset == null)
            {
                Log.Warning("[PresetEnter] unknown presetId=" + presetId);
                return;
            }

            string[] banks = preset.BankNames;
            if (banks == null || banks.Length == 0 || AreBanksLocallyReady(banks))
            {
                PendingDownloadIntent.Clear();
                MarkHeard(preset);
                GameEntry.Presets?.RequestTransition(presetId);
                return;
            }

            if (UserBankDownloadFlow.ShouldRejectNewBatch(preemptBusyBatch: true))
            {
                UiDialogService.ShowToast(UserBankDownloadFlow.BusyToastText);
                return;
            }

            PendingDownloadIntent.SetPreset(presetId, source);
            int captured = presetId;
            UserBankDownloadFlow.Start(
                banks,
                new DownloadUiOptions
                {
                    SuppressProgressDialog = true,
                    OnCancel = () =>
                    {
                        if (PendingDownloadIntent.IsPresetMatch(captured))
                            PendingDownloadIntent.Clear();
                    },
                },
                onBanksDone: _ => OnBanksDone(captured).Forget(),
                prioritize: false,
                preempt: true,
                abortOtherUserDownloads: true,
                preemptBusyBatch: true);
        }

        static async UniTaskVoid OnBanksDone(int presetId)
        {
            IslandPresetAsset preset = GameEntry.Presets?.GetById(presetId);
            string[] banks = preset != null ? preset.BankNames : null;

            bool allLocal = AreBanksLocallyReady(banks);
            if (!allLocal)
                allLocal = await WaitBanksSettled(banks);

            if (!allLocal)
            {
                if (AnyBusy(banks))
                    return;
                if (PendingDownloadIntent.IsPresetMatch(presetId))
                    PendingDownloadIntent.Clear();
                return;
            }

            if (banks != null && banks.Length > 0)
                BankDownloader.MarkLocalReady(banks);

            if (!PendingDownloadIntent.ShouldAutoEnterPreset(presetId))
            {
                if (PendingDownloadIntent.IsPresetMatch(presetId))
                    PendingDownloadIntent.Clear();
                return;
            }

            PendingDownloadIntent.Clear();
            if (preset == null)
                return;

            MarkHeard(preset);
            GameEntry.Presets?.RequestTransition(presetId);
        }

        static void MarkHeard(IslandPresetAsset preset)
        {
            if (preset == null || string.IsNullOrEmpty(preset.DisplayName))
                return;
            DownloadedUnplayedManager.MarkPlayed(
                DownloadedUnplayedManager.PresetKey(preset.DisplayName));
        }

        static bool AreBanksLocallyReady(string[] banks)
        {
            if (banks == null || banks.Length == 0)
                return true;
            for (int i = 0; i < banks.Length; i++)
            {
                string bn = banks[i];
                if (string.IsNullOrEmpty(bn))
                    continue;
                if (!BankDownloader.IsLocalReady(bn)
                    && !BankLoadGate.CanEnterWithoutOfficialFile(bn))
                    return false;
            }

            return true;
        }

        static bool AnyBusy(string[] banks)
        {
            if (banks == null)
                return false;
            for (int i = 0; i < banks.Length; i++)
            {
                string bn = banks[i];
                if (!string.IsNullOrEmpty(bn) && BankDownloader.IsBankBusy(bn))
                    return true;
            }

            return false;
        }

        static async UniTask<bool> WaitBanksSettled(string[] banks)
        {
            if (banks == null || banks.Length == 0)
                return true;

            const float idleGraceSeconds = 0.4f;
            const float maxWaitSeconds = 180f;
            float deadline = Time.realtimeSinceStartup + maxWaitSeconds;
            float lastBusyAt = Time.realtimeSinceStartup;

            while (Time.realtimeSinceStartup < deadline)
            {
                if (AreBanksLocallyReady(banks))
                    return true;

                if (AnyBusy(banks))
                    lastBusyAt = Time.realtimeSinceStartup;
                else if (Time.realtimeSinceStartup - lastBusyAt >= idleGraceSeconds)
                    return AreBanksLocallyReady(banks);

                for (int i = 0; i < banks.Length; i++)
                {
                    string bn = banks[i];
                    if (string.IsNullOrEmpty(bn) || BankDownloader.IsLocalReady(bn))
                        continue;
                    BankDownloader.InvalidateLocalReadyCache(bn);
                }

                await UniTask.Delay(100, DelayType.UnscaledDeltaTime);
            }

            return AreBanksLocallyReady(banks);
        }
    }
}
