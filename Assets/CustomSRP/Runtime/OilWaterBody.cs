using UnityEngine;

namespace CustomSRP
{
    /// <summary>
    /// 挂在水体根上。启用时把自己（含 Transform 子级）设到 Water 层。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class OilWaterBody : MonoBehaviour
    {
        void OnEnable()
        {
            if (!Application.isPlaying)
                return;
            OilWaterLayers.ApplyTo(gameObject);
        }
    }
}
