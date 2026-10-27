using System.Collections;
using UnityEngine;
using UnityGameFramework.Runtime;

namespace Game
{
    /// <summary>
    /// Idle-immersive soft unload: suspend planar reflection RT, trim unused
    /// streamer prefab handles. Exit resumes the mirror; handles reload lazily.
    /// 退登 / World.Release 必须走 <see cref="CancelForWorldTeardown"/>，否则
    /// OnExitImmersive 会 Resume 镜面，且 Delay 里的 ForceUnloadUnusedAssets
    /// 会与二次 Prefill 抢主线程（「正在铺装环境」卡死、~0.4 FPS）。
    /// </summary>
    public static class IslandImmersiveSoftUnload
    {
        const float UnusedUnloadDelaySeconds = 1.5f;

        static int _generation;
        static Coroutine _pendingUnused;
        static MonoBehaviour _pendingHost;
        /// <summary>World teardown 期间禁止 Resume planar；Load/揭罩后再清。</summary>
        static bool _suppressPlanarResume;

        public static void OnEnterImmersive()
        {
            _generation++;
            int gen = _generation;

            OilPlanarReflectionControl.SuspendForImmersive();

            int trimmed = GameEntry.World?.MapItemSpawner?.TrimUnusedPrefabHandles() ?? 0;
            Log.Info($"[IslandImmersiveSoftUnload] enter trimHandles={trimmed}");

            CancelPendingUnused();
            MonoBehaviour host = GameEntry.World;
            if (host == null || !host.isActiveAndEnabled)
                return;

            _pendingHost = host;
            _pendingUnused = host.StartCoroutine(DelayedUnusedUnload(gen));
        }

        public static void OnExitImmersive()
        {
            _generation++;
            CancelPendingUnused();
            if (_suppressPlanarResume)
            {
                Log.Info("[IslandImmersiveSoftUnload] exit skip resume (world teardown)");
                return;
            }

            OilPlanarReflectionControl.ResumeFromImmersive();
            Log.Info("[IslandImmersiveSoftUnload] exit resume planar");
        }

        /// <summary>
        /// 退登 / 离岛：取消 pending unload、保持镜面挂起，勿 Resume。
        /// 须在 Close Main（Dispose IdleImmersive）之前或同时调用。
        /// </summary>
        public static void CancelForWorldTeardown()
        {
            _generation++;
            _suppressPlanarResume = true;
            CancelPendingUnused();
            OilPlanarReflectionControl.SuspendForImmersive();
            Log.Info("[IslandImmersiveSoftUnload] cancel for world teardown");
        }

        /// <summary>IsLoaded 后允许镜面恢复（勿在近场揭罩时调用，避免与 Far Prefill 叠帧）。</summary>
        public static void AllowPlanarResumeAfterWorldReady()
        {
            _suppressPlanarResume = false;
            OilPlanarReflectionControl.ResumeFromImmersive();
            Log.Info("[IslandImmersiveSoftUnload] planar resume after world ready");
        }

        /// <summary>进岛 Load 全程压住镜面，避免 Prefill 期间 FindObjects/mirror 抢帧。</summary>
        public static void SuspendPlanarForWorldLoad()
        {
            _suppressPlanarResume = true;
            OilPlanarReflectionControl.SuspendForImmersive();
        }

        static void CancelPendingUnused()
        {
            if (_pendingUnused == null)
                return;
            if (_pendingHost != null)
                _pendingHost.StopCoroutine(_pendingUnused);
            _pendingUnused = null;
            _pendingHost = null;
        }

        static IEnumerator DelayedUnusedUnload(int gen)
        {
            yield return new WaitForSecondsRealtime(UnusedUnloadDelaySeconds);
            _pendingUnused = null;
            _pendingHost = null;

            if (gen != _generation || !IslandIdleImmersive.IsActive)
                yield break;

            // 沉浸态只做 TrimUnusedPrefabHandles（OnEnterImmersive 已做）。
            // 禁止 ForceUnloadUnusedAssets / Yoo UnloadUnusedAssetsAsync：
            // 二者与退登 World.Release + 二次 Prefill 重叠时，主线程会卡在「正在铺装环境」。
            Log.Info("[IslandImmersiveSoftUnload] unused assets pass skipped (trim-only immersive)");
        }
    }
}
