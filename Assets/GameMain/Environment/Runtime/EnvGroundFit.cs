using UnityEngine;
using UnityEngine.Rendering;

namespace Game
{
    /// <summary>
    /// 贴地卡片（Flowers_Flat 等）：Y 贴 Terrain，并关掉投射阴影（alpha 卡阴影又大又脏）。
    /// </summary>
    public static class EnvGroundFit
    {
        public static bool IsFlatGroundCard(string prefabName)
        {
            if (string.IsNullOrEmpty(prefabName))
                return false;
            return prefabName.IndexOf("Flowers_Flat", System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static bool TrySnapY(ref Vector3 worldPos)
        {
            Terrain terrain = Terrain.activeTerrain;
            if (terrain != null && terrain.terrainData != null)
                return TrySnapY(ref worldPos, terrain.terrainData, terrain.transform.position);

            return false;
        }

        public static bool TrySnapY(ref Vector3 worldPos, TerrainData data, Vector3 terrainOrigin)
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

            worldPos.y = terrainOrigin.y + data.GetInterpolatedHeight(nx, nz);
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
