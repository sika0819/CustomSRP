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
        static readonly HashSet<string> InFlight =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static readonly HashSet<string> Failed =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, int> Refs =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

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
                InFlight.Clear();
                Failed.Clear();
                Refs.Clear();
                PendingUnloads.Clear();
            }
        }

        public static void ClearAfterEngineUnload()
        {
            lock (Gate)
            {
                Loaded.Clear();
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
        /// Cheap pre-check before calling native LoadBank.
        /// WIAEngine throws C++ on bad/unsupported headers (uncaught → SIGABRT).
        /// </summary>
        public static bool IsLikelyValidBankFile(string absolutePath)
        {
            if (string.IsNullOrEmpty(absolutePath) || !File.Exists(absolutePath))
                return false;

            try
            {
                var info = new FileInfo(absolutePath);
                if (info.Length < MinBankBytes)
                    return false;

                byte[] head = new byte[Math.Min(256, (int)Math.Min(info.Length, 256))];
                using (var fs = File.OpenRead(absolutePath))
                {
                    int n = fs.Read(head, 0, head.Length);
                    if (n < 4)
                        return false;

                    // Reject legacy BKHD — native loadBankHeader aborts on this fourCC.
                    if (StartsWith(head, n, BkhdFourCc))
                    {
                        Debug.LogWarning(
                            $"[{LogTag}] reject BKHD bank (need Wanos BankHeader archive): {absolutePath}");
                        return false;
                    }

                    // Reject obvious text / HTML / JSON error bodies.
                    if (head[0] == '<' || head[0] == '{' || head[0] == '[' || head[0] == '#' || head[0] == '/')
                        return false;
                    if (n >= 2 && head[0] == 0xEF && head[1] == 0xBB)
                        return false;

                    // Prefer Wanos "BankHeader" archive (patched .wb / .wsbk).
                    if (IndexOf(head, n, BankHeaderMarker) >= 0)
                        return true;

                    Debug.LogWarning(
                        $"[{LogTag}] reject unknown bank header (no BankHeader marker): {absolutePath}");
                    return false;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[{LogTag}] IsLikelyValidBankFile({absolutePath}): {e.Message}");
                return false;
            }
        }

        /// <summary>Load by absolute path (from downloader). Tag = bank stem.</summary>
        public static bool LoadFromPath(string bankName, string absolutePath)
        {
            if (!WanosNative.Instance.IsCreated)
            {
                Debug.LogWarning($"[{LogTag}] LoadFromPath skipped — engine not created ({bankName})");
                return false;
            }

            string stem = Strip(bankName);
            if (string.IsNullOrEmpty(stem))
                return false;

            lock (Gate)
            {
                if (Loaded.Contains(stem))
                    return true;
                if (Failed.Contains(stem) || InFlight.Contains(stem))
                    return false;
                InFlight.Add(stem);
            }

            try
            {
                if (!IsLikelyValidBankFile(absolutePath))
                {
                    Debug.LogWarning(
                        $"[{LogTag}] skip native LoadBank — invalid/unsupported bank={stem} path={absolutePath}");
                    lock (Gate)
                        Failed.Add(stem);
                    return false;
                }

                WanosResult r;
                try
                {
                    r = WanosNative.Instance.LoadBank(absolutePath);
                }
                catch (Exception e)
                {
                    // Managed exceptions only; native C++ throw still aborts — validation above is the real guard.
                    Debug.LogWarning($"[{LogTag}] LoadBank threw bank={stem}: {e.Message}");
                    lock (Gate)
                        Failed.Add(stem);
                    return false;
                }

                if (r != WanosResult.Success)
                {
                    Debug.LogWarning($"[{LogTag}] LoadBank failed bank={stem} result={r}");
                    lock (Gate)
                        Failed.Add(stem);
                    return false;
                }

                lock (Gate)
                {
                    Loaded.Add(stem);
                    Failed.Remove(stem);
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

            // 下载完成后文件可能已修好；允许对 Failed 条目再试一次（仍无效会再次记入 Failed）。
            lock (Gate)
            {
                if (Failed.Contains(stem))
                {
                    if (!IsLikelyValidBankFile(path))
                        return false;
                    Failed.Remove(stem);
                }
            }

            return LoadFromPath(stem, path);
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
                Loaded.Remove(stem);
                Refs.Remove(stem);
            }
            return true;
        }

        /// <summary>
        /// Ref-counted load for shared ambience/weather banks.
        /// First retain loads; subsequent retains only bump the count.
        /// </summary>
        public static bool Retain(string bankName)
        {
            string stem = Strip(bankName);
            if (string.IsNullOrEmpty(stem))
                return false;

            lock (Gate)
            {
                if (Refs.TryGetValue(stem, out int n) && n > 0)
                {
                    Refs[stem] = n + 1;
                    return true;
                }
            }

            if (!Load(stem))
                return false;

            lock (Gate)
                Refs[stem] = 1;
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
        /// </summary>
        static void ImmediateUnload(string bankName)
        {
            string stem = Strip(bankName);
            if (string.IsNullOrEmpty(stem))
                return;

            try
            {
                WanosNative.Instance.UnloadBankByTag(stem);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[{LogTag}] ImmediateUnload({stem}): {e.Message}");
            }

            lock (Gate)
            {
                Loaded.Remove(stem);
            }
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
    }
}
