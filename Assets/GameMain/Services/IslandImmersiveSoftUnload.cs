using System.Collections;
using UnityEngine;
using UnityGameFramework.Runtime;

namespace Game
{
    /// <summary>
    /// Idle-immersive soft unload: suspend planar reflection RT, trim unused
    /// streamer prefab handles, then (after a short delay) GF + YooAsset unused.
    /// Exit only resumes the mirror; handles/AB reload lazily on next spawn.
    /// </summary>
    public static class IslandImmersiveSoftUnload
    {
        const float UnusedUnloadDelaySeconds = 1.5f;

        static int _generation;
        static Coroutine _pendingUnused;
        static MonoBehaviour _pendingHost;

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
            OilPlanarReflectionControl.ResumeFromImmersive();
            Log.Info("[IslandImmersiveSoftUnload] exit resume planar");
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

            if (GameEntry.Resource != null)
                GameEntry.Resource.ForceUnloadUnusedAssets(false);

            YooResourceModule.UnloadUnusedAssets();
            Log.Info("[IslandImmersiveSoftUnload] unused assets pass");
        }
    }
}
