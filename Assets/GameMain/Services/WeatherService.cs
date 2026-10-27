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
        public float FogDensity { get; private set; } = 0.003f;
        public bool IsTimeTransitioning => _timeLerpActive;

        /// <summary>
        /// Panel gesture lock, and the weather visual blend length.
        /// Preset apply clears the lock and is never refused.
        /// </summary>
        public const float PanelTransitionSeconds = 2f;

        /// <summary>0–1 wind shown on grass, ocean, and clouds. Eases while <see cref="IsWeatherTransitioning"/>.</summary>
        public float VisualWind01 { get; private set; }

        /// <summary>Boluo Wind/Rain/Foggy RTPC units (level × 30) while a weather blend is in flight.</summary>
        public float VisualRainAudio { get; private set; }

        public float VisualFoggyAudio { get; private set; }

        public bool IsWeatherTransitioning => _weatherLerpActive;

        public bool IsWeatherBusy => Time.unscaledTime < _panelBusyUntil;

        float _panelBusyUntil;

        /// <summary>开雾基系数（默认 0.003）；天气 UI 用 <see cref="WeatherFogScale"/> 乘出绝对密度。</summary>
        float _hazeFogDensity = 0.003f;

        bool _timeLerpActive;
        float _timeLerpElapsed;
        float _timeLerpFrom;
        float _timeLerpToUnwrapped;
        float _timeLerpCommitHours;
        IslandTimePeriod _timeLerpPeriod;
        IAtmosphereVisualDriver _timeLerpDriver;
        bool _lookBlendActive;
        IslandLookKind _lookBlendTo;
        bool _suppressPersist;

        bool _weatherLerpActive;
        float _weatherLerpElapsed;
        int _weatherBlendId;
        float _weatherFromFog;
        float _weatherToFog;
        float _weatherFromWind;
        float _weatherToWind;
        float _weatherFromRainAudio;
        float _weatherToRainAudio;
        float _weatherFromFoggyAudio;
        float _weatherToFoggyAudio;
        float _displayedFog;

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
            // 进岛 Procedure 会 RestoreOrApplyLocalClock；此处仅在无快照时跟墙钟。
            ApplyLocalClockIfNeeded();
            PushAll();
        }

        void Update()
        {
            TickTimeLerp();
            TickWeatherLerp();
        }

        void TickTimeLerp()
        {
            if (!_timeLerpActive)
                return;

            _timeLerpElapsed += Time.deltaTime;
            float t = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(_timeLerpElapsed / TimeSwitchLerpSeconds));
            Hours = Mathf.Repeat(Mathf.Lerp(_timeLerpFrom, _timeLerpToUnwrapped, t), 24f);
            // Look blend and hours share this t so sky, light, and ambient move together.
            if (_lookBlendActive)
                PushLookBlend(t);
            PushTimeToDriver(_timeLerpDriver);

            if (t < 1f)
                return;

            _timeLerpActive = false;
            _lookBlendActive = false;
            _timeLerpDriver = null;
            Hours = Mathf.Repeat(_timeLerpCommitHours, 24f);
            Period = _timeLerpPeriod;
            PushTime();
            if (GameEntry.Event != null)
                GameEntry.Event.Fire(this, TimeOfDayChangedEventArgs.Create(Hours, Period));
            GameEntry.AtmosphereProps?.Refresh();
            PersistAtmosphereIfSettled();
        }

        void TickWeatherLerp()
        {
            if (!_weatherLerpActive)
                return;

            // Unscaled so the blend ends with the panel lock (also unscaled).
            _weatherLerpElapsed += Time.unscaledDeltaTime;
            float t = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(_weatherLerpElapsed / PanelTransitionSeconds));
            ApplyWeatherLerp(t);
            if (t < 1f)
                return;

            _weatherLerpActive = false;
            VisualWind01 = _weatherToWind;
            VisualRainAudio = _weatherToRainAudio;
            VisualFoggyAudio = _weatherToFoggyAudio;
            _displayedFog = _weatherToFog;
            PushWeather();
            PushFogDensity();
            WeatherAudioBridge.Refresh();
            PersistAtmosphereIfSettled();
        }

        /// <summary>
        /// 进岛：有存档快照则恢复；否则墙钟 → 时段锚点，并写入首份快照。
        /// </summary>
        public void RestoreOrApplyLocalClock()
        {
            if (BoluoGameSave.TryApplyIslandAtmosphere(this))
                return;
            ApplyWallClock();
            PersistAtmosphereIfSettled();
        }

        /// <summary>切账号 / 退登：内存回到默认大气，禁止写盘（磁盘已由 persistence 清空）。</summary>
        public void ClearForAccountSwitch()
        {
            _suppressPersist = true;
            try
            {
                StopTimeLerp();
                _weatherLerpActive = false;
                ClearPanelTransitionLock();
                Kind = IslandWeatherKind.Cloud;
                Level = 1;
                Hours = 12f;
                Period = IslandTimePeriod.Noon;
                LookKind = IslandLookKind.Canonical;
                FogDensity = 0.003f;
                _hazeFogDensity = 0.003f;
                PushAll();
            }
            finally
            {
                _suppressPersist = false;
            }
        }

        /// <summary>
        /// HUD / 冷启动：已有大气快照则不动（避免覆盖恢复结果）；否则跟墙钟。
        /// </summary>
        public void ApplyLocalClockIfNeeded()
        {
            BoluoGameSave.EnsureLoaded();
            if (BoluoGameSave.HasIslandAtmosphere)
                return;
            ApplyWallClock();
        }

        void ApplyWallClock()
        {
            var now = System.DateTime.Now;
            float clockHours = now.Hour + now.Minute / 60f;
            SetPeriod(ResolvePeriod(clockHours), animate: false);
        }

        /// <summary>存档灌入（不写回盘，由调用方决定是否随后 Capture）。</summary>
        public void ApplyAtmosphereSnapshot(
            IslandWeatherKind kind,
            int level,
            float hours,
            IslandLookKind lookKind,
            float fogDensity,
            bool fireEvent = false)
        {
            _suppressPersist = true;
            try
            {
                StopTimeLerp();
                SetLookKind(lookKind);
                Hours = Mathf.Repeat(hours, 24f);
                Period = ResolvePeriod(Hours);
                PushTime();
                SetWeather(kind, level, fireEvent);
                SetFogDensity(fogDensity);
                if (fireEvent && GameEntry.Event != null)
                    GameEntry.Event.Fire(this, TimeOfDayChangedEventArgs.Create(Hours, Period));
            }
            finally
            {
                _suppressPersist = false;
            }
        }

        public void SetTimeOfDay(float hours, bool fireEvent = true)
        {
            StopTimeLerp();
            Hours = Mathf.Repeat(hours, 24f);
            Period = ResolvePeriod(Hours);
            PushTime();
            if (fireEvent && GameEntry.Event != null)
                GameEntry.Event.Fire(this, TimeOfDayChangedEventArgs.Create(Hours, Period));
            PersistAtmosphereIfSettled();
        }

        public void SetPeriod(IslandTimePeriod period) => SetPeriod(period, animate: false);

        /// <summary>
        /// Jump or blend to a period anchor (06 / 12 / 18 / 00).
        /// Animated path matches Boluo IETimeLerp: sky/light SmoothStep over <see cref="TimeSwitchLerpSeconds"/>.
        /// </summary>
        public void SetPeriod(IslandTimePeriod period, bool animate)
        {
            // Boluo GameData: dial daytime → Camera_1, night → Atmosphere 10 (decoupled from preset).
            IslandLookKind nextLook = period == IslandTimePeriod.Night
                ? IslandLookKind.NightCold
                : IslandLookKind.Canonical;

            float commitHours = PeriodToAnchorHours(period);
            if (!animate || !isActiveAndEnabled)
            {
                SetLookKind(nextLook);
                SetTimeOfDay(commitHours);
                return;
            }

            if (_timeLerpActive)
                return;

            bool lookChanges = LookKind != nextLook;
            LookKind = nextLook;
            if (lookChanges)
            {
                // Visual blend over the same 2s as Hours. t=0 keeps the current look.
                _lookBlendActive = true;
                _lookBlendTo = nextLook;
                PushLookBlend(0f);
            }
            else
            {
                // Same palette (morning/noon/evening). Pin look without persisting mid-lerp.
                _lookBlendActive = false;
                PushLookKind();
            }

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

            // Period commits now so the dial knows the next slot. The hand eases;
            // sky / light follow Hours over TimeSwitchLerpSeconds.
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

        public void SetWeather(IslandWeatherKind kind, int level, bool fireEvent = true, bool animate = false)
        {
            // Level 0 keeps the requested kind (Wind/Rain/Foggy off). Clear is the only kind forced to 0.
            int nextLevel = kind == IslandWeatherKind.Clear ? 0 : Mathf.Clamp(level, 0, 3);
            bool changed = Kind != kind || Level != nextLevel;
            if (!animate || !isActiveAndEnabled || !changed)
            {
                CommitWeather(kind, nextLevel, fireEvent);
                return;
            }

            float fromFog = _weatherLerpActive ? _displayedFog : FogDensity;
            float fromWind = VisualWind01;
            float fromRainAudio = _weatherLerpActive
                ? VisualRainAudio
                : AudioUnits(Kind, Level, IslandWeatherKind.Rain);
            float fromFoggyAudio = _weatherLerpActive
                ? VisualFoggyAudio
                : AudioUnits(Kind, Level, IslandWeatherKind.Foggy);

            Kind = kind;
            Level = nextLevel;
            FogDensity = _hazeFogDensity * WeatherFogScale(Kind, Level);
            _weatherFromFog = fromFog;
            _weatherToFog = FogDensity;
            _weatherFromWind = fromWind;
            _weatherToWind = WindFactor(Kind, Level);
            _weatherFromRainAudio = fromRainAudio;
            _weatherToRainAudio = AudioUnits(Kind, Level, IslandWeatherKind.Rain);
            _weatherFromFoggyAudio = fromFoggyAudio;
            _weatherToFoggyAudio = AudioUnits(Kind, Level, IslandWeatherKind.Foggy);
            _weatherBlendId++;
            _weatherLerpElapsed = 0f;
            _weatherLerpActive = true;
            // t = 0 keeps the current look. Kind is already the selection so the icon can update.
            ApplyWeatherLerp(0f);
            if (fireEvent && GameEntry.Event != null)
                GameEntry.Event.Fire(this, WeatherChangedEventArgs.Create(Kind, Level));
            GameEntry.AtmosphereProps?.Refresh();
        }

        void CommitWeather(IslandWeatherKind kind, int level, bool fireEvent)
        {
            _weatherLerpActive = false;
            Kind = kind;
            Level = level;
            FogDensity = _hazeFogDensity * WeatherFogScale(Kind, Level);
            _displayedFog = FogDensity;
            VisualWind01 = WindFactor(Kind, Level);
            VisualRainAudio = AudioUnits(Kind, Level, IslandWeatherKind.Rain);
            VisualFoggyAudio = AudioUnits(Kind, Level, IslandWeatherKind.Foggy);
            PushWeather();
            PushFogDensity();
            if (fireEvent && GameEntry.Event != null)
                GameEntry.Event.Fire(this, WeatherChangedEventArgs.Create(Kind, Level));
            GameEntry.AtmosphereProps?.Refresh();
            PersistAtmosphereIfSettled();
        }

        void ApplyWeatherLerp(float t)
        {
            VisualWind01 = Mathf.Lerp(_weatherFromWind, _weatherToWind, t);
            VisualRainAudio = Mathf.Lerp(_weatherFromRainAudio, _weatherToRainAudio, t);
            VisualFoggyAudio = Mathf.Lerp(_weatherFromFoggyAudio, _weatherToFoggyAudio, t);
            _displayedFog = Mathf.Lerp(_weatherFromFog, _weatherToFog, t);
            foreach (IAtmosphereVisualDriver driver in FindDrivers())
                driver.SetWeatherBlend(_weatherBlendId, Kind, Level, t);
            PushDisplayedFog();
            WeatherAudioBridge.Refresh();
        }

        static float WindFactor(IslandWeatherKind kind, int level) =>
            kind == IslandWeatherKind.Wind && level > 0 ? Mathf.Clamp(level, 0, 3) / 3f : 0f;

        /// <summary>Boluo RTPC: curWeatherState × 30. Other kinds contribute 0.</summary>
        static float AudioUnits(IslandWeatherKind kind, int level, IslandWeatherKind slot)
        {
            if (kind != slot || level <= 0)
                return 0f;
            return Mathf.Clamp(level, 0, 3) * 30f;
        }

        /// <summary>
        /// Pin absolute ExpSquared density (preset apply). Backs out haze so later weather UI can rescale.
        /// </summary>
        public void SetFogDensity(float density)
        {
            if (_weatherLerpActive)
            {
                _weatherLerpActive = false;
                VisualWind01 = WindFactor(Kind, Level);
                VisualRainAudio = AudioUnits(Kind, Level, IslandWeatherKind.Rain);
                VisualFoggyAudio = AudioUnits(Kind, Level, IslandWeatherKind.Foggy);
                PushWeather();
            }

            FogDensity = Mathf.Max(0f, density);
            float scale = WeatherFogScale(Kind, Level);
            _hazeFogDensity = scale > 1e-4f ? FogDensity / scale : FogDensity;
            _displayedFog = FogDensity;
            PushFogDensity();
            PersistAtmosphereIfSettled();
        }

        /// <summary>
        /// Multiplier on haze base (default 0.003).
        /// 关雾 / Clear → 1（0.003）；Foggy 小/中/大 → 2/3/4（0.006 / 0.009 / 0.012）。
        /// </summary>
        public static float WeatherFogScale(IslandWeatherKind kind, int level)
        {
            int lv = Mathf.Clamp(level, 0, 3);
            // Closed gear (any kind) and Clear: same as haze base.
            if (lv <= 0 || kind == IslandWeatherKind.Clear)
                return 1f;

            switch (kind)
            {
                case IslandWeatherKind.Foggy:
                    if (lv == 1) return 2f; // 小雾 0.006
                    if (lv == 2) return 3f; // 中雾 0.009
                    return 4f;              // 大雾 0.012
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
            if (_lookBlendActive)
                PushLookBlend(1f);
            _timeLerpActive = false;
            _lookBlendActive = false;
            _timeLerpDriver = null;
        }

        void PushAll()
        {
            if (!_weatherLerpActive)
            {
                VisualWind01 = WindFactor(Kind, Level);
                VisualRainAudio = AudioUnits(Kind, Level, IslandWeatherKind.Rain);
                VisualFoggyAudio = AudioUnits(Kind, Level, IslandWeatherKind.Foggy);
                _displayedFog = FogDensity;
            }

            PushLookKind();
            PushTime();
            PushWeather();
            PushFogDensity();
        }

        public void SetLookKind(IslandLookKind lookKind)
        {
            LookKind = lookKind;
            _lookBlendActive = false;
            PushLookKind();
            PersistAtmosphereIfSettled();
        }

        void PersistAtmosphereIfSettled()
        {
            if (_suppressPersist || _timeLerpActive || _weatherLerpActive)
                return;
            BoluoGameSave.CaptureIslandAtmosphere(Kind, Level, Hours, Period, LookKind, FogDensity);
        }

        void PushLookBlend(float t)
        {
            foreach (IAtmosphereVisualDriver driver in FindDrivers())
                driver.SetLookBlend(_lookBlendTo, t);
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
            if (_weatherLerpActive)
            {
                float t = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(_weatherLerpElapsed / PanelTransitionSeconds));
                foreach (IAtmosphereVisualDriver driver in FindDrivers())
                    driver.SetWeatherBlend(_weatherBlendId, Kind, Level, t);
                return;
            }

            foreach (IAtmosphereVisualDriver driver in FindDrivers())
                driver.SetWeather(Kind, Level);
        }

        void PushFogDensity()
        {
            PushDisplayedFog(_weatherLerpActive ? _displayedFog : FogDensity);
        }

        void PushDisplayedFog() => PushDisplayedFog(_displayedFog);

        void PushDisplayedFog(float density)
        {
            foreach (IAtmosphereVisualDriver driver in FindDrivers())
                driver.SetFogDensity(density);
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
