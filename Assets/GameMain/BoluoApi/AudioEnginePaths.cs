using System;
using System.Collections.Generic;
using System.IO;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityGameFramework.Runtime;

namespace Game.BoluoApi
{
    /// <summary>Wanos bank 落盘 / 解析路径（GameMain → WanosBankStore）。</summary>
    public static class AudioEnginePaths
    {
        public const string BankFolderName = "BoluoAudioBanks";
        public const string BankExtension = ".wsbk";
        public const string LegacyBankExtension = ".wb";

        /// <summary>
        /// APK 内置 bank（StreamingAssets/WanosBank）。Android 上须先解压到 persistent，
        /// 原生 LoadBank 只能吃真实文件路径。
        /// </summary>
        static readonly string[] BuiltinStreamingRelativePaths =
        {
            "WanosBank/Common_Init.wb",
            "WanosBank/Common_Music.wb",
            "WanosBank/Music1_1.wsbk",
            "WanosBank/Ocean_Calm_Quad.wsbk",
        };

        /// <summary>
        /// companyName/productName 改过之后，旧 Editor persistent 里的完整 bank 仍可用。
        /// 相对「Application Support」根（persistentDataPath 上两级）。
        /// </summary>
        static readonly string[] LegacyPersistentBankRelativeRoots =
        {
            "DefaultCompany/IslandTerrain/BoluoAudioBanks",
            "DefaultCompany/CustomSRP Game/BoluoAudioBanks",
            "Boluo/Boluo/BoluoAudioBanks",
        };

        const string ExtractMarkerName = ".streaming_banks_extracted";
        const string LegacyMigrateMarkerName = ".legacy_banks_migrated_v1";

        static int _extractState; // 0 idle, 1 running, 2 done
        static UniTaskCompletionSource _extractTcs;
        static int _migrateState; // 0 idle, 1 running, 2 done
        static UniTaskCompletionSource _migrateTcs;

        public static string PersistentBankPath =>
            Path.Combine(Application.persistentDataPath, BankFolderName);

        public static string StreamingBankPath =>
            Path.Combine(Application.streamingAssetsPath, BankFolderName);

        /// <summary>
        /// 备选目录：<c>StreamingAssets/WanosBank/</c>。保留旧工程目录布局的兼容性。
        /// 银行文件 (.wb / .wsbk) 可能由 Wanos 工具链直接放这里（不经过 BoluoAudioBanks）。
        /// </summary>
        public static string LegacyStreamingBankPath =>
            Path.Combine(Application.streamingAssetsPath, "WanosBank");

        public static void EnsureWritableDirs()
        {
            Directory.CreateDirectory(PersistentBankPath);
        }

        /// <summary>Editor Disable Domain Reload / 会话重置。</summary>
        public static void ResetSession()
        {
            _extractState = 0;
            _extractTcs = null;
            _migrateState = 0;
            _migrateTcs = null;
        }

        public static string StripBankExtension(string name)
        {
            if (string.IsNullOrEmpty(name))
                return name;
            if (name.EndsWith(BankExtension, System.StringComparison.OrdinalIgnoreCase))
                return name.Substring(0, name.Length - BankExtension.Length);
            if (name.EndsWith(LegacyBankExtension, System.StringComparison.OrdinalIgnoreCase))
                return name.Substring(0, name.Length - LegacyBankExtension.Length);
            return name;
        }

        public static string ResolveBankPath(string bankName)
        {
            if (TryFindLocalBank(bankName, out string existing))
                return existing;
            return Path.Combine(PersistentBankPath, StripBankExtension(bankName ?? "unknown") + BankExtension);
        }

        public static bool TryFindLocalBank(string bankName, out string path)
        {
            if (TryFindExistingBank(PersistentBankPath, bankName, out path))
                return true;

#if UNITY_ANDROID && !UNITY_EDITOR
            // jar 内路径 File.Exists 恒 false；须先 EnsureStreamingBanksExtractedAsync。
            path = null;
            return false;
#else
            if (TryFindExistingBank(StreamingBankPath, bankName, out path))
                return true;
            // 兼容旧工程：Wanos 工具链会把 Common_Init / Common_Music 等基础银行放在
            // StreamingAssets/WanosBank/ 下，这里兜底扫一下。
            return TryFindExistingBank(LegacyStreamingBankPath, bankName, out path);
#endif
        }

        /// <summary>
        /// 把 StreamingAssets 内置 bank 解到 persistent（按 buildGUID 跳过重复）。
        /// 进引擎 / Ambience Retain 前必须 await。主线程调用。
        /// </summary>
        public static UniTask EnsureStreamingBanksExtractedAsync()
        {
            if (_extractState >= 2)
                return UniTask.CompletedTask;
            if (_extractState == 1 && _extractTcs != null)
                return _extractTcs.Task;

            _extractState = 1;
            _extractTcs = new UniTaskCompletionSource();
            ExtractStreamingBanksAsync().Forget();
            return _extractTcs.Task;
        }

        /// <summary>
        /// company/product 改名后，把旧 persistent 里已下完的 .wsbk 拷到当前目录，避免重下。
        /// </summary>
        public static UniTask EnsureLegacyBanksMigratedAsync()
        {
            if (_migrateState >= 2)
                return UniTask.CompletedTask;
            if (_migrateState == 1 && _migrateTcs != null)
                return _migrateTcs.Task;

            _migrateState = 1;
            _migrateTcs = new UniTaskCompletionSource();
            MigrateLegacyBanksAsync().Forget();
            return _migrateTcs.Task;
        }

        static async UniTaskVoid ExtractStreamingBanksAsync()
        {
            try
            {
                EnsureWritableDirs();
                string markerPath = Path.Combine(PersistentBankPath, ExtractMarkerName);
                string buildId = Application.buildGUID ?? Application.version ?? "dev";

                if (File.Exists(markerPath))
                {
                    try
                    {
                        if (File.ReadAllText(markerPath) == buildId
                            && AllBuiltinBanksPresentOnDisk())
                        {
                            _extractState = 2;
                            _extractTcs?.TrySetResult();
                            return;
                        }
                    }
                    catch
                    {
                        // fall through and re-extract
                    }
                }

                int ok = 0;
                for (int i = 0; i < BuiltinStreamingRelativePaths.Length; i++)
                {
                    string relative = BuiltinStreamingRelativePaths[i];
                    string fileName = Path.GetFileName(relative);
                    string dest = Path.Combine(PersistentBankPath, fileName);
                    if (File.Exists(dest) && new FileInfo(dest).Length > 0)
                    {
                        ok++;
                        continue;
                    }

                    if (await StreamingAssetsUtil.CopyFileToAsync(relative, dest))
                    {
                        ok++;
                        Log.Info($"[AudioEnginePaths] extracted {relative} -> {dest}");
                    }
                    else
                    {
                        Log.Warning($"[AudioEnginePaths] extract miss {relative}");
                    }
                }

                try
                {
                    File.WriteAllText(markerPath, buildId);
                }
                catch (System.Exception e)
                {
                    Log.Warning($"[AudioEnginePaths] write extract marker: {e.Message}");
                }

                Log.Info($"[AudioEnginePaths] streaming bank extract done ok={ok}/{BuiltinStreamingRelativePaths.Length}");
            }
            catch (System.Exception e)
            {
                Log.Warning($"[AudioEnginePaths] extract failed: {e.Message}");
            }
            finally
            {
                _extractState = 2;
                _extractTcs?.TrySetResult();
            }
        }

        static bool AllBuiltinBanksPresentOnDisk()
        {
            for (int i = 0; i < BuiltinStreamingRelativePaths.Length; i++)
            {
                string fileName = Path.GetFileName(BuiltinStreamingRelativePaths[i]);
                string dest = Path.Combine(PersistentBankPath, fileName);
                if (!File.Exists(dest) || new FileInfo(dest).Length == 0)
                    return false;
            }

            return true;
        }

        static async UniTaskVoid MigrateLegacyBanksAsync()
        {
            try
            {
                EnsureWritableDirs();
                string destRoot = PersistentBankPath;
                string markerPath = Path.Combine(destRoot, LegacyMigrateMarkerName);

                // 每次启动都扫一遍缺文件；有 marker 时仍补缺（只跳过「目录都空」的早退）。
                var sources = CollectLegacyBankRoots(destRoot);
                if (sources.Count == 0)
                {
                    TryWriteMigrateMarker(markerPath);
                    return;
                }

                int copied = 0;
                int skipped = 0;
                for (int s = 0; s < sources.Count; s++)
                {
                    string srcRoot = sources[s];
                    string[] files;
                    try
                    {
                        files = Directory.GetFiles(srcRoot, "*" + BankExtension);
                    }
                    catch
                    {
                        continue;
                    }

                    for (int i = 0; i < files.Length; i++)
                    {
                        string src = files[i];
                        string name = Path.GetFileName(src);
                        if (string.IsNullOrEmpty(name))
                            continue;

                        string dest = Path.Combine(destRoot, name);
                        try
                        {
                            var srcFi = new FileInfo(src);
                            if (!srcFi.Exists || srcFi.Length <= 0)
                                continue;

                            // 目标已有完整文件：保留；顺手清掉同名残缺 .tmp。
                            if (File.Exists(dest))
                            {
                                var destFi = new FileInfo(dest);
                                if (destFi.Exists && destFi.Length > 0)
                                {
                                    TryDeleteQuiet(dest + ".tmp");
                                    skipped++;
                                    continue;
                                }
                            }

                            // 禁止 UniTask.RunOnThreadPool（热更 IL2CPP NRE）；用 ThreadPool + 回主线程。
                            string srcCopy = src;
                            string destCopy = dest;
                            long srcLen = srcFi.Length;
                            var copyTcs = new UniTaskCompletionSource();
                            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                            {
                                string tmp = destCopy + ".migrate";
                                try
                                {
                                    if (File.Exists(tmp))
                                        File.Delete(tmp);
                                    File.Copy(srcCopy, tmp, overwrite: true);
                                    if (File.Exists(destCopy))
                                        File.Delete(destCopy);
                                    File.Move(tmp, destCopy);
                                    copyTcs.TrySetResult();
                                }
                                catch (Exception e)
                                {
                                    try
                                    {
                                        if (File.Exists(tmp))
                                            File.Delete(tmp);
                                    }
                                    catch
                                    {
                                        // ignore
                                    }

                                    copyTcs.TrySetException(e);
                                }
                            });
                            await copyTcs.Task;
                            await UniTask.SwitchToMainThread();

                            if (File.Exists(dest) && new FileInfo(dest).Length == srcLen)
                            {
                                TryDeleteQuiet(dest + ".tmp");
                                copied++;
                                Log.Info($"[AudioEnginePaths] migrated bank {name} from {srcRoot}");
                            }
                        }
                        catch (Exception e)
                        {
                            Log.Warning($"[AudioEnginePaths] migrate miss {name}: {e.Message}");
                        }

                        // 每拷几个让一帧，避免长时间占满。
                        if ((copied + skipped) % 4 == 0)
                            await UniTask.Yield(PlayerLoopTiming.Update);
                    }
                }

                TryWriteMigrateMarker(markerPath);
                Log.Info(
                    $"[AudioEnginePaths] legacy bank migrate done copied={copied} kept={skipped} " +
                    $"sources={sources.Count} dest={destRoot}");
            }
            catch (Exception e)
            {
                Log.Warning($"[AudioEnginePaths] legacy migrate failed: {e.Message}");
            }
            finally
            {
                _migrateState = 2;
                _migrateTcs?.TrySetResult();
            }
        }

        static List<string> CollectLegacyBankRoots(string currentRoot)
        {
            var list = new List<string>();
            string appSupport = TryGetApplicationSupportRoot();
            if (string.IsNullOrEmpty(appSupport))
                return list;

            for (int i = 0; i < LegacyPersistentBankRelativeRoots.Length; i++)
            {
                string root = Path.Combine(appSupport, LegacyPersistentBankRelativeRoots[i]);
                if (!Directory.Exists(root))
                    continue;
                if (string.Equals(
                        Path.GetFullPath(root),
                        Path.GetFullPath(currentRoot),
                        StringComparison.OrdinalIgnoreCase))
                    continue;
                list.Add(root);
            }

            return list;
        }

        /// <summary>
        /// persistentDataPath = .../Application Support/{company}/{product}
        /// </summary>
        static string TryGetApplicationSupportRoot()
        {
            try
            {
                string product = Application.persistentDataPath;
                if (string.IsNullOrEmpty(product))
                    return null;
                string company = Path.GetDirectoryName(product);
                if (string.IsNullOrEmpty(company))
                    return null;
                return Path.GetDirectoryName(company);
            }
            catch
            {
                return null;
            }
        }

        static void TryWriteMigrateMarker(string markerPath)
        {
            try
            {
                File.WriteAllText(markerPath, Application.buildGUID ?? Application.version ?? "dev");
            }
            catch
            {
                // ignore
            }
        }

        static void TryDeleteQuiet(string path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
                // ignore
            }
        }

        static bool TryFindExistingBank(string dir, string bankName, out string path)
        {
            path = null;
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                return false;
            string stem = StripBankExtension(bankName);
            if (string.IsNullOrEmpty(stem))
                return false;

            string[] candidates =
            {
                Path.Combine(dir, stem + BankExtension),
                Path.Combine(dir, stem + LegacyBankExtension),
                Path.Combine(dir, stem),
            };
            for (int i = 0; i < candidates.Length; i++)
            {
                if (File.Exists(candidates[i]))
                {
                    path = candidates[i];
                    return true;
                }
            }
            return false;
        }
    }
}
