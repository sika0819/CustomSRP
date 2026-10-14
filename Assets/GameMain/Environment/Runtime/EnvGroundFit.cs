using UnityEngine;
using UnityEngine.Rendering;

namespace Game
{
    /// <summary>
    /// 贴地：Flowers_Flat 卡片 + 树（Songshu/Lvshu/Yeshu/Guanmu）Y 贴 Terrain。
    /// 树额外下压一小段，抵消 HeightmapPixelError / 枢轴略高于可视根底。
    /// </summary>
    public static class EnvGroundFit
    {
        /// <summary>树枢轴相对地表再下沉的米数（负值）。不改 mesh。</summary>
        public const float TreeBuryMeters = -0.12f;

        public static bool IsFlatGroundCard(string prefabName)
        {
            if (string.IsNullOrEmpty(prefabName))
                return false;
            return prefabName.IndexOf("Flowers_Flat", System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>按类别决定是否贴地，以及是否下压。</summary>
        public static bool TrySnapMapItemY(ref Vector3 worldPos, string category, string prefabName)
        {
            if (IsFlatGroundCard(prefabName))
                return TrySnapY(ref worldPos, 0f);

            if (EnvStreamingConfig.IsTreeCategory(category))
                return TrySnapY(ref worldPos, TreeBuryMeters);

            return false;
        }

        public static bool TrySnapY(ref Vector3 worldPos)
        {
            return TrySnapY(ref worldPos, 0f);
        }

        public static bool TrySnapY(ref Vector3 worldPos, float yBias)
        {
            Terrain terrain = ResolveTerrain();
            if (terrain != null && terrain.terrainData != null)
                return TrySnapY(ref worldPos, terrain.terrainData, terrain.transform.position, yBias);

            return false;
        }

        static Terrain ResolveTerrain()
        {
            Terrain terrain = Terrain.activeTerrain;
            if (terrain != null && terrain.terrainData != null)
                return terrain;

            // Island 异步加载时 activeTerrain 偶发为空，回退场景内查找
            var all = Object.FindObjectsByType<Terrain>(FindObjectsInactive.Exclude);
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] != null && all[i].terrainData != null)
                    return all[i];
            }

            return null;
        }

        public static bool TrySnapY(ref Vector3 worldPos, TerrainData data, Vector3 terrainOrigin)
        {
            return TrySnapY(ref worldPos, data, terrainOrigin, 0f);
        }

        public static bool TrySnapY(ref Vector3 worldPos, TerrainData data, Vector3 terrainOrigin, float yBias)
        {
            if (data == null)
                return false;

            Vector3 size = data.size;
            if (size.x <= 0.0001f || size.z <= 0.0001f)
                return false;

            float nx = (worldPos.x - terrainOrigin.x) / size.x;
            float nz = (worldPos.z - terrainOrigin.z) / size.z;
            if (nx < 0f || nx > 1f || nz < 0f || nz > 1f)
                return false;

            worldPos.y = terrainOrigin.y + data.GetInterpolatedHeight(nx, nz) + yBias;
            return true;
        }

        public static void DisableCastShadows(GameObject go)
        {
            if (go == null)
                return;

            var renderers = go.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                if (renderers[i] != null)
                    renderers[i].shadowCastingMode = ShadowCastingMode.Off;
            }
        }
    }
}
