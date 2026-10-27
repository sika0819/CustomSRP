using System.Collections;
using UnityEngine;
using UnityGameFramework.Runtime;

namespace Game
{
    /// <summary>
    /// 退岛 World.Release 之后卸掉不再引用的 Yoo bundle。
    /// 包体留着时，二次 Prefill 会在旧资源还占着内存的情况下再实例化一套，
    /// 主线程在铺到几十个物体后不再渲染。先空等几帧让 Destroy 落地，再卸。
    /// </summary>
    public static class IslandLeaveAssetReclaim
    {
        const int YooUnloadLoops = 8;

        static int _generation;
        static bool _busy;
        static Coroutine _routine;
        static MonoBehaviour _host;

        public static bool IsBusy => _busy;

        /// <summary>World.Release 后调度；可重入（只保留最新一代）。</summary>
        public static void ScheduleAfterWorldRelease()
        {
            IslandAssetReclaimScheduler.Cancel();
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

        /// <summary>进岛 Load / ChangeState 前挡住，卸完才放行，避免 Prefill 与卸载叠主线程。</summary>
        public static IEnumerator WaitIfBusy()
        {
            while (_busy)
                yield return null;
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
            // 运行时材质要等上面的 Destroy 落帧后才失去贴图引用。
            Log.Info("[IslandLeaveAssetReclaim] begin Resources.UnloadUnusedAssets");
            yield return Resources.UnloadUnusedAssets();
            if (gen != _generation)
            {
                _busy = false;
                _routine = null;
                _host = null;
                yield break;
            }

            // Destroy 落帧后再卸。加载协程已改为按帧等待，避免和 Unload 叠在同一次 LoadFromFile 上。
            for (int i = 0; i < 6; i++)
                yield return null;
            if (gen != _generation)
            {
                _busy = false;
                _routine = null;
                _host = null;
                yield break;
            }

            Log.Info("[IslandLeaveAssetReclaim] begin Yoo UnloadUnused after leave");
            yield return YooResourceModule.UnloadUnusedAssetsCoroutine(YooUnloadLoops);
            if (gen != _generation)
            {
                _busy = false;
                _routine = null;
                _host = null;
                yield break;
            }

            Log.Info("[IslandLeaveAssetReclaim] begin Resources.UnloadUnusedAssets pass2");
            yield return Resources.UnloadUnusedAssets();

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

