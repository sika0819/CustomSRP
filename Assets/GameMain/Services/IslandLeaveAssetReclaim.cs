using System.Collections;
using UnityEngine;
using UnityGameFramework.Runtime;

namespace Game
{
    /// <summary>
    /// 退岛 World.Release 之后、二次 Prefill 之前回收 Yoo 未用包体。
    /// SoftUnload 故意跳过 UnloadUnused（怕和 Prefill 抢帧）；此处改到 GuestLogin 等 Arm
    /// 的空窗执行，避免二次进岛 RSS 顶满导致 ~8FPS。
    /// </summary>
    public static class IslandLeaveAssetReclaim
    {
        const float WaitCapSeconds = 8f;
        const int YooUnloadLoops = 8;

        static int _generation;
        static bool _busy;
        static Coroutine _routine;
        static MonoBehaviour _host;

        public static bool IsBusy => _busy;

        /// <summary>World.Release 后调度；可重入（只保留最新一代）。</summary>
        public static void ScheduleAfterWorldRelease()
        {
            _generation++;
            int gen = _generation;
            CancelRoutineOnly();

            MonoBehaviour host = GameEntry.Base;
            if (host == null)
            {
                RunInline(gen);
                return;
            }

            _busy = true;
            _host = host;
            _routine = host.StartCoroutine(Run(gen));
        }

        /// <summary>进岛 Load / ChangeState 前挡住，避免 Prefill 与卸载叠主线程。</summary>
        public static IEnumerator WaitIfBusy()
        {
            if (!_busy)
                yield break;

            float start = Time.realtimeSinceStartup;
            while (_busy && Time.realtimeSinceStartup - start < WaitCapSeconds)
                yield return null;

            if (_busy)
            {
                Log.Warning(
                    "[IslandLeaveAssetReclaim] WaitIfBusy wall-cap — proceed (unload still running)");
            }
        }

        /// <summary>关机 / 强杀流程作废进行中的回收。</summary>
        public static void Cancel()
        {
            _generation++;
            CancelRoutineOnly();
            _busy = false;
        }

        static void CancelRoutineOnly()
        {
            if (_routine != null && _host != null)
                _host.StopCoroutine(_routine);
            _routine = null;
            _host = null;
        }

        static void RunInline(int gen)
        {
            _busy = true;
            try
            {
                if (YooResourceModule.IsInitialized)
                    YooResourceModule.UnloadUnusedAssets(YooUnloadLoops);
                System.GC.Collect();
                Log.Info("[IslandLeaveAssetReclaim] inline unload kicked (no host)");
            }
            finally
            {
                if (gen == _generation)
                    _busy = false;
            }
        }

        static IEnumerator Run(int gen)
        {
            // Destroy 落到帧末再卸，避免 handle 仍短暂持有。
            yield return null;
            yield return null;
            if (gen != _generation)
            {
                _busy = false;
                _routine = null;
                _host = null;
                yield break;
            }

            float t0 = Time.realtimeSinceStartup;
            Log.Info("[IslandLeaveAssetReclaim] begin Yoo UnloadUnused after leave");
            yield return YooResourceModule.UnloadUnusedAssetsCoroutine(YooUnloadLoops);
            if (gen != _generation)
            {
                _busy = false;
                _routine = null;
                _host = null;
                yield break;
            }

            // 轻量托管回收；加载罩盖着，可接受一次尖刺。
            System.GC.Collect();
            yield return null;

            float ms = (Time.realtimeSinceStartup - t0) * 1000f;
            Log.Info($"[IslandLeaveAssetReclaim] done ms={ms:F0} gen={gen}");
            if (gen == _generation)
                _busy = false;
            _routine = null;
            _host = null;
        }
    }
}

