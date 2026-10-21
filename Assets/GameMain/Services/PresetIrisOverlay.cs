using UnityEngine;

namespace Game
{
    /// <summary>
    /// Drives circular iris blackout via shader globals.
    /// CustomSRP <c>PresetIrisPass</c> composites them onto the camera target.
    /// Idle: Softness=1, Color.a=0 — pass early-outs.
    /// Peak: Softness=0, Color.a=1 — opaque black (mobile GLES-safe).
    /// </summary>
    public static class PresetIrisOverlay
    {
        static readonly int SoftnessId = Shader.PropertyToID("_PresetIrisSoftness");
        static readonly int ColorId = Shader.PropertyToID("_PresetIrisColor");
        static readonly int TransitionId = Shader.PropertyToID("_PresetIrisTransition");
        static readonly int SizeId = Shader.PropertyToID("_PresetIrisSize");
        static readonly int ActiveId = Shader.PropertyToID("_PresetIrisActive");

        const float DefaultTransition = 1f;
        const float DefaultSize = 1f; // iris hole scale (Boluo mat default)

        public static bool IsActive { get; private set; }

        public static void Set(float softness, float colorAlpha)
        {
            softness = Mathf.Clamp01(softness);
            colorAlpha = Mathf.Clamp01(colorAlpha);
            bool active = colorAlpha > 0.001f || softness < 0.999f;
            IsActive = active;
            Shader.SetGlobalFloat(ActiveId, active ? 1f : 0f);
            Shader.SetGlobalFloat(SoftnessId, softness);
            Shader.SetGlobalFloat(TransitionId, DefaultTransition);
            Shader.SetGlobalFloat(SizeId, DefaultSize);
            Shader.SetGlobalColor(ColorId, new Color(0f, 0f, 0f, colorAlpha));
        }

        /// <summary>Hard full-screen black peak used while teleporting.</summary>
        public static void Peak() => Set(0f, 1f);

        /// <summary>Idle invisible — no residual dark rim.</summary>
        public static void Clear()
        {
            IsActive = false;
            Shader.SetGlobalFloat(ActiveId, 0f);
            Shader.SetGlobalFloat(SoftnessId, 1f);
            Shader.SetGlobalFloat(TransitionId, DefaultTransition);
            Shader.SetGlobalFloat(SizeId, DefaultSize);
            Shader.SetGlobalColor(ColorId, new Color(0f, 0f, 0f, 0f));
        }
    }
}
