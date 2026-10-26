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
        /// <summary>0 = 关雾档（启动默认），与 Clear 同为 haze×1 → 0.003。</summary>
        public int Level { get; private set; } = 0;
        /// <summary>Active Boluo AtmosphereProfile look (sky / light / fog pigments).</summary>
        public IslandLookKind LookKind { get; private set; } = IslandLookKind.Canonical;
        /// <summary>Absolute ExpSquared density pushed to <see cref="IAtmosphereVisualDriver"/>.</summary>
        public float FogDensity { get; private set; } = 0.003f;
        public bool IsTimeTransitioning => _timeLerpActive;
        public bool IsWeatherTransitioning => _weatherLerpActive;

        /// <summary>
        /// Continuous wind weight 0–1 (Level/3 while Kind is Wind). Lerps during weather transitions
        /// so grass / ocean / clouds ramp instead of snapping with <see cref="Kind"/>.
        /// </summary>
        public float VisualWind01 { get; private set; }
        /// <summary>Continuous rain weight 0–1 (Level/3 while Kind is Rain). Audio RTPC uses ×90.</summary>
        public float VisualRain01 { get; private set; }
        /// <summary>Continuous foggy weight 0–1 (Level/3 while Kind is Foggy). Audio RTPC uses ×90.</summary>
        public float VisualFoggy01 { get; private set; }

        /// <summary>Panel gesture lock / weather visual lerp duration (matches time dial).</summary>
        public const float PanelTransitionSeconds = 2f;

        public bool IsWeatherBusy => Time.unscaledTime < _panelBusyUntil || _weatherLerpActive;

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
        IslandWeatherKind _weatherLerpFromKind;
        int _weatherLerpFromLevel;
        float _weatherLerpFromFog;
        float _weatherLerpToFog;

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
            ApplyVisualWeights(Kind, Level);
            PushAll();
        }

        void Update()
        {
            if (_weatherLerpActive)
                TickWeatherLerp();

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
            _weatherLerpElapsed += Time.deltaTime;
            float t = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(_weatherLerpElapsed / PanelTransitionSeconds));
            FogDensity = Mathf.Lerp(_weatherLerpFromFog, _weatherLerpToFog, t);
            ApplyVisualWeightsLerp(
                _weatherLerpFromKind, _weatherLerpFromLevel,
                Kind, Level, t);
            PushFogDensity();
            PushWeatherBlend(t);
            WeatherAudioBridge.Refresh();

            if (t < 1f)
                return;

            _weatherLerpActive = false;
            FogDensity = _weatherLerpToFog;
            ApplyVisualWeights(Kind, Level);
            PushWeather();
            PushFogDensity();
            WeatherAudioBridge.Refresh();
            GameEntry.AtmosphereProps?.Refresh();
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
                StopWeatherLerp(commitTarget: false);
                ClearPanelTransitionLock();
                Kind = IslandWeatherKind.Cloud;
                Level = 0;
                Hours = 12f;
                Period = IslandTimePeriod.Noon;
                LookKind = IslandLookKind.Canonical;
                FogDensity = 0.003f;
                _hazeFogDensity = 0.003f;
                ApplyVisualWeights(Kind, Level);
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
                StopWeatherLerp(commitTarget: false);
                SetLookKind(lookKind);
                Hours = Mathf.Repeat(hours, 24f);
                Period = ResolvePeriod(Hours);
                PushTime();
                SetWeather(kind, level, fireEvent, animate: false);
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
            => SetWeather(kind, level, fireEvent, animate: false);

        /// <summary>
        /// Jump or SmoothStep-blend weather over <see cref="PanelTransitionSeconds"/>.
        /// Logical <see cref="Kind"/>/<see cref="Level"/> snap immediately (HUD / props);
        /// fog density and visual weights lerp when <paramref name="animate"/> is true.
        /// </summary>
        public void SetWeather(IslandWeatherKind kind, int level, bool fireEvent, bool animate)
        {
            // Level 0 keeps the requested kind (Wind/Rain/Foggy off). Clear is the only kind forced to 0.
            IslandWeatherKind nextKind = kind;
            int nextLevel = kind == IslandWeatherKind.Clear ? 0 : Mathf.Clamp(level, 0, 3);
            float nextFog = _hazeFogDensity * WeatherFogScale(nextKind, nextLevel);

            if (animate && isActiveAndEnabled)
            {
                IslandWeatherKind fromKind = Kind;
                int fromLevel = Level;
                float fromFog = FogDensity;

                Kind = nextKind;
                Level = nextLevel;

                if (fromKind == Kind && fromLevel == Level && Mathf.Abs(fromFog - nextFog) < 1e-6f)
                {
                    _weatherLerpActive = false;
                    ApplyVisualWeights(Kind, Level);
                    PushWeather();
                    PushFogDensity();
                    if (fireEvent && GameEntry.Event != null)
                        GameEntry.Event.Fire(this, WeatherChangedEventArgs.Create(Kind, Level));
                    GameEntry.AtmosphereProps?.Refresh();
                    PersistAtmosphereIfSettled();
                    return;
                }

                _weatherLerpActive = true;
                _weatherLerpElapsed = 0f;
                _weatherLerpFromKind = fromKind;
                _weatherLerpFromLevel = fromLevel;
                _weatherLerpFromFog = fromFog;
                _weatherLerpToFog = nextFog;
                BeginPanelTransitionLock();

                ApplyVisualWeightsLerp(fromKind, fromLevel, Kind, Level, 0f);
                FogDensity = fromFog;
                PushFogDensity();
                PushWeatherBlend(0f);

                if (fireEvent && GameEntry.Event != null)
                    GameEntry.Event.Fire(this, WeatherChangedEventArgs.Create(Kind, Level));
                // Props follow logical Kind immediately (campfire / sunbeam); visuals still lerp.
                GameEntry.AtmosphereProps?.Refresh();
                return;
            }

            StopWeatherLerp(commitTarget: false);
            Kind = nextKind;
            Level = nextLevel;
            FogDensity = nextFog;
            ApplyVisualWeights(Kind, Level);
            PushWeather();
            PushFogDensity();
            if (fireEvent && GameEntry.Event != null)
                GameEntry.Event.Fire(this, WeatherChangedEventArgs.Create(Kind, Level));
            GameEntry.AtmosphereProps?.Refresh();
            PersistAtmosphereIfSettled();
        }

        /// <summary>
        /// Pin absolute ExpSquared density (preset apply). Backs out haze so later weather UI can rescale.
        /// </summary>
        public void SetFogDensity(float density)
        {
            // Absolute pin wins over an in-flight weather fog lerp (preset apply order).
            if (_weatherLerpActive)
            {
                _weatherLerpActive = false;
                ApplyVisualWeights(Kind, Level);
                PushWeather();
            }

            FogDensity = Mathf.Max(0f, density);
            float scale = WeatherFogScale(Kind, Level);
            _hazeFogDensity = scale > 1e-4f ? FogDensity / scale : FogDensity;
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

        void StopWeatherLerp(bool commitTarget)
        {
            if (!_weatherLerpActive)
                return;

            _weatherLerpActive = false;
            if (commitTarget)
            {
                FogDensity = _weatherLerpToFog;
                ApplyVisualWeights(Kind, Level);
                PushWeather();
                PushFogDensity();
            }
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

        void ApplyVisualWeights(IslandWeatherKind kind, int level)
        {
            VisualWind01 = ChannelWeight(kind, level, IslandWeatherKind.Wind);
            VisualRain01 = ChannelWeight(kind, level, IslandWeatherKind.Rain);
            VisualFoggy01 = ChannelWeight(kind, level, IslandWeatherKind.Foggy);
        }

        void ApplyVisualWeightsLerp(
            IslandWeatherKind fromKind, int fromLevel,
            IslandWeatherKind toKind, int toLevel,
            float t)
        {
            VisualWind01 = Mathf.Lerp(
                ChannelWeight(fromKind, fromLevel, IslandWeatherKind.Wind),
                ChannelWeight(toKind, toLevel, IslandWeatherKind.Wind), t);
            VisualRain01 = Mathf.Lerp(
                ChannelWeight(fromKind, fromLevel, IslandWeatherKind.Rain),
                ChannelWeight(toKind, toLevel, IslandWeatherKind.Rain), t);
            VisualFoggy01 = Mathf.Lerp(
                ChannelWeight(fromKind, fromLevel, IslandWeatherKind.Foggy),
                ChannelWeight(toKind, toLevel, IslandWeatherKind.Foggy), t);
        }

        static float ChannelWeight(IslandWeatherKind kind, int level, IslandWeatherKind channel)
        {
            if (kind != channel || level <= 0)
                return 0f;
            return Mathf.Clamp(level, 0, 3) / 3f;
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
            foreach (IAtmosphereVisualDriver driver in FindDrivers())
                driver.SetWeather(Kind, Level);
        }

        void PushWeatherBlend(float t)
        {
            foreach (IAtmosphereVisualDriver driver in FindDrivers())
            {
                driver.SetWeatherBlend(
                    _weatherLerpFromKind, _weatherLerpFromLevel,
                    Kind, Level, t);
            }
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
