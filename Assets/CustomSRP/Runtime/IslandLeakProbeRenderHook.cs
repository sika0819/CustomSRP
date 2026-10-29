using Game;
using UnityEngine;

namespace CustomSRP
{
    /// <summary>泄漏对照关掉云之后，丢掉已经建好的云环网格。</summary>
    static class IslandLeakProbeRenderHook
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Hook()
        {
            IslandLeakProbe.Changed -= OnChanged;
            IslandLeakProbe.Changed += OnChanged;
        }

        static void OnChanged(IslandLeakProbe.Module module, bool on)
        {
            if (module == IslandLeakProbe.Module.Cloud && !on)
                OilCloudLayer.ReleaseSharedMeshes();
        }
    }
}
