using UnityEngine;

namespace Game
{
    /// <summary>
    /// Island world (Terrain etc.) is loaded from YooAsset, not present in Launch.unity.
    /// Strip Engine Code drops native types that only appear in AssetBundles — keep static
    /// refs and a Resources TerrainLayer so IL2CPP / engine stripper retain them.
    /// </summary>
    static class ForceIncludeEngineTypes
    {
        // Resources path: Assets/GameMain/Resources/EngineKeepAlive/KeepTerrainLayer.terrainlayer
        const string KeepTerrainLayerResource = "EngineKeepAlive/KeepTerrainLayer";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Touch()
        {
            // Terrain module (IslandTerrain prefab / TerrainData layers)
            _ = typeof(Terrain);
            _ = typeof(TerrainCollider);
            _ = typeof(TerrainData);
            _ = typeof(TerrainLayer);

            // typeof alone is not enough for TerrainLayer (class ID 1953259897) under
            // Strip Engine Code + AssetBundles. Loading a Resources TerrainLayer forces
            // the native type into the player.
            TerrainLayer keep = Resources.Load<TerrainLayer>(KeepTerrainLayerResource);
            if (keep != null)
                _ = keep.diffuseTexture;

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
