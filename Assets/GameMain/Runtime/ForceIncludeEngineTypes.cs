using UnityEngine;

namespace Game
{
    /// <summary>
    /// Island world (Terrain etc.) is loaded from YooAsset, not present in Launch.unity.
    /// Strip Engine Code would drop those native types — keep static refs so IL2CPP keeps them.
    /// </summary>
    static class ForceIncludeEngineTypes
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Touch()
        {
            // Terrain module (IslandTerrain prefab)
            _ = typeof(Terrain);
            _ = typeof(TerrainCollider);
            _ = typeof(TerrainData);
            _ = typeof(TerrainLayer);

            // Common environment types that may only appear in streamed prefabs
            _ = typeof(LODGroup);
            _ = typeof(ReflectionProbe);
            _ = typeof(LightProbeGroup);
            _ = typeof(LightProbeProxyVolume);
            _ = typeof(WindZone);
            _ = typeof(ParticleSystem);
            _ = typeof(Tree);
        }
    }
}
