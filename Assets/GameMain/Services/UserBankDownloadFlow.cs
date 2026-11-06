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
    /// FeatureOn+流量：仅本次 banks；「继续下载」会话开闸至回 Wi‑Fi / 离岛（批次结束不 Revoke）。
    /// FeatureOff：Bootstrap 静默全量 + 蜂窝自动开闸零弹窗；点下载走常规 pending。
    /// Busy：区域进度窗占用时新批 Toast 拒绝；无进度窗的软占用可被列表点击或区域门抢占并优先插队。
    /// </summary>
    public static class UserBankDownloadFlow
    {
        /// <summary>用户批次占用中拒绝新批时的提示文案。</summary>
        public const string BusyToastText = "已有资源包在下载中";

        static int _activeTicket;
        static bool _busy;
        /// <summary>占批代数：软占用被抢占时自增，旧批 finally 不得清掉新批的 busy。</summary>
        static int _busySerial;

        /// <summary>进行中的进度窗票据（区域门重开进度用）。</summary>
        public static int ActiveProgressTicket => _activeTicket;

        public static bool IsBusy => _busy;

        /// <summary>
        /// 跑同意链；通过后按选项开进度窗并下载。
        /// <paramref name="onComplete"/> 在关窗之后调用（成功或失败都调）。
        /// <paramref name="onBanksDone"/> 在下载 await 返回后调用（可空）；进度窗在传输齐时已关，不等本回调。
        /// <paramref name="occupyUserBusy"/> false：邻近环境音等自动 Kick，不挡列表点击。
        /// <paramref name="preemptBusyBatch"/> true：无进度窗软占用时可抢占并优先当前项。
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
            bool abortOtherUserDownloads = false,
            bool occupyUserBusy = true,
            bool preemptBusyBatch = false)
        {
            string[] clicked = FilterPending(Snapshot(banks));
            if (clicked.Length == 0)
            {
                CompleteAlreadyReady(onDownloadStarting, onBanksDone, onComplete);
                return;
            }

            int busySerial = 0;
            if (occupyUserBusy)
            {
                if (ShouldRejectNewBatch(preemptBusyBatch))
                {
                    RejectBusyBatch(onComplete, opts.OnCancel);
                    return;
                }

                if (_busy)
                {
                    // 无票软占用（预设环/音乐行）被列表点击抢占。
                    _busySerial++;
                    Log.Info(
                        "[UserBankDownloadFlow] preempt soft busy — serial=" + _busySerial);
                }
                else
                {
                    _busy = true;
                }

                busySerial = _busySerial;
            }

            // 同意链体积：FeatureOn+WiFi 未确认前用全部剩余；下载/进度始终只用本次。
            string[] consentBanks = BackgroundDownloadCoordinator.BanksForWifiConsentSize(clicked);
            bool showProgress = !opts.SuppressProgressDialog;

            Action prevCancel = opts.OnCancel;
            opts.OnCancel = () =>
            {
                ReleaseBusy(occupyUserBusy, busySerial);
                try
                {
                    prevCancel?.Invoke();
                }
                catch (Exception e)
                {
                    Log.Warning("[UserBankDownloadFlow] OnCancel: " + e.Message);
                }
            };

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
                        ReleaseBusy(occupyUserBusy, busySerial);
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
                            abortOtherUserDownloads,
                            busyAlreadyHeld: occupyUserBusy,
                            occupyUserBusy: occupyUserBusy,
                            busySerial: busySerial)
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
                abortOtherUserDownloads,
                busyAlreadyHeld: false,
                occupyUserBusy: true,
                busySerial: _busySerial);

        static UniTask DownloadWithProgress(
            IList<string> banks,
            Action onComplete,
            Action<IReadOnlyList<BankDownloadItem>> onBanksDone,
            Action onProgressDismissed,
            bool prioritize,
            bool preempt,
            bool loadIntoEngine,
            bool showProgressDialog,
            bool abortOtherUserDownloads,
            bool busyAlreadyHeld,
            bool occupyUserBusy,
            int busySerial) =>
            DownloadWithProgressAsync(
                banks,
                onComplete,
                onBanksDone,
                onProgressDismissed,
                prioritize,
                preempt,
                loadIntoEngine,
                showProgressDialog,
                abortOtherUserDownloads,
                busyAlreadyHeld,
                occupyUserBusy,
                busySerial);

        static async UniTask DownloadWithProgressAsync(
            IList<string> banks,
            Action onComplete,
            Action<IReadOnlyList<BankDownloadItem>> onBanksDone,
            Action onProgressDismissed,
            bool prioritize,
            bool preempt,
            bool loadIntoEngine,
            bool showProgressDialog,
            bool abortOtherUserDownloads,
            bool busyAlreadyHeld,
            bool occupyUserBusy,
            int busySerial)
        {
            string[] pending = FilterPending(Snapshot(banks));
            if (pending.Length == 0)
            {
                if (busyAlreadyHeld)
                    ReleaseBusy(occupyUserBusy, busySerial);
                InvokeSafe(onBanksDone, Array.Empty<BankDownloadItem>(), "onBanksDone");
                InvokeSafe(onComplete, "onComplete");
                return;
            }

            if (!busyAlreadyHeld)
            {
                if (occupyUserBusy)
                {
                    if (_busy)
                    {
                        RejectBusyBatch(onComplete, onCancel: null);
                        return;
                    }

                    _busy = true;
                    busySerial = _busySerial;
                }
            }

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

                // 传输齐时 PushProgress 已关窗；批结束再关一次兜底（失败/未推满进度）。
                // 必须在 onBanksDone 之前关，避免同步翻牌/进场拖住弹窗。
                if (ticket > 0)
                {
                    PushProgress(ticket, pending);
                    UiDialogService.CloseDownloadProgress(ticket);
                }

                // 先写正向缓存再 onBanksDone：PresetForm 同步段内 IsLocalReady 须已为正，
                // 否则会清 _userTriggered 并 force 刷成「未下载」，且闲置后不再翻牌。
                MarkResultLocalReady(result);
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
                ReleaseBusy(occupyUserBusy, busySerial);
                InvokeSafe(onComplete, "onComplete");
            }
        }

        /// <summary>仅当前占批代数可清 busy（被抢占的旧批忽略）。</summary>
        static void ReleaseBusy(bool occupyUserBusy, int busySerial)
        {
            if (!occupyUserBusy)
                return;
            if (_busySerial != busySerial)
                return;
            _busy = false;
            if (_activeTicket != 0)
                _activeTicket = 0;
        }

        /// <summary>Busy 拒绝：Toast + 收尾回调；不入队、不调成功 onBanksDone。</summary>
        static void RejectBusyBatch(Action onComplete, Action onCancel)
        {
            Log.Info("[UserBankDownloadFlow] reject busy — " + BusyToastText);
            if (GameEntry.UI != null)
                UiDialogService.ShowToast(BusyToastText);
            InvokeSafe(onCancel, "OnCancel");
            InvokeSafe(onComplete, "onComplete");
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
            // HTTP 写完 Progress=1 后仍要 MD5/Move；传输齐就关窗，勿等 DownloadBanks await。
            if (AreBanksTransferComplete(banks))
                UiDialogService.CloseDownloadProgress(ticket);
        }

        /// <summary>
        /// 本批传输是否已齐：已 <see cref="BankDownloader.IsLocalReady"/>，
        /// 或活跃项 <c>Progress≥1</c>（HTTP 写完、MD5 校验前）。
        /// 用于进度窗早关，避免大文件校验拖住弹窗。
        /// </summary>
        public static bool AreBanksTransferComplete(IList<string> banks)
        {
            if (banks == null || banks.Count == 0)
                return true;

            for (int i = 0; i < banks.Count; i++)
            {
                string name = banks[i];
                if (string.IsNullOrEmpty(name))
                    continue;
                if (BankDownloader.IsLocalReady(name))
                    continue;
                if (BankDownloader.TryGetActiveItem(name, out BankDownloadItem item)
                    && item != null
                    && item.Progress >= 0.999f)
                    continue;
                return false;
            }

            return true;
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

        /// <summary>
        /// 是否应 Toast 拒绝新批：硬占用（有进度窗）始终拒；软占用仅当未请求抢占时拒。
        /// </summary>
        public static bool ShouldRejectNewBatch(bool preemptBusyBatch) =>
            _busy && (!preemptBusyBatch || _activeTicket > 0);

#if UNITY_EDITOR
        /// <summary>单测：注入 busy / 进度票。</summary>
        public static void EditorForceBusy(bool busy, int activeTicket = 0)
        {
            _busy = busy;
            _activeTicket = activeTicket;
            if (!busy)
                _busySerial = 0;
        }
#endif

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
