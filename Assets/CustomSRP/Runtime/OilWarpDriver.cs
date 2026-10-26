using Game;
using UnityEngine;

namespace CustomSRP
{
    /// <summary>
    /// Immersive breath → oil UV-warp globals for OilWarpPass.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class OilWarpDriver : MonoBehaviour, IOilWarpVisualDriver
    {
        static readonly int StrengthId = Shader.PropertyToID("_OilWarpStrength");
        static readonly int TimeId = Shader.PropertyToID("_OilWarpTime");
        static readonly int ScaleId = Shader.PropertyToID("_OilWarpScale");

        static OilWarpDriver _instance;

        float _strength;
        float _phaseRadians;
        float _scale = 1f;

        /// <summary>Effective immersive warp amount 0..1 (breath × weight).</summary>
        public static float Strength => _instance != null ? _instance._strength : 0f;

        public static bool IsActive => Strength > 1e-4f;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void AutoAttach() => EnsureOnSkybox();

        public static OilWarpDriver EnsureOnSkybox()
        {
            OilSkyboxTime sky = FindAnyObjectByType<OilSkyboxTime>();
            if (sky == null)
                return FindAnyObjectByType<OilWarpDriver>();

            OilWarpDriver onSky = sky.GetComponent<OilWarpDriver>();
            if (onSky != null)
                return onSky;

            return sky.gameObject.AddComponent<OilWarpDriver>();
        }

        void OnEnable()
        {
            _instance = this;
            OilWarpVisual.Driver = this;
            PushGlobals();
        }

        void OnDisable()
        {
            if (OilWarpVisual.Driver == (IOilWarpVisualDriver)this)
                OilWarpVisual.Driver = null;
            if (_instance == this)
                _instance = null;
            ClearGlobals();
        }

        public void SetImmersiveWarp(float breath01, float weight01, float phaseRadians)
        {
            float weight = Mathf.Clamp01(weight01);
            // Stand / walk / exit: weight 0 → strength 0 so OilWarpPass.IsActive is false
            // and PostFX keeps OilFilter→Final merge (Gmj no-regression floor).
            if (weight <= 1e-4f)
            {
                _strength = 0f;
                _phaseRadians = 0f;
                PushGlobals();
                return;
            }

            // Breath only pulses a little — keep a floor so oil never rests while immersive.
            const float BreathFloor = 0.72f;
            float breath = Mathf.Lerp(BreathFloor, 1f, Mathf.Clamp01(breath01));
            _strength = breath * weight;
            _phaseRadians = phaseRadians;
            PushGlobals();
        }

        public void SetScale(float scale) => _scale = Mathf.Max(0.1f, scale);

        void PushGlobals()
        {
            // Unscaled seconds; shader closes an 8s loop via sin/cos phase (seamless oil flow).
            float t = Time.unscaledTime + _phaseRadians * 0.08f;
            Shader.SetGlobalFloat(StrengthId, _strength);
            Shader.SetGlobalFloat(TimeId, t);
            Shader.SetGlobalFloat(ScaleId, _scale);
        }

        static void ClearGlobals()
        {
            Shader.SetGlobalFloat(StrengthId, 0f);
            Shader.SetGlobalFloat(TimeId, 0f);
            Shader.SetGlobalFloat(ScaleId, 1f);
        }
    }
}
