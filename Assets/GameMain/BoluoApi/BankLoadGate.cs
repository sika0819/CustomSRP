using System;
using System.Collections.Generic;
using System.IO;
using Cysharp.Threading.Tasks;
using Game;
using UnityGameFramework.Runtime;
using WanosAudio;

namespace Game.BoluoApi
{
    /// <summary>
    /// persistent bank 进原生引擎之前的门。
    /// 旁路标记对得上（size + mtime），或这次校验通过的一次性放行，才允许 LoadBank。
    /// 文件头不合法、比 md5 缓存体积短、标记体积对不上：改名为 .bad，改用包内副本。
    /// 只是修改时间变了（体积仍对）不能隔离，否则下过的 bank 下次进岛会变成未下载。
    /// </summary>
    public static class BankLoadGate
    {
        const string MarkerSuffix = ".loadok";
        const string PackageSuffix = ".pkg";

        static readonly HashSet<string> Once =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static readonly HashSet<string> Toasted =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static readonly HashSet<string> Quarantined =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static readonly HashSet<string> Skipped =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, string> BlockReasons =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static string LastBlockReason { get; private set; }

        public static void Install()
        {
            WanosBankStore.PersistentLoadBlock = BlockReason;
            WanosBankStore.AfterSuccessfulLoad = NoteEngineLoaded;
        }

        public static void ResetSession()
        {
            Once.Clear();
            Toasted.Clear();
            Quarantined.Clear();
            Skipped.Clear();
            BlockReasons.Clear();
            LastBlockReason = null;
        }

        /// <summary>刚校验替换过的文件，允许进引擎一次。成功后才写标记。</summary>
        public static void AllowVerifiedThisSession(string absolutePath)
        {
            Install();
            if (string.IsNullOrEmpty(absolutePath))
                return;
            try
            {
                Once.Add(Path.GetFullPath(absolutePath));
            }
            catch
            {
                Once.Add(absolutePath);
            }
        }

        /// <summary>包内原件刚落盘，或原生加载成功。写 size + mtime 标记。</summary>
        public static void NoteEngineLoaded(string absolutePath)
        {
            if (!IsOfficialPersistent(absolutePath) || !File.Exists(absolutePath))
                return;
            try
            {
                var fi = new FileInfo(absolutePath);
                fi.Refresh();
                File.WriteAllText(
                    absolutePath + MarkerSuffix,
                    fi.Length + "\t" + fi.LastWriteTimeUtc.Ticks);
                string stem = AudioEnginePaths.StripBankExtension(Path.GetFileName(absolutePath));
                if (!string.IsNullOrEmpty(stem))
                {
                    Quarantined.Remove(stem);
                    Skipped.Remove(stem);
                }
            }
            catch (Exception e)
            {
                Log.Warning($"[BankLoadGate] write marker failed path={absolutePath} err={e.Message}");
            }
        }

        /// <summary>
        /// 进岛前：坏文件隔离，md5 缓存对得上的补标记，内置 bank 没有安全文件时解出包内副本。
        /// </summary>
        public static async UniTask PrepareForBootAsync()
        {
            Install();
            AudioEnginePaths.EnsureWritableDirs();
            int quarantined = 0;
            int stamped = 0;
            string root = AudioEnginePaths.PersistentBankPath;
            if (Directory.Exists(root))
            {
                string[] files;
                try
                {
                    files = Directory.GetFiles(root);
                }
                catch (Exception e)
                {
                    Log.Warning($"[BankLoadGate] list persistent failed: {e.Message}");
                    files = Array.Empty<string>();
                }

                for (int i = 0; i < files.Length; i++)
                {
                    if (!IsOfficialPersistent(files[i]))
                        continue;
                    bool hadMarker = MarkerMatches(files[i]);
                    string reason = ReconcileOfficial(files[i]);
                    if (reason == null && !hadMarker && MarkerMatches(files[i]))
                        stamped++;
                    else if (reason != null
                             && reason != "unconfirmed"
                             && !File.Exists(files[i]))
                        quarantined++;
                }
            }

            int pkg = await MaterializeBuiltinFallbacksAsync();
            MarkSkippedWithoutFallback();
            Log.Info(
                $"[BankLoadGate] prepare quarantine={quarantined} stamped={stamped} pkg={pkg} " +
                $"skipped={Skipped.Count}");
        }

        /// <summary>
        /// 给引擎用的路径。persistent 不安全时改用包内副本。都没有则不加载，并 Toast。
        /// </summary>
        public static bool TryResolveForEngine(string bankName, out string path)
        {
            Install();
            path = null;
            LastBlockReason = null;
            string stem = AudioEnginePaths.StripBankExtension(bankName);
            if (string.IsNullOrEmpty(stem))
            {
                LastBlockReason = "empty-name";
                return false;
            }

            if (AudioEnginePaths.TryFindPersistentBank(stem, out string official))
            {
                string reason = ReconcileOfficial(official);
                if (reason == null && File.Exists(official))
                {
                    path = official;
                    return true;
                }

                LastBlockReason = reason;
            }
            else
                LastBlockReason = Quarantined.Contains(stem) ? "quarantined" : "no-persistent";

            if (TryPackageFallback(stem, out path))
            {
                LastBlockReason = null;
                Log.Info($"[BankLoadGate] use package bank={stem} path={path}");
                return true;
            }

            if (string.IsNullOrEmpty(LastBlockReason) || LastBlockReason == "unconfirmed")
                LastBlockReason = "no-package-copy";
            Skipped.Add(stem);
            Toast(stem, LastBlockReason);
            return false;
        }

        /// <summary>
        /// 这份 bank 不能进引擎，但预设 / 场景门不必等它下完。
        /// </summary>
        public static bool CanEnterWithoutOfficialFile(string bankName)
        {
            string stem = AudioEnginePaths.StripBankExtension(bankName);
            if (string.IsNullOrEmpty(stem))
                return false;
            if (Skipped.Contains(stem) || Quarantined.Contains(stem))
                return true;
            return TryPackageFallback(stem, out _);
        }

        static string BlockReason(string stem, string absolutePath)
        {
            if (!IsOfficialPersistent(absolutePath))
                return null;

            string full;
            try
            {
                full = Path.GetFullPath(absolutePath);
            }
            catch
            {
                full = absolutePath;
            }

            if (Once.Remove(full))
                return null;

            if (!File.Exists(full))
                return "missing";
            if (IsShorterThanCache(full))
                return "shorter-than-cache";
            string header = WanosBankStore.ExplainInvalidBankFile(full);
            if (header != null)
                return header;
            if (MarkerMatches(full))
                return null;
            if (File.Exists(full + MarkerSuffix))
                return "marker-mismatch";
            return "unconfirmed";
        }

        /// <returns>null 表示可以加载这份 persistent 文件。否则是原因，文件可能已被改名。</returns>
        static string ReconcileOfficial(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return "missing";

            string stem = AudioEnginePaths.StripBankExtension(Path.GetFileName(path));
            string header = WanosBankStore.ExplainInvalidBankFile(path);
            if (header != null)
            {
                Quarantine(path, stem, header);
                return header;
            }

            if (IsShorterThanCache(path))
            {
                Quarantine(path, stem, "shorter-than-cache");
                return "shorter-than-cache";
            }

            if (MarkerMatches(path))
                return null;

            var fi = new FileInfo(path);
            fi.Refresh();
            bool cacheExact = BankDownloader.CacheMatchesFile(stem, fi.Length, fi.LastWriteTimeUtc.Ticks);
            bool cacheSameSize = !cacheExact
                && BankDownloader.TryGetCachedSize(stem, out long cachedSize)
                && cachedSize == fi.Length;
            // 标记或缓存只差修改时间：文件还是那一份，重写标记，不要改成 .bad。
            if (cacheExact || cacheSameSize || MarkerSizeMatches(path, fi.Length))
            {
                NoteEngineLoaded(path);
                if (cacheSameSize)
                {
                    Log.Info(
                        $"[BankLoadGate] stamp despite mtime drift bank={stem} bytes={fi.Length}");
                }
                else if (cacheExact)
                {
                    Log.Info($"[BankLoadGate] stamp from md5 cache bank={stem} bytes={fi.Length}");
                }

                return null;
            }

            if (File.Exists(path + MarkerSuffix))
            {
                Quarantine(path, stem, "marker-mismatch");
                return "marker-mismatch";
            }

            // 缓存体积对不上：留给下载侧比 md5。改名会让已下过的包在界面上变成未下载。
            return "unconfirmed";
        }

        static bool IsShorterThanCache(string path)
        {
            if (!File.Exists(path))
                return false;
            string stem = AudioEnginePaths.StripBankExtension(Path.GetFileName(path));
            if (!BankDownloader.TryGetCachedSize(stem, out long cached) || cached <= 0)
                return false;
            var fi = new FileInfo(path);
            fi.Refresh();
            return fi.Length < cached;
        }

        static bool MarkerMatches(string path)
        {
            string mark = path + MarkerSuffix;
            if (!File.Exists(mark) || !File.Exists(path))
                return false;
            try
            {
                var fi = new FileInfo(path);
                fi.Refresh();
                string text = File.ReadAllText(mark).Trim();
                int tab = text.IndexOf('\t');
                if (tab <= 0 || tab >= text.Length - 1)
                    return false;
                if (!long.TryParse(text.Substring(0, tab), out long size) || size != fi.Length)
                    return false;
                return long.TryParse(text.Substring(tab + 1), out long ticks)
                       && ticks == fi.LastWriteTimeUtc.Ticks;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>标记里的体积等于当前文件。修改时间可以不同。</summary>
        static bool MarkerSizeMatches(string path, long fileLength)
        {
            string mark = path + MarkerSuffix;
            if (!File.Exists(mark) || fileLength <= 0)
                return false;
            try
            {
                string text = File.ReadAllText(mark).Trim();
                int tab = text.IndexOf('\t');
                if (tab <= 0)
                    return false;
                return long.TryParse(text.Substring(0, tab), out long size) && size == fileLength;
            }
            catch
            {
                return false;
            }
        }

        static void Quarantine(string path, string stem, string reason)
        {
            try
            {
                string bad = path + ".bad";
                if (File.Exists(bad))
                    File.Delete(bad);
                File.Move(path, bad);
                string mark = path + MarkerSuffix;
                if (File.Exists(mark))
                    File.Delete(mark);
                if (!string.IsNullOrEmpty(stem))
                {
                    Quarantined.Add(stem);
                    BlockReasons[stem] = reason;
                }
                Log.Warning(
                    $"[BankLoadGate] quarantine bank={stem} reason={reason} from={path} to={bad}");
            }
            catch (Exception e)
            {
                Log.Warning(
                    $"[BankLoadGate] quarantine failed bank={stem} reason={reason} err={e.Message}");
            }
        }

        static async UniTask<int> MaterializeBuiltinFallbacksAsync()
        {
            int copied = 0;
            int count = AudioEnginePaths.BuiltinStreamingCount;
            for (int i = 0; i < count; i++)
            {
                string relative = AudioEnginePaths.GetBuiltinStreamingRelative(i);
                string stem = AudioEnginePaths.StripBankExtension(Path.GetFileName(relative));
                if (AudioEnginePaths.TryFindPersistentBank(stem, out string official)
                    && MarkerMatches(official))
                    continue;
                if (TryPackageFallback(stem, out _))
                    continue;

                string pkg = PackagePath(relative);
                if (await StreamingAssetsUtil.CopyFileToAsync(relative, pkg)
                    && WanosBankStore.ExplainInvalidBankFile(pkg) == null)
                {
                    copied++;
                    Log.Info($"[BankLoadGate] package copy bank={stem} path={pkg}");
                }
            }

            return copied;
        }

        static void MarkSkippedWithoutFallback()
        {
            if (Quarantined.Count == 0)
                return;
            var stems = new string[Quarantined.Count];
            Quarantined.CopyTo(stems);
            for (int i = 0; i < stems.Length; i++)
            {
                if (TryPackageFallback(stems[i], out _))
                    continue;
                Skipped.Add(stems[i]);
                BlockReasons.TryGetValue(stems[i], out string reason);
                Toast(stems[i], string.IsNullOrEmpty(reason) ? "quarantined" : reason);
            }
        }

        static bool TryPackageFallback(string stem, out string path)
        {
            path = null;
            if (AudioEnginePaths.TryFindStreamingBankFile(stem, out string streaming)
                && WanosBankStore.ExplainInvalidBankFile(streaming) == null)
            {
                path = streaming;
                return true;
            }

            if (!AudioEnginePaths.TryGetBuiltinStreamingRelative(stem, out string relative))
                return false;
            string pkg = PackagePath(relative);
            if (!File.Exists(pkg) || WanosBankStore.ExplainInvalidBankFile(pkg) != null)
                return false;
            path = pkg;
            return true;
        }

        static string PackagePath(string streamingRelative)
        {
            string fileName = Path.GetFileName(streamingRelative);
            return Path.Combine(AudioEnginePaths.PersistentBankPath, fileName + PackageSuffix);
        }

        static bool IsOfficialPersistent(string path)
        {
            if (string.IsNullOrEmpty(path))
                return false;
            string full;
            string root;
            try
            {
                full = Path.GetFullPath(path);
                root = Path.GetFullPath(AudioEnginePaths.PersistentBankPath);
            }
            catch
            {
                return false;
            }

            if (!root.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
                root += Path.DirectorySeparatorChar;
            if (!full.StartsWith(root, StringComparison.Ordinal))
                return false;

            string name = Path.GetFileName(full);
            if (name.EndsWith(PackageSuffix, StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".bad", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(MarkerSuffix, StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                return false;
            return name.EndsWith(AudioEnginePaths.BankExtension, StringComparison.OrdinalIgnoreCase)
                   || name.EndsWith(AudioEnginePaths.LegacyBankExtension, StringComparison.OrdinalIgnoreCase);
        }

        static void Toast(string stem, string reason)
        {
            if (string.IsNullOrEmpty(stem) || IsNotDownloadedYet(reason))
                return;
            if (!Toasted.Add(stem))
                return;
            string text = stem + " 无法播放（" + ReasonText(reason) + "）";
            Log.Warning("[BankLoadGate] " + text);
            if (!BoluoDeviceInfo.IsTestChannel())
                return;
            try
            {
                UiDialogService.ShowRequestFailure(text);
            }
            catch (Exception e)
            {
                Log.Warning($"[BankLoadGate] toast failed bank={stem} err={e.Message}");
            }
        }

        /// <summary>首次还没有落盘，或文件还没写过加载标记。不是线上坏包。</summary>
        static bool IsNotDownloadedYet(string reason)
        {
            return string.IsNullOrEmpty(reason)
                || reason == "unconfirmed"
                || reason == "no-persistent"
                || reason == "no-package-copy"
                || reason == "missing";
        }

        static string ReasonText(string reason)
        {
            if (reason == "shorter-than-cache")
                return "文件比已记录体积短";
            if (reason == "marker-mismatch")
                return "加载标记与文件不一致";
            if (reason == "md5-cache-mismatch")
                return "与已记录的 md5 对不上";
            if (reason == "quarantined")
                return "本地文件已隔离";
            if (string.IsNullOrEmpty(reason)
                || reason == "unconfirmed"
                || reason == "no-persistent"
                || reason == "no-package-copy")
                return "本地文件未确认完整，且没有包内副本";
            return "文件头不合法 " + reason;
        }
    }
}
