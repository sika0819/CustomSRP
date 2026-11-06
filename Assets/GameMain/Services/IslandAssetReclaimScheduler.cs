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
    /// 岛上流送期间，以及闲置沉浸与切预设揭幕之后，每帧大约 1 毫秒释放已经没人引用的资源。
    /// 虹膜推镜 / 黑场 / 开镜，以及流送还在生成时，直接让开。流送自己的卸载预算不动。
    /// 本帧已经慢于目标帧时间，就整帧不做。
    /// 先丢掉空的合批原型，再在这一毫秒里放掉多个预制体句柄。
    /// 放掉之后隔一段没有生成的时间，再请框架切一次 <c>Resources.UnloadUnusedAssets</c>（不 <c>GC.Collect</c>）。
    /// 走路窗（含松杆后 0.75s 消抖）跳过 Yoo / Bank / Resources 重卸；句柄释放预算仍保留。
    /// 闲置回收另外只启动一轮按 2 毫秒切片的 Yoo 卸载。
    /// </summary>
    public sealed class IslandAssetReclaimScheduler : MonoBehaviour
    {
        const long ReclaimTimeSliceMs = 2;
        const long WorkBudgetMs = 1;
        const int YooUnloadLoops = 8;
        const float ResourceUnloadQuietSeconds = 1.5f;
        /// <summary>松杆后再等这么久才允许重卸，避免 IsWalking 抖一下就开火。</summary>
        const float HeavyUnloadStandStillSeconds = 0.75f;

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
        static bool _needResourceUnload;
        static float _resourceUnloadNotBefore;
        static float _lastWalkingRealtime = -1000f;
        static float _yooUnloadHitchStartedAt;

        /// <summary>进岛流送开始后就挂上，走动中也能放零引用句柄。</summary>
        public static void EnsureForIsland()
        {
            Ensure();
        }

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

        /// <summary>
        /// 走路中，或松杆后未满 <see cref="HeavyUnloadStandStillSeconds"/>：禁止 Yoo / Bank / Resources 重卸。
        /// </summary>
        static bool IsReclaimHeavyBusy
        {
            get
            {
                if (IslandLocomotionState.IsWalking)
                {
                    _lastWalkingRealtime = Time.realtimeSinceStartup;
                    return true;
                }

                return Time.realtimeSinceStartup - _lastWalkingRealtime < HeavyUnloadStandStillSeconds;
            }
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
                float hitchMs = (Time.realtimeSinceStartup - _yooUnloadHitchStartedAt) * 1000f;
                Log.Info(
                    $"[IslandAssetReclaim] yoo destroyedLoaders={destroyed} hitchMs={hitchMs:F0}");
                if (!_wantRun)
                    Report("cancel");
                return;
            }

            bool onIsland = EnvItemStreamerLite.Current != null;
            if (!_wantRun && !onIsland)
                return;

            IslandLeaveAssetReclaim leave = IslandLeaveAssetReclaim.Ensure();
            if (leave != null && leave.IsBusy)
            {
                if (_wantRun)
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

            bool heavyBusy = IsReclaimHeavyBusy;

            if (_yooPending)
            {
                if (_wantRun)
                {
                    if (heavyBusy)
                        return;
                    StartYooUnload();
                }
                else
                    _yooPending = false;
                return;
            }

            Work.Restart();
            bool freed = false;
            // 原型还握着 Mesh/Material 时，先放句柄也卸不掉。丢掉原型算进这 1 毫秒。
            while (Work.ElapsedMilliseconds < WorkBudgetMs
                   && EnvNativeInstancing.TryDropOneEmptyPrototype())
            {
                _prototypeDrops++;
                freed = true;
            }

            if (streamer != null)
            {
                while (Work.ElapsedMilliseconds < WorkBudgetMs
                       && streamer.ReleaseOneUnusedPrefab())
                {
                    _prefabReleases++;
                    freed = true;
                }
            }

            if (freed)
            {
                _needResourceUnload = true;
                if (_resourceUnloadNotBefore <= Time.realtimeSinceStartup)
                    _resourceUnloadNotBefore = Time.realtimeSinceStartup + ResourceUnloadQuietSeconds;
            }

            if (_wantRun && freed)
            {
                _yooPending = true;
                return;
            }

            TryResourceUnload();

            if (!_wantRun)
                return;

            if (heavyBusy)
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

        static void TryResourceUnload()
        {
            if (!_needResourceUnload)
                return;
            if (Time.realtimeSinceStartup < _resourceUnloadNotBefore)
                return;
            if (_op != null || _yooPending)
                return;
            // 走路窗 + 松杆消抖：不做重 Unload；句柄 1ms 释放仍可走。
            if (IsReclaimHeavyBusy)
                return;

            ResourceComponent resource = GameEntry.Resource;
            if (resource == null || resource.UnloadUnusedAssetsBusy)
                return;

            resource.ForceUnloadUnusedAssets(false);
            _needResourceUnload = false;
            _resourceUnloadNotBefore = Time.realtimeSinceStartup + ResourceUnloadQuietSeconds;
            // ForceUnload 只入队；真正耗时在 ResourceComponent.Update，勿把入队耗时当成 hitchMs。
            Log.Info("[IslandAssetReclaim] resources unload queued");
        }

        static void StartYooUnload()
        {
            _yooPending = false;
            if (IsReclaimHeavyBusy)
            {
                // 走路中保留 pending，站定后再卸。
                _yooPending = true;
                return;
            }

            if (!YooResourceModule.IsInitialized || YooResourceModule.DefaultPackage == null)
                return;

            _savedSlice = YooAssets.OperationSystemMaxTimeSlice;
            YooAssets.SetOperationSystemMaxTimeSliceExact(ReclaimTimeSliceMs);
            _sliceLowered = true;
            _yooUnloadHitchStartedAt = Time.realtimeSinceStartup;
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

        /// <summary>
        /// 离岛用。闲置回收在退登时被取消，延迟卸载也没人再驱动。
        /// 音乐 bank 留下，其余引用已经归零的卸掉。
        /// </summary>
        public static int UnloadLooseBanks()
        {
            WanosBankStore.FlushPendingUnloads();
            int unloaded = 0;
            while (TryUnloadOneBank())
                unloaded++;
            return unloaded;
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

            float sessionMs = (Time.realtimeSinceStartup - _startedAt) * 1000f;
            long reserved = Profiler.GetTotalReservedMemoryLong();
            Log.Info(
                $"[IslandAssetReclaim] {reason} prefabs={_prefabReleases} " +
                $"protos={_prototypeDrops} banks={_bankUnloads} sessionMs={sessionMs:F0} " +
                $"reserved={_reservedAtStart}->{reserved}");
        }
    }
}
