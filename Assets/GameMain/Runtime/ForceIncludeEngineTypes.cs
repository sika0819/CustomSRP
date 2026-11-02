using UnityEngine;

namespace Game
{
    /// <summary>
    /// Island world (Terrain etc.) is loaded from YooAsset, not present in Launch.unity.
    /// Strip Engine Code drops native types that only appear in AssetBundles — keep static
    /// refs and a Resources TerrainLayer so IL2CPP / engine stripper retain them.
    /// TerrainOilNPR 的 BaseMap*/AddPass 只写在 Dependency 字符串里；Always Included 仍可能漏包
    /// （同雨丝 streakMaterial）。Resources 材质硬引用强制打进 Player。
    /// </summary>
    static class ForceIncludeEngineTypes
    {
        // Resources path: Assets/GameMain/Resources/EngineKeepAlive/KeepTerrainLayer.terrainlayer
        const string KeepTerrainLayerResource = "EngineKeepAlive/KeepTerrainLayer";

        static readonly string[] KeepTerrainShaderMaterials =
        {
            "EngineKeepAlive/KeepTerrainOilNPR",
            "EngineKeepAlive/KeepTerrainLitBasemap",
            "EngineKeepAlive/KeepTerrainLitBasemapGen",
            "EngineKeepAlive/KeepTerrainLitAdd",
        };

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

            // Hard-ref Hidden basemap / add / oil terrain shaders (Dependency-only names).
            for (int i = 0; i < KeepTerrainShaderMaterials.Length; i++)
            {
                Material mat = Resources.Load<Material>(KeepTerrainShaderMaterials[i]);
                if (mat != null && mat.shader != null)
                    _ = mat.shader.name;
            }

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
