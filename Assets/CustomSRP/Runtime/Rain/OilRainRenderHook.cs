using UnityEngine;

namespace CustomSRP
{
    /// <summary>
    /// Bridges the GPU rain component to the post-process pass.
    /// Forwards AddRipple calls to the OilRainPostPass and pushes
    /// the wet-surface shader globals once per frame.
    ///
    /// Use this on a scene rain manager GameObject next to
    /// OilRainGPUParticles. Reads OilRainSettings on the same object.
    /// </summary>
    public class OilRainRenderHook : MonoBehaviour
    {
        [SerializeField] OilRainSettings _settings;
        [SerializeField] OilRainRippleManager _rippleManager;

        // Last frame we updated globals (avoids every-frame set)
        float _lastGlobalTime;

        // Last frame we checked the intensity transition (smooth ramp)
        float _lastIntensityTime;
        // Target intensity for runtime SetIntensity. -1 = use settings.intensity as-is.
        float _targetIntensity = -1f;

        void OnEnable()
        {
            if (_settings == null)
                _settings = OilRainSettings.Current ?? GetComponent<OilRainSettings>();
        }

        void LateUpdate()
        {
            if (_settings == null) return;

            float now = Time.unscaledTime;

            // Smooth runtime intensity transition (1.5s ramp).
            if (_targetIntensity >= 0f && now - _lastIntensityTime > 0.25f)
            {
                _lastIntensityTime = now;
                float cur = _settings.intensity;
                if (Mathf.Abs(cur - _targetIntensity) > 0.01f)
                {
                    _settings.intensity = Mathf.MoveTowards(cur, _targetIntensity, 0.05f);
                }
                else
                {
                    _settings.intensity = _targetIntensity;
                    _targetIntensity = -1f;
                }
            }

            // Push rain/wet globals at 4Hz to minimise cost (they only change with
            // inspector edits and not frame-to-frame).
            if (now - _lastGlobalTime > 0.25f)
            {
                _lastGlobalTime = now;
                float rainI = _settings.intensity;
                Shader.SetGlobalFloat("_OilRainWetStrength", _settings.wetStrength * rainI);
                Shader.SetGlobalFloat("_OilRainWetSmoothnessBoost", _settings.wetSmoothnessBoost * rainI);
                Shader.SetGlobalFloat("_OilRainWetDarkening", _settings.wetDarkening * rainI);
                Shader.SetGlobalFloat("_OilRainIntensity", rainI);
                Shader.SetGlobalColor("_OilRainAmbientColor", new Color(0.3f, 0.6f, 0.8f, 1f));
            }
        }

        /// <summary>
        /// External API for OilRainManager / OilRainRippleManager.OnRainHit
        /// to push ripple spawn events.
        /// </summary>
        public void OnRainHit(Vector3 worldPos, Vector3 normal)
        {
            _rippleManager?.ProcessRainHit(worldPos, normal);

            // Also enqueue for the screen-space ripple pass when the scene water
            // surface is below worldPos (cheap heuristic: any normal).
            if (_settings != null && _settings.postRippleEnabled)
            {
                OilRainPostPass.EnqueueRipple(worldPos);
            }
        }

        /// <summary>
        /// Runtime-smoothed intensity transition. The transition fires over
        /// ~1.5s in 0.05 increments every 0.25s. Outside Editor it preserves
        /// performance: only one tick per quarter-second updates the value.
        /// </summary>
        public void SetIntensity(float target01)
        {
            target01 = Mathf.Clamp01(target01);
            _targetIntensity = target01;
            _lastIntensityTime = Time.unscaledTime;
        }
    }
}
