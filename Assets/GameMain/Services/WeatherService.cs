using UnityEngine;

namespace Game
{
    /// <summary>
    /// Owns island time-of-day and weather state. Visuals are pushed to <see cref="IAtmosphereVisualDriver"/>.
    /// </summary>
    public sealed class WeatherService : MonoBehaviour
    {
        public const int MorningMinutes = 6 * 60;
        public const int NoonMinutes = 12 * 60;
        public const int EveningMinutes = 18 * 60;
        public const int NightMinutes = 21 * 60;

        /// <summary>Boluo HudTimeDialController.TimeSwitchLerpDuration.</summary>
        public const float TimeSwitchLerpSeconds = 2f;

        public float Hours { get; private set; } = 12f;
        public IslandTimePeriod Period { get; private set; } = IslandTimePeriod.Noon;
        public IslandWeatherKind Kind { get; private set; } = IslandWeatherKind.Cloud;
        public int Level { get; private set; } = 1;
        /// <summary>Active Boluo AtmosphereProfile look (sky / light / fog pigments).</summary>
        public IslandLookKind LookKind { get; private set; } = IslandLookKind.Canonical;
        /// <summary>Absolute ExpSquared density pushed to <see cref="IAtmosphereVisualDriver"/>.</summary>
        public float FogDensity { get; private set; } = 0.001f;
        public bool IsTimeTransitioning => _timeLerpActive;

        /// <summary>Panel gesture lock. Preset apply clears it and is never refused.</summary>
        public const float PanelTransitionSeconds = 2f;

        public bool IsWeatherBusy => Time.unscaledTime < _panelBusyUntil;

        float _panelBusyUntil;

        /// <summary>Cloud/L1 haze (default 0.001); weather UI scales absolute <see cref="FogDensity"/> from this.</summary>
        float _hazeFogDensity = 0.001f;

        bool _timeLerpActive;
        float _timeLerpElapsed;
        float _timeLerpFrom;
        float _timeLerpToUnwrapped;
        float _timeLerpCommitHours;
        IslandTimePeriod _timeLerpPeriod;
        IAtmosphereVisualDriver _timeLerpDriver;

        public static WeatherService Ensure()
        {
            WeatherService existing = FindAnyObjectByType<WeatherService>();
            if (existing != null)
                return existing;

            var go = new GameObject("WeatherService");
            DontDestroyOnLoad(go);
            return go.AddComponent<WeatherService>();
        }

        void Awake()
        {
            ApplyLocalClockIfNeeded();
            PushAll();
        }

        void Update()
        {
            if (!_timeLerpActive)
                return;

            _timeLerpElapsed += Time.deltaTime;
            float t = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(_timeLerpElapsed / TimeSwitchLerpSeconds));
            Hours = Mathf.Repeat(Mathf.Lerp(_timeLerpFrom, _timeLerpToUnwrapped, t), 24f);
            PushTimeToDriver(_timeLerpDriver);

            if (t < 1f)
                return;

            _timeLerpActive = false;
            _timeLerpDriver = null;
            Hours = Mathf.Repeat(_timeLerpCommitHours, 24f);
            Period = _timeLerpPeriod;
            PushTime();
            if (GameEntry.Event != null)
                GameEntry.Event.Fire(this, TimeOfDayChangedEventArgs.Create(Hours, Period));
            GameEntry.AtmosphereProps?.Refresh();
        }

        /// <summary>
        /// Cold-start / HUD open: wall clock → <see cref="IslandTimePeriod"/> → period anchor
        /// (06 / 12 / 18 / 00). Matches Boluo InitClockHandFromTimeType so sky, main-light
        /// intensity, and fog tint use that slot's presets rather than mid-slot interpolation.
        /// </summary>
        public void ApplyLocalClockIfNeeded()
        {
            var now = System.DateTime.Now;
            float clockHours = now.Hour + now.Minute / 60f;
            SetPeriod(ResolvePeriod(clockHours), animate: false);
        }

        public void SetTimeOfDay(float hours, bool fireEvent = true)
        {
            StopTimeLerp();
            Hours = Mathf.Repeat(hours, 24f);
            Period = ResolvePeriod(Hours);
            PushTime();
            if (fireEvent && GameEntry.Event != null)
                GameEntry.Event.Fire(this, TimeOfDayChangedEventArgs.Create(Hours, Period));
        }

        public void SetPeriod(IslandTimePeriod period) => SetPeriod(period, animate: false);

        /// <summary>
        /// Jump or blend to a period anchor (06 / 12 / 18 / 00).
        /// Animated path matches Boluo IETimeLerp: sky/light SmoothStep over <see cref="TimeSwitchLerpSeconds"/>.
        /// </summary>
        public void SetPeriod(IslandTimePeriod period, bool animate)
        {
            // Boluo GameData: dial daytime → Camera_1, night → Atmosphere 10 (decoupled from preset).
            SetLookKind(period == IslandTimePeriod.Night
                ? IslandLookKind.NightCold
                : IslandLookKind.Canonical);

            float commitHours = PeriodToAnchorHours(period);
            if (!animate || !isActiveAndEnabled)
            {
                SetTimeOfDay(commitHours);
                return;
            }

            if (_timeLerpActive)
                return;

            float from = Hours;
            float toUnwrapped = commitHours;
            // Forward slot cycle: Evening → Night crosses midnight (18 → 24 → commit 0).
            if (period == IslandTimePeriod.Night && from > 0.01f)
                toUnwrapped = 24f;
            else if (toUnwrapped + 0.01f < from)
                toUnwrapped += 24f;

            _timeLerpActive = true;
            _timeLerpElapsed = 0f;
            _timeLerpFrom = from;
            _timeLerpToUnwrapped = toUnwrapped;
            _timeLerpCommitHours = commitHours;
            _timeLerpPeriod = period;
            _timeLerpDriver = FindFirstDriver();

            // Dial / toast snap immediately; sky lerps via Hours.
            Period = period;
            if (GameEntry.Event != null)
                GameEntry.Event.Fire(this, TimeOfDayChangedEventArgs.Create(Hours, Period));
        }

        /// <summary>Starts the panel lock. Does not change weather; call after a successful panel write.</summary>
        public void BeginPanelTransitionLock()
        {
            _panelBusyUntil = Time.unscaledTime + PanelTransitionSeconds;
        }

        /// <summary>Preset / health apply must not stay blocked by a panel transition.</summary>
        public void ClearPanelTransitionLock()
        {
            _panelBusyUntil = 0f;
        }

        public void SetWeather(IslandWeatherKind kind, int level, bool fireEvent = true)
        {
            // Level 0 keeps the requested kind (Wind/Rain/Foggy off). Clear is the only kind forced to 0.
            Kind = kind;
            Level = kind == IslandWeatherKind.Clear ? 0 : Mathf.Clamp(level, 0, 3);
            FogDensity = _hazeFogDensity * WeatherFogScale(Kind, Level);
            PushWeather();
            PushFogDensity();
            if (fireEvent && GameEntry.Event != null)
                GameEntry.Event.Fire(this, WeatherChangedEventArgs.Create(Kind, Level));
            GameEntry.AtmosphereProps?.Refresh();
        }

        /// <summary>
        /// Pin absolute ExpSquared density (preset apply). Backs out haze so later weather UI can rescale.
        /// </summary>
        public void SetFogDensity(float density)
        {
            FogDensity = Mathf.Max(0f, density);
            float scale = WeatherFogScale(Kind, Level);
            _hazeFogDensity = scale > 1e-4f ? FogDensity / scale : FogDensity;
            PushFogDensity();
        }

        /// <summary>
        /// Multiplier on haze base (default 0.001). Foggy L1/L2/L3 → 0.0016 / 0.0019 / 0.0022.
        /// </summary>
        public static float WeatherFogScale(IslandWeatherKind kind, int level)
        {
            int lv = Mathf.Clamp(level, 0, 3);
            // Closed gear (any kind) and Clear share the clear-sky haze.
            if (lv <= 0 || kind == IslandWeatherKind.Clear)
                return 0.85f;

            switch (kind)
            {
                case IslandWeatherKind.Foggy:
                    if (lv == 1) return 1.6f; // 小雾
                    if (lv == 2) return 1.9f; // 中雾
                    return 2.2f;              // 大雾
                case IslandWeatherKind.Rain:
                    if (lv == 1) return 1.2f;
                    if (lv == 2) return 1.35f;
                    return 1.5f;
                case IslandWeatherKind.Wind:
                    return 1.05f;
                default:
                    return 1f;
            }
        }

        public static float PeriodToAnchorHours(IslandTimePeriod period) => period switch
        {
            IslandTimePeriod.Morning => 6f,
            IslandTimePeriod.Noon => 12f,
            IslandTimePeriod.Evening => 18f,
            IslandTimePeriod.Night => 0f,
            _ => 12f,
        };

        public static IslandTimePeriod ResolvePeriod(float hours)
        {
            int minutes = Mathf.FloorToInt(Mathf.Repeat(hours, 24f) * 60f) % (24 * 60);
            if (minutes >= NightMinutes || minutes < MorningMinutes)
                return IslandTimePeriod.Night;
            if (minutes < NoonMinutes)
                return IslandTimePeriod.Morning;
            if (minutes < EveningMinutes)
                return IslandTimePeriod.Noon;
            return IslandTimePeriod.Evening;
        }

        /// <summary>
        /// Sector art points left at 0°. Only four snaps (Boluo shijianzhizhen):
        /// left morning, up noon, right evening, down night.
        /// </summary>
        public float DialAngleDegrees => PeriodToDialDegrees(Period);

        public static float PeriodToDialDegrees(IslandTimePeriod period) => period switch
        {
            IslandTimePeriod.Morning => 0f,
            IslandTimePeriod.Noon => 90f,
            IslandTimePeriod.Evening => 180f,
            IslandTimePeriod.Night => 270f,
            _ => 90f,
        };

        /// <summary>Re-push time/weather after the island world (and OilAtmosphereDriver) exists.</summary>
        public void PushVisuals() => PushAll();

        void StopTimeLerp()
        {
            _timeLerpActive = false;
            _timeLerpDriver = null;
        }

        void PushAll()
        {
            PushLookKind();
            PushTime();
            PushWeather();
            PushFogDensity();
        }

        public void SetLookKind(IslandLookKind lookKind)
        {
            LookKind = lookKind;
            PushLookKind();
        }

        void PushLookKind()
        {
            foreach (IAtmosphereVisualDriver driver in FindDrivers())
                driver.SetLookKind(LookKind);
        }

        void PushTime()
        {
            foreach (IAtmosphereVisualDriver driver in FindDrivers())
                driver.SetTimeOfDayHours(Hours);
        }

        void PushTimeToDriver(IAtmosphereVisualDriver driver)
        {
            if (driver == null || (driver is Object uo && uo == null))
            {
                _timeLerpDriver = FindFirstDriver();
                driver = _timeLerpDriver;
            }

            driver?.SetTimeOfDayHours(Hours);
        }

        void PushWeather()
        {
            foreach (IAtmosphereVisualDriver driver in FindDrivers())
                driver.SetWeather(Kind, Level);
        }

        void PushFogDensity()
        {
            foreach (IAtmosphereVisualDriver driver in FindDrivers())
                driver.SetFogDensity(FogDensity);
        }

        Color? _lightColorOverride;

        public void SetLightColorOverride(Color? color)
        {
            _lightColorOverride = color;
            PushLightColorOverride();
        }

        void PushLightColorOverride()
        {
            foreach (IAtmosphereVisualDriver driver in FindDrivers())
                driver.SetLightColorOverride(_lightColorOverride);
        }

        static readonly System.Collections.Generic.List<IAtmosphereVisualDriver> DriverCache =
            new System.Collections.Generic.List<IAtmosphereVisualDriver>(4);
        static float _driverCacheTime = -1f;

        static IAtmosphereVisualDriver FindFirstDriver()
        {
            var drivers = FindDrivers();
            return drivers.Count > 0 ? drivers[0] : null;
        }

        static System.Collections.Generic.List<IAtmosphereVisualDriver> FindDrivers()
        {
            // Avoid FindObjectsByType every PushVisuals call (island HUD / weather).
            if (DriverCache.Count > 0 && Time.unscaledTime - _driverCacheTime < 2f)
            {
                for (int i = DriverCache.Count - 1; i >= 0; i--)
                {
                    if (DriverCache[i] is Object uo && uo == null)
                        DriverCache.RemoveAt(i);
                }

                if (DriverCache.Count > 0)
                    return DriverCache;
            }

            DriverCache.Clear();
            MonoBehaviour[] behaviours = FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include);
            for (int i = 0; i < behaviours.Length; i++)
            {
                if (behaviours[i] is IAtmosphereVisualDriver driver)
                    DriverCache.Add(driver);
            }

            _driverCacheTime = Time.unscaledTime;
            return DriverCache;
        }
    }
}
