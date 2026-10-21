using Game;
using GameFramework.Event;
using UnityEngine;
using UnityEngine.Rendering;

namespace CustomSRP
{
    /// <summary>
    /// Bridges Game.WeatherService to OilSkyboxTime / fog / cloud density.
    /// Lives in Assembly-CSharp so it can reference both Game and CustomSRP types.
    /// Sun/moon always follow Cozy clock orbits — never pinned to the camera.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class OilAtmosphereDriver : MonoBehaviour, IAtmosphereVisualDriver
    {
        public OilSkyboxTime SkyboxTime;
        public OilCloudLayer CloudLayer;
        [Range(0f, 1f)] public float ClearCloudDensity = 0.35f;
        [Range(0f, 1f)] public float StormCloudDensity = 0.85f;

        /// <summary>Default ExpSquared density when no preset has been applied.</summary>
        public const float DefaultFogDensity = 0.001f;

        static readonly int FogDensityId = Shader.PropertyToID("_FogDensity");

        IslandWeatherKind _kind = IslandWeatherKind.Cloud;
        int _level = 1;
        float _fogDensity = DefaultFogDensity;
        bool _presetEventBound;

        [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.AfterSceneLoad)]
        static void AutoAttach() => EnsureOnSkybox();

        /// <summary>
        /// Attach beside OilSkyboxTime when the island world exists.
        /// Does not create an orphan host before the terrain prefab is spawned.
        /// </summary>
        public static OilAtmosphereDriver EnsureOnSkybox()
        {
            OilSkyboxTime sky = FindAnyObjectByType<OilSkyboxTime>();
            if (sky == null)
                return FindAnyObjectByType<OilAtmosphereDriver>();

            OilAtmosphereDriver onSky = sky.GetComponent<OilAtmosphereDriver>();
            if (onSky != null)
            {
                onSky.ResolveRefs();
                return onSky;
            }

            // Drop orphan hosts created before the terrain prefab existed.
            OilAtmosphereDriver[] all = FindObjectsByType<OilAtmosphereDriver>();
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] != null && all[i].GetComponent<OilSkyboxTime>() == null)
                    Destroy(all[i].gameObject);
            }

            return sky.gameObject.AddComponent<OilAtmosphereDriver>();
        }

        void Awake()
        {
            ResolveRefs();
            ResolveRainSettings();
            ClearCelestialLookOverride();
            // Sync rain/cloud from default weather so a stale OilRainSettings.intensity
            // (asset default) does not leave the curtain on before WeatherService pushes.
            ApplyWeatherVisuals();
            ApplyFogSettings();
            Shader.SetGlobalFloat(FogDensityId, FoggyShaderWeight());
        }

        void OnEnable()
        {
            BindPresetEvent();
            ClearCelestialLookOverride();
            PresetWeatherHold.HeldChanged += OnWeatherHoldChanged;
        }

        void OnDisable()
        {
            PresetWeatherHold.HeldChanged -= OnWeatherHoldChanged;
            UnbindPresetEvent();
        }

        void LateUpdate()
        {
            BindPresetEvent();
        }

        void OnWeatherHoldChanged() => SetRainIntensity();

        void ResolveRefs()
        {
            if (SkyboxTime == null)
                SkyboxTime = GetComponent<OilSkyboxTime>();
            if (SkyboxTime == null)
                SkyboxTime = FindAnyObjectByType<OilSkyboxTime>();
            if (CloudLayer == null)
                CloudLayer = FindAnyObjectByType<OilCloudLayer>();
        }

        /// <summary>
        /// Find OilRainSettings via the pipeline asset and expose it as OilRainSettings.Current
        /// so all rain components (RippleManager, FlipbookDriver, RenderHook) can
        /// read it without a direct inspector reference.
        /// </summary>
        void ResolveRainSettings()
        {
            // Try pipeline asset first (always set in CustomRenderPipelineAsset).
            var pipeline = GraphicsSettings.defaultRenderPipeline as CustomRenderPipelineAsset;
            OilRainSettings settings = pipeline?.Settings?.rainSettings;

            // Fallback: find in scene if pipeline not assigned yet.
            if (settings == null)
                settings = FindAnyObjectByType<OilRainSettings>();

            OilRainSettings.Current = settings;
        }

        public void SetTimeOfDayHours(float hours)
        {
            if (SkyboxTime == null)
                ResolveRefs();
            if (SkyboxTime == null)
                return;

            SkyboxTime.ReleaseCelestialLookForClock();
            SkyboxTime.SetTimeOfDay(hours);
            // Re-scale ExpSquared density for Cozy fogDensityMultiplier at this TOD.
            ApplyFogSettings();
        }

        public void SetWeather(IslandWeatherKind kind, int level01To03)
        {
            _kind = kind;
            _level = Mathf.Clamp(level01To03, 0, 3);
            ApplyWeatherVisuals();
        }

        public void SetFogDensity(float exponentialSquaredDensity)
        {
            _fogDensity = Mathf.Max(0f, exponentialSquaredDensity);
            ApplyFogSettings();
            Shader.SetGlobalFloat(FogDensityId, FoggyShaderWeight());
        }

        public void SetLightColorOverride(Color? color)
        {
            if (SkyboxTime == null)
                ResolveRefs();
            if (SkyboxTime == null)
                return;
            SkyboxTime.SetLightColorOverride(color);
        }

        public void SetLookKind(IslandLookKind lookKind)
        {
            if (SkyboxTime == null)
                ResolveRefs();
            if (SkyboxTime == null)
                return;
            SkyboxTime.ClearColorOverride();
            SkyboxTime.SetLookId((OilSkyLookId)(int)lookKind);
            ApplyFogSettings();
        }

        void BindPresetEvent()
        {
            if (_presetEventBound || GameEntry.Event == null)
                return;

            GameEntry.Event.Subscribe(PresetChangedEventArgs.EventId, OnPresetChanged);
            _presetEventBound = true;
        }

        void UnbindPresetEvent()
        {
            if (!_presetEventBound || GameEntry.Event == null)
                return;

            GameEntry.Event.Unsubscribe(PresetChangedEventArgs.EventId, OnPresetChanged);
            _presetEventBound = false;
        }

        void OnPresetChanged(object sender, GameEventArgs e)
        {
            ClearCelestialLookOverride();
            if (e is PresetChangedEventArgs preset)
                SkyboxTime?.ApplyPresetPoster(preset.PresetId);
        }

        void ClearCelestialLookOverride()
        {
            if (SkyboxTime == null)
                ResolveRefs();
            if (SkyboxTime != null)
                SkyboxTime.ClearCelestialLookPlacement();
        }

        void ApplyWeatherVisuals()
        {
            float t = _level / 3f;
            float cloud = _level <= 0 || _kind == IslandWeatherKind.Clear
                ? ClearCloudDensity
                : _kind switch
                {
                    IslandWeatherKind.Cloud => Mathf.Lerp(0.4f, 0.7f, t),
                    IslandWeatherKind.Wind => Mathf.Lerp(0.35f, 0.55f, t),
                    IslandWeatherKind.Rain => Mathf.Lerp(0.55f, StormCloudDensity, t),
                    IslandWeatherKind.Foggy => Mathf.Lerp(0.5f, 0.75f, t),
                    _ => ClearCloudDensity,
                };

            if (CloudLayer != null)
                CloudLayer.coverage = cloud;

            // Drive rain visual intensity from weather state.
            SetRainIntensity();

            ApplyFogSettings();
            Shader.SetGlobalFloat(FogDensityId, FoggyShaderWeight());
        }

        void SetRainIntensity()
        {
            var settings = OilRainSettings.Current;
            if (settings == null) return;

            // Map weather level 0–3 → rain intensity 0–1.
            // Level 0 (Clear / Cloud) = no rain, level 1 = light, level 2 = medium, level 3 = heavy.
            float rainIntensity = _kind == IslandWeatherKind.Rain ? (_level / 3f) : 0f;
            // Preset iris close / black / half-open: force dry so rain never punches through.
            if (PresetWeatherHold.IsHeld)
                rainIntensity = 0f;
            if (Mathf.Abs(settings.intensity - rainIntensity) > 0.001f)
            {
                settings.intensity = rainIntensity;
            }
        }

        void ApplyFogSettings()
        {
            float foggyT = FoggyShaderWeight();
            OilSkyboxTime.SetFoggyBlend(foggyT);

            if (SkyboxTime == null)
                ResolveRefs();

            // Note: TOD fog-density scaling (Cozy fogDensityMultiplier) is intentionally
            // removed — the weather panel writes an absolute ExpSquared density
            // (WeatherService.FogDensity) and the user-facing knob is now authoritative.
            // We still lift the haze during Foggy weather
            // (Boluo Foggy dens 2→5 / mul 1.53 ≈ up to ~1.7×).
            float foggyScale = foggyT > 0.001f ? Mathf.Lerp(1f, 1.7f, foggyT) : 1f;

            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            RenderSettings.fogDensity = Mathf.Max(0f, _fogDensity * foggyScale);

            float period = SkyboxTime != null ? SkyboxTime.CurrentPeriod() : OilSkyboxTime.HoursToPeriod(12f);
            OilSkyboxTime.EvaluateFog(period, out Color tint);
            RenderSettings.fogColor = tint;
            SkyboxTime?.PushFogGlobals();
        }

        float FoggyShaderWeight()
        {
            if (_kind != IslandWeatherKind.Foggy || _level <= 0)
                return 0f;
            return _level / 3f;
        }
    }
}
