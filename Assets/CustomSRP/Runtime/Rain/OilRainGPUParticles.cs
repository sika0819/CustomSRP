using UnityEngine;

namespace CustomSRP
{
    /// <summary>
    /// Legacy GPU instanced rain particle system.
    /// This component is DISABLED in favour of OilRainStreakQuad (single-quad path).
    /// Kept as a stub so existing prefab references remain valid.
    /// </summary>
    [DisallowMultipleComponent]
    public class OilRainGPUParticles : MonoBehaviour
    {
        [SerializeField] OilRainSettings _settings;
        [SerializeField] Transform _followTarget;
        [SerializeField] Mesh _rainMesh;
        [SerializeField] Material _rainMaterial;

        void OnEnable()
        {
            if (_settings == null) _settings = OilRainSettings.Current;
        }
    }
}
