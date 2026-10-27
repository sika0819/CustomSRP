using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using UnityEngine.Profiling;
using UnityGameFramework.Runtime;
using WanosAudio;
using YooAsset;

namespace Game
{
    /// <summary>
    /// 闲置沉浸与切预设揭幕之后，每帧大约 1 毫秒释放已经没人引用的资源。
    /// 虹膜推镜 / 黑场 / 开镜，以及流送还在生成时，直接让开。
    /// 不调用 <c>Resources.UnloadUnusedAssets</c>，也不 <c>GC.Collect</c>。
    /// </summary>
    public sealed class IslandAssetReclaimScheduler : MonoBehaviour
    {
        const long ReclaimTimeSliceMs = 2;
        const long WorkBudgetMs = 1;
        const int YooUnloadLoops = 8;

        static readonly Stopwatch Work = new Stopwatch();
        static readonly List<string> BankScratch = new List<string>(32);

        static IslandAssetReclaimScheduler _runner;
        static bool _wantRun;
        static bool _yooPending;
        static bool _sliceLowered;
        static bool _reported;
        static long _savedSlice = 30;
        static UnloadUnusedAssetsOperation _op;
        static int _prefabReleases;
        static int _prototypeDrops;
        static int _bankUnloads;
        static int _lastSpawned = int.MinValue;
        static float _startedAt;
        static float _quietSince;
        static long _reservedAtStart;

        public static void Begin()
        {
            bool fresh = !_wantRun;
            _wantRun = true;
            if (fresh)
            {
                _reported = false;
                _prefabReleases = 0;
                _prototypeDrops = 0;
                _bankUnloads = 0;
                _lastSpawned = int.MinValue;
                _startedAt = Time.realtimeSinceStartup;
                _quietSince = _startedAt;
                _reservedAtStart = Profiler.GetTotalReservedMemoryLong();
                Log.Info($"[IslandAssetReclaim] begin reserved={_reservedAtStart}");
            }

            Ensure();
        }

        /// <summary>停掉还没做的步骤。已经放开的句柄不收回。</summary>
        public static void Cancel()
        {
            _wantRun = false;
            _yooPending = false;
            if (_op == null)
            {
                RestoreSlice();
                Report("cancel");
            }
        }

        void LateUpdate()
        {
            Tick();
        }

        void OnDestroy()
        {
            if (_runner == this)
                _runner = null;
            if (_op == null)
                RestoreSlice();
        }

        static void Ensure()
        {
            if (_runner != null)
                return;
            MonoBehaviour host = GameEntry.Base;
            if (host == null)
                return;
            _runner = host.GetComponent<IslandAssetReclaimScheduler>();
            if (_runner == null)
                _runner = host.gameObject.AddComponent<IslandAssetReclaimScheduler>();
        }

        static void Tick()
        {
            if (_op != null)
            {
                if (!_op.IsDone)
                    return;
                int destroyed = _op.DestroyedLoaderCount;
                _op = null;
                RestoreSlice();
                Log.Info($"[IslandAssetReclaim] yoo destroyedLoaders={destroyed}");
                if (!_wantRun)
                    Report("cancel");
                return;
            }

            if (!_wantRun)
                return;

            if (IslandLeaveAssetReclaim.IsBusy)
            {
                Cancel();
                return;
            }

            // 推镜、黑场、开镜全程不卸。排队的下一次预设会再次把 busy 抬起来。
            if (IslandMainForm.IsPresetTransitionBusy)
                return;

            int fps = Application.targetFrameRate;
            if (fps < 1)
                fps = IslandCameraSrpSetup.DefaultFrameRate;
            if (Time.unscaledDeltaTime > 1.5f / fps)
                return;

            EnvItemStreamerLite streamer = EnvItemStreamerLite.Current;
            if (streamer != null && streamer.HasPendingSpawn)
                return;

            if (_yooPending)
            {
                StartYooUnload();
                return;
            }

            Work.Restart();
            if (streamer != null && streamer.ReleaseOneUnusedPrefab())
            {
                _prefabReleases++;
                _yooPending = true;
                return;
            }

            if (Work.ElapsedMilliseconds >= WorkBudgetMs)
                return;

            if (EnvNativeInstancing.TryDropOneEmptyPrototype())
            {
                _prototypeDrops++;
                _yooPending = true;
                return;
            }

            if (Work.ElapsedMilliseconds >= WorkBudgetMs)
                return;

            if (TryUnloadOneBank())
            {
                _bankUnloads++;
                return;
            }

            // 沉浸期间人还可以走，远处预制体会继续被收掉，所以一直盯到退出。
            if (IslandIdleImmersive.IsActive)
                return;

            // 切预设后要等流送把旧点收完、新点铺完，再多等一会儿才收工。
            int spawned = streamer != null ? streamer.SpawnedCount : 0;
            if (spawned != _lastSpawned)
            {
                _lastSpawned = spawned;
                _quietSince = Time.realtimeSinceStartup;
                return;
            }

            if (Time.realtimeSinceStartup - _quietSince < 1.5f)
                return;

            _wantRun = false;
            Report("done");
        }

        static void StartYooUnload()
        {
            _yooPending = false;
            if (!YooResourceModule.IsInitialized || YooResourceModule.DefaultPackage == null)
                return;

            _savedSlice = YooAssets.OperationSystemMaxTimeSlice;
            YooAssets.SetOperationSystemMaxTimeSliceExact(ReclaimTimeSliceMs);
            _sliceLowered = true;
            _op = YooResourceModule.DefaultPackage.UnloadUnusedAssetsAsync(YooUnloadLoops);
            if (_op == null)
                RestoreSlice();
        }

        static void RestoreSlice()
        {
            if (!_sliceLowered)
                return;
            _sliceLowered = false;
            long restore = _savedSlice < 1 ? 30 : _savedSlice;
            YooAssets.SetOperationSystemMaxTimeSliceExact(restore);
        }

        static bool TryUnloadOneBank()
        {
            WanosBankStore.CollectUnreferenced(BankScratch);
            string keepMusic = GameEntry.Music != null ? GameEntry.Music.CurrentBankName : null;
            for (int i = 0; i < BankScratch.Count; i++)
            {
                string stem = BankScratch[i];
                if (IsKeptBank(stem, keepMusic))
                    continue;
                WanosBankStore.Unload(stem);
                return true;
            }

            return false;
        }

        static bool IsKeptBank(string stem, string currentMusic)
        {
            if (string.Equals(stem, "Common_Init", System.StringComparison.OrdinalIgnoreCase))
                return true;
            if (string.Equals(stem, "Common_Music", System.StringComparison.OrdinalIgnoreCase))
                return true;
            return !string.IsNullOrEmpty(currentMusic)
                && string.Equals(stem, currentMusic, System.StringComparison.OrdinalIgnoreCase);
        }

        static void Report(string reason)
        {
            if (_reported)
                return;
            _reported = true;
            if (reason == "cancel"
                && _prefabReleases == 0
                && _prototypeDrops == 0
                && _bankUnloads == 0)
                return;

            float ms = (Time.realtimeSinceStartup - _startedAt) * 1000f;
            long reserved = Profiler.GetTotalReservedMemoryLong();
            Log.Info(
                $"[IslandAssetReclaim] {reason} prefabs={_prefabReleases} " +
                $"protos={_prototypeDrops} banks={_bankUnloads} ms={ms:F0} " +
                $"reserved={_reservedAtStart}->{reserved}");
        }
    }
}
