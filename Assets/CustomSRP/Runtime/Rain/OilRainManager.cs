using UnityEngine;

namespace CustomSRP
{
    /// <summary>
    /// Public façade for the rain system. Drop this on a manager
    /// GameObject alongside OilRainStreakQuad + OilRainRippleManager
    /// + OilRainSettings to receive hit notifications.
    /// </summary>
    public class OilRainManager : MonoBehaviour
    {
        [SerializeField] OilRainSettings _settings;
        [SerializeField] OilRainRippleManager _rippleManager;
        [SerializeField] OilRainRenderHook _renderHook;
        [SerializeField] OilRainStreakQuad _streakQuad;
        [SerializeField] bool _autoFindInScene = true;

        void OnEnable()
        {
            if (_autoFindInScene)
            {
                if (_settings == null)
                    _settings = OilRainSettings.Current;
                if (_rippleManager == null)
                    _rippleManager = GetComponent<OilRainRippleManager>()
                        ?? FindAnyObjectByType<OilRainRippleManager>();
                if (_renderHook == null)
                    _renderHook = GetComponent<OilRainRenderHook>()
                        ?? FindAnyObjectByType<OilRainRenderHook>();
                if (_streakQuad == null)
                    _streakQuad = GetComponent<OilRainStreakQuad>();
            }

            // Self-heal: older IslandTerrain bundles / prefabs may lack the curtain.
            if (_streakQuad == null)
                _streakQuad = gameObject.AddComponent<OilRainStreakQuad>();
        }

        /// <summary>
        /// Public entry point for external hit detection (e.g. raycasting from
        /// OilRainGPUParticles when particle bounds intersect water colliders).
        /// </summary>
        public void OnRainHit(Vector3 worldPos, Vector3 normal)
        {
            _rippleManager?.ProcessRainHit(worldPos, normal);
            _renderHook?.OnRainHit(worldPos, normal);
        }

        /// <summary>
        /// Runtime-smoothed rain intensity change. 0=stop, 1=downpour.
        /// Transition ramps over ~1.5s with 0.05 increments every 0.25s
        /// so changes look natural instead of an instant jump.
        /// </summary>
        public void SetIntensity(float target01)
        {
            if (_renderHook != null) _renderHook.SetIntensity(target01);
            else if (_settings != null) _settings.intensity = Mathf.Clamp01(target01);
        }

        /// <summary>
        /// Returns the active settings (or null if none assigned).
        /// </summary>
        public OilRainSettings Settings => _settings;

        /// <summary>
        /// Current rain intensity (0~1). Use SetIntensity for ramped changes.
        /// </summary>
        public float CurrentIntensity => _settings != null ? _settings.intensity : 0f;
    }
}
