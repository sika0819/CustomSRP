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
    /// <see cref="DownloadConsentSession.EnsureReady"/> →（可选）进度窗 → <see cref="BankDownloader"/> → 关窗。
    /// FeatureOn+WiFi：同意链体积可按全部剩余；确认后 KickSilentAll；进度/await 只用本次 banks。
    /// FeatureOn+流量：仅本次 banks，一次性开闸，批次结束 Revoke。
    /// FeatureOff：通常 Bootstrap 已静默/全量确认；点下载走常规 pending。
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
        /// <paramref name="onComplete"/> 在关窗之后调用（成功或失败都调）。
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
            bool loadIntoEngine = true,
            bool abortOtherUserDownloads = false)
        {
            string[] clicked = FilterPending(Snapshot(banks));
            if (clicked.Length == 0)
            {
                CompleteAlreadyReady(onDownloadStarting, onBanksDone, onComplete);
                return;
            }

            // 同意链体积：FeatureOn+WiFi 未确认前用全部剩余；下载/进度始终只用本次。
            string[] consentBanks = BackgroundDownloadCoordinator.BanksForWifiConsentSize(clicked);
            bool showProgress = !opts.SuppressProgressDialog;

            DownloadConsentSession.EnsureReady(
                consentBanks,
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

                    // 先开本区/本次进度窗，再 Kick 静默全量：避免 Critical/Rest Forget
                    // 抢在 OpenDownloadProgress 前占 Dialog 栈（流量切 Wi‑Fi → §4.6 后尤甚）。
                    string[] toDownload = FilterPending(clicked);
                    if (toDownload.Length == 0)
                    {
                        BackgroundDownloadCoordinator.KickSilentAllIfNeeded();
                        InvokeSafe(onBanksDone, Array.Empty<BankDownloadItem>(), "onBanksDone");
                        InvokeSafe(onComplete, "onComplete");
                        return;
                    }

                    DownloadWithProgress(
                            toDownload,
                            onComplete,
                            onBanksDone,
                            onProgressDismissed,
                            prioritize,
                            preempt,
                            loadIntoEngine,
                            showProgress,
                            abortOtherUserDownloads)
                        .Forget();

                    // FeatureOn+WiFi：确认后（及之后）静默全量，同关弹窗；不占 User 进度批。
                    BackgroundDownloadCoordinator.KickSilentAllIfNeeded();
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
            bool showProgressDialog = true,
            bool abortOtherUserDownloads = false) =>
            DownloadWithProgressAsync(
                banks,
                onComplete,
                onBanksDone,
                onProgressDismissed,
                prioritize,
                preempt,
                loadIntoEngine,
                showProgressDialog,
                abortOtherUserDownloads);

        static async UniTask DownloadWithProgressAsync(
            IList<string> banks,
            Action onComplete,
            Action<IReadOnlyList<BankDownloadItem>> onBanksDone,
            Action onProgressDismissed,
            bool prioritize,
            bool preempt,
            bool loadIntoEngine,
            bool showProgressDialog,
            bool abortOtherUserDownloads)
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
                    onProgressDismissed,
                    prioritize,
                    preempt,
                    loadIntoEngine,
                    showProgressDialog,
                    abortOtherUserDownloads);
                return;
            }

            _busy = true;
            int ticket = 0;
            IReadOnlyList<BankDownloadItem> result = Array.Empty<BankDownloadItem>();
            try
            {
                // 同意链等待期间 Rest 可能已落盘：再滤一次，避免空开进度窗闪一下就关。
                pending = FilterPending(pending);
                if (pending.Length == 0)
                {
                    InvokeSafe(onBanksDone, Array.Empty<BankDownloadItem>(), "onBanksDone");
                    return;
                }

                if (showProgressDialog && GameEntry.UI != null)
                {
                    ticket = UiDialogService.OpenDownloadProgress(onProgressDismissed);
                    _activeTicket = ticket;
                    // 先推本区总量，避免 KickSilentAll 抢跑期间窗上一直是 0/0。
                    PushProgress(ticket, pending);
                }

                result = await RunDownloadBanks(
                    pending,
                    ticket,
                    loadIntoEngine,
                    prioritize,
                    preempt,
                    abortOtherUserDownloads);

                if (ticket > 0)
                    PushProgress(ticket, pending);

                // 先 onBanksDone；就绪 bank 写入正向缓存（勿全量 Invalidate，多预设并下会把已完成卡闪回未下载）。
                InvokeSafe(onBanksDone, result, "onBanksDone");
                MarkResultLocalReady(result);
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
                _busy = false;
                InvokeSafe(onComplete, "onComplete");
            }
        }

        /// <summary>
        /// 已有批次在跑：并入 <see cref="BankDownloader"/> 队列。
        /// 复用已有进度票；若持有方 SuppressProgress（无票）而本路要进度，则在此开窗，并在本路结束时关。
        /// 不清 <see cref="_busy"/>（由首路 finally 负责）。
        /// </summary>
        static async UniTask MergeIntoBusyDownload(
            string[] pending,
            Action onComplete,
            Action<IReadOnlyList<BankDownloadItem>> onBanksDone,
            Action onProgressDismissed,
            bool prioritize,
            bool preempt,
            bool loadIntoEngine,
            bool showProgressDialog,
            bool abortOtherUserDownloads)
        {
            int ticket = _activeTicket;
            bool openedProgressHere = false;
            if (showProgressDialog
                && GameEntry.UI != null
                && (ticket <= 0 || UiDialogService.IsProgressRetired(ticket)))
            {
                ticket = UiDialogService.OpenDownloadProgress(onProgressDismissed);
                _activeTicket = ticket;
                openedProgressHere = ticket > 0;
                if (ticket > 0)
                    PushProgress(ticket, pending);
            }

            IReadOnlyList<BankDownloadItem> result = Array.Empty<BankDownloadItem>();
            try
            {
                Log.Info(
                    $"[UserBankDownloadFlow] merge into busy count={pending.Length} " +
                    $"ticket={ticket} openedProgress={openedProgressHere} " +
                    $"abortOtherUser={abortOtherUserDownloads}");
                result = await RunDownloadBanks(
                    pending,
                    ticket,
                    loadIntoEngine,
                    prioritize,
                    preempt,
                    abortOtherUserDownloads);
                if (ticket > 0)
                    PushProgress(ticket, pending);
                InvokeSafe(onBanksDone, result, "onBanksDone");
                MarkResultLocalReady(result);
            }
            catch (Exception e)
            {
                Log.Warning("[UserBankDownloadFlow] merge download failed: " + e.Message);
            }
            finally
            {
                if (openedProgressHere && ticket > 0)
                {
                    UiDialogService.CloseDownloadProgress(ticket);
                    if (_activeTicket == ticket)
                        _activeTicket = 0;
                }

                InvokeSafe(onComplete, "onComplete");
            }
        }

        static void MarkResultLocalReady(IReadOnlyList<BankDownloadItem> result)
        {
            if (result == null || result.Count == 0)
                return;
            var names = new List<string>(result.Count);
            for (int i = 0; i < result.Count; i++)
            {
                var item = result[i];
                if (item == null || string.IsNullOrEmpty(item.BankName))
                    continue;
                if (item.State == BankDownloadState.Ready)
                    names.Add(item.BankName);
            }

            if (names.Count > 0)
                BankDownloader.MarkLocalReady(names);
        }

        static async UniTask<IReadOnlyList<BankDownloadItem>> RunDownloadBanks(
            IList<string> pending,
            int ticket,
            bool loadIntoEngine,
            bool prioritize,
            bool preempt,
            bool abortOtherUserDownloads)
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
                preempt: preempt,
                abortOtherUserDownloads: abortOtherUserDownloads);
        }

        static void CompleteAlreadyReady(
            Action onDownloadStarting,
            Action<IReadOnlyList<BankDownloadItem>> onBanksDone,
            Action onComplete)
        {
            Log.Info("[UserBankDownloadFlow] all banks local ready — skip consent/progress");
            try
            {
                onDownloadStarting?.Invoke();
            }
            catch (Exception e)
            {
                Log.Warning("[UserBankDownloadFlow] onDownloadStarting: " + e.Message);
            }

            // 已就绪短路：勿全量 Invalidate（会清掉 onBanksDone 刚写入的正向 ready 缓存）。
            InvokeSafe(onBanksDone, Array.Empty<BankDownloadItem>(), "onBanksDone");
            InvokeSafe(onComplete, "onComplete");
        }

        static void PushProgress(int ticket, IList<string> banks)
        {
            AggregateBanksProgress(banks, out float downloaded, out float total, out float percent);
            UiDialogService.SetDownloadProgress(ticket, percent, downloaded, total);
        }

        /// <summary>
        /// 按固定 bank 列表聚合进度（区域门 / 预设共用语义）。
        /// 已 <see cref="BankDownloader.IsLocalReady"/> 计满；Entries 已 Remove 的完成项仍计入。
        /// 体积优先 catalog，避免完成项出队后 total 缩水导致百分比回退。
        /// </summary>
        public static void AggregateBanksProgress(
            IList<string> banks,
            out float downloadedMb,
            out float totalMb,
            out float percent)
        {
            downloadedMb = 0f;
            totalMb = 0f;
            if (banks == null || banks.Count == 0)
            {
                percent = 0f;
                return;
            }

            for (int i = 0; i < banks.Count; i++)
            {
                string name = banks[i];
                if (string.IsNullOrEmpty(name))
                    continue;

                float sizeMb = ResolveBankSizeMb(name);
                totalMb += sizeMb;

                if (BankDownloader.IsLocalReady(name))
                {
                    downloadedMb += sizeMb;
                    continue;
                }

                if (BankDownloader.TryGetActiveItem(name, out BankDownloadItem item) && item != null)
                    downloadedMb += sizeMb * Mathf.Clamp01(item.Progress);
            }

            percent = totalMb > 0.001f ? downloadedMb / totalMb * 100f : 0f;
        }

        static float ResolveBankSizeMb(string bankName)
        {
            string stem = AudioEnginePaths.StripBankExtension(bankName);
            if (string.IsNullOrEmpty(stem))
                stem = bankName;

            if (PresetBankCatalog.TryGet(stem, out BankRemoteInfo remote)
                && remote != null
                && !string.IsNullOrEmpty(remote.size)
                && long.TryParse(remote.size, out long catalogKb)
                && catalogKb > 0)
                return catalogKb / 1024f;

            if (BankDownloader.TryGetActiveItem(stem, out BankDownloadItem active)
                && active != null
                && long.TryParse(active.SizeKb, out long activeKb)
                && activeKb > 0)
                return activeKb / 1024f;

            // catalog/队列皆无体积时仍占位，避免整批被跳过变成 0/0。
            return 1f;
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
