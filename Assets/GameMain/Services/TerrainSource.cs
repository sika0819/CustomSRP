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
            IslandSceneComponent.Active?.RegisterTerrain(GetComponent<Terrain>());
        }

        void OnDisable()
        {
            if (!Application.isPlaying)
                return;
            IslandSceneComponent.Active?.UnregisterTerrain(GetComponent<Terrain>());
        }
    }
}
