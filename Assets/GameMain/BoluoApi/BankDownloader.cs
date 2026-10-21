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
        public string BankName;
        public string Url;
        public string Md5;
        public string SizeKb;
        public string Guid;
        public string LocalPath;
        public BankDownloadState State = BankDownloadState.Pending;
        public float Progress;
        public string Error;
        public string SkipReason;
        public int AttemptCount;
    }

    public enum BankQueueKind : byte
    {
        Normal = 0,
        Startup = 1,
        User = 2,
    }

    /// <summary>
    /// 按 upgrade/bank 返回的 banks[] 下载到 <see cref="PersistentBankPath"/>。
    /// 三队列：用户点击 / 启动优先 / 后台。启动最多 3 路，后台 1 路；点击插队并打断。
    /// </summary>
    public static class BankDownloader
    {
        static readonly Http.RetryPolicy Retry = Http.RetryPolicy.DefaultDownload;

        /// <summary>整队 Failed 再入队的最大轮次（防止无限循环）。</summary>
        const int MaxFailedRequeuePasses = 2;

        /// <summary>主线程每处理几个已就绪 bank 让出一帧（预留给新音频 SDK 灌引擎）。</summary>
        const int LoadBanksPerFrame = 2;

        const int MaxStartupSlots = 3;
        const int MaxBackgroundSlots = 1;
        static int _startupParallelSlots = MaxStartupSlots;

        static readonly Dictionary<string, QueueEntry> Entries =
            new Dictionary<string, QueueEntry>(StringComparer.OrdinalIgnoreCase);

        static readonly List<string> UserQueue = new List<string>();
        static readonly List<string> StartupQueue = new List<string>();
        static readonly List<string> NormalQueue = new List<string>();
        static readonly List<ActiveSlot> Actives = new List<ActiveSlot>();

        static bool _runnerActive;

        public static void ResetSession()
        {
            _runnerActive = false;
            _startupParallelSlots = MaxStartupSlots;
            Entries.Clear();
            UserQueue.Clear();
            StartupQueue.Clear();
            NormalQueue.Clear();
            Actives.Clear();
        }

        sealed class ActiveSlot
        {
            public string BankName;
            public BankQueueKind Kind;
            public bool Abort;
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

        /// <summary>队列里正在下 / 等待的条目。UI 进度用这个，不要只看磁盘上是否已有旧文件。</summary>
        public static bool TryGetActiveItem(string bankName, out BankDownloadItem item)
        {
            item = null;
            if (string.IsNullOrEmpty(bankName))
                return false;
            if (!Entries.TryGetValue(bankName, out var entry) || entry?.Item == null)
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
                if (item.State == BankDownloadState.Ready || item.State == BankDownloadState.Skipped)
                    return true;
                // 正在 HTTP 拉取才挡；Pending/Failed 仍看磁盘，避免启动队列占用时 UI 显示未下载
                if (item.State == BankDownloadState.Downloading)
                    return false;
            }

            string stem = AudioEnginePaths.StripBankExtension(bankName);
            string path = Path.Combine(PersistentBankPath, stem + AudioEnginePaths.BankExtension);
            if (!File.Exists(path))
                AudioEnginePaths.TryFindLocalBank(bankName, out path);

            PresetBankCatalog.TryGet(bankName, out var remote);

            // 下载完成写在 .tmp、Move 被打断：size 对齐则提升为正式文件。
            TryPromoteCompleteTemp(path, remote);

            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return false;

            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists || fi.Length <= 0)
                    return false;

                // 正式文件已就绪时，残留 .tmp 只删不挡（旧逻辑会导致「本地有了还显示要下」）。
                string tmpPath = path + ".tmp";
                if (File.Exists(tmpPath))
                    TryDelete(tmpPath);

                if (remote == null)
                    return true;
                return FileMatchesRemoteSize(fi.Length, remote);
            }
            catch
            {
                return false;
            }
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

        /// <summary>Critical 波用 3，Fill / Weather 用 1。点击进行中强制单槽。</summary>
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
        /// </summary>
        public static async UniTask<IReadOnlyList<BankDownloadItem>> DownloadBanks(
            IList<BankRemoteInfo> banks,
            bool loadIntoEngine = true,
            Action<IReadOnlyList<BankDownloadItem>> onComplete = null,
            Action<BankDownloadItem> onItemDone = null,
            Action<BankDownloadItem> onItemProgress = null,
            bool prioritize = false,
            bool preempt = false)
        {
            int frameBeforeQueue = Time.frameCount;
            float tBeforeQueue = Time.realtimeSinceStartup;
            Log.Info(
                $"[BankDownloader] DownloadBanks ENTER frame={frameBeforeQueue} " +
                $"loadIntoEngine={loadIntoEngine} prioritize={prioritize} preempt={preempt}");

            var all = BuildQueue(banks);
            await VerifyLocalReadyAsync(all);

            // 是否下载只看本地 MD5 / 是否存在（对齐老工程 WanosAssets.LoadAsync）。
            // bank.version 是 bank 内容版本（如 1.0.0），不能和 Application.version 比。

            for (int i = 0; i < all.Count; i++)
            {
                var item = all[i];
                if (item.State == BankDownloadState.Pending && string.IsNullOrEmpty(item.Url))
                {
                    item.State = BankDownloadState.Skipped;
                    item.Error = "url empty";
                }
            }

            Log.Info(
                $"[BankDownloader] start count={all.Count} dir={PersistentBankPath} " +
                $"ready={CountState(all, BankDownloadState.Ready)} " +
                $"pending={CountState(all, BankDownloadState.Pending)} " +
                $"maxAttempts={Retry.MaxAttempts} stall={Retry.StallTimeoutSeconds}s " +
                $"verifyWallMs={(Time.realtimeSinceStartup - tBeforeQueue) * 1000f:F0}");

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
                    if (loadIntoEngine)
                        TryLoadBank(item);
                    NotifyBankReady(item.BankName);
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
            onComplete?.Invoke(all);
            return all;
        }

        /// <summary>单 bank 下载入口（优先队列 / UI 点选可复用）。</summary>
        public static async UniTask<BankDownloadItem> DownloadBank(
            BankRemoteInfo bank,
            bool loadIntoEngine = true,
            Action<BankDownloadItem> onDone = null,
            bool prioritize = false,
            bool preempt = false)
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
                preempt);
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
            Action<BankDownloadItem> onDone,
            Action<BankDownloadItem> onProgress)
        {
            if (item == null || string.IsNullOrEmpty(item.BankName))
                return;

            var kind = preempt ? BankQueueKind.User
                : prioritize ? BankQueueKind.Startup
                : BankQueueKind.Normal;

            if (Entries.TryGetValue(item.BankName, out var entry))
            {
                entry.LoadIntoEngine |= loadIntoEngine;
                if (kind > entry.Kind)
                    entry.Kind = kind;
                if (entry.Item.State == BankDownloadState.Pending ||
                    entry.Item.State == BankDownloadState.Failed)
                {
                    if (!string.IsNullOrEmpty(item.Url))
                        entry.Item.Url = item.Url;
                    if (!string.IsNullOrEmpty(item.Md5))
                        entry.Item.Md5 = item.Md5;
                    if (!string.IsNullOrEmpty(item.SizeKb))
                        entry.Item.SizeKb = item.SizeKb;
                    if (!string.IsNullOrEmpty(item.Guid))
                        entry.Item.Guid = item.Guid;
                }

                if (onDone != null)
                    entry.OnDone.Add(onDone);
                if (onProgress != null)
                    entry.OnProgress.Add(onProgress);

                if (entry.Item.State == BankDownloadState.Ready ||
                    entry.Item.State == BankDownloadState.Skipped ||
                    (entry.Item.State == BankDownloadState.Failed &&
                     entry.RequeueCount >= MaxFailedRequeuePasses))
                {
                    CopyItemState(item, entry.Item);
                    onDone?.Invoke(entry.Item);
                    if (entry.OnDone.Count > 0)
                        entry.OnDone.Remove(onDone);
                    return;
                }

                PlaceInQueue(item.BankName, entry.Kind, preempt);
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
            PlaceInQueue(item.BankName, kind, preempt);
        }

        static void PlaceInQueue(string bankName, BankQueueKind kind, bool preempt)
        {
            RemoveFromQueue(UserQueue, bankName);
            RemoveFromQueue(StartupQueue, bankName);
            RemoveFromQueue(NormalQueue, bankName);

            if (kind == BankQueueKind.User)
            {
                UserQueue.Insert(0, bankName);
                AbortBackgroundExcept(bankName);
                Log.Info($"[BankDownloader] User enqueue {bankName}");
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

        /// <summary>点另一张预设时不打断已在下的用户 bank，避免上一张进度被清零。</summary>
        static void AbortBackgroundExcept(string keepBankName)
        {
            for (int i = 0; i < Actives.Count; i++)
            {
                var slot = Actives[i];
                if (slot == null)
                    continue;
                if (slot.Kind == BankQueueKind.User)
                    continue;
                if (string.Equals(slot.BankName, keepBankName, StringComparison.OrdinalIgnoreCase))
                    continue;
                slot.Abort = true;
                Log.Info($"[BankDownloader] abort {slot.BankName} kind={slot.Kind} for user {keepBankName}");
            }
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
            if (UserQueue.Count > 0 || HasActiveKind(BankQueueKind.User))
                return 1;
            if (StartupQueue.Count > 0 || HasActiveKind(BankQueueKind.Startup))
                return _startupParallelSlots;
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

            if (UserQueue.Count > 0 || StartupQueue.Count > 0 || NormalQueue.Count > 0 || Entries.Count > 0)
                EnsureRunner();
        }

        static void PumpStarts()
        {
            int max = DesiredMaxSlots();
            while (Actives.Count < max)
            {
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

                if (string.IsNullOrEmpty(entry.Item.Url))
                {
                    entry.Item.State = BankDownloadState.Skipped;
                    entry.Item.Error = "url empty";
                    FireDone(entry);
                    Entries.Remove(bankName);
                    continue;
                }

                RunSlot(entry, kind).Forget();
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

            if (StartupQueue.Count > 0)
            {
                bankName = StartupQueue[0];
                StartupQueue.RemoveAt(0);
                kind = BankQueueKind.Startup;
                return true;
            }

            if (NormalQueue.Count > 0
                && !HasActiveKind(BankQueueKind.Startup)
                && !HasActiveKind(BankQueueKind.User))
            {
                bankName = NormalQueue[0];
                NormalQueue.RemoveAt(0);
                kind = BankQueueKind.Normal;
                return true;
            }

            return false;
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
                await WaitForNetworkAsync();
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
                    FireProgress(entry);
                    Log.Info($"[BankDownloader] aborted, requeue {bankName} kind={entry.Kind}");
                    return;
                }

                if (entry.Item.State == BankDownloadState.Ready)
                {
                    if (entry.LoadIntoEngine)
                    {
                        TryLoadBank(entry.Item);
                        await UniTask.Yield(PlayerLoopTiming.Update);
                    }

                    NotifyBankReady(bankName);
                    FireDone(entry);
                    Entries.Remove(bankName);
                    return;
                }

                if (entry.Item.State == BankDownloadState.Failed &&
                    IsRetryableItem(entry.Item) &&
                    entry.RequeueCount < MaxFailedRequeuePasses)
                {
                    entry.RequeueCount++;
                    entry.Item.State = BankDownloadState.Pending;
                    entry.Item.Progress = 0f;
                    entry.Item.AttemptCount = 0;
                    PlaceInQueue(bankName, entry.Kind, preempt: entry.Kind == BankQueueKind.User);
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

                FireDone(entry);
                Entries.Remove(bankName);
            }
            finally
            {
                Actives.Remove(slot);
            }
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

        static void NotifyBankReady(string bankName)
        {
            // Preset hooks deferred — download path only.
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
                entry.RequeueCount++;
                entry.Item.State = BankDownloadState.Pending;
                entry.Item.Progress = 0f;
                entry.Item.AttemptCount = 0;
                PlaceInQueue(name, entry.Kind, preempt: entry.Kind == BankQueueKind.User);
            }

            return true;
        }

        static void FireProgress(QueueEntry entry)
        {
            if (entry == null)
                return;
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

            // Preset progress hook deferred.
        }

        static void FireDone(QueueEntry entry)
        {
            if (entry == null)
                return;
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
        /// 本地 bank 就绪：缓存(size+mtime+md5) 命中则跳过；否则 ThreadPool 算 MD5。
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
            int missing = 0;

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

                if (string.IsNullOrEmpty(item.Md5))
                {
                    item.State = BankDownloadState.Ready;
                    item.Progress = 1f;
                    item.LocalPath = verifyPath;
                    continue;
                }

                long size;
                long mtimeTicks;
                try
                {
                    var fi = new FileInfo(verifyPath);
                    size = fi.Length;
                    mtimeTicks = fi.LastWriteTimeUtc.Ticks;
                }
                catch
                {
                    missing++;
                    continue;
                }

                if (cache.Matches(item.BankName, size, mtimeTicks, item.Md5))
                {
                    item.State = BankDownloadState.Ready;
                    item.Progress = 1f;
                    item.LocalPath = verifyPath;
                    cacheHits++;
                    continue;
                }

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

                bool cacheDirty = false;
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
                        cache.Set(item.BankName, job.Size, job.MtimeTicks, item.Md5);
                        cacheDirty = true;
                    }
                    else
                    {
                        Log.Warning(
                            $"[BankDownloader] local md5 mismatch bank={item.BankName} " +
                            $"expect={job.ExpectedMd5} actual={job.ActualMd5} path={job.Path} — will re-download");
                    }
                }

                if (cacheDirty)
                    cache.Save();
            }

            Log.Info(
                $"[BankDownloader] VerifyLocal END frameDelta={Time.frameCount - frameBegin} " +
                $"wallMs={(Time.realtimeSinceStartup - t0) * 1000f:F0} " +
                $"cacheHits={cacheHits} hashed={hashed} matched={matched} missing={missing} " +
                $"hashThreadMs={hashMs:F0} ready={CountState(list, BankDownloadState.Ready)}");
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
            if (item.Error.StartsWith("md5 mismatch", StringComparison.OrdinalIgnoreCase))
                return true;
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
                if (File.Exists(item.LocalPath))
                {
                    long len = 0;
                    try { len = new FileInfo(item.LocalPath).Length; } catch { /* ignore */ }
                    bool sizeOk = sizeHint == null
                                  || string.IsNullOrEmpty(sizeHint.size)
                                  || FileMatchesRemoteSize(len, sizeHint);
                    if (sizeOk
                        && (string.IsNullOrEmpty(item.Md5)
                            || await LocalFileMd5MatchesAsync(item.LocalPath, item.Md5)))
                    {
                        TryDelete(item.LocalPath + ".tmp");
                        RememberLocalMd5(item);
                        item.State = BankDownloadState.Ready;
                        item.Progress = 1f;
                        item.Error = null;
                        onProgress?.Invoke(item);
                        Log.Info($"[BankDownloader] skip HTTP, local ready {item.BankName}");
                        return false;
                    }
                }

                await WaitForNetworkAsync();
                if (shouldAbort != null && shouldAbort())
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
                        httpErr =
                            $"{req.error ?? $"code={req.responseCode}"} bytes={req.downloadedBytes} resume={resume}";
                        anyNetworkFail = anyNetworkFail || Http.IsRetryableNetworkError(req);
                    }
                }

                if (!httpOk)
                {
                    // stall / 可重试网络错误：保留 .tmp 做 Range 续传；不可恢复才删。
                    bool retryable = stalled
                                     || string.Equals(httpErr, "range ignored", StringComparison.Ordinal)
                                     || string.Equals(httpErr, "range not satisfiable", StringComparison.Ordinal)
                                     || Http.IsRetryableNetworkError(httpErr);
                    if (!retryable)
                        TryDelete(tmpPath);

                    item.Progress = resumeFrom > 0 && retryable
                        ? ResolveProgressFromFile(item, tmpPath)
                        : 0f;
                    item.Error = httpErr;
                    item.State = BankDownloadState.Failed;

                    if (!retryable || attempt >= max)
                    {
                        Log.Error($"[BankDownloader] fail {item.BankName}: {httpErr} attempt={attempt}/{max}");
                        if (!retryable)
                            TryDelete(tmpPath);
                        break;
                    }

                    Log.Warning(
                        $"[BankDownloader] {item.BankName} attempt={attempt}/{max} retry in {delay:0.#}s " +
                        $"err={httpErr} keepTmp={retryable} tmpBytes={GetExistingLength(tmpPath)}");
                    await UniTask.Delay(TimeSpan.FromSeconds(delay), DelayType.UnscaledDeltaTime);
                    delay = Mathf.Min(delay * 2f, Retry.RetryMaxSeconds);
                    continue;
                }

                if (!string.IsNullOrEmpty(item.Md5))
                {
                    string actual = await RunOffMainAsync(() => ComputeMd5(tmpPath));
                    if (!string.Equals(actual, item.Md5, StringComparison.OrdinalIgnoreCase))
                    {
                        TryDelete(tmpPath);
                        item.State = BankDownloadState.Failed;
                        item.Error = $"md5 mismatch expect={item.Md5} actual={actual}";
                        item.Progress = 0f;
                        if (attempt >= max)
                        {
                            Log.Error($"[BankDownloader] {item.Error} bank={item.BankName}");
                            break;
                        }

                        Log.Warning($"[BankDownloader] md5 mismatch {item.BankName}, retry in {delay:0.#}s");
                        await UniTask.Delay(TimeSpan.FromSeconds(delay), DelayType.UnscaledDeltaTime);
                        delay = Mathf.Min(delay * 2f, Retry.RetryMaxSeconds);
                        continue;
                    }
                }

                try
                {
                    string dir = Path.GetDirectoryName(item.LocalPath);
                    if (!string.IsNullOrEmpty(dir))
                        Directory.CreateDirectory(dir);
                    if (File.Exists(item.LocalPath))
                        File.Delete(item.LocalPath);
                    File.Move(tmpPath, item.LocalPath);
                }
                catch (Exception e)
                {
                    TryDelete(tmpPath);
                    item.State = BankDownloadState.Failed;
                    item.Error = e.Message;
                    Log.Error($"[BankDownloader] move fail {item.BankName}: {e.Message}");
                    break;
                }

                RememberLocalMd5(item);
                item.State = BankDownloadState.Ready;
                item.Progress = 1f;
                item.Error = null;
                Log.Info($"[BankDownloader] ready {item.BankName} path={item.LocalPath} attempts={attempt}");
                return false;
            }

            return anyNetworkFail;
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
                cache.Set(item.BankName, fi.Length, fi.LastWriteTimeUtc.Ticks, item.Md5);
                cache.Save();
            }
            catch
            {
                // ignore cache write failures
            }
        }

        static async UniTask<bool> WaitForNetworkAsync()
        {
            float interval = Retry.NetworkCheckIntervalSeconds > 0
                ? Retry.NetworkCheckIntervalSeconds
                : 2f;
            while (Application.internetReachability == NetworkReachability.NotReachable)
                await UniTask.Delay(TimeSpan.FromSeconds(interval), DelayType.UnscaledDeltaTime);
            return true;
        }

        static float ResolveDownloadProgress01(
            BankDownloadItem item, UnityWebRequest req, long resumeFrom = 0)
        {
            float p = req != null ? req.downloadProgress : 0f;
            if (item != null
                && req != null
                && int.TryParse(item.SizeKb, out int kb)
                && kb > 0)
            {
                double total = kb * 1024.0;
                double got = resumeFrom + (double)req.downloadedBytes;
                float fromBytes = (float)(got / total);
                if (fromBytes > p)
                    p = fromBytes;
            }

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

            bool ok = WanosAudio.WanosBankStore.LoadFromPath(item.BankName, path);
            if (!ok)
            {
                // 探测具体原因
                string reason = WanosAudio.WanosBankStore.IsLoaded(item.BankName)
                    ? "already-loaded-but-returned-false"
                    : (!System.IO.File.Exists(path)
                        ? "file-missing"
                        : "header-invalid-or-native-fail");
                Log.Warning($"[BankDownloader] TryLoadBank FAIL bank={item.BankName} ok=False path={path} reason={reason} size={(System.IO.File.Exists(path) ? new System.IO.FileInfo(path).Length : -1)}");
            }
            else
            {
                Log.Info($"[BankDownloader] TryLoadBank OK bank={item.BankName} path={path}");
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
        /// path 旁路缓存：bankName → size + mtime + expectedMd5。命中则跳过读盘哈希。
        /// </summary>
        sealed class BankMd5Cache
        {
            const string FileName = ".bank_md5_cache";
            static BankMd5Cache _instance;
            static readonly object Gate = new object();

            readonly Dictionary<string, Entry> _map = new Dictionary<string, Entry>(128);
            bool _loaded;

            struct Entry
            {
                public long Size;
                public long MtimeTicks;
                public string Md5;
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

            public bool Matches(string bankName, long size, long mtimeTicks, string md5)
            {
                if (string.IsNullOrEmpty(bankName) || string.IsNullOrEmpty(md5))
                    return false;
                lock (Gate)
                {
                    if (!_map.TryGetValue(bankName, out var entry))
                        return false;
                    return entry.Size == size &&
                           entry.MtimeTicks == mtimeTicks &&
                           string.Equals(entry.Md5, md5, StringComparison.OrdinalIgnoreCase);
                }
            }

            public void Set(string bankName, long size, long mtimeTicks, string md5)
            {
                if (string.IsNullOrEmpty(bankName) || string.IsNullOrEmpty(md5))
                    return;
                lock (Gate)
                {
                    _map[bankName] = new Entry
                    {
                        Size = size,
                        MtimeTicks = mtimeTicks,
                        Md5 = md5,
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
                        string name = parts[0].Trim().TrimStart('\uFEFF');
                        string md5 = parts[3].Trim();
                        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(md5))
                            continue;
                        _map[name] = new Entry { Size = size, MtimeTicks = mtime, Md5 = md5 };
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
