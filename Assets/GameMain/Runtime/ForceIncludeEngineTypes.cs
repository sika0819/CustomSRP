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
            // 组件已废弃，但已打包的岛预制体仍可能引用；保留 typeof 以免 IL2CPP 裁掉。
#pragma warning disable CS0618
            _ = typeof(LightProbeProxyVolume);
#pragma warning restore CS0618
            _ = typeof(WindZone);
            _ = typeof(ParticleSystem);
            _ = typeof(Tree);
        }
    }
}
