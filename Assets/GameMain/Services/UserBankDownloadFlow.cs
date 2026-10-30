using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Game.BoluoApi;
using UnityEngine;
using UnityGameFramework.Runtime;

namespace Game
{
    /// <summary>
    /// 用户主动下载统一流水线（区域 / 预设 / 音乐）：
    /// <see cref="DownloadConsentSession.EnsureReady"/> →（可选）进度窗 → <see cref="BankDownloader"/> → 关窗 →
    /// <see cref="DownloadCellularGate.RevokeCellularAllow"/>。
    /// 已 <see cref="BankDownloader.IsLocalReady"/> 的 bank 不进同意链 / HTTP。
    /// 流量窗仅蜂窝弹出；Wi‑Fi 直接开下。预设可 <see cref="DownloadUiOptions.SuppressProgressDialog"/>。
    /// </summary>
    public static class UserBankDownloadFlow
    {
        static int _activeTicket;
        static bool _busy;

        /// <summary>进行中的进度窗票据（区域门重开进度用）。</summary>
        public static int ActiveProgressTicket => _activeTicket;

        public static bool IsBusy => _busy;

        /// <summary>
        /// 跑同意链；通过后按选项开进度窗并下载。
        /// <paramref name="onComplete"/> 在关窗 + Revoke 之后调用（成功或失败都调）。
        /// <paramref name="onBanksDone"/> 在下载 await 返回后、关窗前调用（可空）。
        /// </summary>
        public static void Start(
            IList<string> banks,
            DownloadUiOptions opts = default,
            Action onComplete = null,
            Action<IReadOnlyList<BankDownloadItem>> onBanksDone = null,
            Action onDownloadStarting = null,
            Action onProgressDismissed = null,
            bool prioritize = true,
            bool preempt = true,
            bool loadIntoEngine = true)
        {
            string[] pending = FilterPending(Snapshot(banks));
            if (pending.Length == 0)
            {
                CompleteAlreadyReady(onDownloadStarting, onBanksDone, onComplete);
                return;
            }

            bool showProgress = !opts.SuppressProgressDialog;
            DownloadConsentSession.EnsureReady(
                pending,
                onReady: () =>
                {
                    try
                    {
                        onDownloadStarting?.Invoke();
                    }
                    catch (Exception e)
                    {
                        Log.Warning("[UserBankDownloadFlow] onDownloadStarting: " + e.Message);
                    }

                    DownloadWithProgress(
                            pending,
                            onComplete,
                            onBanksDone,
                            onProgressDismissed,
                            prioritize,
                            preempt,
                            loadIntoEngine,
                            showProgress)
                        .Forget();
                },
                opts);
        }

        /// <summary>已过同意链时直接开进度窗下载（少见；一般用 <see cref="Start"/>）。</summary>
        public static UniTask DownloadWithProgress(
            IList<string> banks,
            Action onComplete = null,
            Action<IReadOnlyList<BankDownloadItem>> onBanksDone = null,
            Action onProgressDismissed = null,
            bool prioritize = true,
            bool preempt = true,
            bool loadIntoEngine = true,
            bool showProgressDialog = true) =>
            DownloadWithProgressAsync(
                banks,
                onComplete,
                onBanksDone,
                onProgressDismissed,
                prioritize,
                preempt,
                loadIntoEngine,
                showProgressDialog);

        static async UniTask DownloadWithProgressAsync(
            IList<string> banks,
            Action onComplete,
            Action<IReadOnlyList<BankDownloadItem>> onBanksDone,
            Action onProgressDismissed,
            bool prioritize,
            bool preempt,
            bool loadIntoEngine,
            bool showProgressDialog)
        {
            string[] pending = FilterPending(Snapshot(banks));
            if (pending.Length == 0)
            {
                InvokeSafe(onBanksDone, Array.Empty<BankDownloadItem>(), "onBanksDone");
                InvokeSafe(onComplete, "onComplete");
                return;
            }

            if (_busy)
            {
                await MergeIntoBusyDownload(
                    pending,
                    onComplete,
                    onBanksDone,
                    prioritize,
                    preempt,
                    loadIntoEngine);
                return;
            }

            _busy = true;
            int ticket = 0;
            IReadOnlyList<BankDownloadItem> result = Array.Empty<BankDownloadItem>();
            try
            {
                if (showProgressDialog && GameEntry.UI != null)
                {
                    ticket = UiDialogService.OpenDownloadProgress(onProgressDismissed);
                    _activeTicket = ticket;
                }

                result = await RunDownloadBanks(
                    pending,
                    ticket,
                    loadIntoEngine,
                    prioritize,
                    preempt);

                if (ticket > 0)
                    PushProgress(ticket, pending);

                BankDownloader.InvalidateLocalReadyCache();
                InvokeSafe(onBanksDone, result, "onBanksDone");
            }
            catch (Exception e)
            {
                Log.Warning("[UserBankDownloadFlow] download failed: " + e.Message);
            }
            finally
            {
                if (ticket > 0)
                    UiDialogService.CloseDownloadProgress(ticket);
                if (_activeTicket == ticket)
                    _activeTicket = 0;
                DownloadCellularGate.RevokeCellularAllow();
                _busy = false;
                InvokeSafe(onComplete, "onComplete");
            }
        }

        /// <summary>
        /// 已有批次在跑：并入 <see cref="BankDownloader"/> 队列，复用进度票；
        /// 不关窗、不清 <see cref="_busy"/>、不 Revoke（由持有进度窗的那一路 finally 负责）。
        /// </summary>
        static async UniTask MergeIntoBusyDownload(
            string[] pending,
            Action onComplete,
            Action<IReadOnlyList<BankDownloadItem>> onBanksDone,
            bool prioritize,
            bool preempt,
            bool loadIntoEngine)
        {
            int ticket = _activeTicket;
            IReadOnlyList<BankDownloadItem> result = Array.Empty<BankDownloadItem>();
            try
            {
                Log.Info(
                    $"[UserBankDownloadFlow] merge into busy count={pending.Length} " +
                    $"ticket={ticket}");
                result = await RunDownloadBanks(
                    pending,
                    ticket,
                    loadIntoEngine,
                    prioritize,
                    preempt);
                if (ticket > 0)
                    PushProgress(ticket, pending);
                BankDownloader.InvalidateLocalReadyCache();
                InvokeSafe(onBanksDone, result, "onBanksDone");
            }
            catch (Exception e)
            {
                Log.Warning("[UserBankDownloadFlow] merge download failed: " + e.Message);
            }
            finally
            {
                InvokeSafe(onComplete, "onComplete");
            }
        }

        static async UniTask<IReadOnlyList<BankDownloadItem>> RunDownloadBanks(
            IList<string> pending,
            int ticket,
            bool loadIntoEngine,
            bool prioritize,
            bool preempt)
        {
            List<BankRemoteInfo> remotes = PresetBankCatalog.ResolveRemotes(pending);
            if (remotes == null || remotes.Count == 0)
            {
                Log.Warning("[UserBankDownloadFlow] no catalog remotes");
                return Array.Empty<BankDownloadItem>();
            }

            return await BankDownloader.DownloadBanks(
                remotes,
                loadIntoEngine: loadIntoEngine,
                onComplete: null,
                onItemDone: null,
                onItemProgress: item =>
                {
                    if (ticket <= 0 || item == null)
                        return;
                    PushProgress(ticket, pending);
                },
                prioritize: prioritize,
                preempt: preempt);
        }

        static void CompleteAlreadyReady(
            Action onDownloadStarting,
            Action<IReadOnlyList<BankDownloadItem>> onBanksDone,
            Action onComplete)
        {
            Log.Info("[UserBankDownloadFlow] all banks local ready — skip consent/progress");
            BankDownloader.InvalidateLocalReadyCache();
            try
            {
                onDownloadStarting?.Invoke();
            }
            catch (Exception e)
            {
                Log.Warning("[UserBankDownloadFlow] onDownloadStarting: " + e.Message);
            }

            InvokeSafe(onBanksDone, Array.Empty<BankDownloadItem>(), "onBanksDone");
            InvokeSafe(onComplete, "onComplete");
        }

        static void PushProgress(int ticket, IList<string> banks)
        {
            float downloaded = 0f;
            float total = 0f;
            if (banks != null)
            {
                for (int i = 0; i < banks.Count; i++)
                {
                    string name = banks[i];
                    if (string.IsNullOrEmpty(name))
                        continue;
                    if (!BankDownloader.TryGetActiveItem(name, out BankDownloadItem item) || item == null)
                        continue;
                    float sizeMb = 0f;
                    if (long.TryParse(item.SizeKb, out long kb) && kb > 0)
                        sizeMb = kb / 1024f;
                    total += sizeMb;
                    downloaded += sizeMb * Mathf.Clamp01(item.Progress);
                }
            }

            float percent = total > 0.001f ? downloaded / total * 100f : 0f;
            UiDialogService.SetDownloadProgress(ticket, percent, downloaded, total);
        }

        static string[] Snapshot(IList<string> banks)
        {
            if (banks == null || banks.Count == 0)
                return Array.Empty<string>();
            var list = new List<string>(banks.Count);
            for (int i = 0; i < banks.Count; i++)
            {
                string n = banks[i];
                if (!string.IsNullOrEmpty(n))
                    list.Add(n);
            }

            return list.ToArray();
        }

        /// <summary>去掉已 <see cref="BankDownloader.IsLocalReady"/> 的 stem；调用方可仍传完整列表。</summary>
        public static string[] FilterPending(IList<string> banks)
        {
            if (banks == null || banks.Count == 0)
                return Array.Empty<string>();
            var list = new List<string>(banks.Count);
            for (int i = 0; i < banks.Count; i++)
            {
                string n = banks[i];
                if (string.IsNullOrEmpty(n))
                    continue;
                if (BankDownloader.IsLocalReady(n))
                    continue;
                list.Add(n);
            }

            return list.ToArray();
        }

        static void InvokeSafe(Action action, string label)
        {
            if (action == null)
                return;
            try
            {
                action();
            }
            catch (Exception e)
            {
                Log.Warning($"[UserBankDownloadFlow] {label}: " + e.Message);
            }
        }

        static void InvokeSafe(
            Action<IReadOnlyList<BankDownloadItem>> action,
            IReadOnlyList<BankDownloadItem> arg,
            string label)
        {
            if (action == null)
                return;
            try
            {
                action(arg);
            }
            catch (Exception e)
            {
                Log.Warning($"[UserBankDownloadFlow] {label}: " + e.Message);
            }
        }
    }
}
