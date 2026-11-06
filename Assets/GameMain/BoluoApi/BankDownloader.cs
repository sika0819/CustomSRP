using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityGameFramework.Runtime;
using Game.Utility;
using UnityEngine;
using UnityEngine.Networking;

namespace Game.BoluoApi
{
    public enum BankDownloadState
    {
        Pending,
        Downloading,
        Ready,
        Failed,
        Skipped,
    }

    public sealed class BankDownloadItem
    {
        /// <summary>空 url 跳过时写入 <see cref="Error"/>；IsLocalReady 不得当 Ready。</summary>
        public const string UrlEmptyError = "url empty";
        public const string Md5EmptyError = "md5 empty";

        public string BankName;
        public string Url;
        public string Md5;
        public string SizeKb;
        public string Guid;
        /// <summary>服务端 bank 内容版本。与 md5 一起决定本地是否就绪。</summary>
        public string Version;
        public string LocalPath;
        public BankDownloadState State = BankDownloadState.Pending;
        public float Progress;
        public string Error;
        public string SkipReason;
        public int AttemptCount;
        /// <summary>上次内容校验失败时 tmp 的实际 md5。再碰到同一个值就停止重下这条。</summary>
        public string LastMismatchMd5;
    }

    public enum BankQueueKind : byte
    {
        Normal = 0,
        Startup = 1,
        User = 2,
    }

    /// <summary>
    /// 按 upgrade/bank 返回的 banks[] 下载到 <see cref="PersistentBankPath"/>。
    /// 三档：用户点击（区域门 / 音乐 / 预设）最先且可打断；静默先下完 Music*（含失败重试）；
    /// 再下预设 / 氛围，同时最多 <see cref="MaxBackgroundSlots"/> 路。
    /// </summary>
    public static class BankDownloader
    {
        static readonly Http.RetryPolicy Retry = Http.RetryPolicy.DefaultDownload;

        /// <summary>整队 Failed 再入队的最大轮次（防止无限循环）。</summary>
        const int MaxFailedRequeuePasses = 2;

        /// <summary>主线程每处理几个已就绪 bank 让出一帧（预留给新音频 SDK 灌引擎）。</summary>
        const int LoadBanksPerFrame = 2;

        const int MaxStartupSlots = 3;
        /// <summary>静默 Rest（Normal）并发上限；与 Startup 同为≤3，避免全串行过慢、也禁止全开。</summary>
        const int MaxBackgroundSlots = 3;
        static int _startupParallelSlots = MaxStartupSlots;
        static readonly HashSet<string> ServerGapToasted =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        static readonly Dictionary<string, QueueEntry> Entries =
            new Dictionary<string, QueueEntry>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 自动重试用尽后仍失败的 stem。条目出队后 UI 仍显示重试，直到用户再点。
        /// </summary>
        static readonly HashSet<string> TerminalFailed =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// <see cref="LocalSatisfiesCatalog"/> 磁盘结果缓存。PresetForm 进度 tick 会高频调用；
        /// 未缓存时每张卡每 bank 反复 File.Exists/FileInfo，闺蜜机开预设面板会掉帧。
        /// </summary>
        static readonly Dictionary<string, LocalReadyCacheEntry> LocalReadyCache =
            new Dictionary<string, LocalReadyCacheEntry>(StringComparer.OrdinalIgnoreCase);

        /// <summary>未就绪结果短 TTL，便于下载完成后尽快翻牌；已就绪结果靠显式 Invalidate。</summary>
        const float LocalReadyNegativeTtlSeconds = 1f;

        static readonly List<string> UserQueue = new List<string>();
        static readonly List<string> StartupQueue = new List<string>();
        static readonly List<string> NormalQueue = new List<string>();
        static readonly List<ActiveSlot> Actives = new List<ActiveSlot>();

        static bool _runnerActive;
        static bool _pausedForLeave;

        struct LocalReadyCacheEntry
        {
            public bool Ready;
            public float ExpireUnscaled;
        }

        /// <summary>
        /// 进度 / 状态变更单调计数；PresetForm 用它跳过无变化的全卡扫描。
        /// </summary>
        public static int ProgressEpoch { get; private set; }

        /// <summary>
        /// Bank 已落盘就绪（未必已灌引擎）。<c>loadIntoEngine=false</c> 时不会走
        /// <see cref="WanosAudio.WanosBankStore.OnBankLoaded"/>，环境音等须订此事件立刻重试开播。
        /// </summary>
        public static event Action<string> OnBankLocalReady;

        /// <summary>队列或活跃槽非空 —— 有下载在跑或排队。</summary>
        public static bool HasBusyWork =>
            Actives.Count > 0
            || UserQueue.Count > 0
            || StartupQueue.Count > 0
            || NormalQueue.Count > 0;

        /// <summary>
        /// 离岛：中止进行中的 UnityWebRequest，队列先留着但不泵。
        /// Abort 走 <c>removeFileOnAbort=false</c>，半成品 .tmp 留下次续传。
        /// </summary>
        public static void PauseForLeave()
        {
            _pausedForLeave = true;
            for (int i = 0; i < Actives.Count; i++)
            {
                if (Actives[i] != null)
                    Actives[i].Abort = true;
            }

            Log.Info(
                $"[BankDownloader] pause for leave actives={Actives.Count} " +
                $"userQ={UserQueue.Count} startQ={StartupQueue.Count} normalQ={NormalQueue.Count}");
        }

        /// <summary>换票成功后恢复离岛前暂停的队列。</summary>
        public static void ResumeAfterLeave()
        {
            if (!_pausedForLeave)
                return;
            _pausedForLeave = false;
            Log.Info("[BankDownloader] resume after leave");
            if (UserQueue.Count > 0 || StartupQueue.Count > 0 || NormalQueue.Count > 0 || Actives.Count > 0)
                EnsureRunner();
        }

        public static void ResetSession()
        {
            _pausedForLeave = false;
            _runnerActive = false;
            _startupParallelSlots = MaxStartupSlots;
            Entries.Clear();
            LocalReadyCache.Clear();
            UserQueue.Clear();
            StartupQueue.Clear();
            NormalQueue.Clear();
            Actives.Clear();
            TerminalFailed.Clear();
            ServerGapToasted.Clear();
            ProgressEpoch = 0;
            DownloadCellularGate.ResetForNewSession();
        }

        /// <summary>该 stem 是否已用尽自动重试、等用户再点。</summary>
        public static bool HasTerminalFailure(string bankName)
        {
            if (string.IsNullOrEmpty(bankName))
                return false;
            string stem = AudioEnginePaths.StripBankExtension(bankName);
            return !string.IsNullOrEmpty(stem) && TerminalFailed.Contains(stem);
        }

        /// <summary>
        /// 传输失败且还没成功：含自动重试排队中、正在静默重下、以及用尽后的终态。
        /// 真正 <see cref="BankDownloadState.Downloading"/> 时音乐行仍显示进度环；预设卡另看是否用户点击。
        /// </summary>
        public static bool HasUnresolvedFailure(string bankName)
        {
            if (HasTerminalFailure(bankName))
                return true;
            string stem = AudioEnginePaths.StripBankExtension(bankName);
            if (string.IsNullOrEmpty(stem))
                return false;
            if (!Entries.TryGetValue(stem, out QueueEntry entry) || entry?.Item == null)
                return false;
            if (entry.Item.State == BankDownloadState.Ready
                || entry.Item.State == BankDownloadState.Skipped)
                return false;
            if (entry.Item.State == BankDownloadState.Failed)
                return true;
            return entry.RequeueCount > 0;
        }

        /// <summary>仍在用户槽排队或占用用户活跃槽（点音乐 / 点预设 / 区域门）。</summary>
        public static bool IsUserAttemptLive(string bankName)
        {
            string stem = AudioEnginePaths.StripBankExtension(bankName);
            if (string.IsNullOrEmpty(stem))
                return false;
            if (QueueContains(UserQueue, stem))
                return true;
            for (int i = 0; i < Actives.Count; i++)
            {
                ActiveSlot slot = Actives[i];
                if (slot == null || slot.Kind != BankQueueKind.User)
                    continue;
                if (string.Equals(slot.BankName, stem, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// 用户点重试：清掉「已经失败不用再下」的标记，并把仍停在 Failed / 空 url Skipped 的条目改回 Pending。
        /// 自动重试次数只限制没人点时的循环，不拦住这一次。
        /// </summary>
        public static void ArmUserRetry(IList<string> bankNames)
        {
            if (bankNames == null || bankNames.Count == 0)
                return;

            for (int i = 0; i < bankNames.Count; i++)
            {
                string name = bankNames[i];
                if (string.IsNullOrEmpty(name))
                    continue;
                string stem = AudioEnginePaths.StripBankExtension(name);
                if (string.IsNullOrEmpty(stem))
                    continue;

                ForgetTerminalFailure(stem);
                if (!Entries.TryGetValue(stem, out QueueEntry entry) || entry?.Item == null)
                    continue;
                if (IsLocalReady(stem))
                    continue;
                // 正在传的槽等抢占 Abort 收尾，但点击已经续上重试额度。
                if (entry.Item.State == BankDownloadState.Downloading)
                {
                    entry.RequeueCount = 0;
                    continue;
                }

                ResetEntryForUserRetry(entry);
            }
        }

        /// <summary>
        /// 流量窗「继续」前 Priority：把待下 bank 提到 User 队列头。
        /// 须在 <see cref="DownloadCellularGate.UserAllowCellularAlways"/> 之前调用。
        /// </summary>
        public static void PriorityBanks(IList<string> bankNames)
        {
            if (bankNames == null || bankNames.Count == 0)
                return;

            for (int i = bankNames.Count - 1; i >= 0; i--)
            {
                string name = bankNames[i];
                if (string.IsNullOrEmpty(name))
                    continue;
                string stem = AudioEnginePaths.StripBankExtension(name);
                if (IsLocalReady(stem))
                    continue;

                if (!Entries.TryGetValue(stem, out QueueEntry entry) || entry?.Item == null)
                {
                    if (!PresetBankCatalog.TryGet(stem, out BankRemoteInfo remote) || remote == null)
                        continue;
                    List<BankDownloadItem> built = BuildQueue(new[] { remote });
                    if (built.Count == 0 || string.IsNullOrEmpty(built[0].Url))
                        continue;
                    entry = new QueueEntry
                    {
                        Item = built[0],
                        LoadIntoEngine = true,
                        Kind = BankQueueKind.User,
                    };
                    Entries[stem] = entry;
                }
                else
                {
                    entry.Kind = BankQueueKind.User;
                    entry.LoadIntoEngine = true;
                }

                if (EntryNeedsUserRetryReset(entry))
                    ResetEntryForUserRetry(entry);
                PlaceInQueue(stem, BankQueueKind.User);
            }

            Log.Info($"[BankDownloader] PriorityBanks count={bankNames.Count}");
            EnsureRunner();
        }

        /// <summary>开闸后续泵；禁止与 Priority / Allow 叠第二次 Resume。</summary>
        public static void NotifyTransmitAllowed()
        {
            if (_pausedForLeave)
                return;
            EnsureRunner();
        }

        /// <summary>关闸：蜂窝上活跃槽 Abort 回队，等 Wi‑Fi 或再次开闸。</summary>
        public static void HoldForCellularPolicy()
        {
            if (DownloadCellularGate.CanTransmitNow())
                return;

            for (int i = 0; i < Actives.Count; i++)
            {
                if (Actives[i] != null)
                    Actives[i].Abort = true;
            }

            Log.Info(
                $"[BankDownloader] hold for cellular policy actives={Actives.Count} " +
                $"userQ={UserQueue.Count} startQ={StartupQueue.Count} normalQ={NormalQueue.Count}");
            if (!_pausedForLeave
                && (UserQueue.Count > 0 || StartupQueue.Count > 0 || NormalQueue.Count > 0 || Actives.Count > 0))
                EnsureRunner();
        }

        /// <summary>下载完成 / 落盘变更后清缓存；bankName 空则全清。</summary>
        public static void InvalidateLocalReadyCache(string bankName = null)
        {
            if (string.IsNullOrEmpty(bankName))
                LocalReadyCache.Clear();
            else
                LocalReadyCache.Remove(AudioEnginePaths.StripBankExtension(bankName));
            BumpProgressEpoch();
        }

        /// <summary>
        /// 确认盘上就绪后写入正向缓存，避免随后全量 Invalidate / Entries.Remove 导致 UI 闪回「未下载」。
        /// </summary>
        public static void MarkLocalReady(string bankName)
        {
            if (string.IsNullOrEmpty(bankName))
                return;
            string stem = AudioEnginePaths.StripBankExtension(bankName);
            CacheLocalReady(stem, true);
            ForgetTerminalFailure(stem);
            BumpProgressEpoch();
        }

        public static void MarkLocalReady(IList<string> bankNames)
        {
            if (bankNames == null || bankNames.Count == 0)
                return;
            for (int i = 0; i < bankNames.Count; i++)
            {
                string n = bankNames[i];
                if (string.IsNullOrEmpty(n))
                    continue;
                string stem = AudioEnginePaths.StripBankExtension(n);
                CacheLocalReady(stem, true);
                ForgetTerminalFailure(stem);
            }

            BumpProgressEpoch();
        }

        /// <summary>该 bank 是否仍在队列 / 活跃槽（Pending 或 Downloading）。</summary>
        public static bool IsBankBusy(string bankName)
        {
            if (string.IsNullOrEmpty(bankName))
                return false;
            if (!TryGetActiveItem(bankName, out var item) || item == null)
                return false;
            return item.State == BankDownloadState.Pending
                || item.State == BankDownloadState.Downloading;
        }

        static void BumpProgressEpoch() => ProgressEpoch++;

        sealed class ActiveSlot
        {
            public string BankName;
            public BankQueueKind Kind;
            public bool Abort;
            /// <summary>
            /// 被 User 插队 AbortConflicting 时为 true：Abort 后 FireDone 释放旧批 await。
            /// 蜂窝 Hold / 离岛 Pause 为 false：保持 await，等回队续传后再 CompleteOne。
            /// </summary>
            public bool ReleaseWaitersOnAbort;
        }

        sealed class QueueEntry
        {
            public BankDownloadItem Item;
            public bool LoadIntoEngine;
            public BankQueueKind Kind;
            public int RequeueCount;
            public readonly List<Action<BankDownloadItem>> OnDone = new List<Action<BankDownloadItem>>();
            public readonly List<Action<BankDownloadItem>> OnProgress = new List<Action<BankDownloadItem>>();
        }

        public const string BankFolderName = AudioEnginePaths.BankFolderName;

        public static string PersistentBankPath => AudioEnginePaths.PersistentBankPath;

        public static string StreamingBankPath => AudioEnginePaths.StreamingBankPath;

        public static string ResolveLocalPath(string bankName)
        {
            if (AudioEnginePaths.TryFindLocalBank(bankName, out string existing))
                return existing;

            return PersistentDestPath(bankName);
        }

        static string PersistentDestPath(string bankName)
        {
            AudioEnginePaths.EnsureWritableDirs();
            string stem = AudioEnginePaths.StripBankExtension(bankName ?? "unknown");
            return Path.Combine(PersistentBankPath, stem + AudioEnginePaths.BankExtension);
        }

        public static bool IsLocalReady(string bankName)
        {
            return LocalSatisfiesCatalog(bankName);
        }

#if UNITY_EDITOR
        /// <summary>单测：注入队列项（如空 url Skipped）以验证 IsLocalReady 窗口。</summary>
        public static void EditorPutActiveItem(BankDownloadItem item)
        {
            if (item == null || string.IsNullOrEmpty(item.BankName))
                return;
            string stem = AudioEnginePaths.StripBankExtension(item.BankName);
            Entries[stem] = new QueueEntry
            {
                Item = item,
                LoadIntoEngine = false,
                Kind = BankQueueKind.Normal,
            };
            InvalidateLocalReadyCache(stem);
        }

        /// <summary>单测：标成自动重试用尽。</summary>
        public static void EditorNoteTerminalFailure(string bankName) =>
            RememberTerminalFailure(bankName);

        /// <summary>单测：读写自动重试轮次。</summary>
        public static void EditorSetRequeueCount(string bankName, int count)
        {
            string stem = AudioEnginePaths.StripBankExtension(bankName);
            if (string.IsNullOrEmpty(stem) || !Entries.TryGetValue(stem, out QueueEntry entry) || entry == null)
                return;
            entry.RequeueCount = count;
        }

        public static int EditorGetRequeueCount(string bankName)
        {
            string stem = AudioEnginePaths.StripBankExtension(bankName);
            if (string.IsNullOrEmpty(stem) || !Entries.TryGetValue(stem, out QueueEntry entry) || entry == null)
                return -1;
            return entry.RequeueCount;
        }

        /// <summary>单测：把 bank 放进指定队列（不跑 HTTP）。</summary>
        public static void EditorSeedQueue(string bankName, BankQueueKind kind)
        {
            if (string.IsNullOrEmpty(bankName))
                return;
            string stem = AudioEnginePaths.StripBankExtension(bankName);
            if (!Entries.TryGetValue(stem, out QueueEntry entry) || entry?.Item == null)
            {
                entry = new QueueEntry
                {
                    Item = new BankDownloadItem
                    {
                        BankName = stem,
                        Url = "https://example.test/" + stem,
                        State = BankDownloadState.Pending,
                    },
                    Kind = kind,
                };
                Entries[stem] = entry;
            }

            entry.Kind = kind;
            entry.Item.State = BankDownloadState.Pending;
            PlaceInQueue(stem, kind);
        }

        /// <summary>单测：走一档出队。</summary>
        public static bool EditorTryDequeueNext(out string bankName, out BankQueueKind kind) =>
            TryDequeueNext(out bankName, out kind);

        /// <summary>单测：模拟可重试失败，按音乐优先插回队列。</summary>
        public static bool EditorFailAndRequeue(string bankName)
        {
            string stem = AudioEnginePaths.StripBankExtension(bankName);
            if (string.IsNullOrEmpty(stem) || !Entries.TryGetValue(stem, out QueueEntry entry) || entry?.Item == null)
                return false;
            entry.Item.State = BankDownloadState.Failed;
            entry.Item.Error = "test-fail";
            if (entry.RequeueCount >= MaxFailedRequeuePasses)
                return false;
            EnqueueFailedRetry(entry);
            return true;
        }

        /// <summary>单测：清掉活跃槽，便于测预设并发上限。</summary>
        public static void EditorClearActives() => Actives.Clear();

        public static bool EditorShouldFinishWithoutRestart(
            BankDownloadState state,
            string error,
            int requeueCount,
            bool userRetry) =>
            ShouldFinishWithoutRestart(state, error, requeueCount, userRetry);

        /// <summary>单测：走完成通知（MarkLocalReady + OnBankLocalReady）。</summary>
        public static void EditorNotifyBankReady(string bankName) => NotifyBankReady(bankName);

        /// <summary>单测：注入活跃槽（测 AbortConflictingActives）。</summary>
        public static void EditorPutActiveSlot(string bankName, BankQueueKind kind)
        {
            if (string.IsNullOrEmpty(bankName))
                return;
            Actives.Add(new ActiveSlot { BankName = bankName, Kind = kind });
        }

        /// <summary>单测：触发 AbortConflictingActives。</summary>
        public static void EditorAbortConflictingActives(
            string keepBankName,
            bool abortOtherUserDownloads,
            ICollection<string> keepBatch = null) =>
            AbortConflictingActives(keepBankName, abortOtherUserDownloads, keepBatch);

        /// <summary>单测：某活跃槽是否被标 Abort。</summary>
        public static bool EditorIsActiveAbort(string bankName)
        {
            if (string.IsNullOrEmpty(bankName))
                return false;
            for (int i = 0; i < Actives.Count; i++)
            {
                var slot = Actives[i];
                if (slot == null)
                    continue;
                if (string.Equals(slot.BankName, bankName, StringComparison.OrdinalIgnoreCase))
                    return slot.Abort;
            }

            return false;
        }

        /// <summary>单测：抢占 Abort 是否标记 ReleaseWaitersOnAbort。</summary>
        public static bool EditorIsReleaseWaitersOnAbort(string bankName)
        {
            if (string.IsNullOrEmpty(bankName))
                return false;
            for (int i = 0; i < Actives.Count; i++)
            {
                var slot = Actives[i];
                if (slot == null)
                    continue;
                if (string.Equals(slot.BankName, bankName, StringComparison.OrdinalIgnoreCase))
                    return slot.ReleaseWaitersOnAbort;
            }

            return false;
        }

        /// <summary>单测：给已有 entry 挂 OnDone（模拟 DownloadBanks CompleteOne）。</summary>
        public static void EditorAttachDoneCallback(string bankName, Action<BankDownloadItem> onDone)
        {
            if (string.IsNullOrEmpty(bankName) || onDone == null)
                return;
            string stem = AudioEnginePaths.StripBankExtension(bankName);
            if (!Entries.TryGetValue(stem, out QueueEntry entry) || entry == null)
                return;
            entry.OnDone.Add(onDone);
        }

        /// <summary>
        /// 单测：模拟 RunSlot 抢占 Abort 收尾（Requeue + FireDone），验证旧批 OnDone 立刻触发。
        /// </summary>
        public static bool EditorSimulatePreemptAbortFireDone(string bankName)
        {
            if (string.IsNullOrEmpty(bankName))
                return false;
            string stem = AudioEnginePaths.StripBankExtension(bankName);
            if (!Entries.TryGetValue(stem, out QueueEntry entry) || entry?.Item == null)
                return false;

            ActiveSlot slot = null;
            for (int i = 0; i < Actives.Count; i++)
            {
                if (Actives[i] != null
                    && string.Equals(Actives[i].BankName, stem, StringComparison.OrdinalIgnoreCase))
                {
                    slot = Actives[i];
                    break;
                }
            }

            if (slot == null || !slot.Abort || !slot.ReleaseWaitersOnAbort)
                return false;

            entry.Item.State = BankDownloadState.Pending;
            entry.Item.Progress = 0f;
            entry.Item.Error = null;
            RequeueAfterAbort(stem, entry.Kind);
            FireDone(entry);
            Actives.Remove(slot);
            return true;
        }

        /// <summary>单测：写入已接受的 version + md5（只进内存，按文件 size/mtime）。</summary>
        public static void EditorRememberIdentity(string bankName, string version, string md5, string path = null)
        {
            string stem = AudioEnginePaths.StripBankExtension(bankName);
            if (string.IsNullOrEmpty(stem) || string.IsNullOrEmpty(md5))
                return;
            if (string.IsNullOrEmpty(path))
                path = Path.Combine(PersistentBankPath, stem + AudioEnginePaths.BankExtension);
            var fi = new FileInfo(path);
            if (!fi.Exists || fi.Length <= 0)
                return;
            BankMd5Cache.Load().Set(stem, fi.Length, fi.LastWriteTimeUtc.Ticks, md5, version ?? "");
        }

        /// <summary>单测：丢掉该 bank 的身份记录；若曾落盘则把缓存文件一并改回。</summary>
        public static void EditorForgetIdentity(string bankName)
        {
            string stem = AudioEnginePaths.StripBankExtension(bankName);
            if (string.IsNullOrEmpty(stem))
                return;
            var cache = BankMd5Cache.Load();
            if (cache.Remove(stem))
                cache.Save();
        }

        /// <summary>单测：读已接受的 version / md5（不校验 size/mtime）。</summary>
        public static bool EditorTryGetIdentity(string bankName, out string version, out string md5)
        {
            version = null;
            md5 = null;
            string stem = AudioEnginePaths.StripBankExtension(bankName);
            if (string.IsNullOrEmpty(stem))
                return false;
            return BankMd5Cache.Load().TryPeek(stem, out version, out md5);
        }

        /// <summary>单测：当前 Startup 并行槽上限。</summary>
        public static int EditorStartupParallelSlots => _startupParallelSlots;

        /// <summary>单测：Normal/Rest 并发上限。</summary>
        public static int EditorMaxBackgroundSlots => MaxBackgroundSlots;
#endif

        /// <summary>队列里正在下 / 等待的条目。UI 进度用这个，不要只看磁盘上是否已有旧文件。</summary>
        public static bool TryGetActiveItem(string bankName, out BankDownloadItem item)
        {
            item = null;
            if (string.IsNullOrEmpty(bankName))
                return false;
            string stem = AudioEnginePaths.StripBankExtension(bankName);
            if (string.IsNullOrEmpty(stem))
                return false;
            if (!Entries.TryGetValue(stem, out var entry) || entry?.Item == null)
                return false;
            item = entry.Item;
            return true;
        }

        /// <summary>
        /// 本地文件是否对得上 catalog。同名残缺文件 File.Exists 仍为 true，不能仅凭存在判定就绪。
        /// </summary>
        public static bool LocalSatisfiesCatalog(string bankName)
        {
            if (string.IsNullOrEmpty(bankName))
                return true;

            if (TryGetActiveItem(bankName, out var item))
            {
                if (item.State == BankDownloadState.Ready)
                    return true;
                // 线上缺 url / md5：有本地文件就当就绪，预设可以切；没有文件再回落探针。
                if (item.State == BankDownloadState.Skipped
                    && IsServerGapError(item.Error))
                {
                    if (ProbeLocalSatisfiesCatalog(bankName))
                    {
                        AcceptLocalDespiteServerGap(item);
                        return true;
                    }
                }
                else if (item.State == BankDownloadState.Skipped)
                    return true;
                // Downloading：完整本地文件仍算就绪。探针暂时失败时不要把已记下的正向结果写成未下载。
                if (item.State == BankDownloadState.Downloading)
                {
                    string stem = AudioEnginePaths.StripBankExtension(bankName);
                    bool probeWhileDl = ProbeLocalSatisfiesCatalog(bankName);
                    if (probeWhileDl)
                    {
                        CacheLocalReady(stem, true);
                        return true;
                    }

                    if (LocalReadyCache.TryGetValue(stem, out LocalReadyCacheEntry held) && held.Ready)
                        return true;

                    CacheLocalReady(stem, false);
                    return false;
                }
            }

            string cacheKey = AudioEnginePaths.StripBankExtension(bankName);
            float now = Time.unscaledTime;
            if (LocalReadyCache.TryGetValue(cacheKey, out var cached))
            {
                if (cached.Ready || now < cached.ExpireUnscaled)
                    return cached.Ready;
            }

            bool ready = ProbeLocalSatisfiesCatalog(bankName);
            CacheLocalReady(cacheKey, ready);
            return ready;
        }

        static void CacheLocalReady(string bankName, bool ready)
        {
            if (string.IsNullOrEmpty(bankName))
                return;
            string stem = AudioEnginePaths.StripBankExtension(bankName);
            float now = Time.unscaledTime;
            LocalReadyCache[stem] = new LocalReadyCacheEntry
            {
                Ready = ready,
                ExpireUnscaled = ready
                    ? float.PositiveInfinity
                    : now + LocalReadyNegativeTtlSeconds,
            };
        }

        /// <summary>
        /// 盘上是否满足 catalog（供单测 / UI）。半截 persistent .wsbk 会回退其它路径。
        /// </summary>
        public static bool ProbeLocalSatisfiesCatalog(string bankName)
        {
            if (string.IsNullOrEmpty(bankName))
                return true;

            string stem = AudioEnginePaths.StripBankExtension(bankName);
            PresetBankCatalog.TryGet(stem, out var remote);

            string primary = Path.Combine(PersistentBankPath, stem + AudioEnginePaths.BankExtension);
            TryPromoteCompleteTemp(primary, remote);
            if (PathSatisfiesCatalog(primary, stem, remote))
                return true;

            // 残缺 .wsbk 挡住好文件时，扫 persistent .wb / Streaming / Legacy。
            if (TryFindSatisfyingAlternate(stem, remote, primary, out _))
                return true;

            return false;
        }

        enum LocalIdentityKind : byte
        {
            None = 0,
            Cache = 1,
            Streaming = 2,
            /// <summary>这条 size+mtime 的缓存 md5 与服务端不同，不必再哈希。</summary>
            CachedMd5Mismatch = 3,
        }

        /// <summary>
        /// 同步身份，不读盘哈希。服务端 md5 非空，且至少一条成立才就绪：
        /// 缓存 md5 对上（mtime 变了但体积和 md5 没变也算，并回写当前 mtime；
        /// version 空着或变了但字节没变则回写成服务端 version）；
        /// 或没有这条缓存时，首包 version 与 md5 都等于服务端。
        /// </summary>
        static LocalIdentityKind MatchLocalIdentity(
            string stem,
            long size,
            long mtimeTicks,
            string remoteVersion,
            string remoteMd5,
            bool stampVersion)
        {
            if (string.IsNullOrEmpty(stem) || string.IsNullOrEmpty(remoteMd5))
                return LocalIdentityKind.None;

            var cache = BankMd5Cache.Load();
            if (cache.TryGet(stem, size, mtimeTicks, out string cachedMd5, out string cachedVersion))
                return AcceptCachedMd5(
                    cache, stem, size, mtimeTicks, cachedMd5, cachedVersion, remoteVersion, remoteMd5, stampVersion);

            // 同一体积且已接受的 md5 等于服务端：修改时间被拷贝、进引擎或文件系统精度改过，
            // 不能当成未下载。md5 不同则留给校验线程重算，不能记成「这批字节已经对不上」。
            if (cache.TryPeek(stem, out string peekedVersion, out string peekedMd5)
                && cache.TryGetStoredSize(stem, out long storedSize)
                && storedSize == size
                && string.Equals(peekedMd5, remoteMd5, StringComparison.OrdinalIgnoreCase))
            {
                return AcceptCachedMd5(
                    cache,
                    stem,
                    size,
                    mtimeTicks,
                    peekedMd5,
                    peekedVersion,
                    remoteVersion,
                    remoteMd5,
                    stampVersion);
            }

            if (!string.IsNullOrEmpty(remoteVersion)
                && StreamingBankCatalog.IsSameVersionAsStreaming(stem, remoteVersion)
                && StreamingBankCatalog.TryGetMd5(stem, out string streamingMd5)
                && string.Equals(streamingMd5, remoteMd5, StringComparison.OrdinalIgnoreCase))
            {
                if (stampVersion)
                {
                    cache.Set(stem, size, mtimeTicks, remoteMd5, remoteVersion);
                    cache.Save();
                }

                return LocalIdentityKind.Streaming;
            }

            return LocalIdentityKind.None;
        }

        static LocalIdentityKind AcceptCachedMd5(
            BankMd5Cache cache,
            string stem,
            long size,
            long mtimeTicks,
            string cachedMd5,
            string cachedVersion,
            string remoteVersion,
            string remoteMd5,
            bool stampVersion)
        {
            if (!string.Equals(cachedMd5, remoteMd5, StringComparison.OrdinalIgnoreCase))
                return LocalIdentityKind.CachedMd5Mismatch;

            string versionToStore = !string.IsNullOrEmpty(remoteVersion)
                ? remoteVersion
                : (cachedVersion ?? "");
            bool versionDrift = !string.Equals(
                cachedVersion ?? "",
                versionToStore,
                StringComparison.OrdinalIgnoreCase);
            bool mtimeDrift = !cache.TryGet(stem, size, mtimeTicks, out _, out _);

            if (stampVersion && (versionDrift || mtimeDrift))
            {
                cache.Set(stem, size, mtimeTicks, remoteMd5, versionToStore);
                cache.Save();
            }

            return LocalIdentityKind.Cache;
        }

        static bool IdentityAccepted(LocalIdentityKind kind) =>
            kind == LocalIdentityKind.Cache || kind == LocalIdentityKind.Streaming;

        static bool PathSatisfiesCatalog(string path, string stem, BankRemoteInfo remote)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return false;

            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists || fi.Length <= 0)
                    return false;

                string tmpPath = path + ".tmp";
                if (File.Exists(tmpPath))
                    TryDelete(tmpPath);

                if (remote == null)
                    return true;

                // 体积只否决明显偏小的残缺文件，不能单独算就绪。
                if (RejectedByRemoteSize(fi.Length, remote.size))
                    return false;

                // 这条记录没有 url / md5：有文件就先用。不要在探针里弹 toast，
                // 目录占位（只有名字）和还没下完都会走到这里，不是线上坏包。
                if (string.IsNullOrEmpty(remote.url) || string.IsNullOrEmpty(remote.md5))
                    return true;

                return IdentityAccepted(MatchLocalIdentity(
                    stem,
                    fi.Length,
                    fi.LastWriteTimeUtc.Ticks,
                    remote.version,
                    remote.md5,
                    stampVersion: true));
            }
            catch
            {
                return false;
            }
        }

        static bool TryFindSatisfyingAlternate(
            string stem,
            BankRemoteInfo remote,
            string excludePath,
            out string foundPath)
        {
            foundPath = null;
            string[] dirs =
            {
                PersistentBankPath,
#if !(UNITY_ANDROID && !UNITY_EDITOR)
                AudioEnginePaths.StreamingBankPath,
                AudioEnginePaths.LegacyStreamingBankPath,
#endif
            };
            string[] exts =
            {
                AudioEnginePaths.BankExtension,
                AudioEnginePaths.LegacyBankExtension,
                "",
            };

            for (int d = 0; d < dirs.Length; d++)
            {
                string dir = dirs[d];
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                    continue;
                for (int e = 0; e < exts.Length; e++)
                {
                    string path = string.IsNullOrEmpty(exts[e])
                        ? Path.Combine(dir, stem)
                        : Path.Combine(dir, stem + exts[e]);
                    if (!string.IsNullOrEmpty(excludePath)
                        && string.Equals(path, excludePath, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!PathSatisfiesCatalog(path, stem, remote))
                        continue;
                    foundPath = path;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 若 <c>path.tmp</c> 长度已对齐 catalog（或无 size 时 &gt;0），提升为正式 bank。
        /// </summary>
        static bool TryPromoteCompleteTemp(string finalPath, BankRemoteInfo remote)
        {
            if (string.IsNullOrEmpty(finalPath))
                return false;
            string tmpPath = finalPath + ".tmp";
            try
            {
                if (!File.Exists(tmpPath))
                    return false;

                var tmpFi = new FileInfo(tmpPath);
                if (!tmpFi.Exists || tmpFi.Length <= 0)
                    return false;

                if (File.Exists(finalPath))
                {
                    var finalFi = new FileInfo(finalPath);
                    if (finalFi.Exists && finalFi.Length > 0
                        && (remote == null
                            || string.IsNullOrEmpty(remote.size)
                            || FileMatchesRemoteSize(finalFi.Length, remote)))
                    {
                        TryDelete(tmpPath);
                        return true;
                    }
                }

                // 无 catalog size 不提升：半截 .tmp（如 Plateau 下到 25MB）会被误当成完整包。
                if (remote == null || string.IsNullOrEmpty(remote.size))
                    return false;
                if (!FileMatchesRemoteSize(tmpFi.Length, remote))
                    return false;

                string dir = Path.GetDirectoryName(finalPath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                if (File.Exists(finalPath))
                    File.Delete(finalPath);
                File.Move(tmpPath, finalPath);
                Log.Info($"[BankDownloader] promoted complete tmp -> {finalPath} bytes={tmpFi.Length}");
                return true;
            }
            catch (Exception e)
            {
                Log.Warning($"[BankDownloader] promote tmp failed path={finalPath}: {e.Message}");
                return false;
            }
        }

        static bool FileMatchesRemoteSize(long length, BankRemoteInfo remote)
        {
            if (remote == null || string.IsNullOrEmpty(remote.size))
                return length > 0;
            if (!int.TryParse(remote.size, out int kb) || kb <= 0)
                return length > 0;
            long expected = kb * 1024L;
            return length >= expected * 9 / 10;
        }

        /// <summary>catalog 给了 size 且本地明显小于 90% 时否决。没给 size 则不否决。</summary>
        static bool RejectedByRemoteSize(long length, string sizeKb)
        {
            if (length <= 0 || string.IsNullOrEmpty(sizeKb))
                return false;
            return !FileMatchesRemoteSize(length, new BankRemoteInfo { size = sizeKb });
        }

        public static async UniTask<bool> WaitLocalReady(string bankName, float timeoutSeconds)
        {
            if (IsLocalReady(bankName))
                return true;
            float deadline = Time.realtimeSinceStartup + Mathf.Max(0.1f, timeoutSeconds);
            while (Time.realtimeSinceStartup < deadline)
            {
                if (IsLocalReady(bankName))
                    return true;
                await UniTask.Delay(100, DelayType.UnscaledDeltaTime);
            }

            return IsLocalReady(bankName);
        }

        /// <summary>Startup 并发上限（默认≤3）。用户点击进行中 DesiredMaxSlots 仍强制单槽。</summary>
        public static void SetStartupParallelSlots(int slots)
        {
            _startupParallelSlots = Mathf.Clamp(slots, 1, MaxStartupSlots);
        }

        public static bool IsStreamingBank(string path)
        {
            if (string.IsNullOrEmpty(path))
                return false;
            string root = StreamingBankPath;
            if (string.IsNullOrEmpty(root))
                return false;
            return path.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }

        public static void EnsureWritableDirs()
        {
            Directory.CreateDirectory(PersistentBankPath);
        }

        /// <summary>
        /// 仅建队列元数据（不读盘 MD5）。本地就绪由 <see cref="DownloadBanks"/> 异步校验。
        /// </summary>
        public static List<BankDownloadItem> BuildQueue(IList<BankRemoteInfo> banks)
        {
            var list = new List<BankDownloadItem>();
            if (banks == null)
                return list;

            foreach (var b in banks)
            {
                if (b == null || string.IsNullOrEmpty(b.bankName))
                    continue;

                var item = new BankDownloadItem
                {
                    BankName = AudioEnginePaths.StripBankExtension(b.bankName),
                    Url = b.url ?? "",
                    Md5 = b.md5 ?? "",
                    SizeKb = b.size ?? "",
                    Guid = b.guid ?? "",
                    Version = b.version ?? "",
                    LocalPath = PersistentDestPath(b.bankName),
                };

                list.Add(item);
            }

            return list;
        }

        static int CountState(List<BankDownloadItem> list, BankDownloadState state)
        {
            int n = 0;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].State == state)
                    n++;
            }

            return n;
        }

        /// <summary>
        /// 下载 banks 队列。已 Ready（本地 MD5 / 缓存命中）跳过；完成后可选 loadIntoEngine。
        /// <paramref name="prioritize"/> 走启动优先队列（最多 3 路）。
        /// <paramref name="preempt"/> 为用户点击：插队首并打断当前非本 bank。
        /// <paramref name="abortOtherUserDownloads"/> 为 true 时中止其它 User 槽（用户点击优先当前项）。
        /// </summary>
        public static async UniTask<IReadOnlyList<BankDownloadItem>> DownloadBanks(
            IList<BankRemoteInfo> banks,
            bool loadIntoEngine = true,
            Action<IReadOnlyList<BankDownloadItem>> onComplete = null,
            Action<BankDownloadItem> onItemDone = null,
            Action<BankDownloadItem> onItemProgress = null,
            bool prioritize = false,
            bool preempt = false,
            bool abortOtherUserDownloads = false)
        {
            int frameBeforeQueue = Time.frameCount;
            float tBeforeQueue = Time.realtimeSinceStartup;
            Log.Info(
                $"[BankDownloader] DownloadBanks ENTER frame={frameBeforeQueue} " +
                $"loadIntoEngine={loadIntoEngine} prioritize={prioritize} preempt={preempt} " +
                $"abortOtherUser={abortOtherUserDownloads}");

            var all = BuildQueue(banks);
            await StreamingBankCatalog.EnsureLoadedAsync();
            await VerifyLocalReadyAsync(all);

            // 跳过条件：已接受的 md5 与服务端一致（version 空或只改了修改时间也算），或首包 version 与 md5 都一致。
            // bank.version 是 bank 内容版本（如 1.0.0），不能和 Application.version 比。

            for (int i = 0; i < all.Count; i++)
            {
                var item = all[i];
                if (item.State == BankDownloadState.Pending
                    && (string.IsNullOrEmpty(item.Url) || string.IsNullOrEmpty(item.Md5)))
                {
                    if (ProbeLocalSatisfiesCatalog(item.BankName))
                    {
                        AcceptLocalDespiteServerGap(item);
                        Log.Warning(
                            "[BankDownloader] server gap use local bank=" + item.BankName +
                            " url=" + item.Url + " md5=" + item.Md5 +
                            " path=" + item.LocalPath);
                    }
                    else
                    {
                        bool noUrl = string.IsNullOrEmpty(item.Url);
                        item.State = BankDownloadState.Skipped;
                        item.Error = noUrl ? BankDownloadItem.UrlEmptyError : BankDownloadItem.Md5EmptyError;
                        Log.Error(
                            "[BankDownloader] server gap no local bank=" + item.BankName +
                            " version=" + item.Version +
                            " url=" + item.Url +
                            " md5=" + item.Md5 +
                            " path=" + item.LocalPath);
                        NoteServerGap(item);
                    }
                }
            }

            Log.Info(
                $"[BankDownloader] start count={all.Count} dir={PersistentBankPath} " +
                $"ready={CountState(all, BankDownloadState.Ready)} " +
                $"pending={CountState(all, BankDownloadState.Pending)} " +
                $"maxAttempts={Retry.MaxAttempts} stall={Retry.StallTimeoutSeconds}s " +
                $"verifyWallMs={(Time.realtimeSinceStartup - tBeforeQueue) * 1000f:F0}");

            // 本批白名单：abortOtherUser 时勿自相残杀同批多 bank。
            var keepBatch = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < all.Count; i++)
            {
                var item = all[i];
                if (item == null || string.IsNullOrEmpty(item.BankName))
                    continue;
                if (item.State == BankDownloadState.Ready || item.State == BankDownloadState.Skipped)
                    continue;
                keepBatch.Add(item.BankName);
            }

            int remaining = 0;
            var tcs = new UniTaskCompletionSource();
            int loadCount = 0;
            int loadsThisFrame = 0;
            var loadSw = Stopwatch.StartNew();
            int loadFrameBegin = Time.frameCount;

            void CompleteOne()
            {
                remaining--;
                if (remaining <= 0)
                    tcs.TrySetResult();
            }

            foreach (var item in all)
            {
                if (item.State == BankDownloadState.Ready)
                {
                    // 落盘完成立刻回调；灌引擎延后，勿挡 Pending 入队 / DownloadBanks 返回。
                    // 否则 PrefillSuppress / 罩未近场时 WaitUntilSafe 会死等，进度 100% 却走不过去。
                    NotifyBankReady(item.BankName);
                    if (loadIntoEngine)
                        ScheduleTryLoadBankWhenSafe(item);
                    loadCount++;
                    loadsThisFrame++;
                    if (loadsThisFrame >= LoadBanksPerFrame)
                    {
                        loadsThisFrame = 0;
                        await UniTask.Yield(PlayerLoopTiming.Update);
                    }

                    onItemDone?.Invoke(item);
                }
                else if (item.State == BankDownloadState.Skipped)
                {
                    string reason = !string.IsNullOrEmpty(item.SkipReason) ? item.SkipReason : item.Error;
                    Log.Warning($"[BankDownloader] skip {item.BankName}: {reason}");
                    onItemDone?.Invoke(item);
                }
                else
                {
                    remaining++;
                    var batchItem = item;
                    EnqueueOrBump(
                        batchItem,
                        loadIntoEngine,
                        prioritize,
                        preempt,
                        abortOtherUserDownloads,
                        keepBatch,
                        done =>
                        {
                            CopyItemState(batchItem, done);
                            onItemDone?.Invoke(batchItem);
                            CompleteOne();
                        },
                        prog =>
                        {
                            CopyItemState(batchItem, prog);
                            onItemProgress?.Invoke(batchItem);
                        });
                }
            }

            loadSw.Stop();
            Log.Info(
                $"[BankDownloader] LoadReadyLoop END frames={Time.frameCount - loadFrameBegin + 1} " +
                $"loadCount={loadCount} loadMs={loadSw.Elapsed.TotalMilliseconds:F1} " +
                $"enqueue={remaining} userQ={UserQueue.Count} startQ={StartupQueue.Count} normalQ={NormalQueue.Count}");

            if (remaining > 0)
            {
                EnsureRunner();
                await tcs.Task;
            }

            int ready = 0, failed = 0, skipped = 0;
            foreach (var it in all)
            {
                if (it.State == BankDownloadState.Ready) ready++;
                else if (it.State == BankDownloadState.Failed) failed++;
                else if (it.State == BankDownloadState.Skipped) skipped++;
            }

            Log.Info($"[BankDownloader] done ready={ready} failed={failed} skipped={skipped}");
            LogBatchSummary(all);
            onComplete?.Invoke(all);
            return all;
        }

        /// <summary>单 bank 下载入口（优先队列 / UI 点选可复用）。</summary>
        public static async UniTask<BankDownloadItem> DownloadBank(
            BankRemoteInfo bank,
            bool loadIntoEngine = true,
            Action<BankDownloadItem> onDone = null,
            bool prioritize = false,
            bool preempt = false,
            bool abortOtherUserDownloads = false)
        {
            if (bank == null)
            {
                onDone?.Invoke(null);
                return null;
            }

            BankDownloadItem result = null;
            await DownloadBanks(
                new[] { bank },
                loadIntoEngine,
                null,
                item =>
                {
                    result = item;
                    onDone?.Invoke(item);
                },
                null,
                prioritize,
                preempt,
                abortOtherUserDownloads);
            return result;
        }

        static void CopyItemState(BankDownloadItem dst, BankDownloadItem src)
        {
            if (dst == null || src == null || ReferenceEquals(dst, src))
                return;
            dst.State = src.State;
            dst.Progress = src.Progress;
            dst.Error = src.Error;
            dst.AttemptCount = src.AttemptCount;
            if (!string.IsNullOrEmpty(src.Url))
                dst.Url = src.Url;
            if (!string.IsNullOrEmpty(src.Md5))
                dst.Md5 = src.Md5;
            if (!string.IsNullOrEmpty(src.LocalPath))
                dst.LocalPath = src.LocalPath;
        }

        static void EnqueueOrBump(
            BankDownloadItem item,
            bool loadIntoEngine,
            bool prioritize,
            bool preempt,
            bool abortOtherUserDownloads,
            ICollection<string> keepBatch,
            Action<BankDownloadItem> onDone,
            Action<BankDownloadItem> onProgress)
        {
            if (item == null || string.IsNullOrEmpty(item.BankName))
                return;

            var kind = preempt ? BankQueueKind.User
                : prioritize ? BankQueueKind.Startup
                : BankQueueKind.Normal;
            bool userRetry = preempt;

            if (Entries.TryGetValue(item.BankName, out var entry))
            {
                entry.LoadIntoEngine |= loadIntoEngine;
                if (preempt)
                    entry.Kind = BankQueueKind.User;
                else if (entry.Kind == BankQueueKind.User && !IsUserAttemptLive(item.BankName))
                    entry.Kind = kind;
                else if (kind > entry.Kind)
                    entry.Kind = kind;
                // 用户再点：失败 / 空 url 跳过不能当成已结束。自动轮次上限只留给没人点的循环。
                if (userRetry && EntryNeedsUserRetryReset(entry))
                    ResetEntryForUserRetry(entry);
                if (entry.Item.State == BankDownloadState.Pending ||
                    entry.Item.State == BankDownloadState.Failed)
                {
                    if (!string.IsNullOrEmpty(item.Url))
                        entry.Item.Url = item.Url;
                    if (!string.IsNullOrEmpty(item.Md5)
                        && !string.Equals(entry.Item.Md5, item.Md5, StringComparison.OrdinalIgnoreCase))
                    {
                        entry.Item.Md5 = item.Md5;
                        entry.Item.LastMismatchMd5 = null;
                    }
                    else if (!string.IsNullOrEmpty(item.Md5))
                    {
                        entry.Item.Md5 = item.Md5;
                    }
                    if (!string.IsNullOrEmpty(item.SizeKb))
                        entry.Item.SizeKb = item.SizeKb;
                    if (!string.IsNullOrEmpty(item.Guid))
                        entry.Item.Guid = item.Guid;
                    if (!string.IsNullOrEmpty(item.Version))
                        entry.Item.Version = item.Version;
                }

                if (onDone != null)
                    entry.OnDone.Add(onDone);
                if (onProgress != null)
                    entry.OnProgress.Add(onProgress);

                if (ShouldFinishWithoutRestart(
                        entry.Item.State,
                        entry.Item.Error,
                        entry.RequeueCount,
                        userRetry))
                {
                    CopyItemState(item, entry.Item);
                    onDone?.Invoke(entry.Item);
                    if (entry.OnDone.Count > 0)
                        entry.OnDone.Remove(onDone);
                    return;
                }

                PlaceInQueue(item.BankName, entry.Kind, abortOtherUserDownloads, keepBatch);
                return;
            }

            entry = new QueueEntry
            {
                Item = item,
                LoadIntoEngine = loadIntoEngine,
                Kind = kind,
            };
            if (onDone != null)
                entry.OnDone.Add(onDone);
            if (onProgress != null)
                entry.OnProgress.Add(onProgress);
            Entries[item.BankName] = entry;
            PlaceInQueue(item.BankName, kind, abortOtherUserDownloads, keepBatch);
        }

        static void PlaceInQueue(
            string bankName,
            BankQueueKind kind,
            bool abortOtherUserDownloads = false,
            ICollection<string> keepBatch = null)
        {
            RemoveFromQueue(UserQueue, bankName);
            RemoveFromQueue(StartupQueue, bankName);
            RemoveFromQueue(NormalQueue, bankName);

            if (kind == BankQueueKind.User)
            {
                UserQueue.Insert(0, bankName);
                AbortConflictingActives(bankName, abortOtherUserDownloads, keepBatch);
                Log.Info(
                    $"[BankDownloader] User enqueue {bankName} abortOtherUser={abortOtherUserDownloads}");
            }
            else if (kind == BankQueueKind.Startup)
            {
                StartupQueue.Add(bankName);
                Log.Info($"[BankDownloader] Startup enqueue {bankName}");
            }
            else
            {
                NormalQueue.Add(bankName);
                Log.Info($"[BankDownloader] Background enqueue {bankName}");
            }
        }

        /// <summary>
        /// 默认不打断已在下的 User bank（同批多 bank 入队时避免自相残杀）。
        /// <paramref name="abortOtherUserDownloads"/> 为 true 时中止其它 User 槽
        /// （用户点击优先当前项，含音乐打断预设 / 预设打断音乐）。Startup/Normal 仍一律中止。
        /// <paramref name="keepBatch"/> 本批 stem 白名单；同批 Active 不 Abort。
        /// </summary>
        static void AbortConflictingActives(
            string keepBankName,
            bool abortOtherUserDownloads,
            ICollection<string> keepBatch = null)
        {
            for (int i = 0; i < Actives.Count; i++)
            {
                var slot = Actives[i];
                if (slot == null)
                    continue;
                if (string.Equals(slot.BankName, keepBankName, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (keepBatch != null && ContainsBankIgnoreCase(keepBatch, slot.BankName))
                    continue;
                if (slot.Kind == BankQueueKind.User && !abortOtherUserDownloads)
                    continue;

                slot.Abort = true;
                slot.ReleaseWaitersOnAbort = true;
                Log.Info(
                    $"[BankDownloader] abort {slot.BankName} kind={slot.Kind} for user {keepBankName}");
            }
        }

        static bool ContainsBankIgnoreCase(ICollection<string> batch, string bankName)
        {
            if (batch == null || string.IsNullOrEmpty(bankName))
                return false;
            if (batch is HashSet<string> set)
                return set.Contains(bankName);
            foreach (string n in batch)
            {
                if (string.Equals(n, bankName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        /// <summary>曲目 bank 约定：stem 以 Music 开头（与 AmbienceEventCatalog / MusicLists 一致）。</summary>
        static bool IsMusicBankName(string bankName)
        {
            if (string.IsNullOrEmpty(bankName))
                return false;
            string stem = AudioEnginePaths.StripBankExtension(bankName);
            return !string.IsNullOrEmpty(stem)
                   && stem.StartsWith("Music", StringComparison.OrdinalIgnoreCase);
        }

        static bool QueueContains(List<string> queue, string bankName)
        {
            for (int i = 0; i < queue.Count; i++)
            {
                if (string.Equals(queue[i], bankName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        static bool RemoveFromQueue(List<string> queue, string bankName)
        {
            for (int i = queue.Count - 1; i >= 0; i--)
            {
                if (string.Equals(queue[i], bankName, StringComparison.OrdinalIgnoreCase))
                {
                    queue.RemoveAt(i);
                    return true;
                }
            }

            return false;
        }

        static bool HasActiveKind(BankQueueKind kind)
        {
            for (int i = 0; i < Actives.Count; i++)
            {
                if (Actives[i] != null && Actives[i].Kind == kind)
                    return true;
            }

            return false;
        }

        static bool IsActive(string bankName)
        {
            for (int i = 0; i < Actives.Count; i++)
            {
                if (Actives[i] != null &&
                    string.Equals(Actives[i].BankName, bankName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        static int DesiredMaxSlots()
        {
            // 用户槽插队期间单槽，并打断静默。静默音乐 / 预设各自不超过 3，且不同时把两档加超。
            if (UserQueue.Count > 0 || HasActiveKind(BankQueueKind.User))
                return 1;
            return MaxBackgroundSlots;
        }

        static void EnsureRunner()
        {
            if (_runnerActive)
                return;
            _runnerActive = true;
            RunLoop().Forget();
        }

        static async UniTaskVoid RunLoop()
        {
            try
            {
                while (true)
                {
                    if (_pausedForLeave)
                    {
                        await UniTask.Yield(PlayerLoopTiming.Update);
                        continue;
                    }

                    // Wi‑Fi→蜂窝且未开闸：FeatureOff 自动开闸；FeatureOn 弹流量窗续传。
                    if (DownloadCellularGate.NeedsMobileDataPrompt() && HasBusyWork)
                    {
                        DownloadCellularGate.EnsureFeatureOffCellularAutoAllow();
                        if (DownloadCellularGate.NeedsMobileDataPrompt())
                        {
                            if (Actives.Count > 0)
                                HoldForCellularPolicy();
                            NotifyCellularConsentNeeded();
                            await UniTask.Delay(500, DelayType.UnscaledDeltaTime);
                            continue;
                        }
                    }

                    PumpStarts();
                    if (Actives.Count == 0
                        && UserQueue.Count == 0
                        && StartupQueue.Count == 0
                        && NormalQueue.Count == 0)
                    {
                        if (TryRequeueFailed())
                        {
                            await UniTask.Yield(PlayerLoopTiming.Update);
                            continue;
                        }

                        break;
                    }

                    await UniTask.Yield(PlayerLoopTiming.Update);
                }
            }
            finally
            {
                _runnerActive = false;
                Log.Info("[BankDownloader] RunLoop END");
            }

            if (!_pausedForLeave
                && (UserQueue.Count > 0 || StartupQueue.Count > 0 || NormalQueue.Count > 0 || Entries.Count > 0))
                EnsureRunner();
        }

        static void PumpStarts()
        {
            if (!DownloadCellularGate.CanTransmitNow())
                return;

            int max = DesiredMaxSlots();
            while (Actives.Count < max)
            {
                if (!DownloadCellularGate.CanTransmitNow())
                    break;
                if (!TryDequeueNext(out string bankName, out var kind))
                    break;
                if (IsActive(bankName))
                    continue;
                if (!Entries.TryGetValue(bankName, out var entry) || entry?.Item == null)
                    continue;
                if (entry.Item.State == BankDownloadState.Ready ||
                    entry.Item.State == BankDownloadState.Skipped)
                {
                    FireDone(entry);
                    Entries.Remove(bankName);
                    continue;
                }

                if (string.IsNullOrEmpty(entry.Item.Url) || string.IsNullOrEmpty(entry.Item.Md5))
                {
                    if (ProbeLocalSatisfiesCatalog(bankName))
                    {
                        AcceptLocalDespiteServerGap(entry.Item);
                        NotifyBankReady(bankName);
                        FireDone(entry);
                        Entries.Remove(bankName);
                        continue;
                    }

                    bool noUrl = string.IsNullOrEmpty(entry.Item.Url);
                    entry.Item.State = BankDownloadState.Skipped;
                    entry.Item.Error = noUrl ? BankDownloadItem.UrlEmptyError : BankDownloadItem.Md5EmptyError;
                    NoteServerGap(entry.Item);
                    FireDone(entry);
                    Entries.Remove(bankName);
                    continue;
                }

                RunSlot(entry, kind).Forget();
            }
        }

        /// <summary>
        /// 队列被蜂窝闸拦住：FeatureOn 可弹流量窗续传；FeatureOff 已由 auto-allow 处理。
        /// </summary>
        static void NotifyCellularConsentNeeded()
        {
            var names = CollectBusyBankNames();
            if (names.Count == 0)
                return;
            Game.DownloadConsentSession.NotifyNeedCellularConsent(names);
        }

        static List<string> CollectBusyBankNames()
        {
            var names = new List<string>(
                Actives.Count + UserQueue.Count + StartupQueue.Count + NormalQueue.Count);
            for (int i = 0; i < Actives.Count; i++)
            {
                ActiveSlot slot = Actives[i];
                if (slot != null && !string.IsNullOrEmpty(slot.BankName))
                    names.Add(slot.BankName);
            }

            AppendQueueNames(UserQueue, names);
            AppendQueueNames(StartupQueue, names);
            AppendQueueNames(NormalQueue, names);
            return names;
        }

        static void AppendQueueNames(List<string> queue, List<string> dest)
        {
            for (int i = 0; i < queue.Count; i++)
            {
                string name = queue[i];
                if (!string.IsNullOrEmpty(name))
                    dest.Add(name);
            }
        }

        static bool TryDequeueNext(out string bankName, out BankQueueKind kind)
        {
            bankName = null;
            kind = BankQueueKind.Normal;
            if (UserQueue.Count > 0)
            {
                bankName = UserQueue[0];
                UserQueue.RemoveAt(0);
                kind = BankQueueKind.User;
                return true;
            }

            // 静默：还有 Music*（在排或在传，含失败重试）就只开音乐，不开预设。
            // 已在传的预设不在这里 Abort，只是不再往预设相位加新任务。
            if (SilentMusicOutstanding())
                return TryTakeQueuedMusic(out bankName, out kind);

            if (CountActiveNonMusic() >= MaxBackgroundSlots)
                return false;

            if (StartupQueue.Count > 0)
            {
                bankName = StartupQueue[0];
                StartupQueue.RemoveAt(0);
                kind = BankQueueKind.Startup;
                return true;
            }

            if (NormalQueue.Count > 0 && !HasActiveKind(BankQueueKind.User))
            {
                bankName = NormalQueue[0];
                NormalQueue.RemoveAt(0);
                kind = BankQueueKind.Normal;
                return true;
            }

            return false;
        }

        /// <summary>静默队列或静默活跃槽里还有曲目 bank。</summary>
        static bool SilentMusicOutstanding()
        {
            if (QueueHasMusic(StartupQueue) || QueueHasMusic(NormalQueue))
                return true;
            for (int i = 0; i < Actives.Count; i++)
            {
                ActiveSlot slot = Actives[i];
                if (slot == null || slot.Kind == BankQueueKind.User)
                    continue;
                if (IsMusicBankName(slot.BankName))
                    return true;
            }

            return false;
        }

        static bool QueueHasMusic(List<string> queue)
        {
            for (int i = 0; i < queue.Count; i++)
            {
                if (IsMusicBankName(queue[i]))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// 先取 Normal 队首的 Music*（失败重试插在这里），再取 Startup / Normal 里其余曲目。
        /// </summary>
        static bool TryTakeQueuedMusic(out string bankName, out BankQueueKind kind)
        {
            bankName = null;
            kind = BankQueueKind.Normal;
            if (NormalQueue.Count > 0 && IsMusicBankName(NormalQueue[0]))
            {
                bankName = NormalQueue[0];
                NormalQueue.RemoveAt(0);
                return true;
            }

            if (TryRemoveFirstMusic(StartupQueue, out bankName))
            {
                kind = BankQueueKind.Startup;
                return true;
            }

            if (TryRemoveFirstMusic(NormalQueue, out bankName))
                return true;

            return false;
        }

        static bool TryRemoveFirstMusic(List<string> queue, out string bankName)
        {
            for (int i = 0; i < queue.Count; i++)
            {
                if (!IsMusicBankName(queue[i]))
                    continue;
                bankName = queue[i];
                queue.RemoveAt(i);
                return true;
            }

            bankName = null;
            return false;
        }

        static int CountActiveNonMusic()
        {
            int n = 0;
            for (int i = 0; i < Actives.Count; i++)
            {
                ActiveSlot slot = Actives[i];
                if (slot == null || slot.Kind == BankQueueKind.User)
                    continue;
                if (!IsMusicBankName(slot.BankName))
                    n++;
            }

            return n;
        }

        static async UniTaskVoid RunSlot(QueueEntry entry, BankQueueKind kind)
        {
            string bankName = entry.Item.BankName;
            var slot = new ActiveSlot { BankName = bankName, Kind = kind };
            Actives.Add(slot);
            Log.Info(
                $"[BankDownloader] begin {bankName} kind={kind} " +
                $"userQ={UserQueue.Count} startQ={StartupQueue.Count} normalQ={NormalQueue.Count} " +
                $"active={Actives.Count}");

            try
            {
                if (!await WaitForNetworkAsync(() => slot.Abort) || slot.Abort)
                {
                    entry.Item.State = BankDownloadState.Pending;
                    entry.Item.Progress = 0f;
                    entry.Item.Error = null;
                    RequeueAfterAbort(bankName, entry.Kind);
                    // 抢占 Abort：FireDone 立刻放旧批 await；蜂窝/离岛 Abort 只刷进度等续传。
                    if (slot.ReleaseWaitersOnAbort)
                        FireDone(entry);
                    else
                        FireProgress(entry);
                    Log.Info($"[BankDownloader] aborted before transfer, requeue {bankName} kind={entry.Kind}");
                    return;
                }

                bool aborted = false;
                bool needBackoff = await DownloadOneWithRetries(
                    entry.Item,
                    _ => FireProgress(entry),
                    () => slot.Abort,
                    () => aborted = true);

                if (aborted)
                {
                    entry.Item.State = BankDownloadState.Pending;
                    entry.Item.Progress = 0f;
                    entry.Item.Error = null;
                    RequeueAfterAbort(bankName, entry.Kind);
                    if (slot.ReleaseWaitersOnAbort)
                        FireDone(entry);
                    else
                        FireProgress(entry);
                    Log.Info($"[BankDownloader] aborted, requeue {bankName} kind={entry.Kind}");
                    return;
                }

                if (entry.Item.State == BankDownloadState.Ready)
                {
                    // 先 FireDone 释放槽位 / CompleteOne；灌引擎延后。
                    // 旧顺序：WaitUntilSafe → TryLoad → FireDone，铺装期会卡死整批 onBanksDone。
                    bool loadIntoEngine = entry.LoadIntoEngine;
                    BankDownloadItem loaded = entry.Item;
                    NotifyBankReady(bankName);
                    FireDone(entry);
                    Entries.Remove(bankName);
                    if (loadIntoEngine)
                        ScheduleTryLoadBankWhenSafe(loaded);
                    return;
                }

                if (entry.Item.State == BankDownloadState.Failed &&
                    IsRetryableItem(entry.Item) &&
                    entry.RequeueCount < MaxFailedRequeuePasses)
                {
                    EnqueueFailedRetry(entry);
                    Log.Warning(
                        $"[BankDownloader] requeue {bankName} pass={entry.RequeueCount}/" +
                        $"{MaxFailedRequeuePasses} kind={entry.Kind}");
                    if (needBackoff)
                    {
                        await UniTask.Delay(
                            TimeSpan.FromSeconds(Retry.RetryBaseSeconds), DelayType.UnscaledDeltaTime);
                    }

                    return;
                }

                if (entry.Item.State == BankDownloadState.Failed)
                    RememberTerminalFailure(bankName);
                FireDone(entry);
                Entries.Remove(bankName);
            }
            finally
            {
                Actives.Remove(slot);
            }
        }

        static bool EntryNeedsUserRetryReset(QueueEntry entry)
        {
            if (entry?.Item == null)
                return false;
            if (entry.Item.State == BankDownloadState.Failed)
                return true;
            return entry.Item.State == BankDownloadState.Skipped
                   && string.Equals(
                       entry.Item.Error,
                       BankDownloadItem.UrlEmptyError,
                       StringComparison.Ordinal);
        }

        static void ResetEntryForUserRetry(QueueEntry entry)
        {
            if (entry?.Item == null)
                return;
            entry.RequeueCount = 0;
            entry.Item.State = BankDownloadState.Pending;
            entry.Item.Progress = 0f;
            entry.Item.AttemptCount = 0;
            entry.Item.Error = null;
            // 上次把空文件哈希记成 Error 后，下一次会立刻判 md5 stable 并只弹检查线上。
            entry.Item.LastMismatchMd5 = null;
            ForgetTerminalFailure(entry.Item.BankName);
            BumpProgressEpoch();
        }

        /// <summary>
        /// 已结束、不必再下：Ready，或非空 url 的 Skipped。
        /// 用户再点时 Failed / 空 url Skipped 必须重下；后台仍受 <see cref="MaxFailedRequeuePasses"/> 限制。
        /// </summary>
        static bool ShouldFinishWithoutRestart(
            BankDownloadState state,
            string error,
            int requeueCount,
            bool userRetry)
        {
            if (state == BankDownloadState.Ready)
                return true;
            bool urlEmptySkip = state == BankDownloadState.Skipped
                                && string.Equals(
                                    error,
                                    BankDownloadItem.UrlEmptyError,
                                    StringComparison.Ordinal);
            if (state == BankDownloadState.Skipped && !urlEmptySkip)
                return true;
            if (userRetry)
                return false;
            if (urlEmptySkip)
                return true;
            return state == BankDownloadState.Failed
                   && requeueCount >= MaxFailedRequeuePasses;
        }

        static void RememberTerminalFailure(string bankName)
        {
            string stem = AudioEnginePaths.StripBankExtension(bankName);
            if (string.IsNullOrEmpty(stem))
                return;
            if (TerminalFailed.Add(stem))
                BumpProgressEpoch();
        }

        static void ForgetTerminalFailure(string bankName)
        {
            string stem = AudioEnginePaths.StripBankExtension(bankName);
            if (string.IsNullOrEmpty(stem))
                return;
            if (TerminalFailed.Remove(stem))
                BumpProgressEpoch();
        }

        static void RequeueAfterAbort(string bankName, BankQueueKind kind)
        {
            RemoveFromQueue(UserQueue, bankName);
            RemoveFromQueue(StartupQueue, bankName);
            RemoveFromQueue(NormalQueue, bankName);
            if (kind == BankQueueKind.User)
                UserQueue.Add(bankName);
            else if (kind == BankQueueKind.Startup)
                StartupQueue.Add(bankName);
            else
                NormalQueue.Insert(0, bankName);
        }

        /// <summary>
        /// 自动重试：退出用户槽。Music* 插回静默音乐相位队首（预设之前）；预设失败只排在预设相位队尾。
        /// 用户点击失败时 FireDone，进度环收掉，图标回到重试；静默批次的回调留到真正结束。
        /// </summary>
        static void EnqueueFailedRetry(QueueEntry entry)
        {
            if (entry?.Item == null || string.IsNullOrEmpty(entry.Item.BankName))
                return;

            bool releaseUser = entry.Kind == BankQueueKind.User;
            string bankName = entry.Item.BankName;
            entry.RequeueCount++;
            entry.Item.State = BankDownloadState.Pending;
            entry.Item.Progress = 0f;
            entry.Item.AttemptCount = 0;
            entry.Kind = BankQueueKind.Normal;

            RemoveFromQueue(UserQueue, bankName);
            RemoveFromQueue(StartupQueue, bankName);
            RemoveFromQueue(NormalQueue, bankName);
            if (IsMusicBankName(bankName))
                NormalQueue.Insert(0, bankName);
            else
                NormalQueue.Add(bankName);

            BumpProgressEpoch();
            if (releaseUser)
                FireDone(entry);
        }

        static void NotifyBankReady(string bankName)
        {
            if (string.IsNullOrEmpty(bankName))
                return;
            string stem = AudioEnginePaths.StripBankExtension(bankName);
            if (string.IsNullOrEmpty(stem))
                return;
            // 正向缓存：静默 Rest 完成 Entries.Remove 后 UI 立刻 IsLocalReady，避免负缓存闪回。
            MarkLocalReady(stem);
            try
            {
                OnBankLocalReady?.Invoke(stem);
            }
            catch (Exception e)
            {
                Log.Warning($"[BankDownloader] OnBankLocalReady({stem}) threw: {e.Message}");
            }
        }

        static bool TryRequeueFailed()
        {
            var names = new List<string>();
            foreach (var kv in Entries)
            {
                var entry = kv.Value;
                if (entry?.Item == null)
                    continue;
                if (entry.Item.State != BankDownloadState.Failed)
                    continue;
                if (!IsRetryableItem(entry.Item))
                    continue;
                if (entry.RequeueCount >= MaxFailedRequeuePasses)
                    continue;
                names.Add(kv.Key);
            }

            if (names.Count == 0)
                return false;

            Log.Warning($"[BankDownloader] queue empty, requeue failed count={names.Count}");
            for (int i = 0; i < names.Count; i++)
            {
                string name = names[i];
                if (!Entries.TryGetValue(name, out var entry))
                    continue;
                EnqueueFailedRetry(entry);
            }

            return true;
        }

        static void FireProgress(QueueEntry entry)
        {
            if (entry == null)
                return;
            BumpProgressEpoch();
            for (int i = 0; i < entry.OnProgress.Count; i++)
            {
                try
                {
                    entry.OnProgress[i]?.Invoke(entry.Item);
                }
                catch (Exception e)
                {
                    Log.Warning($"[BankDownloader] onProgress threw: {e.Message}");
                }
            }
        }

        static void FireDone(QueueEntry entry)
        {
            if (entry == null)
                return;
            BumpProgressEpoch();
            // 拷贝列表，避免回调里再 Enqueue 改集合
            var done = entry.OnDone.ToArray();
            entry.OnDone.Clear();
            entry.OnProgress.Clear();
            for (int i = 0; i < done.Length; i++)
            {
                try
                {
                    done[i]?.Invoke(entry.Item);
                }
                catch (Exception e)
                {
                    Log.Warning($"[BankDownloader] onDone threw: {e.Message}");
                }
            }
        }

        /// <summary>
        /// 本地 bank 就绪：version 与 md5 都对上则跳过。缓存未命中时 ThreadPool 算 MD5。
        /// </summary>
        static async UniTask<bool> VerifyLocalReadyAsync(List<BankDownloadItem> list)
        {
            if (list == null || list.Count == 0)
                return true;

            int frameBegin = Time.frameCount;
            float t0 = Time.realtimeSinceStartup;
            var cache = BankMd5Cache.Load();

            var toHash = new List<Md5Job>(list.Count);
            int cacheHits = 0;
            int versionHits = 0;
            int missing = 0;
            bool cacheDirty = false;

            for (int i = 0; i < list.Count; i++)
            {
                var item = list[i];
                if (item.State == BankDownloadState.Skipped)
                    continue;
                string verifyPath = item.LocalPath;
                PresetBankCatalog.TryGet(item.BankName, out var remoteForPromote);
                TryPromoteCompleteTemp(verifyPath, remoteForPromote);
                if (!File.Exists(verifyPath)
                    && AudioEnginePaths.TryFindLocalBank(item.BankName, out string existing)
                    && !string.Equals(existing, verifyPath, StringComparison.OrdinalIgnoreCase))
                    verifyPath = existing;
                if (!File.Exists(verifyPath))
                {
                    missing++;
                    continue;
                }

                long size;
                long mtimeTicks;
                try
                {
                    var fi = new FileInfo(verifyPath);
                    size = fi.Length;
                    mtimeTicks = fi.LastWriteTimeUtc.Ticks;
                    if (size <= 0)
                    {
                        missing++;
                        continue;
                    }
                }
                catch
                {
                    missing++;
                    continue;
                }

                // 体积只否决明显偏小的残缺文件。线上缺 url 或 md5 时，完整本地文件直接就绪。
                string sizeKb = !string.IsNullOrEmpty(item.SizeKb) ? item.SizeKb : remoteForPromote?.size;
                if (RejectedByRemoteSize(size, sizeKb))
                    continue;

                bool noUrl = string.IsNullOrEmpty(item.Url)
                             && string.IsNullOrEmpty(remoteForPromote?.url);
                string remoteVersion = !string.IsNullOrEmpty(item.Version)
                    ? item.Version
                    : remoteForPromote?.version;
                string remoteMd5 = !string.IsNullOrEmpty(item.Md5)
                    ? item.Md5
                    : remoteForPromote?.md5;
                if (noUrl || string.IsNullOrEmpty(remoteMd5))
                {
                    item.State = BankDownloadState.Ready;
                    item.Progress = 1f;
                    item.LocalPath = verifyPath;
                    item.Error = null;
                    item.SkipReason = noUrl ? "no url, use local" : "no md5, use local";
                    cacheHits++;
                    continue;
                }

                var identity = MatchLocalIdentity(
                    item.BankName, size, mtimeTicks, remoteVersion, remoteMd5, stampVersion: true);
                if (IdentityAccepted(identity))
                {
                    item.State = BankDownloadState.Ready;
                    item.Progress = 1f;
                    item.LocalPath = verifyPath;
                    if (identity == LocalIdentityKind.Streaming)
                    {
                        item.SkipReason = $"streaming version+md5 {remoteVersion}";
                        versionHits++;
                    }
                    else
                        cacheHits++;
                    continue;
                }

                if (identity == LocalIdentityKind.CachedMd5Mismatch
                    || string.IsNullOrEmpty(remoteMd5))
                    continue;

                if (string.IsNullOrEmpty(item.Md5))
                    item.Md5 = remoteMd5;
                if (string.IsNullOrEmpty(item.Version))
                    item.Version = remoteVersion;

                toHash.Add(new Md5Job
                {
                    Index = i,
                    Path = verifyPath,
                    ExpectedMd5 = item.Md5,
                    Size = size,
                    MtimeTicks = mtimeTicks,
                });
            }

            int hashed = 0;
            int matched = 0;
            double hashMs = 0;

            if (toHash.Count > 0)
            {
                // 禁止 UniTask.RunOnThreadPool<T>：热更 + Dev IL2CPP 启动该状态机 NRE。
                var results = await RunOffMainAsync(() => HashJobs(toHash));
                hashMs = results.ElapsedMs;
                hashed = results.Jobs.Count;

                for (int i = 0; i < results.Jobs.Count; i++)
                {
                    var job = results.Jobs[i];
                    var item = list[job.Index];
                    if (job.Match)
                    {
                        item.State = BankDownloadState.Ready;
                        item.Progress = 1f;
                        item.LocalPath = job.Path;
                        matched++;
                        cache.Set(item.BankName, job.Size, job.MtimeTicks, item.Md5, item.Version ?? "");
                        cacheDirty = true;
                    }
                    else
                    {
                        Log.Warning(
                            $"[BankDownloader] local md5 mismatch bank={item.BankName} " +
                            $"expect={job.ExpectedMd5} actual={job.ActualMd5} path={job.Path} — will re-download");
                    }
                }
            }

            if (cacheDirty)
                cache.Save();

            Log.Info(
                $"[BankDownloader] VerifyLocal END frameDelta={Time.frameCount - frameBegin} " +
                $"wallMs={(Time.realtimeSinceStartup - t0) * 1000f:F0} " +
                $"versionHits={versionHits} cacheHits={cacheHits} hashed={hashed} matched={matched} " +
                $"missing={missing} hashThreadMs={hashMs:F0} " +
                $"ready={CountState(list, BankDownloadState.Ready)}");
            return true;
        }

        static HashBatchResult HashJobs(List<Md5Job> jobs)
        {
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < jobs.Count; i++)
            {
                var job = jobs[i];
                string actual = ComputeMd5(job.Path);
                job.Match = !string.IsNullOrEmpty(actual) &&
                            !string.Equals(actual, "Error", StringComparison.Ordinal) &&
                            string.Equals(actual, job.ExpectedMd5, StringComparison.OrdinalIgnoreCase);
                job.ActualMd5 = actual;
                jobs[i] = job;
            }

            sw.Stop();
            return new HashBatchResult { Jobs = jobs, ElapsedMs = sw.Elapsed.TotalMilliseconds };
        }

        static string ComputeMd5(string path)
        {
            try
            {
                using var md5 = System.Security.Cryptography.MD5.Create();
                using var stream = File.OpenRead(path);
                byte[] hash = md5.ComputeHash(stream);
                var sb = new StringBuilder(hash.Length * 2);
                for (int i = 0; i < hash.Length; i++)
                    sb.Append(hash[i].ToString("x2"));
                return sb.ToString();
            }
            catch
            {
                return "Error";
            }
        }

        static async UniTask<bool> LocalFileMd5MatchesAsync(string path, string expectedMd5)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(expectedMd5) || !File.Exists(path))
                return false;
            string actual = await RunOffMainAsync(() => ComputeMd5(path));
            return !string.IsNullOrEmpty(actual)
                   && !string.Equals(actual, "Error", StringComparison.Ordinal)
                   && string.Equals(actual, expectedMd5, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 热更里不要走 <c>UniTask.RunOnThreadPool&lt;T&gt;</c>（Dev IL2CPP 复现 NRE）。
        /// ThreadPool + <c>UniTaskCompletionSource&lt;T&gt;</c> 已在进岛路径验证过。
        /// </summary>
        static async UniTask<T> RunOffMainAsync<T>(Func<T> func)
        {
            var tcs = new UniTaskCompletionSource<T>();
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    tcs.TrySetResult(func());
                }
                catch (Exception e)
                {
                    tcs.TrySetException(e);
                }
            });
            T result = await tcs.Task;
            // TCS 在线程池完成，默认 continuation 也在工作线程；Time / persistentDataPath 必须回主线程。
            await UniTask.SwitchToMainThread();
            return result;
        }

        static bool IsRetryableItem(BankDownloadItem item)
        {
            if (item == null || string.IsNullOrEmpty(item.Error))
                return true;
            if (item.Error.StartsWith("md5 mismatch", StringComparison.OrdinalIgnoreCase)
                || item.Error.StartsWith("empty body", StringComparison.OrdinalIgnoreCase)
                || item.Error.StartsWith("md5 unreadable", StringComparison.OrdinalIgnoreCase))
                return true;
            if (item.Error.StartsWith("md5 stable", StringComparison.OrdinalIgnoreCase)
                || item.Error.StartsWith("header ", StringComparison.OrdinalIgnoreCase))
                return false;
            return Http.IsRetryableNetworkError(item.Error)
                   || item.Error.IndexOf("stall", StringComparison.OrdinalIgnoreCase) >= 0
                   || item.Error.IndexOf("no network", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <returns>是否因网络类失败需要队列级 backoff。</returns>
        static async UniTask<bool> DownloadOneWithRetries(
            BankDownloadItem item,
            Action<BankDownloadItem> onProgress,
            Func<bool> shouldAbort = null,
            Action onAborted = null)
        {
            int max = Math.Max(1, Retry.MaxAttempts);
            float delay = Retry.RetryBaseSeconds;
            bool anyNetworkFail = false;

            for (int attempt = 1; attempt <= max; attempt++)
            {
                if (shouldAbort != null && shouldAbort())
                {
                    onAborted?.Invoke();
                    return false;
                }

                item.AttemptCount = attempt;
                item.State = BankDownloadState.Downloading;
                item.Progress = 0f;
                item.Error = null;
                onProgress?.Invoke(item);

                // 插队/改名迁移后可能本地已齐：直接 Ready，勿再 HTTP。
                PresetBankCatalog.TryGet(item.BankName, out var catalogRemote);
                BankRemoteInfo sizeHint = catalogRemote;
                if ((sizeHint == null || string.IsNullOrEmpty(sizeHint.size))
                    && !string.IsNullOrEmpty(item.SizeKb))
                {
                    sizeHint = new BankRemoteInfo
                    {
                        bankName = item.BankName,
                        size = item.SizeKb,
                        md5 = !string.IsNullOrEmpty(item.Md5) ? item.Md5 : catalogRemote?.md5,
                    };
                }

                TryPromoteCompleteTemp(item.LocalPath, sizeHint);
                string localPath = item.LocalPath;
                if (!File.Exists(localPath)
                    && AudioEnginePaths.TryFindLocalBank(item.BankName, out string found)
                    && File.Exists(found))
                    localPath = found;
                if (File.Exists(localPath))
                {
                    long len = 0;
                    long mtimeTicks = 0;
                    try
                    {
                        var fi = new FileInfo(localPath);
                        len = fi.Length;
                        mtimeTicks = fi.LastWriteTimeUtc.Ticks;
                    }
                    catch { /* ignore */ }

                    string version = !string.IsNullOrEmpty(item.Version)
                        ? item.Version
                        : catalogRemote?.version;
                    string md5 = !string.IsNullOrEmpty(item.Md5)
                        ? item.Md5
                        : catalogRemote?.md5;
                    string sizeKb = !string.IsNullOrEmpty(sizeHint?.size) ? sizeHint.size : item.SizeKb;
                    if (len > 0 && !RejectedByRemoteSize(len, sizeKb))
                    {
                        var identity = MatchLocalIdentity(
                            item.BankName, len, mtimeTicks, version, md5, stampVersion: true);
                        bool accept = IdentityAccepted(identity);
                        if (!accept
                            && identity != LocalIdentityKind.CachedMd5Mismatch
                            && !string.IsNullOrEmpty(md5)
                            && await LocalFileMd5MatchesAsync(localPath, md5))
                        {
                            if (string.IsNullOrEmpty(item.Version))
                                item.Version = version;
                            if (string.IsNullOrEmpty(item.Md5))
                                item.Md5 = md5;
                            var cache = BankMd5Cache.Load();
                            cache.Set(item.BankName, len, mtimeTicks, md5, version);
                            cache.Save();
                            accept = true;
                        }

                        if (accept)
                        {
                            TryDelete(item.LocalPath + ".tmp");
                            item.LocalPath = localPath;
                            if (string.IsNullOrEmpty(item.Version))
                                item.Version = version ?? "";
                            if (string.IsNullOrEmpty(item.Md5))
                                item.Md5 = md5 ?? "";
                            RememberLocalMd5(item);
                            item.State = BankDownloadState.Ready;
                            item.Progress = 1f;
                            item.Error = null;
                            onProgress?.Invoke(item);
                            Log.Info(
                                "[BankDownloader] skip HTTP, local ready " + item.BankName +
                                " version=" + version +
                                " md5=" + md5);
                            return false;
                        }
                    }
                }

                if (!await WaitForNetworkAsync(shouldAbort)
                    || (shouldAbort != null && shouldAbort()))
                {
                    onAborted?.Invoke();
                    return false;
                }

                if (Application.internetReachability == NetworkReachability.NotReachable)
                {
                    item.State = BankDownloadState.Failed;
                    item.Error = "no network";
                    anyNetworkFail = true;
                    if (attempt >= max)
                        break;
                    Log.Warning($"[BankDownloader] {item.BankName} no network, retry in {delay:0.#}s");
                    await UniTask.Delay(TimeSpan.FromSeconds(delay), DelayType.UnscaledDeltaTime);
                    delay = Mathf.Min(delay * 2f, Retry.RetryMaxSeconds);
                    continue;
                }

                bool stalled = false;
                bool httpOk = false;
                string httpErr = null;
                long httpCode = 0;
                string tmpPath = item.LocalPath + ".tmp";
                long resumeFrom = GetExistingLength(tmpPath);
                bool resume = resumeFrom > 0;
                if (!resume)
                    PrepareTemp(tmpPath);
                else
                    EnsureParentDir(tmpPath);

                using (var req = new UnityWebRequest(item.Url, UnityWebRequest.kHttpVerbGET))
                {
                    if (resume)
                        req.SetRequestHeader("Range", $"bytes={resumeFrom}-");
                    // append=true 续写 .tmp；Abort 时保留进度，避免百兆 bank 每次 stall/重启都从头下。
                    req.downloadHandler = new DownloadHandlerFile(tmpPath, resume) { removeFileOnAbort = false };
                    // 0 = 不设总超时；大 bank 只靠 stall 检测，避免 120s 整请求超时误杀。
                    if (Retry.RequestTimeoutSeconds > 0)
                        req.timeout = Retry.RequestTimeoutSeconds;
                    var op = req.SendWebRequest();

                    ulong lastBytes = 0;
                    float lastProgressTime = Time.realtimeSinceStartup;

                    while (!op.isDone)
                    {
                        if (shouldAbort != null && shouldAbort())
                        {
                            req.Abort();
                            // 用户插队：保留 .tmp，下次还能 Range 续传
                            onAborted?.Invoke();
                            return false;
                        }

                        item.Progress = ResolveDownloadProgress01(item, req, resumeFrom);
                        onProgress?.Invoke(item);

                        ulong bytes = req.downloadedBytes;
                        if (bytes > lastBytes)
                        {
                            lastBytes = bytes;
                            lastProgressTime = Time.realtimeSinceStartup;
                        }
                        else if (Retry.StallTimeoutSeconds > 0f &&
                                 Time.realtimeSinceStartup - lastProgressTime > Retry.StallTimeoutSeconds)
                        {
                            Log.Warning(
                                $"[BankDownloader] stall >{Retry.StallTimeoutSeconds}s abort {item.BankName} " +
                                $"sessionBytes={lastBytes} resumeFrom={resumeFrom} sizeKb={item.SizeKb}");
                            req.Abort();
                            stalled = true;
                            break;
                        }

                        await UniTask.Yield(PlayerLoopTiming.Update);
                    }

                    if (stalled)
                    {
                        httpErr = $"stall timeout bytes={lastBytes} resumeFrom={resumeFrom}";
                        anyNetworkFail = true;
                    }
                    else if (req.result == UnityWebRequest.Result.Success
                             || (resume && req.responseCode == 206))
                    {
                        // Range 被忽略回 200：append 会把整包接到半截后 → MD5 必炸，删掉重来。
                        if (resume && req.responseCode == 200)
                        {
                            Log.Warning(
                                $"[BankDownloader] Range ignored (HTTP 200) {item.BankName} — restart full download");
                            TryDelete(tmpPath);
                            httpErr = "range ignored";
                            anyNetworkFail = true;
                        }
                        else
                        {
                            httpOk = true;
                            item.Progress = 1f;
                            onProgress?.Invoke(item);
                        }
                    }
                    else if (resume && req.responseCode == 416)
                    {
                        // Range 起点超出远端（.tmp 损坏 / 已写满 / 远端变短）：保留半截会反复 416，删掉重来。
                        Log.Warning(
                            $"[BankDownloader] Range not satisfiable (HTTP 416) {item.BankName} " +
                            $"resumeFrom={resumeFrom} — restart full download");
                        TryDelete(tmpPath);
                        httpErr = "range not satisfiable";
                        anyNetworkFail = true;
                    }
                    else
                    {
                        httpCode = req.responseCode;
                        httpErr =
                            $"{req.error ?? $"code={req.responseCode}"} bytes={req.downloadedBytes} resume={resume}";
                        anyNetworkFail = anyNetworkFail || Http.IsRetryableNetworkError(req);
                    }

                    if (httpCode == 0)
                        httpCode = req.responseCode;
                }

                if (!httpOk)
                {
                    // stall / 可重试网络错误：保留 .tmp 做 Range 续传；不可恢复才删。
                    bool retryable = stalled
                                     || string.Equals(httpErr, "range ignored", StringComparison.Ordinal)
                                     || string.Equals(httpErr, "range not satisfiable", StringComparison.Ordinal)
                                     || Http.IsRetryableNetworkError(httpErr);
                    item.Progress = resumeFrom > 0 && retryable
                        ? ResolveProgressFromFile(item, tmpPath)
                        : 0f;
                    item.Error = httpErr;
                    item.State = BankDownloadState.Failed;

                    if (!retryable || attempt >= max)
                    {
                        LogHttpFail(item, httpErr, httpCode, tmpPath, attempt, max, terminal: true);
                        if (!retryable)
                            TryDelete(tmpPath);
                        break;
                    }

                    LogHttpFail(item, httpErr, httpCode, tmpPath, attempt, max, terminal: false);
                    await UniTask.Delay(TimeSpan.FromSeconds(delay), DelayType.UnscaledDeltaTime);
                    delay = Mathf.Min(delay * 2f, Retry.RetryMaxSeconds);
                    continue;
                }

                // 先校验 .tmp，通过后再替换正式文件。失败则删 tmp，上一份还在。
                if (!string.IsNullOrEmpty(item.Md5))
                {
                    string actual = await RunOffMainAsync(() => ComputeMd5(tmpPath));
                    long bytes = GetExistingLength(tmpPath);
                    // 空包 / 读不出哈希不是线上内容错，按可重试的下载失败处理，避免一次就锁成 md5 stable。
                    if (bytes <= 0 || string.Equals(actual, "Error", StringComparison.Ordinal))
                    {
                        TryDelete(tmpPath);
                        item.State = BankDownloadState.Failed;
                        item.Progress = 0f;
                        item.Error = bytes <= 0 ? "empty body" : "md5 unreadable";
                        Log.Warning(
                            "[BankDownloader] empty download bank=" + item.BankName +
                            " version=" + item.Version +
                            " url=" + item.Url +
                            " path=" + item.LocalPath +
                            " bytes=" + bytes +
                            " keptPrevious=" + (File.Exists(item.LocalPath) ? "1" : "0") +
                            " attempt=" + attempt + "/" + max);
                        if (attempt >= max)
                            break;
                        await UniTask.Delay(TimeSpan.FromSeconds(delay), DelayType.UnscaledDeltaTime);
                        delay = Mathf.Min(delay * 2f, Retry.RetryMaxSeconds);
                        continue;
                    }

                    if (!string.Equals(actual, item.Md5, StringComparison.OrdinalIgnoreCase))
                    {
                        bool stable = !string.IsNullOrEmpty(item.LastMismatchMd5)
                                      && string.Equals(item.LastMismatchMd5, actual, StringComparison.OrdinalIgnoreCase);
                        item.LastMismatchMd5 = actual;
                        TryDelete(tmpPath);
                        item.State = BankDownloadState.Failed;
                        item.Progress = 0f;
                        item.Error = (stable ? "md5 stable" : "md5 mismatch")
                                     + $" expect={item.Md5} actual={actual}";
                        Log.Error(
                            "[BankDownloader] md5 fail bank=" + item.BankName +
                            " version=" + item.Version +
                            " url=" + item.Url +
                            " path=" + item.LocalPath +
                            " expect=" + item.Md5 +
                            " actual=" + actual +
                            " bytes=" + bytes +
                            " keptPrevious=" + (File.Exists(item.LocalPath) ? "1" : "0") +
                            " stable=" + (stable ? "1" : "0") +
                            " attempt=" + attempt + "/" + max);
                        if (stable || attempt >= max)
                        {
                            if (stable)
                                NoteContentTerminal(item.BankName, "md5 与线上不一致");
                            break;
                        }

                        await UniTask.Delay(TimeSpan.FromSeconds(delay), DelayType.UnscaledDeltaTime);
                        delay = Mathf.Min(delay * 2f, Retry.RetryMaxSeconds);
                        continue;
                    }
                }

                string header = WanosAudio.WanosBankStore.ExplainInvalidBankFile(tmpPath);
                if (header != null)
                {
                    long bytes = GetExistingLength(tmpPath);
                    TryDelete(tmpPath);
                    item.State = BankDownloadState.Failed;
                    item.Progress = 0f;
                    item.Error = "header " + header;
                    Log.Error(
                        "[BankDownloader] header fail bank=" + item.BankName +
                        " version=" + item.Version +
                        " url=" + item.Url +
                        " path=" + item.LocalPath +
                        " reason=" + header +
                        " bytes=" + bytes +
                        " keptPrevious=" + (File.Exists(item.LocalPath) ? "1" : "0"));
                    NoteContentTerminal(item.BankName, "文件头不合法 " + header);
                    break;
                }

                try
                {
                    ReplaceWithVerifiedTemp(tmpPath, item.LocalPath);
                }
                catch (Exception e)
                {
                    TryDelete(tmpPath);
                    item.State = BankDownloadState.Failed;
                    item.Error = e.Message;
                    Log.Error(
                        "[BankDownloader] move fail bank=" + item.BankName +
                        " version=" + item.Version +
                        " url=" + item.Url +
                        " path=" + item.LocalPath +
                        " err=" + e.Message +
                        " keptPrevious=" + (File.Exists(item.LocalPath) ? "1" : "0"));
                    break;
                }

                item.State = BankDownloadState.Ready;
                item.Progress = 1f;
                item.Error = null;
                item.LastMismatchMd5 = null;
                RememberLocalMd5(item);
                NotifyBankReady(item.BankName);
                onProgress?.Invoke(item);
                Log.Info(
                    "[BankDownloader] ready " + item.BankName +
                    " version=" + item.Version +
                    " md5=" + item.Md5 +
                    " path=" + item.LocalPath +
                    " attempts=" + attempt);
                return false;
            }

            return anyNetworkFail;
        }

        public static bool TryGetCachedSize(string bankName, out long size)
            => BankMd5Cache.Load().TryGetStoredSize(bankName, out size);

        public static bool CacheMatchesFile(string bankName, long size, long mtimeTicks)
            => BankMd5Cache.Load().TryGet(bankName, size, mtimeTicks, out _, out _);

        public static void EditorSetMd5Cache(
            string bankName, long size, long mtimeTicks, string md5, string version)
        {
            var cache = BankMd5Cache.Load();
            cache.Set(bankName, size, mtimeTicks, md5, version);
            cache.Save();
        }

        public static void EditorRemoveMd5Cache(string bankName)
        {
            var cache = BankMd5Cache.Load();
            cache.Remove(bankName);
            cache.Save();
        }

        static void RememberLocalMd5(BankDownloadItem item)
        {
            if (item == null || string.IsNullOrEmpty(item.Md5) || string.IsNullOrEmpty(item.LocalPath))
                return;
            if (!File.Exists(item.LocalPath))
                return;

            try
            {
                var fi = new FileInfo(item.LocalPath);
                var cache = BankMd5Cache.Load();
                cache.Set(item.BankName, fi.Length, fi.LastWriteTimeUtc.Ticks, item.Md5, item.Version ?? "");
                cache.Save();
            }
            catch
            {
                // ignore cache write failures
            }
        }

        /// <returns>false if aborted while waiting。</returns>
        static async UniTask<bool> WaitForNetworkAsync(Func<bool> shouldAbort = null)
        {
            float interval = Retry.NetworkCheckIntervalSeconds > 0
                ? Retry.NetworkCheckIntervalSeconds
                : 2f;
            while (!DownloadCellularGate.CanTransmitNow())
            {
                if (shouldAbort != null && shouldAbort())
                    return false;
                // 无网或 WifiOnly+蜂窝：停在槽内等可达/开闸；HoldForCellularPolicy 会 Abort。
                await UniTask.Delay(TimeSpan.FromSeconds(interval), DelayType.UnscaledDeltaTime);
            }

            return shouldAbort == null || !shouldAbort();
        }

        static float ResolveDownloadProgress01(
            BankDownloadItem item, UnityWebRequest req, long resumeFrom = 0)
        {
            // 体积已知时只信已写字节。downloadProgress 提前变成 1 会把窗在条还是空的时关掉。
            if (item != null
                && int.TryParse(item.SizeKb, out int kb)
                && kb > 0)
            {
                double total = kb * 1024.0;
                double got = resumeFrom;
                if (req != null)
                    got += req.downloadedBytes;
                if (total > 0d)
                    return Mathf.Clamp01((float)(got / total));
            }

            float p = req != null ? req.downloadProgress : 0f;
            return Mathf.Clamp01(p);
        }

        static float ResolveProgressFromFile(BankDownloadItem item, string path)
        {
            if (item == null || string.IsNullOrEmpty(path))
                return 0f;
            if (!int.TryParse(item.SizeKb, out int kb) || kb <= 0)
                return 0f;
            long len = GetExistingLength(path);
            if (len <= 0)
                return 0f;
            return Mathf.Clamp01((float)(len / (kb * 1024.0)));
        }

        static long GetExistingLength(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                    return 0;
                var fi = new FileInfo(path);
                return fi.Exists ? fi.Length : 0;
            }
            catch
            {
                return 0;
            }
        }

        static void EnsureParentDir(string path)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
        }

        static void PrepareTemp(string tmpPath)
        {
            EnsureParentDir(tmpPath);
            TryDelete(tmpPath);
        }

        /// <summary>
        /// 落盘完成后异步灌引擎。不得挡住 FireDone / DownloadBanks 返回。
        /// </summary>
        static void ScheduleTryLoadBankWhenSafe(BankDownloadItem item)
        {
            if (item == null || string.IsNullOrEmpty(item.BankName))
                return;
            TryLoadBankWhenSafeAsync(item).Forget();
        }

        static async UniTaskVoid TryLoadBankWhenSafeAsync(BankDownloadItem item)
        {
            try
            {
                await WaitUntilSafeToLoadBank();
                TryLoadBank(item);
                await UniTask.Yield(PlayerLoopTiming.Update);
            }
            catch (Exception e)
            {
                Log.Warning(
                    $"[BankDownloader] deferred TryLoadBank failed bank={item?.BankName}: {e.Message}");
            }
        }

        /// <summary>
        /// 原生 LoadBank 占主线程。
        /// 铺装期间不能灌，否则近场 Prefill 无法揭罩。
        /// 罩还盖着且近场已铺完：在罩下灌，卡顿不进已经能看的岛。
        /// 罩已经撤了：让过揭罩静默窗，避免和 StartEngine / 第一帧全场景叠在同一拍上把岛卡死。
        /// 仅用于延后灌包；下载完成回调不得 await 本方法。
        /// </summary>
        static async UniTask WaitUntilSafeToLoadBank()
        {
            // 兜底：Prefill / 罩状态异常时不能无限占协程（灌包延后也不应永久挂起）。
            const float maxWaitSeconds = 90f;
            float deadline = Time.realtimeSinceStartup + maxWaitSeconds;
            while (true)
            {
                if (Time.realtimeSinceStartup >= deadline)
                {
                    Log.Warning(
                        "[BankDownloader] WaitUntilSafeToLoadBank timed out — load anyway " +
                        $"prefill={EnvItemStreamerLite.PrefillSuppressRender} " +
                        $"bootVisible={BootLoadingService.IsVisible}");
                    return;
                }

                if (EnvItemStreamerLite.PrefillSuppressRender)
                {
                    await UniTask.Yield(PlayerLoopTiming.Update);
                    continue;
                }

                if (BootLoadingService.IsVisible)
                {
                    IslandWorldService world = GameEntry.World;
                    if (world == null || !world.IsNearReady)
                    {
                        await UniTask.Yield(PlayerLoopTiming.Update);
                        continue;
                    }

                    return;
                }

                if (EnvStreamingConfig.IsPostUncoverQuiet)
                {
                    await UniTask.Yield(PlayerLoopTiming.Update);
                    continue;
                }

                await UniTask.Yield(PlayerLoopTiming.Update);
                return;
            }
        }

        static void TryLoadBank(BankDownloadItem item)
        {
            if (item == null || string.IsNullOrEmpty(item.BankName))
                return;
            string path = item.LocalPath;
            if (string.IsNullOrEmpty(path))
                path = ResolveLocalPath(item.BankName);

            // 诊断：引擎未创建时直接说明原因
            if (!WanosAudio.WanosNative.Instance.IsCreated)
            {
                Log.Warning($"[BankDownloader] TryLoadBank SKIP bank={item.BankName} reason=engine-not-created path={path}");
                return;
            }

            var sw = Stopwatch.StartNew();
            // 校验已通过。标记要等原生加载成功才落盘；这次先一次性放行。
            BankLoadGate.AllowVerifiedThisSession(path);
            bool ok = WanosAudio.WanosBankStore.LoadFromPath(item.BankName, path);
            sw.Stop();
            if (sw.ElapsedMilliseconds >= 200)
            {
                Log.Warning(
                    $"[BankDownloader] TryLoadBank slow bank={item.BankName} ms={sw.ElapsedMilliseconds} ok={ok}");
            }
            if (!ok)
            {
                string detail = WanosAudio.WanosBankStore.LastLoadDetail;
                if (string.IsNullOrEmpty(detail))
                {
                    detail = WanosAudio.WanosBankStore.IsLoaded(item.BankName)
                        ? "already-loaded-but-returned-false"
                        : (!File.Exists(path) ? "file-missing" : "header-invalid-or-native-fail");
                }

                Log.Warning(
                    "[BankDownloader] load fail bank=" + item.BankName +
                    " version=" + item.Version +
                    " url=" + item.Url +
                    " path=" + path +
                    " detail=" + detail +
                    " — 不中断后续 bank");
                if (IsContentLoadDetail(detail))
                    NoteContentTerminal(item.BankName, detail);
            }
            else
            {
                Log.Info(
                    "[BankDownloader] TryLoadBank OK bank=" + item.BankName +
                    " version=" + item.Version +
                    " md5=" + item.Md5 +
                    " path=" + path);
            }
        }

        static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch { /* ignore */ }
        }

        static bool IsServerGapError(string error) =>
            string.Equals(error, BankDownloadItem.UrlEmptyError, StringComparison.Ordinal)
            || string.Equals(error, BankDownloadItem.Md5EmptyError, StringComparison.Ordinal);

        static void AcceptLocalDespiteServerGap(BankDownloadItem item)
        {
            if (item == null)
                return;
            bool noUrl = string.IsNullOrEmpty(item.Url);
            bool noMd5 = string.IsNullOrEmpty(item.Md5);
            item.State = BankDownloadState.Ready;
            item.Progress = 1f;
            item.Error = null;
            if (string.IsNullOrEmpty(item.SkipReason))
                item.SkipReason = noUrl ? "no url, use local" : "no md5, use local";
            MarkLocalReady(item.BankName);
        }

        /// <summary>
        /// 目录缺 url/md5：只打日志。本地有就直接用，不弹「已使用本地」。
        /// 真正坏包走 <see cref="NoteContentTerminal"/>。
        /// </summary>
        static void NoteServerGap(BankDownloadItem item)
        {
            if (item == null)
                return;
            bool noUrl = string.IsNullOrEmpty(item.Url);
            bool noMd5 = string.IsNullOrEmpty(item.Md5);
            if (!noUrl && !noMd5)
                return;

            string stem = AudioEnginePaths.StripBankExtension(item.BankName);
            if (string.IsNullOrEmpty(stem))
                return;

            string missing = noUrl && noMd5
                ? "目录缺 url 与 md5"
                : noUrl
                    ? "目录缺 url"
                    : "目录缺 md5";
            Log.Warning(
                "[BankDownloader] " + stem + " " + missing +
                " — 有本地则静默使用，不弹提示");
        }

        static void NoteContentTerminal(string bankName, string reason)
        {
            string stem = AudioEnginePaths.StripBankExtension(bankName);
            if (string.IsNullOrEmpty(stem))
                return;
            string text = stem + " 无法播放（" + reason + "）";
            Log.Warning("[BankDownloader] " + text);
            if (!ServerGapToasted.Add(stem))
                return;
            ShowTestReminder(text);
        }

        static void ShowTestReminder(string text)
        {
            if (!BoluoDeviceInfo.IsTestChannel())
                return;
            Game.UiDialogService.ShowRequestFailure(text);
        }

        static bool IsContentLoadDetail(string detail)
        {
            if (string.IsNullOrEmpty(detail))
                return false;
            if (detail == "engine-not-created"
                || detail == "empty-name"
                || detail == "already-failed"
                || detail == "in-flight"
                || detail == "file-missing"
                || detail == "already-loaded-but-returned-false"
                || detail.StartsWith("gate ", StringComparison.Ordinal))
                return false;
            return true;
        }

        static void LogHttpFail(
            BankDownloadItem item,
            string httpErr,
            long httpCode,
            string tmpPath,
            int attempt,
            int max,
            bool terminal)
        {
            string line =
                "[BankDownloader] http fail bank=" + item.BankName +
                " version=" + item.Version +
                " url=" + item.Url +
                " path=" + item.LocalPath +
                " http=" + httpCode +
                " err=" + httpErr +
                " keptPrevious=" + (File.Exists(item.LocalPath) ? "1" : "0") +
                " tmpBytes=" + GetExistingLength(tmpPath) +
                " attempt=" + attempt + "/" + max;
            if (terminal)
                Log.Error(line);
            else
                Log.Warning(line);
        }

        static void LogBatchSummary(List<BankDownloadItem> all)
        {
            if (all == null)
                return;
            var updated = new StringBuilder();
            var failed = new StringBuilder();
            var kept = new StringBuilder();
            for (int i = 0; i < all.Count; i++)
            {
                BankDownloadItem item = all[i];
                if (item == null || string.IsNullOrEmpty(item.BankName))
                    continue;
                if (item.State == BankDownloadState.Ready)
                {
                    if (updated.Length > 0)
                        updated.Append(',');
                    updated.Append(item.BankName);
                    continue;
                }

                bool contentFail = item.State == BankDownloadState.Failed
                    || (item.State == BankDownloadState.Skipped
                        && string.Equals(item.Error, BankDownloadItem.UrlEmptyError, StringComparison.Ordinal));
                if (!contentFail)
                    continue;
                if (failed.Length > 0)
                    failed.Append(',');
                failed.Append(item.BankName);
                failed.Append(':');
                failed.Append(string.IsNullOrEmpty(item.Error) ? item.State.ToString() : item.Error);
                if (!string.IsNullOrEmpty(item.LocalPath) && File.Exists(item.LocalPath))
                {
                    if (kept.Length > 0)
                        kept.Append(',');
                    kept.Append(item.BankName);
                }
            }

            Log.Info(
                "[BankDownloader] batch updated=[" + updated +
                "] failed=[" + failed +
                "] keptPrevious=[" + kept + "]");
        }

        /// <summary>校验已通过的 tmp 换成正式文件。第二步失败时把上一份放回去。</summary>
        static void ReplaceWithVerifiedTemp(string tmpPath, string finalPath)
        {
            string dir = Path.GetDirectoryName(finalPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            if (!File.Exists(tmpPath))
                throw new FileNotFoundException("verified tmp missing", tmpPath);
            if (!File.Exists(finalPath))
            {
                File.Move(tmpPath, finalPath);
                return;
            }

            string bak = finalPath + ".replacing";
            TryDelete(bak);
            File.Move(finalPath, bak);
            try
            {
                File.Move(tmpPath, finalPath);
                TryDelete(bak);
            }
            catch
            {
                if (!File.Exists(finalPath) && File.Exists(bak))
                    File.Move(bak, finalPath);
                throw;
            }
        }

        struct Md5Job
        {
            public int Index;
            public string Path;
            public string ExpectedMd5;
            public string ActualMd5;
            public long Size;
            public long MtimeTicks;
            public bool Match;
        }

        sealed class HashBatchResult
        {
            public List<Md5Job> Jobs;
            public double ElapsedMs;
        }

        /// <summary>
        /// path 旁路缓存：bankName → size + mtime + md5 + version。命中则跳过读盘哈希。
        /// 旧的 4 列行 version 为空。
        /// </summary>
        sealed class BankMd5Cache
        {
            const string FileName = ".bank_md5_cache";
            static BankMd5Cache _instance;
            static readonly object Gate = new object();

            readonly Dictionary<string, Entry> _map =
                new Dictionary<string, Entry>(128, StringComparer.OrdinalIgnoreCase);
            bool _loaded;

            struct Entry
            {
                public long Size;
                public long MtimeTicks;
                public string Md5;
                public string Version;
            }

            public static BankMd5Cache Load()
            {
                lock (Gate)
                {
                    if (_instance != null && _instance._loaded)
                        return _instance;

                    _instance = new BankMd5Cache();
                    _instance.LoadFromDisk();
                    return _instance;
                }
            }

            public bool TryGet(
                string bankName, long size, long mtimeTicks, out string md5, out string version)
            {
                md5 = null;
                version = null;
                string stem = AudioEnginePaths.StripBankExtension(bankName);
                if (string.IsNullOrEmpty(stem))
                    return false;
                lock (Gate)
                {
                    if (!_map.TryGetValue(stem, out var entry))
                        return false;
                    if (entry.Size != size || entry.MtimeTicks != mtimeTicks)
                        return false;
                    if (string.IsNullOrEmpty(entry.Md5))
                        return false;
                    md5 = entry.Md5;
                    version = entry.Version ?? "";
                    return true;
                }
            }

            public bool TryGetStoredSize(string bankName, out long size)
            {
                size = 0;
                string stem = AudioEnginePaths.StripBankExtension(bankName);
                if (string.IsNullOrEmpty(stem))
                    return false;
                lock (Gate)
                {
                    if (!_map.TryGetValue(stem, out var entry) || string.IsNullOrEmpty(entry.Md5))
                        return false;
                    size = entry.Size;
                    return size > 0;
                }
            }

            public bool TryPeek(string bankName, out string version, out string md5)
            {
                version = null;
                md5 = null;
                string stem = AudioEnginePaths.StripBankExtension(bankName);
                if (string.IsNullOrEmpty(stem))
                    return false;
                lock (Gate)
                {
                    if (!_map.TryGetValue(stem, out var entry) || string.IsNullOrEmpty(entry.Md5))
                        return false;
                    version = entry.Version ?? "";
                    md5 = entry.Md5;
                    return true;
                }
            }

            public bool Remove(string bankName)
            {
                string stem = AudioEnginePaths.StripBankExtension(bankName);
                if (string.IsNullOrEmpty(stem))
                    return false;
                lock (Gate)
                    return _map.Remove(stem);
            }

            public void Set(string bankName, long size, long mtimeTicks, string md5, string version)
            {
                string stem = AudioEnginePaths.StripBankExtension(bankName);
                if (string.IsNullOrEmpty(stem) || string.IsNullOrEmpty(md5))
                    return;
                lock (Gate)
                {
                    _map[stem] = new Entry
                    {
                        Size = size,
                        MtimeTicks = mtimeTicks,
                        Md5 = md5,
                        Version = version ?? "",
                    };
                }
            }

            public void Save()
            {
                lock (Gate)
                {
                    try
                    {
                        EnsureWritableDirs();
                        string path = Path.Combine(PersistentBankPath, FileName);
                        string tmp = path + ".tmp";
                        var sb = new StringBuilder(_map.Count * 64);
                        foreach (var kv in _map)
                        {
                            sb.Append(kv.Key);
                            sb.Append('\t');
                            sb.Append(kv.Value.Size);
                            sb.Append('\t');
                            sb.Append(kv.Value.MtimeTicks);
                            sb.Append('\t');
                            sb.Append(kv.Value.Md5);
                            sb.Append('\t');
                            sb.Append(kv.Value.Version ?? "");
                            sb.Append('\n');
                        }

                        // UTF8Encoding(false)：禁止 BOM，否则首行 bankName 带 \uFEFF，缓存永远 miss。
                        File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                        if (File.Exists(path))
                            File.Delete(path);
                        File.Move(tmp, path);
                    }
                    catch (Exception e)
                    {
                        Log.Warning($"[BankDownloader] md5 cache save failed: {e.Message}");
                    }
                }
            }

            void LoadFromDisk()
            {
                _loaded = true;
                try
                {
                    string path = Path.Combine(PersistentBankPath, FileName);
                    if (!File.Exists(path))
                        return;

                    foreach (var line in File.ReadAllLines(path))
                    {
                        if (string.IsNullOrEmpty(line) || line[0] == '#')
                            continue;
                        var parts = line.Split('\t');
                        if (parts.Length < 4)
                            continue;
                        if (!long.TryParse(parts[1], out long size))
                            continue;
                        if (!long.TryParse(parts[2], out long mtime))
                            continue;
                        string name = AudioEnginePaths.StripBankExtension(
                            parts[0].Trim().TrimStart('\uFEFF'));
                        string md5 = parts[3].Trim();
                        string version = parts.Length >= 5 ? parts[4].Trim() : "";
                        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(md5))
                            continue;
                        _map[name] = new Entry
                        {
                            Size = size,
                            MtimeTicks = mtime,
                            Md5 = md5,
                            Version = version,
                        };
                    }
                }
                catch (Exception e)
                {
                    Log.Warning($"[BankDownloader] md5 cache load failed: {e.Message}");
                    _map.Clear();
                }
            }
        }
    }
}
