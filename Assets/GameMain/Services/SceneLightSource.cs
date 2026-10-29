using UnityEngine;

namespace Game
{
    /// <summary>
    /// 挂在灯自己的物体上，把这盏灯登记进 <see cref="IslandSceneComponent"/>。
    /// 太阳由天空盒以保留身份再登记一次。
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Light))]
    public sealed class SceneLightSource : MonoBehaviour
    {
        void OnEnable()
        {
            if (!Application.isPlaying)
                return;
            IslandSceneComponent.Active?.RegisterLight(GetComponent<Light>(), false);
        }

        void OnDisable()
        {
            if (!Application.isPlaying)
                return;
            IslandSceneComponent.Active?.UnregisterLight(GetComponent<Light>());
        }
    }
}
