using UnityEngine;

namespace Game
{
    /// <summary>
    /// 挂在带 <see cref="Terrain"/> 的物体上。实例化时 OnEnable 先于调用方返回。
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Terrain))]
    public sealed class TerrainSource : MonoBehaviour
    {
        void OnEnable()
        {
            if (!Application.isPlaying)
                return;

            Terrain terrain = GetComponent<Terrain>();
            EnsureTerrainCollider(terrain);
            DisablePengZhuang(terrain);
            IslandSceneComponent.Active?.RegisterTerrain(terrain);
        }

        /// <summary>
        /// IslandTerrain.prefab 历史上只有 Terrain、没有 TerrainCollider；真机无碰撞。
        /// </summary>
        static void EnsureTerrainCollider(Terrain terrain)
        {
            if (terrain == null || terrain.terrainData == null)
                return;

            TerrainCollider col = terrain.GetComponent<TerrainCollider>();
            if (col == null)
                col = terrain.gameObject.AddComponent<TerrainCollider>();
            if (col.terrainData != terrain.terrainData)
                col.terrainData = terrain.terrainData;
        }

        /// <summary>
        /// 岛范围只走 <see cref="TerrainCollider"/>。PengZhuang 网格不再参与走路，
        /// 避免和高度图叠两层碰撞。
        /// </summary>
        static void DisablePengZhuang(Terrain terrain)
        {
            if (terrain == null)
                return;

            Transform root = terrain.transform.parent != null
                ? terrain.transform.parent
                : terrain.transform;
            for (int i = 0; i < root.childCount; i++)
            {
                Transform child = root.GetChild(i);
                if (child.name.IndexOf("PengZhuang", System.StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                Collider[] cols = child.GetComponentsInChildren<Collider>(true);
                for (int c = 0; c < cols.Length; c++)
                {
                    if (cols[c] != null)
                        cols[c].enabled = false;
                }

                child.gameObject.SetActive(false);
            }
        }

        void OnDisable()
        {
            if (!Application.isPlaying)
                return;
            IslandSceneComponent.Active?.UnregisterTerrain(GetComponent<Terrain>());
        }
    }
}
