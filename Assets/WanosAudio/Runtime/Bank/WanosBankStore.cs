using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace WanosAudio
{
    /// <summary>In-process bank registry. Cleared on engine Unload.</summary>
    public static class WanosBankStore
    {
        const string LogTag = nameof(WanosBankStore);

        /// <summary>Native loadBankHeader aborts on tiny/corrupt files; reject below this.</summary>
        const long MinBankBytes = 256;

        /// <summary>
        /// WIAEngine expects a Wanos archive with a "BankHeader" block.
        /// Legacy BKHD fourCC files abort in native loadBankHeader (uncaught → SIGABRT).
        /// </summary>
        static readonly byte[] BankHeaderMarker = Encoding.ASCII.GetBytes("BankHeader");
        static readonly byte[] BkhdFourCc = { (byte)'B', (byte)'K', (byte)'H', (byte)'D' };

        static readonly object Gate = new object();
        static readonly HashSet<string> Loaded =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        /// <summary>
        /// 已经调用过原生 UnloadBank、但引擎实际还占着的 stem。
        /// 留在 <see cref="Loaded"/> 里，避免 <c>LoadFromPath</c> 再灌一份。
        /// </summary>
        static readonly HashSet<string> NativeHeld =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static readonly HashSet<string> InFlight =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static readonly HashSet<string> Failed =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, long> FailedStamp =
            new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, int> Refs =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        /// <summary>最近一次 <see cref="LoadFromPath"/> 失败原因。成功时清空。</summary>
        public static string LastLoadDetail { get; private set; }

        /// <summary>
        /// 非空表示拒绝这次原生 LoadBank。参数是 bank stem 与调用方传入的绝对路径。
        /// 由 GameMain 安装：persistent 文件须有加载标记，或是刚校验通过的一次性放行。
        /// </summary>
        public static Func<string, string, string> PersistentLoadBlock;

        /// <summary>原生 LoadBank 成功后。参数是调用方传入的原路径，不是循环补丁副本。</summary>
        public static Action<string> AfterSuccessfulLoad;

        /// <summary>
        /// Bank 加载成功后触发。可被 <see cref="AmbienceZoneService"/> 等订阅，
        /// 在 bank ready 时重试之前因 bank 未就绪而失败的 Evaluate。
        /// </summary>
        public static event Action<string> OnBankLoaded;

        /// <summary>
        /// 默认延迟卸载秒数。等待 DSP 线程完成当前音频帧处理后再卸载，
        /// 避免 SIGSEGV（竞态条件导致 DSP 线程访问已释放的 DecodedClipData）。
        /// 区域声可按种类传入更短/更长延迟（见 <see cref="Release(string, float)"/>）。
        /// </summary>
        public const float DefaultUnloadDelaySeconds = 2.0f;

        /// <summary>
        /// 待延迟卸载的 Bank 名 → 预计卸载时间。
        /// </summary>
        static readonly Dictionary<string, float> PendingUnloads = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 每帧检查延迟卸载队列。
        /// </summary>
        public static void Update()
        {
            if (PendingUnloads.Count == 0)
                return;

            float now = Time.realtimeSinceStartup;
            string[] toUnload = null;
            int count = 0;

            lock (Gate)
            {
                foreach (var kvp in PendingUnloads)
                {
                    if (now >= kvp.Value)
                    {
                        if (toUnload == null)
                            toUnload = new string[PendingUnloads.Count];
                        toUnload[count++] = kvp.Key;
                    }
                }

                if (count == 0)
                    return;

                foreach (string name in toUnload)
                {
                    if (name != null)
                        PendingUnloads.Remove(name);
                }
            }

            for (int i = 0; i < count; i++)
            {
                string name = toUnload[i];
                if (name != null)
                {
                    Debug.Log($"[{LogTag}] delayed Unload({name})");
                    ImmediateUnload(name);
                }
            }
        }

        static IWanosBankPaths _paths;

        public static void SetPaths(IWanosBankPaths paths) => _paths = paths;

        public static void ResetSession()
        {
            lock (Gate)
            {
                Loaded.Clear();
                NativeHeld.Clear();
                InFlight.Clear();
                Failed.Clear();
                FailedStamp.Clear();
                Refs.Clear();
                PendingUnloads.Clear();
            }
        }

        public static void ClearAfterEngineUnload()
        {
            lock (Gate)
            {
                Loaded.Clear();
                NativeHeld.Clear();
                InFlight.Clear();
                Refs.Clear();
                PendingUnloads.Clear();
                // Keep Failed so we do not re-hit native abort on the same bad file this session.
            }
        }

        public static bool IsLoaded(string bankName)
        {
            if (string.IsNullOrEmpty(bankName))
                return false;
            lock (Gate)
                return Loaded.Contains(Strip(bankName));
        }

        /// <summary>
        /// 收集可以卸的 bank：已加载、引用计数为 0、不在延迟卸载、也不在加载中。
        /// 正在宽限里的不收集，避免过桥那 2 秒被提前掐掉。
        /// </summary>
        public static void CollectUnreferenced(List<string> dst)
        {
            if (dst == null)
                return;
            dst.Clear();
            lock (Gate)
            {
                foreach (string stem in Loaded)
                {
                    if (string.IsNullOrEmpty(stem))
                        continue;
                    if (InFlight.Contains(stem))
                        continue;
                    if (PendingUnloads.ContainsKey(stem))
                        continue;
                    if (NativeHeld.Contains(stem))
                        continue;
                    if (Refs.TryGetValue(stem, out int count) && count > 0)
                        continue;
                    dst.Add(stem);
                }
            }
        }

        /// <summary>路径表里有这个 bank，或已经灌进引擎。没有文件时不要拿它去 Load。</summary>
        public static bool CanResolve(string bankName)
        {
            string stem = Strip(bankName);
            if (string.IsNullOrEmpty(stem))
                return false;
            if (IsLoaded(stem))
                return true;
            IWanosBankPaths paths = _paths;
            return paths != null && paths.TryResolve(stem, out _);
        }

        /// <summary>
        /// Cheap pre-check before calling native LoadBank.
        /// WIAEngine throws C++ on bad/unsupported headers (uncaught → SIGABRT).
        /// </summary>
        /// <summary>合法返回 null。否则是 BKHD / text-error-page / no-BankHeader 等短原因。</summary>
        public static string ExplainInvalidBankFile(string absolutePath)
        {
            if (string.IsNullOrEmpty(absolutePath) || !File.Exists(absolutePath))
                return "missing";

            try
            {
                var info = new FileInfo(absolutePath);
                if (info.Length < MinBankBytes)
                    return "too-small";

                byte[] head = new byte[Math.Min(256, (int)Math.Min(info.Length, 256))];
                using (var fs = File.OpenRead(absolutePath))
                {
                    int n = fs.Read(head, 0, head.Length);
                    if (n < 4)
                        return "short-header";

                    if (StartsWith(head, n, BkhdFourCc))
                        return "BKHD";

                    if (head[0] == '<' || head[0] == '{' || head[0] == '[' || head[0] == '#' || head[0] == '/')
                        return "text-error-page";
                    if (n >= 2 && head[0] == 0xEF && head[1] == 0xBB)
                        return "text-error-page";

                    if (IndexOf(head, n, BankHeaderMarker) >= 0)
                        return null;

                    return "no-BankHeader";
                }
            }
            catch (Exception e)
            {
                return "read-failed:" + e.Message;
            }
        }

        public static bool IsLikelyValidBankFile(string absolutePath)
        {
            string reason = ExplainInvalidBankFile(absolutePath);
            if (reason == null)
                return true;

            if (reason == "BKHD")
            {
                Debug.LogWarning(
                    $"[{LogTag}] reject BKHD bank (need Wanos BankHeader archive): {absolutePath}");
            }
            else if (reason == "no-BankHeader")
            {
                Debug.LogWarning(
                    $"[{LogTag}] reject unknown bank header (no BankHeader marker): {absolutePath}");
            }
            else if (reason.StartsWith("read-failed", StringComparison.Ordinal))
            {
                Debug.LogWarning($"[{LogTag}] IsLikelyValidBankFile({absolutePath}): {reason}");
            }

            return false;
        }

        /// <summary>Load by absolute path (from downloader). Tag = bank stem.</summary>
        public static bool LoadFromPath(string bankName, string absolutePath)
        {
            LastLoadDetail = null;
            if (!WanosNative.Instance.IsCreated)
            {
                LastLoadDetail = "engine-not-created";
                Debug.LogWarning($"[{LogTag}] LoadFromPath skipped — engine not created ({bankName})");
                return false;
            }

            string stem = Strip(bankName);
            if (string.IsNullOrEmpty(stem))
            {
                LastLoadDetail = "empty-name";
                return false;
            }

            lock (Gate)
            {
                if (Loaded.Contains(stem))
                    return true;
                if (Failed.Contains(stem) || InFlight.Contains(stem))
                {
                    LastLoadDetail = Failed.Contains(stem) ? "already-failed" : "in-flight";
                    return false;
                }
                InFlight.Add(stem);
            }

            try
            {
                // Rain_Small 在原 bank 里只播一遍。加载副本，不改 StreamingAssets 原文件。
                string loadPath = WanosBankSeamlessLoopPatch.ResolveEngineLoadPath(absolutePath);
                string header = ExplainInvalidBankFile(loadPath);
                if (header != null)
                {
                    LastLoadDetail = "header " + header;
                    Debug.LogWarning(
                        $"[{LogTag}] skip native LoadBank — invalid/unsupported bank={stem} path={loadPath} reason={header}");
                    RememberFailed(stem, loadPath);
                    return false;
                }

                string blocked = PersistentLoadBlock?.Invoke(stem, absolutePath);
                if (!string.IsNullOrEmpty(blocked))
                {
                    LastLoadDetail = "gate " + blocked;
                    Debug.LogWarning(
                        $"[{LogTag}] skip native LoadBank — unconfirmed bank={stem} path={absolutePath} reason={blocked}");
                    return false;
                }

                WanosResult r;
                try
                {
                    r = WanosNative.Instance.LoadBank(loadPath);
                }
                catch (Exception e)
                {
                    LastLoadDetail = "exception " + e.Message;
                    Debug.LogWarning($"[{LogTag}] LoadBank threw bank={stem}: {e.Message}");
                    RememberFailed(stem, loadPath);
                    return false;
                }

                if (r != WanosResult.Success)
                {
                    LastLoadDetail = "result=" + r;
                    Debug.LogWarning($"[{LogTag}] LoadBank failed bank={stem} result={r}");
                    RememberFailed(stem, loadPath);
                    return false;
                }

                lock (Gate)
                {
                    Loaded.Add(stem);
                    Failed.Remove(stem);
                    FailedStamp.Remove(stem);
                }
                try
                {
                    AfterSuccessfulLoad?.Invoke(absolutePath);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[{LogTag}] AfterSuccessfulLoad bank={stem}: {e.Message}");
                }
                OnBankLoaded?.Invoke(stem);
                Debug.Log($"[{LogTag}] loaded bank={stem}");
                return true;
            }
            finally
            {
                lock (Gate)
                    InFlight.Remove(stem);
            }
        }

        /// <summary>Resolve via <see cref="IWanosBankPaths"/> then load.</summary>
        public static bool Load(string bankName)
        {
            string stem = Strip(bankName);
            if (string.IsNullOrEmpty(stem))
                return false;

            if (IsLoaded(stem))
                return true;

            if (_paths == null || !_paths.TryResolve(stem, out string path))
            {
                Debug.LogWarning($"[{LogTag}] resolve failed bank={stem}");
                return false;
            }

            // 同一份文件已经让原生加载失败过就不要再送进去。真空圣境这类坏包会在 LoadBank 里把进程打崩。
            // 文件大小或修改时间变了（重新下过）才允许再试。
            lock (Gate)
            {
                if (Failed.Contains(stem)
                    && FailedStamp.TryGetValue(stem, out long stamp)
                    && stamp == FileStamp(path))
                    return false;
            }

            return LoadFromPath(stem, path);
        }

        public static bool HasPendingUnloads
        {
            get
            {
                lock (Gate)
                    return PendingUnloads.Count > 0;
            }
        }

        /// <summary>
        /// 立刻卸掉延迟队列。离岛后环境音服务已经停，没人再调 <see cref="Update"/>。
        /// </summary>
        public static void FlushPendingUnloads()
        {
            string[] names;
            lock (Gate)
            {
                if (PendingUnloads.Count == 0)
                    return;
                names = new string[PendingUnloads.Count];
                PendingUnloads.Keys.CopyTo(names, 0);
                PendingUnloads.Clear();
            }

            for (int i = 0; i < names.Length; i++)
            {
                if (!string.IsNullOrEmpty(names[i]))
                    ImmediateUnload(names[i]);
            }
        }

        public static bool Unload(string bankName)
        {
            string stem = Strip(bankName);
            if (string.IsNullOrEmpty(stem))
                return false;

            try
            {
                WanosNative.Instance.UnloadBankByTag(stem);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[{LogTag}] Unload({stem}): {e.Message}");
            }

            lock (Gate)
            {
                Refs.Remove(stem);
                PendingUnloads.Remove(stem);
                if (Loaded.Contains(stem))
                    NativeHeld.Add(stem);
            }
            return true;
        }

        /// <summary>
        /// Ref-counted load for shared ambience/weather banks.
        /// First retain loads; subsequent retains only bump the count.
        /// Re-retain always cancels a pending delayed UnLoad (Boluo bridge/shore flicker:
        /// Exit→Release schedules UnLoad, Enter within grace must not UnLoad↔Load).
        /// </summary>
        public static bool Retain(string bankName)
        {
            string stem = Strip(bankName);
            if (string.IsNullOrEmpty(stem))
                return false;

            lock (Gate)
            {
                // Cancel deferred UnLoad before any Load path — bank may still be Loaded.
                PendingUnloads.Remove(stem);

                if (Refs.TryGetValue(stem, out int n) && n > 0)
                {
                    Refs[stem] = n + 1;
                    NativeHeld.Remove(stem);
                    return true;
                }

                // 原生没放开时 stem 仍在 Loaded。只把引用加回来，不要再 Load。
                if (Loaded.Contains(stem))
                {
                    Refs[stem] = 1;
                    NativeHeld.Remove(stem);
                    return true;
                }
            }

            if (!Load(stem))
                return false;

            lock (Gate)
            {
                PendingUnloads.Remove(stem);
                Refs[stem] = 1;
            }
            return true;
        }

        /// <summary>
        /// Drop one retain; unload after <see cref="DefaultUnloadDelaySeconds"/> seconds delay.
        /// Delay lets DSP thread finish current audio frame before native UnloadBank.
        /// </summary>
        public static void Release(string bankName) => Release(bankName, DefaultUnloadDelaySeconds);

        /// <summary>
        /// Drop one retain; unload after <paramref name="unloadDelaySeconds"/> (clamped ≥ 0).
        /// Zero schedules unload on the next <see cref="Update"/> tick.
        /// </summary>
        public static void Release(string bankName, float unloadDelaySeconds)
        {
            string stem = Strip(bankName);
            if (string.IsNullOrEmpty(stem))
                return;

            bool unload = false;
            lock (Gate)
            {
                if (!Refs.TryGetValue(stem, out int n))
                    return;
                n--;
                if (n > 0)
                {
                    Refs[stem] = n;
                    return;
                }

                Refs.Remove(stem);
                unload = true;

                // Cancel any pending unload for this bank (fresh retain before the old one expired)
                PendingUnloads.Remove(stem);
            }

            if (unload)
                ScheduleUnload(stem, unloadDelaySeconds);
        }

        /// <summary>
        /// Schedule native UnloadBank after a delay (DSP thread grace period).
        /// </summary>
        static void ScheduleUnload(string bankName, float unloadDelaySeconds)
        {
            string stem = Strip(bankName);
            if (string.IsNullOrEmpty(stem))
                return;

            float delay = unloadDelaySeconds < 0f ? 0f : unloadDelaySeconds;
            float when = Time.realtimeSinceStartup + delay;
            lock (Gate)
            {
                PendingUnloads[stem] = when;
            }
            Debug.Log($"[{LogTag}] Release({stem}) scheduled for {delay:F2}s later");
        }

        /// <summary>
        /// Immediate native UnloadBank without any delay. Used by Update() after grace period
        /// or by ClearAfterEngineUnload().
        /// Aborts if a Retain raced in after the pending entry was dequeued (refs &gt; 0).
        /// </summary>
        static void ImmediateUnload(string bankName)
        {
            string stem = Strip(bankName);
            if (string.IsNullOrEmpty(stem))
                return;

            lock (Gate)
            {
                if (Refs.TryGetValue(stem, out int n) && n > 0)
                {
                    Debug.Log($"[{LogTag}] ImmediateUnload({stem}) aborted — refs={n}");
                    return;
                }

                if (Loaded.Contains(stem))
                    NativeHeld.Add(stem);
            }

            try
            {
                WanosNative.Instance.UnloadBankByTag(stem);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[{LogTag}] ImmediateUnload({stem}): {e.Message}");
            }
        }

        /// <summary>
        /// Estimate one-shot PCM duration from bank <c>sample_count</c> (Editor/macOS Ended fallback).
        /// Assumes <paramref name="sampleRateHz"/> (engine default 48000).
        /// </summary>
        public static bool TryEstimateDurationMs(string bankName, out int durationMs, int sampleRateHz = 48000)
        {
            durationMs = 0;
            string stem = Strip(bankName);
            if (string.IsNullOrEmpty(stem) || sampleRateHz <= 0)
                return false;
            if (_paths == null || !_paths.TryResolve(stem, out string path) || !File.Exists(path))
                return false;

            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                byte[] data = File.ReadAllBytes(path);
                long bytes = data.LongLength;
                // Field layout: type(1) + nameLen(u16 LE) + name + value(i64 LE)
                byte[] key = Encoding.ASCII.GetBytes("sample_count");
                for (int i = 0; i + 1 + 2 + key.Length + 8 <= data.Length; i++)
                {
                    if (data[i] != 0x08)
                        continue;
                    int nameLen = data[i + 1] | (data[i + 2] << 8);
                    if (nameLen != key.Length)
                        continue;
                    bool match = true;
                    for (int j = 0; j < key.Length; j++)
                    {
                        if (data[i + 3 + j] != key[j])
                        {
                            match = false;
                            break;
                        }
                    }
                    if (!match)
                        continue;

                    long samples = BitConverter.ToInt64(data, i + 3 + key.Length);
                    if (samples <= 0 || samples > int.MaxValue)
                        continue;
                    durationMs = (int)(samples * 1000L / sampleRateHz);
                    if (durationMs < 1000)
                        continue;
                    LogEstimateHitch(stem, bytes, sw);
                    return true;
                }

                LogEstimateHitch(stem, bytes, sw);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[{LogTag}] TryEstimateDurationMs({stem}): {e.Message}");
            }

            return false;
        }

        static void LogEstimateHitch(string stem, long bytes, System.Diagnostics.Stopwatch sw)
        {
            long ms = sw.ElapsedMilliseconds;
            if (ms < 16)
                return;
            Debug.Log("[Hitch] TryEstimateDurationMs stem=" + stem + " bytes=" + bytes + " ms=" + ms);
        }

        static string Strip(string name)
        {
            if (string.IsNullOrEmpty(name))
                return name;
            if (name.EndsWith(".wsbk", StringComparison.OrdinalIgnoreCase))
                return name.Substring(0, name.Length - 5);
            if (name.EndsWith(".wb", StringComparison.OrdinalIgnoreCase))
                return name.Substring(0, name.Length - 3);
            return name;
        }

        static bool StartsWith(byte[] buf, int len, byte[] prefix)
        {
            if (len < prefix.Length)
                return false;
            for (int i = 0; i < prefix.Length; i++)
            {
                if (buf[i] != prefix[i])
                    return false;
            }
            return true;
        }

        static int IndexOf(byte[] haystack, int hayLen, byte[] needle)
        {
            if (needle.Length == 0 || hayLen < needle.Length)
                return -1;
            int last = hayLen - needle.Length;
            for (int i = 0; i <= last; i++)
            {
                bool ok = true;
                for (int j = 0; j < needle.Length; j++)
                {
                    if (haystack[i + j] != needle[j])
                    {
                        ok = false;
                        break;
                    }
                }
                if (ok)
                    return i;
            }
            return -1;
        }

        static void RememberFailed(string stem, string path)
        {
            if (string.IsNullOrEmpty(stem))
                return;
            long stamp = FileStamp(path);
            lock (Gate)
            {
                Failed.Add(stem);
                FailedStamp[stem] = stamp;
            }
        }

        /// <summary>size 与 mtime 合成戳；文件没了则用 -1，避免永远锁死。</summary>
        static long FileStamp(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return -1;
            try
            {
                var fi = new FileInfo(path);
                return (fi.Length << 1) ^ fi.LastWriteTimeUtc.Ticks;
            }
            catch
            {
                return -1;
            }
        }
    }
}
