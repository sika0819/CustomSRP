using Game;
using GameFramework.Event;
using UnityEngine;
using UnityEngine.Rendering;

namespace CustomSRP
{
    /// <summary>
    /// Bridges Game.WeatherService to OilSkyboxTime / fog / cloud density.
    /// Lives in Assembly-CSharp so it can reference both Game and CustomSRP types.
    /// Dawn/sleep presets (1 / 4 / 6 / 10) pin sun/moon in view once on apply;
    /// time-dial changes release the pin so discs follow the clock (BoluoProject).
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
        int _syncedCelestialLookId = -1;
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
                // Terrain re-enabled / OilSkyboxTime.OnEnable: pull WeatherService before
                // the prefab noon default (timeOfDay=12) paints day into planar RT.
                onSky.SyncFromWeatherService();
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
            // Prefer WeatherService clock/look/weather before local defaults. IslandTerrain
            // serializes timeOfDay=12; without this, OnEnable Apply paints noon sun + day-blue
            // water reflection until ProcedureIsland.PushVisuals (one-frame cyan flash).
            if (!SyncFromWeatherService())
            {
                ApplyWeatherVisuals();
                ApplyFogSettings();
                Shader.SetGlobalFloat(FogDensityId, FoggyShaderWeight());
            }
        }

        void OnEnable()
        {
            BindPresetEvent();
            PresetWeatherHold.HeldChanged += OnWeatherHoldChanged;
            SyncFromWeatherService();
        }

        /// <summary>
        /// Push WeatherService state into OilSkyboxTime / rain / fog before the first
        /// camera frame. Returns false when WeatherService is not up yet.
        /// </summary>
        bool SyncFromWeatherService()
        {
            WeatherService weather = GameEntry.Weather;
            if (weather == null)
                return false;

            weather.PushVisuals();
            return true;
        }

        void OnDisable()
        {
            PresetWeatherHold.HeldChanged -= OnWeatherHoldChanged;
            UnbindPresetEvent();
        }

        void LateUpdate()
        {
            BindPresetEvent();
            SyncCelestialLookFromPreset();
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

            // BoluoProject: sunTransform.localEuler follows dayPercentage during IETimeLerp.
            // Skip PlaceSun/PlaceMoon while time blending — pins would cover the lerp.
            // Keep _syncedCelestialLookId so LateUpdate does not immediately re-pin.
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

        public void SetLookBlend(IslandLookKind to, float t)
        {
            if (SkyboxTime == null)
                ResolveRefs();
            if (SkyboxTime == null)
                return;
            // Capture blend before clearing a poster pin so that Apply stays on the blended table.
            SkyboxTime.SetLookBlend((OilSkyLookId)(int)to, t);
            SkyboxTime.ClearColorOverride();
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
            if (e is PresetChangedEventArgs preset)
                SkyboxTime?.ApplyPresetPoster(preset.PresetId);
            // Force re-pin dawn/sleep looks even when CurrentId is unchanged (re-apply same preset).
            _syncedCelestialLookId = -1;
            SyncCelestialLookFromPreset();
        }

        void SyncCelestialLookFromPreset()
        {
            if (SkyboxTime == null)
                ResolveRefs();
            if (SkyboxTime == null)
                return;

            int lookId = GameEntry.Presets != null ? GameEntry.Presets.CurrentId : 0;
            if (lookId == _syncedCelestialLookId)
                return;

            _syncedCelestialLookId = lookId;
            ApplyCelestialLook(lookId);
        }

        void ApplyCelestialLook(int lookId)
        {
            bool dawn = lookId == 1;
            bool sleep = lookId == 4 || lookId == 6 || lookId == 10;
            if (!dawn && !sleep)
            {
                SkyboxTime.ClearCelestialLookPlacement();
                return;
            }

            Vector3 camFwd = ResolveCameraForward();
            float period = SkyboxTime.CurrentPeriod();
            Vector3 orbitSun = OilSkyboxTime.SunDirectionAt(period);
            Vector3 orbitMoon = OilSkyboxTime.MoonDirectionAt(period);

            if (dawn)
            {
                OilSkyboxTime.PlaceSunForView(
                    camFwd, OilSkyboxTime.DawnSunElevationDeg, out Vector3 sunDir, out Vector3 moonDir);
                SkyboxTime.SetCelestialLookPlacement(lookId, sunDir, moonDir);
                return;
            }

            OilSkyboxTime.PlaceMoonForView(
                camFwd,
                OilSkyboxTime.SleepMoonElevationDeg,
                orbitSun,
                orbitMoon,
                out Vector3 sleepSun,
                out Vector3 sleepMoon);
            SkyboxTime.SetCelestialLookPlacement(lookId, sleepSun, sleepMoon);
        }

        static Vector3 ResolveCameraForward()
        {
            IslandJoystickCamera joy = IslandJoystickCamera.Current;
            if (joy != null)
                return joy.transform.forward;

            Camera cam = Camera.main;
            if (cam != null)
                return cam.transform.forward;

            return Vector3.forward;
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

            // Map weather level → rain intensity (L1/L2/L3 → 0.4 / 0.7 / 1.0).
            // level/3 made L1 too faint on mobile quality scale.
            float rainIntensity = 0f;
            if (_kind == IslandWeatherKind.Rain && _level > 0)
            {
                rainIntensity = _level switch
                {
                    1 => 0.4f,
                    2 => 0.7f,
                    _ => 1f,
                };
            }
            // Preset iris close / black / half-open: force dry so rain never punches through.
            if (PresetWeatherHold.IsHeld)
                rainIntensity = 0f;
            if (Mathf.Abs(settings.intensity - rainIntensity) > 0.001f)
                settings.intensity = rainIntensity;
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
