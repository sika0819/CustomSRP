using UnityEngine;

namespace CustomSRP
{
    /// <summary>
    /// All-in-one rain system configuration: ripples, splashes, wet surface, and related effects.
    /// Create via Assets > Create > CustomSRP > OilRainSettings.
    /// </summary>
    [CreateAssetMenu(fileName = "OilRainSettings", menuName = "CustomSRP/OilRainSettings")]
    public class OilRainSettings : ScriptableObject
    {
        /// <summary>
        /// Singleton-style runtime access. Initialised by OilAtmosphereDriver.Awake()
        /// so that OilRainRippleManager / OilRainFlipbookDriver / etc. can safely read it
        /// without needing a direct inspector reference.
        /// </summary>
        public static OilRainSettings Current { get; internal set; }
        // =====================================================================
        // Rain GPU Particles
        // =====================================================================
        [Header("Rain Particles — GPU InstancedMesh")]
        [Tooltip("0~1, overall rain intensity. Controls emission rate multiplier.")]
        [Range(0f, 1f)]
        public float intensity = 0.6f;

        [Tooltip("Fall speed in m/s (positive = downward).")]
        public float speed = 20f;

        [Tooltip("Wind direction in XZ plane (normalised automatically).")]
        public Vector3 windDirection = new Vector3(1f, 0f, 0f);

        [Tooltip("Wind strength multiplier on top of windDirection.")]
        [Range(0f, 1f)]
        public float windStrength = 0.3f;

        [Tooltip("Horizontal spawn radius around the follow target (metres).")]
        public float spawnRadius = 30f;

        [Tooltip("Height above the follow target where particles are born (metres).")]
        public float spawnHeight = 25f;

        [Tooltip("Maximum particles allocated in the GPU buffer (PC). Mobile halves this automatically.")]
        public int maxParticles = 30000;

        [Tooltip("Lerp speed for smooth camera/target following (0=instant).")]
        public float followLerpSpeed = 6f;

        // =====================================================================
        // Ripples — fully shader-driven (flipbook normals + post-process)
        // GameObject disc pool has been removed.
        // =====================================================================
        [Header("Ripples — shader-driven (no GameObjects)")]
        [Tooltip("Probability 0~1 that a rain particle hitting water triggers a ripple event (screen-space post-pass + flipbook normals).")]
        [Range(0f, 1f)]
        public float rippleChance = 0.1f;

        [Tooltip("Layer mask used to detect water surfaces for ripple spawning.")]
        public LayerMask waterLayerMask = 1 << 10; // default to "Water" layer, adjust in inspector

        // =====================================================================
        // Wet Surface
        // =====================================================================
        [Header("Wet Surface — NPR shader injection")]
        [Tooltip("Wet darkening + specular boost intensity (0=disabled, 1=full wet).")]
        [Range(0f, 1f)]
        public float wetStrength = 0f;

        [Tooltip("Extra smoothness [0~0.3] added to surfaces when wet.")]
        public float wetSmoothnessBoost = 0.15f;

        [Tooltip("Additional diffuse darkening factor when wet (0=none, 1=black).")]
        [Range(0f, 1f)]
        public float wetDarkening = 0.2f;

        // =====================================================================
        // Post-process ripples (screen-space, no objects)
        // =====================================================================
        [Header("Post-process Ripples — screen-space on water")]
        // Off by default: needs Copy Depth to see water, and is invisible once
        // Copy Depth / HDR are stripped for mobile. Water rain still uses the
        // flipbook normals + splash quads below.
        [Tooltip("Enable screen-space water ripple post-process. Needs Copy Depth; leave off on mobile.")]
        public bool postRippleEnabled = false;

        [Tooltip("Screen-space ripple world size (m). Each ripple covers this radius in world space.")]
        public float postRippleWorldSize = 3f;

        [Tooltip("Number of simultaneous screen-space ripples shown on water surface.")]
        public int postRippleCount = 8;

        [Tooltip("Ripple expansion speed (world units/sec).")]
        public float postRippleExpandSpeed = 2f;

        [Tooltip("Post ripple opacity (0~1).")]
        [Range(0f, 1f)]
        public float postRippleOpacity = 0.4f;

        // =====================================================================
        // Rain Streaks — single camera-following Quad (current path)
        // =====================================================================
        [Header("Rain Streaks — single Quad")]
        [Tooltip("Streak colour (HDR).")]
        public Color rainColor = new Color(0.72f, 0.80f, 0.92f, 1f);

        [Tooltip("Peak streak opacity at full intensity.")]
        [Range(0f, 1f)]
        public float streakOpacity = 0.6f;

        [Tooltip("Streak columns per world unit. Higher = denser rain.")]
        [Range(0.05f, 2f)]
        public float streakDensity = 0.9f;

        [Tooltip("Fraction of each column lit, i.e. how long the streaks are.")]
        [Range(0.05f, 1f)]
        public float streakLength = 0.8f;

        [Tooltip("Fraction of a column width that is solid, i.e. streak thickness.")]
        [Range(0.02f, 0.9f)]
        public float streakWidth = 0.5f;

        [Tooltip("How fast streak columns scroll downward.")]
        [Range(0f, 8f)]
        public float streakFallSpeed = 3.5f;

        [Tooltip("Render rain with the single camera-following Quad (OilRainStreakQuad). " +
                 "When off, the legacy 30k-instance OilRainGPUParticles path runs instead.")]
        public bool useSingleQuadStreaks = true;

        // =====================================================================
        // Rain Ripples — baked normal flipbook sampled into the water normal
        // =====================================================================
        [Header("Rain Ripples — normal flipbook")]
        [Tooltip("Enable the rain-ripple flipbook on water surfaces.")]
        public bool rippleFlipbookEnabled = false;

        [Tooltip("Ripple flipbook atlas. Falls back to a generated placeholder if empty.")]
        public Texture2D rippleFlipbook;

        [Tooltip("Columns in the flipbook atlas.")]
        [Range(1, 16)]
        public int rippleFlipbookColumns = 8;

        [Tooltip("Rows in the flipbook atlas.")]
        [Range(1, 16)]
        public int rippleFlipbookRows = 1;

        [Tooltip("Total animation frames in the flipbook.")]
        [Range(1, 64)]
        public int rippleFlipbookFrames = 8;

        [Tooltip("Frames played per second.")]
        [Range(0.5f, 30f)]
        public float rippleFlipbookSpeed = 9f;

        [Tooltip("World units covered by one repeat of the flipbook tile.")]
        [Range(0.5f, 40f)]
        public float rippleFlipbookWorldScale = 4f;

        [Tooltip("How hard the ripple normal perturbs the water normal.")]
        [Range(0f, 2f)]
        public float rippleFlipbookStrength = 0.35f;

        // =====================================================================
        // Splashes — instanced horizontal flipbook quads
        // =====================================================================
        [Header("Splashes — instanced flipbook quads")]
        [Tooltip("Enable splash flipbook quads on rain impact.")]
        public bool splashFlipbookEnabled = false;

        [Tooltip("Splash flipbook atlas. Falls back to a generated placeholder if empty.")]
        public Texture2D splashFlipbook;

        [Tooltip("Columns in the splash flipbook atlas.")]
        [Range(1, 16)]
        public int splashFlipbookColumns = 5;

        [Tooltip("Rows in the splash flipbook atlas.")]
        [Range(1, 16)]
        public int splashFlipbookRows = 1;

        [Tooltip("Total animation frames in the splash flipbook.")]
        [Range(1, 32)]
        public int splashFlipbookFrames = 5;

        [Tooltip("Splash playback speed (frames/sec).")]
        [Range(1f, 60f)]
        public float splashFlipbookSpeed = 14f;

        [Tooltip("World size of one splash quad (metres). Keep small to limit overdraw.")]
        [Range(0.1f, 3f)]
        public float splashQuadSize = 0.35f;

        [Tooltip("Maximum simultaneous splash quads.")]
        [Range(1, 256)]
        public int splashQuadPoolSize = 48;

        [Tooltip("Spawn distance around the follow target where splashes appear.")]
        [Range(1f, 40f)]
        public float splashSpawnRadius = 8f;

        [Tooltip("Splash quad opacity.")]
        [Range(0f, 1f)]
        public float splashOpacity = 0.85f;

        // =====================================================================
        // Editor helpers
        // =====================================================================
        /// <summary>
        /// Returns the effective max particle count, halved for mobile platforms.
        /// </summary>
        public int EffectiveMaxParticles =>
            Application.isMobilePlatform ? maxParticles / 2 : maxParticles;

        /// <summary>
        /// Returns a safe spawn radius clamped to reasonable mobile values.
        /// </summary>
        public float EffectiveSpawnRadius =>
            Application.isMobilePlatform ? Mathf.Min(spawnRadius, 25f) : spawnRadius;

        private void OnValidate()
        {
            // Normalise wind direction in edit mode
            if (windDirection.sqrMagnitude > 0.001f)
                windDirection = windDirection.normalized;
        }
    }
}
