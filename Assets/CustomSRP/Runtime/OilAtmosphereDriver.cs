using Game;
using Game.Native;
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
        public const float DefaultFogDensity = 0.003f;

        static readonly int FogDensityId = Shader.PropertyToID("_FogDensity");

        IslandWeatherKind _kind = IslandWeatherKind.Cloud;
        int _level = 1;
        float _fogDensity = DefaultFogDensity;
        int _weatherBlendId = -1;
        float _displayedCloud;
        float _displayedRain;
        float _displayedFoggy;
        float _blendFromCloud;
        float _blendFromRain;
        float _blendFromFoggy;
        float _blendToCloud;
        float _blendToRain;
        float _blendToFoggy;
        bool _visualReady;
        int _syncedCelestialLookId = -1;
        bool _presetEventBound;
        bool _weatherHoldBound;

        [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.AfterSceneLoad)]
        static void AutoAttach() => EnsureOnSkybox();

        /// <summary>
        /// Attach beside OilSkyboxTime when the island world exists.
        /// Does not create an orphan host before the terrain prefab is spawned.
        /// </summary>
        public static OilAtmosphereDriver EnsureOnSkybox(OilSkyboxTime sky = null)
        {
            if (sky == null)
                sky = AtmosphereVisualRegistry.Skybox as OilSkyboxTime;
            if (sky == null)
                return AtmosphereVisualRegistry.Driver as OilAtmosphereDriver;

            OilAtmosphereDriver onSky = sky.GetComponent<OilAtmosphereDriver>();
            if (onSky != null)
            {
                onSky.ResolveRefs();
                // Terrain re-enabled / OilSkyboxTime.OnEnable: pull WeatherService before
                // the prefab noon default (timeOfDay=12) paints day into planar RT.
                onSky.SyncFromWeatherService();
                return onSky;
            }

            // Drop the orphan host created before the terrain prefab existed.
            // Destroy is deferred, so the new component's OnEnable wins the registry slot.
            if (AtmosphereVisualRegistry.Driver is OilAtmosphereDriver orphan
                && orphan != null
                && orphan.GetComponent<OilSkyboxTime>() == null)
                Destroy(orphan.gameObject);

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
                ApplyWeatherVisuals();
        }

        void OnEnable()
        {
            AtmosphereVisualRegistry.RegisterDriver(this);
            BindPresetEvent();
            BindWeatherHold();
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
            AtmosphereVisualRegistry.UnregisterDriver(this);
            UnbindWeatherHold();
            UnbindPresetEvent();
        }

        void LateUpdate()
        {
            BindPresetEvent();
            BindWeatherHold();
            SyncCelestialLookFromPreset();
        }

        void OnWeatherHoldChanged() => ApplyDisplayedWeather();

        void ResolveRefs()
        {
            if (SkyboxTime == null)
                SkyboxTime = GetComponent<OilSkyboxTime>();
            if (SkyboxTime == null)
                SkyboxTime = AtmosphereVisualRegistry.Skybox as OilSkyboxTime;
            if (CloudLayer == null)
                CloudLayer = GetComponent<OilCloudLayer>();
            if (CloudLayer == null)
                CloudLayer = OilCloudLayer.Current;
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
            _weatherBlendId = -1;
            ApplyWeatherVisuals();
        }

        public void SetWeatherBlend(int blendId, IslandWeatherKind kind, int level01To03, float t)
        {
            int level = Mathf.Clamp(level01To03, 0, 3);
            if (_weatherBlendId != blendId)
            {
                EnsureDisplayed();
                _weatherBlendId = blendId;
                _blendFromCloud = _displayedCloud;
                _blendFromRain = _displayedRain;
                _blendFromFoggy = _displayedFoggy;
                _blendToCloud = CloudCoverage(kind, level);
                _blendToRain = RainIntensity(kind, level);
                _blendToFoggy = FoggyWeight(kind, level);
            }

            _kind = kind;
            _level = level;
            t = Mathf.Clamp01(t);
            _displayedCloud = Mathf.Lerp(_blendFromCloud, _blendToCloud, t);
            _displayedRain = Mathf.Lerp(_blendFromRain, _blendToRain, t);
            _displayedFoggy = Mathf.Lerp(_blendFromFoggy, _blendToFoggy, t);
            ApplyDisplayedWeather();
        }

        public void SetFogDensity(float exponentialSquaredDensity)
        {
            _fogDensity = Mathf.Max(0f, exponentialSquaredDensity);
            ApplyFogSettings();
            Shader.SetGlobalFloat(FogDensityId, _displayedFoggy);
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

        void BindWeatherHold()
        {
            if (_weatherHoldBound)
                return;
            PresetService presets = PresetService.Instance;
            if (presets == null)
                return;
            presets.WeatherHoldChanged += OnWeatherHoldChanged;
            _weatherHoldBound = true;
        }

        void UnbindWeatherHold()
        {
            if (!_weatherHoldBound)
                return;
            if (PresetService.Instance != null)
                PresetService.Instance.WeatherHoldChanged -= OnWeatherHoldChanged;
            _weatherHoldBound = false;
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
            if (e is not PresetChangedEventArgs)
                return;

            // 展位图取样会盖掉预设的时段、天气和雾。这里按 WeatherService 已写入的预设重推。
            WeatherService weather = GameEntry.Weather;
            if (weather != null)
            {
                SetLookKind(weather.LookKind);
                SetTimeOfDayHours(weather.Hours);
                SetWeather(weather.Kind, weather.Level);
                SetFogDensity(weather.FogDensity);
                SetLightColorOverride(weather.LightColorOverride);
            }

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
            _displayedCloud = CloudCoverage(_kind, _level);
            _displayedRain = RainIntensity(_kind, _level);
            _displayedFoggy = FoggyWeight(_kind, _level);
            _visualReady = true;
            ApplyDisplayedWeather();
        }

        float CloudCoverage(IslandWeatherKind kind, int level)
        {
            float t = Mathf.Clamp(level, 0, 3) / 3f;
            if (level <= 0 || kind == IslandWeatherKind.Clear)
                return ClearCloudDensity;

            return kind switch
            {
                IslandWeatherKind.Cloud => Mathf.Lerp(0.4f, 0.7f, t),
                IslandWeatherKind.Wind => Mathf.Lerp(0.35f, 0.55f, t),
                IslandWeatherKind.Rain => Mathf.Lerp(0.55f, StormCloudDensity, t),
                IslandWeatherKind.Foggy => Mathf.Lerp(0.5f, 0.75f, t),
                _ => ClearCloudDensity,
            };
        }

        static float RainIntensity(IslandWeatherKind kind, int level)
        {
            // L1/L2/L3 → 0.4 / 0.7 / 1.0. level/3 made L1 too faint on mobile.
            if (kind != IslandWeatherKind.Rain || level <= 0)
                return 0f;
            if (level == 1)
                return 0.4f;
            if (level == 2)
                return 0.7f;
            return 1f;
        }

        static float FoggyWeight(IslandWeatherKind kind, int level)
        {
            if (kind != IslandWeatherKind.Foggy || level <= 0)
                return 0f;
            return Mathf.Clamp(level, 0, 3) / 3f;
        }

        void EnsureDisplayed()
        {
            if (_visualReady)
                return;

            _displayedCloud = CloudLayer != null ? CloudLayer.coverage : CloudCoverage(_kind, _level);
            OilRainSettings settings = OilRainSettings.Current;
            _displayedRain = settings != null ? settings.intensity : RainIntensity(_kind, _level);
            _displayedFoggy = FoggyWeight(_kind, _level);
            _visualReady = true;
        }

        void ApplyDisplayedWeather()
        {
            if (CloudLayer != null)
                CloudLayer.coverage = _displayedCloud;

            // Preset iris close / black / half-open: force dry so rain never punches through.
            float rainIntensity = PresetService.Instance != null && PresetService.Instance.IsWeatherHeld
                ? 0f
                : _displayedRain;
            OilRainSettings settings = OilRainSettings.Current;
            if (settings != null && Mathf.Abs(settings.intensity - rainIntensity) > 0.0001f)
                settings.intensity = rainIntensity;

            // Overlay GPU yield 雨天抬下限；Game 侧不引用 OilRainSettings。
            NativeOverlayGate.SetRainActiveForYield(rainIntensity > 0.01f);

            ApplyFogSettings();
            Shader.SetGlobalFloat(FogDensityId, _displayedFoggy);
        }

        void ApplyFogSettings()
        {
            OilSkyboxTime.SetFoggyBlend(_displayedFoggy);

            if (SkyboxTime == null)
                ResolveRefs();

            // WeatherService writes absolute ExpSquared density (base 0.003; Foggy
            // L1/L2/L3 → 0.006 / 0.009 / 0.012). Do not re-scale here.
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            RenderSettings.fogDensity = Mathf.Max(0f, _fogDensity);

            float period = SkyboxTime != null ? SkyboxTime.CurrentPeriod() : OilSkyboxTime.HoursToPeriod(12f);
            OilSkyboxTime.EvaluateFog(period, out Color tint);
            RenderSettings.fogColor = tint;
            SkyboxTime?.PushFogGlobals();
        }
    }
}
