using Game;
using GameFramework.Event;
using UnityEngine;

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
                return onSky;
            }

            // Drop orphan hosts created before the terrain prefab existed.
            OilAtmosphereDriver[] all = FindObjectsByType<OilAtmosphereDriver>(FindObjectsSortMode.None);
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
            ApplyFogSettings();
            Shader.SetGlobalFloat(FogDensityId, FoggyShaderWeight());
        }

        void OnEnable()
        {
            BindPresetEvent();
        }

        void OnDisable()
        {
            UnbindPresetEvent();
        }

        void LateUpdate()
        {
            BindPresetEvent();
            SyncCelestialLookFromPreset();
        }

        void ResolveRefs()
        {
            if (SkyboxTime == null)
                SkyboxTime = GetComponent<OilSkyboxTime>();
            if (SkyboxTime == null)
                SkyboxTime = FindAnyObjectByType<OilSkyboxTime>();
            if (CloudLayer == null)
                CloudLayer = FindAnyObjectByType<OilCloudLayer>();
        }

        public void SetTimeOfDayHours(float hours)
        {
            if (SkyboxTime == null)
                ResolveRefs();
            if (SkyboxTime == null)
                return;

            // BoluoProject: sunTransform.localEuler follows dayPercentage during IETimeLerp.
            // BoluoUnity: skip PlaceSun/PlaceMoon while time blending — pins would cover the lerp.
            // Keep _syncedCelestialLookId so LateUpdate does not immediately re-pin.
            SkyboxTime.ReleaseCelestialLookForClock();
            SkyboxTime.SetTimeOfDay(hours);
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
            float cloud = _kind switch
            {
                IslandWeatherKind.Clear => ClearCloudDensity,
                IslandWeatherKind.Cloud => Mathf.Lerp(0.4f, 0.7f, t),
                IslandWeatherKind.Wind => Mathf.Lerp(0.35f, 0.55f, t),
                IslandWeatherKind.Rain => Mathf.Lerp(0.55f, StormCloudDensity, t),
                IslandWeatherKind.Foggy => Mathf.Lerp(0.5f, 0.75f, t),
                _ => ClearCloudDensity,
            };

            if (CloudLayer != null)
                CloudLayer.coverage = cloud;

            ApplyFogSettings();
            Shader.SetGlobalFloat(FogDensityId, FoggyShaderWeight());
        }

        void ApplyFogSettings()
        {
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            RenderSettings.fogDensity = Mathf.Max(0f, _fogDensity);

            if (SkyboxTime == null)
                ResolveRefs();
            if (SkyboxTime != null)
            {
                OilSkyboxTime.EvaluateFog(SkyboxTime.CurrentPeriod(), out Color tint);
                RenderSettings.fogColor = tint;
            }
        }

        float FoggyShaderWeight()
        {
            if (_kind != IslandWeatherKind.Foggy || _level <= 0)
                return 0f;
            return _level / 3f;
        }
    }
}
