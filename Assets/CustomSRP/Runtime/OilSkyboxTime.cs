using UnityEngine;
using UnityEngine.Rendering;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace CustomSRP
{
    /// <summary>
    /// Oil skybox clock. timeOfDay is 0–24 hours.
    /// Internal period 0 dawn / 1 day / 2 dusk / 3 night maps to 06 / 12 / 18 / 00.
    /// Sky colors are mixed oil pigments at the same four anchors; the shader blends them from _Period.
    /// Sun/moon discs use Boluo Cozy celestial orbits (sunDirection=308).
    /// Parallel light color/intensity lerps the active OilSkyLookTables; direction follows the disc.
    /// Edit mode: sky + ambient apply immediately; sun is deferred (same-GO Transform + IMGUI recursion).
    /// </summary>
    [DisallowMultipleComponent]
    public class OilSkyboxTime : MonoBehaviour, ISerializationCallbackReceiver
    {
        public struct SkyLookPreset
        {
            public Color zenith;
            public Color mid;
            public Color horizon;
            public Color ground;

            public float sunSize;
            public float sunIntensity;
            public float sunGlow;
            public float moonSize;
            public float moonIntensity;
            public float moonGlow;
            public float starIntensity;

        /// <summary>Canonical Camera_1 dawn — kept for editor / bake tooling.</summary>
        public static SkyLookPreset Dawn => OilSkyLookTables.SkyLookAt(OilSkyLookId.Canonical, 0);
        public static SkyLookPreset Day => OilSkyLookTables.SkyLookAt(OilSkyLookId.Canonical, 1);
        public static SkyLookPreset Dusk => OilSkyLookTables.SkyLookAt(OilSkyLookId.Canonical, 2);
        public static SkyLookPreset Night => OilSkyLookTables.SkyLookAt(OilSkyLookId.Canonical, 3);

            public static SkyLookPreset Lerp(SkyLookPreset a, SkyLookPreset b, float t)
            {
                t = Mathf.Clamp01(t);
                return new SkyLookPreset
                {
                    zenith = Color.Lerp(a.zenith, b.zenith, t),
                    mid = Color.Lerp(a.mid, b.mid, t),
                    horizon = Color.Lerp(a.horizon, b.horizon, t),
                    ground = Color.Lerp(a.ground, b.ground, t),
                    sunSize = Mathf.Lerp(a.sunSize, b.sunSize, t),
                    sunIntensity = Mathf.Lerp(a.sunIntensity, b.sunIntensity, t),
                    sunGlow = Mathf.Lerp(a.sunGlow, b.sunGlow, t),
                    moonSize = Mathf.Lerp(a.moonSize, b.moonSize, t),
                    moonIntensity = Mathf.Lerp(a.moonIntensity, b.moonIntensity, t),
                    moonGlow = Mathf.Lerp(a.moonGlow, b.moonGlow, t),
                    starIntensity = Mathf.Lerp(a.starIntensity, b.starIntensity, t),
                };
            }
        }

        // Boluo Atmosphere default yaw (Camera_1 / Profile 10). Cozy orbit parent Euler Y.
        public const float DefaultSunDirectionDeg = 308f;
        public const float DefaultSunPitchDeg = 0f;
        public const float DawnSunElevationDeg = 7f;
        public const float SleepMoonElevationDeg = 12f;

        /// <summary>Boluo NightDistanceFog #36363C — Canonical night fog (NightCold may differ).</summary>
        public static readonly Color NightFogColor = new Color(0x36 / 255f, 0x36 / 255f, 0x3C / 255f, 1f);

        /// <summary>Active AtmosphereProfile-equivalent look (Boluo Camera_1/5/3/7/8/10).</summary>
        public OilSkyLookId ActiveLookId { get; private set; } = OilSkyLookId.Canonical;

        static OilSkyLookId s_activeLookId = OilSkyLookId.Canonical;
        static float s_clockHours = 12f;
        static OilSkyLookId s_blendFrom = OilSkyLookId.Canonical;
        static OilSkyLookId s_blendTo = OilSkyLookId.Canonical;
        static float s_blendT = 1f;
        static bool s_lookBlending;

        static bool LookBlendOpen =>
            s_lookBlending && s_blendT < 1f && s_blendFrom != s_blendTo;

        public static OilSkyLookId CurrentLookId => s_activeLookId;
        public static bool ColorOverrideActive => s_overrideColors;

        const int TimeOfDayVersionHours = 1;

        [Range(0f, 24f)]
        [Tooltip("一天中的小时（0–24）。06 黎明，12 白天，18 黄昏，00 夜晚。")]
        public float timeOfDay = 12f;

        [SerializeField, HideInInspector]
        int timeOfDayVersion;

        [Tooltip("Play 模式下按 cycleSeconds 走完 24 小时。")]
        public bool animate;

        [Tooltip("走完 24 小时一圈的秒数。")]
        public float cycleSeconds = 180f;

        [Tooltip("Lighting 里的 Skybox 材质；Stars 等参数以该材质为准，本组件只写 _Period。")]
        public Material skyboxMaterial;
        public Light sun;
        public bool driveSun = true;

        [SerializeField, HideInInspector]
        Texture fogCanvasWeave;

        [Header("Color Override")]
        [Tooltip("打开后天空、雾、环境光、主光颜色用下面这组，不再跟时间表走。日月方向仍跟时钟。")]
        public bool overrideColors;

        public OilSkyColorSet colorSet = OilSkyColorSet.DefaultDay;

        static bool s_overrideColors;
        static OilSkyColorSet s_colorSet = OilSkyColorSet.DefaultDay;

        /// <summary>Optional override that bypasses the 4-anchor LightPresets interpolation. Null = use the time-of-day table.</summary>
        Color? _lightColorOverride;
#if UNITY_EDITOR
        bool _editorSunDriveQueued;
#endif

        public const string ShaderName = "CustomSRP/OilSkyboxNPR";

        static readonly int PeriodId = Shader.PropertyToID("_Period");
        static readonly int OilSkyPeriodId = Shader.PropertyToID("_OilSkyPeriod");
        static readonly int OilPeriodAmbientId = Shader.PropertyToID("_OilPeriodAmbient");
        static readonly int OilSkySunDirId = Shader.PropertyToID("_OilSkySunDir");
        static readonly int OilSkyMoonDirId = Shader.PropertyToID("_OilSkyMoonDir");

        static readonly int OilFogCanvasId = Shader.PropertyToID("_OilFogCanvas");
        static readonly int OilFogCanvasEnableId = Shader.PropertyToID("_OilFogCanvasEnable");
        static readonly int OilFogCanvasStId = Shader.PropertyToID("_OilFogCanvas_ST");
        static readonly int CustomFogEnabledId = Shader.PropertyToID("_CustomFogEnabled");
        static readonly int CustomFogColorId = Shader.PropertyToID("_CustomFogColor");
        static readonly int CustomFogModeId = Shader.PropertyToID("_CustomFogMode");
        static readonly int CustomFogParamsId = Shader.PropertyToID("_CustomFogParams");
        static readonly int CustomFogSkyPowerId = Shader.PropertyToID("_CustomFogSkyPower");
        static readonly int AtmosphereFogColorId = Shader.PropertyToID("_OilAtmosphereFogColor");
        static readonly int AtmosphereFogAmountId = Shader.PropertyToID("_OilAtmosphereFogAmount");
        static readonly int OilSkyZenithId = Shader.PropertyToID("_OilSkyZenith");
        static readonly int OilSkyMidId = Shader.PropertyToID("_OilSkyMid");
        static readonly int OilSkyHorizonId = Shader.PropertyToID("_OilSkyHorizon");
        static readonly int OilSkyGroundId = Shader.PropertyToID("_OilSkyGround");
        static readonly int OilSkySunDiscId = Shader.PropertyToID("_OilSkySunDisc");
        static readonly int OilSkySunGlowId = Shader.PropertyToID("_OilSkySunGlow");
        static readonly int OilSkyMoonDiscId = Shader.PropertyToID("_OilSkyMoonDisc");
        static readonly int OilSkyMoonGlowId = Shader.PropertyToID("_OilSkyMoonGlow");
        static readonly int OilSkySunParamsId = Shader.PropertyToID("_OilSkySunParams");
        static readonly int OilSkyMoonParamsId = Shader.PropertyToID("_OilSkyMoonParams");
        static readonly int OilSkyStarIntensityId = Shader.PropertyToID("_OilSkyStarIntensity");
        static readonly int OilSkyLookReadyId = Shader.PropertyToID("_OilSkyLookReady");
        static readonly int OilSkySunriseId = Shader.PropertyToID("_OilSkySunrise");

        static readonly Vector4[] s_zenithBuf = new Vector4[4];
        static readonly Vector4[] s_midBuf = new Vector4[4];
        static readonly Vector4[] s_horizonBuf = new Vector4[4];
        static readonly Vector4[] s_groundBuf = new Vector4[4];
        static readonly Vector4[] s_sunDiscBuf = new Vector4[4];
        static readonly Vector4[] s_sunGlowBuf = new Vector4[4];
        static readonly Vector4[] s_moonDiscBuf = new Vector4[4];
        static readonly Vector4[] s_moonGlowBuf = new Vector4[4];
        static readonly Vector4[] s_sunParamsBuf = new Vector4[4];
        static readonly Vector4[] s_moonParamsBuf = new Vector4[4];
        static readonly float[] s_starBuf = new float[4];

        // Planar mirror throttle: only redraw when TOD/look jumped enough that a
        // cached day-blue RT would disagree with the live skybox.
        const float MirrorSkyPeriodEpsilon = 0.15f;
        static float s_mirrorSkyPeriod = float.NaN;
        static OilSkyLookId s_mirrorSkyLook = (OilSkyLookId)(-1);
        static bool s_mirrorSkyReady;

        // Boluo ApplyCelestialOrbits moon satellite constants (AbsoluteDay=0).
        const float MoonOrbitOffsetDeg = -95f;
        const float MoonSatelliteDirectionDeg = 11.5f;
        const float MoonSatellitePitchDeg = -30f;
        const float MoonRotationPeriodDays = 28f;
        static readonly float SatRotationAtDay0Deg = -0.5f / MoonRotationPeriodDays * 360f;

        bool _hasCelestialOverride;
        Vector3 _overrideSunDir = Vector3.up;
        Vector3 _overrideMoonDir = Vector3.up;
        int _celestialLookId;

        float _lastSyncedPeriod = float.NaN;

        /// <summary>Guards Apply against OnValidate re-entry when writing Light / Transform.</summary>
        static int skyboxApplyDepth;

        /// <summary>
        /// Clock hours → shader period. Anchors: 06→0 dawn, 12→1 day, 18→2 dusk, 00→3 night.
        /// </summary>
        public static float HoursToPeriod(float hours) =>
            Mathf.Repeat((hours - 6f) / 6f, 4f);

        public static float PeriodToHours(float period) =>
            Mathf.Repeat(period * 6f + 6f, 24f);

        public float CurrentPeriod() => HoursToPeriod(timeOfDay);

        public static bool IsOilSky(Material material) =>
            material != null && material.shader != null && material.shader.name == ShaderName;

        public void OnBeforeSerialize() { }

        public void OnAfterDeserialize()
        {
            if (timeOfDayVersion >= TimeOfDayVersionHours)
            {
                return;
            }

            // Legacy field was period 0–4 (dawn/day/dusk/night).
            timeOfDay = PeriodToHours(Mathf.Clamp(timeOfDay, 0f, 4f));
            timeOfDayVersion = TimeOfDayVersionHours;
        }

        /// <summary>Use these colors instead of the time-of-day look table.</summary>
        public void SetColorOverride(OilSkyColorSet set)
        {
            colorSet = set;
            overrideColors = true;
            PublishColorOverride();
            Apply(markDirty: true);
        }

        /// <summary>
        /// Pin sky, ambient, fog, and sun to zhanweitu1–8. Ids outside 1–8 clear the pin.
        /// </summary>
        public bool ApplyPresetPoster(int presetId)
        {
            if (!TryPoster(presetId, out OilSkyColorSet set))
            {
                ClearColorOverride();
                return false;
            }

            SetColorOverride(set);
            return true;
        }

        /// <summary>展位图取样：天空 / 地平雾 / 环境光（叶草灌木叠色）/ 主光。</summary>
        static bool TryPoster(int presetId, out OilSkyColorSet set)
        {
            set = default;
            switch (presetId)
            {
                case 1: // 清晨起床 — 桃粉霞、暖橄榄植被
                    set = Poster(
                        0xC4, 0x86, 0x84, 0xE4, 0xB0, 0x9C, 0xF2, 0xC8, 0xA4,
                        0x8A, 0x60, 0x48, 0xE0, 0xA0, 0x70,
                        0xFF, 0xB0, 0x60, 0.65f, 0xE8, 0xC0, 0xA8);
                    return true;
                case 2: // 独自旅行 — 青白天、薄荷植被；雾用薄荷灰，忌 #7EC8C8 青墙
                    set = Poster(
                        0xA9, 0xC3, 0xDD, 0x8E, 0xC8, 0xD4, 0x7E, 0xC8, 0xC8,
                        0x5A, 0xA8, 0xB8, 0x7E, 0xD0, 0xC0,
                        0xF2, 0xF7, 0xF4, 1.15f, 0xC0, 0xD4, 0xC8);
                    return true;
                case 3: // 午后放松 — 奶油天空、青绿植被、暖金雾
                    set = Poster(
                        0xD6, 0xCA, 0xA5, 0xC8, 0xB8, 0x88, 0xC8, 0xB8, 0x88,
                        0x8A, 0x9A, 0x78, 0xD0, 0xB0, 0x70,
                        0xFF, 0xE6, 0xA0, 0.85f, 0xD6, 0xC8, 0xA0);
                    return true;
                case 4: // 委屈泛滥 — 夜雨篝火、深蓝雾、暗青植被
                    set = Poster(
                        0x2D, 0x43, 0x60, 0x1A, 0x30, 0x48, 0x13, 0x2C, 0x42,
                        0x1A, 0x30, 0x58, 0x10, 0x28, 0x30,
                        0x6A, 0x88, 0xC0, 0.16f, 0x1A, 0x30, 0x48);
                    return true;
                case 5: // 专注工作 — 雾林，桃顶、青雾、深青植被
                    set = Poster(
                        0xE4, 0xC8, 0xC0, 0xA8, 0xC0, 0xC4, 0x8F, 0xB8, 0xC0,
                        0x3A, 0x78, 0x78, 0x6A, 0x98, 0x98,
                        0xF4, 0xE8, 0xDC, 0.50f, 0x7A, 0xA8, 0xB0);
                    return true;
                case 6: // 放空发呆 — 深夜雨、海军蓝
                    set = Poster(
                        0x1E, 0x3B, 0x52, 0x15, 0x28, 0x38, 0x11, 0x1F, 0x27,
                        0x15, 0x28, 0x38, 0x10, 0x18, 0x20,
                        0x5A, 0x78, 0xA8, 0.14f, 0x15, 0x24, 0x30);
                    return true;
                case 7: // 灵感唤醒 — 晴天瀑布、亮青空、鲜绿
                    set = Poster(
                        0x70, 0xB1, 0xC2, 0x9A, 0xC8, 0xC8, 0xB0, 0xD0, 0xD8,
                        0x6A, 0x9A, 0x78, 0xC0, 0xD8, 0xA8,
                        0xFF, 0xF4, 0xD0, 1.40f, 0xB0, 0xD0, 0xD8);
                    return true;
                case 8: // 假日躺平 — 正午海岛、淡蓝天、饱和绿
                    set = Poster(
                        0x84, 0xC3, 0xE7, 0xA8, 0xD4, 0xE4, 0xA8, 0xD0, 0xE8,
                        0x6A, 0xAA, 0x88, 0xD0, 0xE8, 0xC0,
                        0xFF, 0xF8, 0xEC, 1.50f, 0xA8, 0xD0, 0xE8);
                    return true;
                default:
                    return false;
            }
        }

        static OilSkyColorSet Poster(
            byte zR, byte zG, byte zB,
            byte mR, byte mG, byte mB,
            byte hR, byte hG, byte hB,
            byte azR, byte azG, byte azB,
            byte ahR, byte ahG, byte ahB,
            byte lR, byte lG, byte lB, float lightIntensity,
            byte fR, byte fG, byte fB)
        {
            Color zenith = new Color(zR / 255f, zG / 255f, zB / 255f, 1f);
            Color mid = new Color(mR / 255f, mG / 255f, mB / 255f, 1f);
            Color horizon = new Color(hR / 255f, hG / 255f, hB / 255f, 1f);
            bool night = lightIntensity < 0.35f;
            return new OilSkyColorSet
            {
                zenith = zenith,
                mid = mid,
                horizon = horizon,
                ground = horizon * 0.55f,
                sunDisc = night ? new Color(0.75f, 0.82f, 0.95f, 1f) : new Color(1f, 0.98f, 0.92f, 1f),
                sunGlow = night ? new Color(0.45f, 0.55f, 0.75f, 1f) : horizon,
                moonDisc = night ? new Color(0.90f, 0.93f, 0.96f, 1f) : Color.black,
                moonGlow = night ? new Color(0.66f, 0.74f, 0.84f, 1f) : Color.black,
                lightColor = new Color(lR / 255f, lG / 255f, lB / 255f, 1f),
                lightIntensity = lightIntensity,
                fogColor = new Color(fR / 255f, fG / 255f, fB / 255f, 1f),
                ambientZenith = new Color(azR / 255f, azG / 255f, azB / 255f, 1f),
                ambientHorizon = new Color(ahR / 255f, ahG / 255f, ahB / 255f, 1f),
            };
        }

        /// <summary>Drop the external colors and follow the time-of-day look table again.</summary>
        public void ClearColorOverride()
        {
            if (!overrideColors && !s_overrideColors)
                return;
            overrideColors = false;
            PublishColorOverride();
            Apply(markDirty: true);
        }

        public void PublishColorOverride()
        {
            s_overrideColors = overrideColors;
            bool unset = colorSet.zenith.maxColorComponent <= 0.001f
                && colorSet.horizon.maxColorComponent <= 0.001f
                && colorSet.lightIntensity <= 0.001f;
            s_colorSet = unset ? OilSkyColorSet.DefaultDay : colorSet;
        }

        public void SetTimeOfDay(float hours)
        {
            timeOfDay = Mathf.Repeat(hours, 24f);
            s_clockHours = timeOfDay;
            timeOfDayVersion = TimeOfDayVersionHours;
            Apply(markDirty: Application.isPlaying);
            if (Application.isPlaying && driveSun)
            {
                DisableCompetingDirectionalLights();
            }
        }

        /// <summary>
        /// Swap Boluo AtmosphereProfile-equivalent look table (sky + light + fog).
        /// HUD dial: daytime → Canonical, night → NightCold. Presets pin their own look.
        /// </summary>
        public void SetLookId(OilSkyLookId lookId)
        {
            bool unchanged = !s_lookBlending && ActiveLookId == lookId && s_activeLookId == lookId;
            FinishLookBlend(lookId);
            PushLookGlobals(lookId);
            if (unchanged)
                return;
            Apply(markDirty: true);
        }

        /// <summary>
        /// Dial transition: mix the committed look toward <paramref name="to"/> by <paramref name="t"/>.
        /// t = 1 commits. WeatherService owns the clock so this does not advance time itself.
        /// </summary>
        public void SetLookBlend(OilSkyLookId to, float t)
        {
            t = Mathf.Clamp01(t);
            if (!s_lookBlending || s_blendTo != to)
            {
                s_blendFrom = s_activeLookId;
                s_blendTo = to;
                s_lookBlending = s_blendFrom != to;
            }

            s_blendT = t;
            if (t >= 1f || s_blendFrom == to)
                FinishLookBlend(to);
        }

        void FinishLookBlend(OilSkyLookId lookId)
        {
            s_lookBlending = false;
            s_blendT = 1f;
            s_blendFrom = lookId;
            s_blendTo = lookId;
            ActiveLookId = lookId;
            s_activeLookId = lookId;
        }

        /// <summary>
        /// Sunrise weight shared by the skybox shader, main light, and ambient.
        /// Look blends fade Canonical's weight with the same t as the palette.
        /// </summary>
        public static float CurrentSunriseWeight(float hours)
        {
            if (s_overrideColors)
                return 0f;
            if (LookBlendOpen)
            {
                float from = OilSkyLookTables.CanonicalSunriseWeight(s_blendFrom, hours);
                float to = OilSkyLookTables.CanonicalSunriseWeight(s_blendTo, hours);
                return Mathf.Lerp(from, to, s_blendT);
            }

            return OilSkyLookTables.CanonicalSunriseWeight(s_activeLookId, hours);
        }

        /// <summary>
        /// Bypass the 4-anchor LightPresets interpolation and force a specific directional
        /// light + fog tint. Pass null to clear and fall back to the time-of-day table.
        /// </summary>
        public void SetLightColorOverride(Color? color)
        {
            _lightColorOverride = color;
            if (Application.isPlaying)
                ApplySun(CurrentPeriod());
            else
                ApplyEditorPreview();
        }

        /// <summary>
        /// Inspector / editor preview: sky _Period, ambient, and deferred sun drive.
        /// </summary>
        public void ApplyEditorHours(float hours)
        {
            timeOfDay = Mathf.Repeat(hours, 24f);
            s_clockHours = timeOfDay;
            timeOfDayVersion = TimeOfDayVersionHours;
            ApplyEditorPreview();
        }

        /// <summary>Legacy name — hours, not period.</summary>
        public void ApplyEditorPeriod(float hours) => ApplyEditorHours(hours);

        public void ApplyEditorPreview()
        {
            if (skyboxApplyDepth > 0)
            {
                return;
            }

            PublishColorOverride();
            float period = CurrentPeriod();
            skyboxApplyDepth++;
            try
            {
                Material mat = ResolveSkyMaterialForEditor();
                if (mat != null)
                {
                    SyncSkyMaterial(mat, period, force: true);
                }

                ApplyAmbient(period);
                // Fog is independent of sun Light / driveSun (Sun Source may be None).
                ApplyFogColor(period);
            }
            finally
            {
                skyboxApplyDepth--;
            }

#if UNITY_EDITOR
            if (!Application.isPlaying && driveSun && sun != null)
            {
                QueueEditorSunDrive();
            }
            else if (Application.isPlaying && driveSun && sun != null)
            {
                ApplySun(period);
            }
#else
            if (driveSun && sun != null)
            {
                ApplySun(period);
            }
#endif
        }

        void Reset()
        {
            sun = GetComponent<Light>();
            if (sun == null || sun.type != LightType.Directional)
            {
                Light[] lights = FindObjectsByType<Light>();
                for (int i = 0; i < lights.Length; i++)
                {
                    if (lights[i].type == LightType.Directional)
                    {
                        sun = lights[i];
                        break;
                    }
                }
            }
        }

        void OnEnable()
        {
            // Edit mode: no auto Apply on load (Unity 6 delayCall / same-GO Transform recursion).
            // Inspector EndChangeCheck / period buttons call ApplyEditorPreview.
            if (!Application.isPlaying)
            {
                return;
            }

            // Island terrain may spawn after scene load; attach the Game→SRP bridge here.
            // EnsureOnSkybox → SyncFromWeatherService writes WeatherService Hours/Look into
            // timeOfDay before Apply, so the prefab noon default never reaches the first frame.
            OilAtmosphereDriver.EnsureOnSkybox();
            OilWarpDriver.EnsureOnSkybox();
            if (driveSun && sun == null)
            {
                ResolveSunLight();
            }

            // Keep a look already chosen by WeatherService before this component enabled.
            if (s_activeLookId != OilSkyLookId.Canonical || ActiveLookId != OilSkyLookId.Canonical)
                ActiveLookId = s_activeLookId;
            s_clockHours = timeOfDay;
            PublishColorOverride();
            PushLookGlobals(s_activeLookId);
            // Assign Light color/intensity immediately (Launch scene may keep a second dir light).
            Apply(markDirty: false);
            DisableCompetingDirectionalLights();
            // Noon cold-start: SeedWaterSkyGlobals already armed mirror tracking at
            // period≈day, so NotifyPlanarIfSkyChanged no-ops on the same noon Apply.
            // Force one planar redraw after the real skybox/sun bind — otherwise the
            // idle throttle freezes a pre-Apply mirror RT until the camera moves.
            OilPlanarReflection.InvalidateSky();
        }

        void OnDisable()
        {
#if UNITY_EDITOR
            CancelEditorSunDrive();
#endif
        }

        void OnDestroy()
        {
#if UNITY_EDITOR
            CancelEditorSunDrive();
#endif
        }

        float _lastAppliedTimeOfDay = float.NaN;

        void Update()
        {
            if (!Application.isPlaying)
            {
                return;
            }

            if (!animate || cycleSeconds <= 0.01f)
            {
                return;
            }

            timeOfDay = Mathf.Repeat(timeOfDay + Time.deltaTime / cycleSeconds * 24f, 24f);
            s_clockHours = timeOfDay;
            // Skip redundant sky/light writes when TOD barely moved (keeps oil look).
            if (!float.IsNaN(_lastAppliedTimeOfDay)
                && Mathf.Abs(timeOfDay - _lastAppliedTimeOfDay) < 0.02f)
            {
                return;
            }

            _lastAppliedTimeOfDay = timeOfDay;
            Apply(markDirty: false);
        }

        Material ResolveSkyMaterialForEditor()
        {
            BindSkybox();
            return ActiveSkyMaterial();
        }

        /// <summary>
        /// Scene Lighting skybox when it is Oil; otherwise the assigned asset.
        /// No runtime clone — Stars stay on the shared material for live tweaking.
        /// </summary>
        Material ActiveSkyMaterial()
        {
            Material scene = RenderSettings.skybox;
            if (IsOilSky(scene))
            {
                return scene;
            }

            return IsOilSky(skyboxMaterial) ? skyboxMaterial : null;
        }

        void BindSkybox()
        {
            if (IsOilSky(RenderSettings.skybox))
            {
                return;
            }

            if (IsOilSky(skyboxMaterial))
            {
                RenderSettings.skybox = skyboxMaterial;
            }
        }

        void ApplySkyOnly()
        {
            if (skyboxApplyDepth > 0)
            {
                return;
            }

            skyboxApplyDepth++;
            try
            {
                BindSkybox();
                Material mat = ActiveSkyMaterial();
                if (mat == null)
                {
                    return;
                }

                float period = CurrentPeriod();
                SyncSkyMaterial(mat, period, force: true);
                ApplyAmbient(period);
                ApplyFogColor(period);
            }
            finally
            {
                skyboxApplyDepth--;
            }
        }

        void Apply(bool markDirty)
        {
            if (skyboxApplyDepth > 0)
            {
                return;
            }

            skyboxApplyDepth++;
            try
            {
                ApplyInternal(markDirty);
            }
            finally
            {
                skyboxApplyDepth--;
            }
        }

        void ApplyInternal(bool markDirty)
        {
            PublishColorOverride();
            float period = CurrentPeriod();

            BindSkybox();
            Material mat = ActiveSkyMaterial();
            if (mat != null)
            {
                SyncSkyMaterial(mat, period, force: markDirty || !Application.isPlaying);
            }

            ApplyAmbient(period);
            // Fog must not depend on sun Light existing (Lighting Sun Source can be None).
            ApplyFogColor(period);

            // Always publish disc dirs for the sky shader (even when not driving the Light).
            ResolveCelestialDirections(period, out Vector3 sunDir, out Vector3 moonDir);
            PushCelestialGlobals(sunDir, moonDir);

            if (!driveSun)
            {
                return;
            }

            if (sun == null)
            {
                ResolveSunLight();
            }

            if (sun == null)
            {
                return;
            }

#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                QueueEditorSunDrive();
                return;
            }
#endif

            ApplySun(period);
        }

        void ResolveSunLight()
        {
            Light self = GetComponent<Light>();
            if (self != null && self.type == LightType.Directional)
            {
                sun = self;
                return;
            }

            Light[] lights = FindObjectsByType<Light>();
            for (int i = 0; i < lights.Length; i++)
            {
                if (lights[i] != null && lights[i].type == LightType.Directional)
                {
                    sun = lights[i];
                    return;
                }
            }
        }

        /// <summary>
        /// Launch keeps a bootstrap Directional Light; LightingPass accepts up to 4 dirs.
        /// Only the Oil-driven sun should remain enabled in play.
        /// </summary>
        public void DisableCompetingDirectionalLights()
        {
            if (sun == null)
            {
                ResolveSunLight();
            }

            if (sun == null)
            {
                return;
            }

            Light[] lights = FindObjectsByType<Light>(FindObjectsInactive.Include);
            for (int i = 0; i < lights.Length; i++)
            {
                Light other = lights[i];
                if (other == null || other == sun || other.type != LightType.Directional)
                {
                    continue;
                }

                if (other.enabled)
                {
                    other.enabled = false;
                }
            }
        }

        void ApplySun(float period)
        {
            if (sun == null)
            {
                ResolveSunLight();
            }

            if (sun == null)
            {
                return;
            }

            ResolveCelestialDirections(period, out Vector3 sunDir, out Vector3 moonDir);
            PushCelestialGlobals(sunDir, moonDir);

            Vector3 lightDir = CelestialLightDirection(sunDir, moonDir);
            if (lightDir.sqrMagnitude <= 0.001f)
            {
                return;
            }

            EvaluateLight(period, out Color color, out float intensity);
            if (_lightColorOverride.HasValue)
            {
                color = _lightColorOverride.Value;
            }
            if (sun.color != color)
            {
                sun.color = color;
            }

            if (!Mathf.Approximately(sun.intensity, intensity))
            {
                sun.intensity = intensity;
            }

            if (RenderSettings.sun != sun)
            {
                RenderSettings.sun = sun;
            }

            ApplyFogColor(period);

            Vector3 forward = -lightDir;
            Vector3 up = Mathf.Abs(Vector3.Dot(forward.normalized, Vector3.up)) > 0.98f
                ? Vector3.forward
                : Vector3.up;
            Quaternion rot = Quaternion.LookRotation(forward, up);
            if (sun.transform.rotation != rot)
            {
                sun.transform.rotation = rot;
            }
        }

        void ApplyFogColor(float period)
        {
            EvaluateFog(period, out Color fog);
            if (!RenderSettings.fog)
            {
                RenderSettings.fog = true;
            }

            if (RenderSettings.fogMode != FogMode.ExponentialSquared)
            {
                RenderSettings.fogMode = FogMode.ExponentialSquared;
            }

            // Lighting window defaults (~0.1+) milk ExpSquared into a white slab.
            // Authored Foggy L3 is 0.012 — clamp only values above that band.
            if (RenderSettings.fogDensity > 0.12f)
            {
                RenderSettings.fogDensity = 0.003f;
            }

            if (RenderSettings.fogColor != fog)
            {
                RenderSettings.fogColor = fog;
            }

            // Push TOD fog into shader globals here too — skybox must see tint even when
            // SetupPass Environment evaluate races RenderSettings.fog for a frame.
            PushFogShaderGlobals(fog);
        }

        /// <summary>Re-push fog color/density shader globals after OilAtmosphereDriver edits RenderSettings.</summary>
        public void PushFogGlobals()
        {
            EvaluateFog(CurrentPeriod(), out Color fog);
            if (RenderSettings.fogColor != fog)
                RenderSettings.fogColor = fog;
            PushFogShaderGlobals(fog);
        }

        static void PushFogShaderGlobals(Color fogSrgb)
        {
            Color linear = fogSrgb.linear;
            var fogVec = new Vector4(linear.r, linear.g, linear.b, 1f);
            Shader.SetGlobalFloat(CustomFogEnabledId, 1f);
            Shader.SetGlobalVector(CustomFogColorId, fogVec);
            // Skybox + clouds dye from this pair (not _CustomFogEnabled alone).
            Shader.SetGlobalVector(AtmosphereFogColorId, fogVec);
            Shader.SetGlobalFloat(AtmosphereFogAmountId, 1f);
            // Mode 2 = Exponential Squared (FogSettings.ModeToShader).
            const float invSqrtLn2 = 1.20112240879f;
            float density = Mathf.Max(0f, RenderSettings.fogDensity);
            Shader.SetGlobalFloat(CustomFogModeId, 2f);
            Shader.SetGlobalVector(
                CustomFogParamsId, new Vector4(density * invSqrtLn2, 0f, 0f, 0f));
            Shader.SetGlobalFloat(CustomFogSkyPowerId, 4.5f);
        }

        void PushCelestialGlobals(Vector3 sunDir, Vector3 moonDir)
        {
            Shader.SetGlobalVector(OilSkySunDirId, sunDir);
            Shader.SetGlobalVector(OilSkyMoonDirId, moonDir);
        }

        /// <summary>Orbit dirs, then optional debug override if set.</summary>
        public void ResolveCelestialDirections(float period, out Vector3 sunDir, out Vector3 moonDir)
        {
            sunDir = SunDirectionAt(period);
            moonDir = MoonDirectionAt(period);
            if (_hasCelestialOverride)
            {
                sunDir = _overrideSunDir;
                moonDir = _overrideMoonDir;
            }
        }

        public Vector3 ResolvedSunDirection =>
            _hasCelestialOverride ? _overrideSunDir : SunDirectionAt(CurrentPeriod());

        public Vector3 ResolvedMoonDirection =>
            _hasCelestialOverride ? _overrideMoonDir : MoonDirectionAt(CurrentPeriod());

        public int CelestialLookId => _celestialLookId;

        /// <summary>
        /// Optional override of sun/moon dirs (debug / tooling). Gameplay always uses Cozy orbits.
        /// </summary>
        public void SetCelestialLookPlacement(int lookId, Vector3 sunDir, Vector3 moonDir)
        {
            Vector3 s = sunDir.sqrMagnitude > 1e-8f ? sunDir.normalized : Vector3.up;
            Vector3 m = moonDir.sqrMagnitude > 1e-8f ? moonDir.normalized : Vector3.up;
            if (_hasCelestialOverride
                && _celestialLookId == lookId
                && (s - _overrideSunDir).sqrMagnitude < 1e-6f
                && (m - _overrideMoonDir).sqrMagnitude < 1e-6f)
            {
                return;
            }

            _hasCelestialOverride = true;
            _celestialLookId = lookId;
            _overrideSunDir = s;
            _overrideMoonDir = m;
            Apply(markDirty: true);
        }

        public void ClearCelestialLookPlacement()
        {
            if (!_hasCelestialOverride)
            {
                return;
            }

            ReleaseCelestialLookForClock();
            Apply(markDirty: true);
        }

        /// <summary>
        /// Drop view freeze without Apply — next SetTimeOfDay / Apply uses Cozy orbits
        /// (BoluoProject ApplyCheapSunDiscFromDayPercentage; BoluoUnity skips Place* while blending).
        /// </summary>
        public void ReleaseCelestialLookForClock()
        {
            _hasCelestialOverride = false;
            _celestialLookId = 0;
        }

        /// <summary>Boluo PlaceSunForLook: sun on camera azimuth at elevationDeg.</summary>
        public static void PlaceSunForView(
            Vector3 camForward, float elevationDeg, out Vector3 sunDir, out Vector3 moonDir)
        {
            Vector3 azimuth = new Vector3(camForward.x, 0f, camForward.z);
            if (azimuth.sqrMagnitude < 1e-6f)
            {
                azimuth = Vector3.forward;
            }

            azimuth.Normalize();
            float elev = elevationDeg * Mathf.Deg2Rad;
            sunDir = (azimuth * Mathf.Cos(elev) + Vector3.up * Mathf.Sin(elev)).normalized;

            Vector3 moonDown = -sunDir;
            moonDown.y = Mathf.Min(moonDown.y, -0.38f);
            moonDir = moonDown.sqrMagnitude > 1e-8f ? moonDown.normalized : Vector3.down;
        }

        /// <summary>Boluo PlaceMoonForLook: moon on camera azimuth at elevationDeg; sun forced down.</summary>
        public static void PlaceMoonForView(
            Vector3 camForward,
            float elevationDeg,
            Vector3 orbitSunDir,
            Vector3 orbitMoonDir,
            out Vector3 sunDir,
            out Vector3 moonDir)
        {
            Vector3 sun = orbitSunDir.sqrMagnitude > 1e-8f ? orbitSunDir.normalized : Vector3.down;
            sun.y = Mathf.Min(sun.y, -0.38f);
            sunDir = sun.sqrMagnitude > 1e-8f ? sun.normalized : Vector3.down;

            Vector3 moon = orbitMoonDir.sqrMagnitude > 1e-8f ? orbitMoonDir.normalized : Vector3.up;
            Vector3 azimuth = new Vector3(moon.x, 0f, moon.z);
            Vector3 fwd = new Vector3(camForward.x, 0f, camForward.z);
            if (fwd.sqrMagnitude > 1e-6f)
            {
                fwd.Normalize();
                if (azimuth.sqrMagnitude < 1e-6f || Vector3.Dot(azimuth.normalized, fwd) < 0.22f)
                {
                    azimuth = fwd;
                }
            }

            if (azimuth.sqrMagnitude < 1e-6f)
            {
                azimuth = Vector3.forward;
            }

            azimuth.Normalize();
            float elev = elevationDeg * Mathf.Deg2Rad;
            moonDir = (azimuth * Mathf.Cos(elev) + Vector3.up * Mathf.Sin(elev)).normalized;
        }

#if UNITY_EDITOR
        void QueueEditorSunDrive()
        {
            if (_editorSunDriveQueued)
            {
                return;
            }

            _editorSunDriveQueued = true;
            EditorApplication.delayCall += DriveSunDeferred;
        }

        void CancelEditorSunDrive()
        {
            if (!_editorSunDriveQueued)
            {
                return;
            }

            _editorSunDriveQueued = false;
            EditorApplication.delayCall -= DriveSunDeferred;
        }

        void DriveSunDeferred()
        {
            _editorSunDriveQueued = false;
            if (this == null || !driveSun || sun == null)
            {
                return;
            }

            if (skyboxApplyDepth > 0)
            {
                QueueEditorSunDrive();
                return;
            }

            skyboxApplyDepth++;
            try
            {
                ApplySun(CurrentPeriod());
                EditorUtility.SetDirty(sun);
                if (sun.transform != null)
                {
                    EditorUtility.SetDirty(sun.transform);
                }

                SceneView.RepaintAll();
            }
            finally
            {
                skyboxApplyDepth--;
            }
        }
#endif

        public static SkyLookPreset EvaluateSkyLook(float period)
        {
            if (!LookBlendOpen)
                return EvaluateSkyLook(s_activeLookId, period);
            return SkyLookPreset.Lerp(
                EvaluateSkyLook(s_blendFrom, period),
                EvaluateSkyLook(s_blendTo, period),
                s_blendT);
        }

        public static SkyLookPreset EvaluateSkyLook(OilSkyLookId lookId, float period)
        {
            float wrapped = Mathf.Repeat(period, 4f);
            int i0 = Mathf.FloorToInt(wrapped) % 4;
            int i1 = (i0 + 1) % 4;
            float t = wrapped - Mathf.Floor(wrapped);
            if (s_overrideColors)
                return SkyLookFromOverride();

            SkyLookPreset a = OilSkyLookTables.SkyLookAt(lookId, i0);
            SkyLookPreset b = OilSkyLookTables.SkyLookAt(lookId, i1);
            SkyLookPreset look = SkyLookPreset.Lerp(a, b, t);
            float sunrise = OilSkyLookTables.CanonicalSunriseWeight(lookId, PeriodToHours(period));
            if (sunrise > 0.001f)
            {
                look.zenith = Color.Lerp(look.zenith, OilSkyLookTables.SunriseSkyZenith, sunrise);
                look.mid = Color.Lerp(look.mid, OilSkyLookTables.SunriseSkyMid, sunrise);
                look.horizon = Color.Lerp(look.horizon, OilSkyLookTables.SunriseSkyHorizon, sunrise);
                look.sunIntensity = Mathf.Lerp(
                    look.sunIntensity, OilSkyLookTables.SunriseSunIntensity, sunrise);
                look.sunGlow = Mathf.Lerp(look.sunGlow, OilSkyLookTables.SunriseSunGlow, sunrise);
            }

            return look;
        }

        static SkyLookPreset SkyLookFromOverride()
        {
            OilSkyColorSet c = s_colorSet;
            bool night = c.lightIntensity < 0.35f;
            return new SkyLookPreset
            {
                zenith = c.zenith,
                mid = c.mid,
                horizon = c.horizon,
                ground = c.ground,
                sunSize = night ? 0.02f : 0.07f,
                sunIntensity = night ? 0.02f : 1.05f,
                sunGlow = night ? 0.02f : 0.22f,
                moonSize = night ? 0.07f : 0.02f,
                moonIntensity = night ? 0.55f : 0f,
                moonGlow = night ? 0.32f : 0f,
                starIntensity = night ? 1f : 0f,
            };
        }

        static void PushOverrideLookGlobals()
        {
            OilSkyColorSet c = s_colorSet;
            bool night = c.lightIntensity < 0.35f;
            var sunParams = night ? new Vector4(0.02f, 0.02f, 0.02f, 0f) : new Vector4(0.07f, 1.05f, 0.22f, 0f);
            var moonParams = night ? new Vector4(0.07f, 0.55f, 0.32f, 0f) : new Vector4(0.02f, 0f, 0f, 0f);
            float stars = night ? 1f : 0f;
            for (int i = 0; i < 4; i++)
            {
                s_zenithBuf[i] = c.zenith;
                s_midBuf[i] = c.mid;
                s_horizonBuf[i] = c.horizon;
                s_groundBuf[i] = c.ground;
                s_sunDiscBuf[i] = c.sunDisc;
                s_sunGlowBuf[i] = c.sunGlow;
                s_moonDiscBuf[i] = c.moonDisc;
                s_moonGlowBuf[i] = c.moonGlow;
                s_sunParamsBuf[i] = sunParams;
                s_moonParamsBuf[i] = moonParams;
                s_starBuf[i] = stars;
            }

            Shader.SetGlobalVectorArray(OilSkyZenithId, s_zenithBuf);
            Shader.SetGlobalVectorArray(OilSkyMidId, s_midBuf);
            Shader.SetGlobalVectorArray(OilSkyHorizonId, s_horizonBuf);
            Shader.SetGlobalVectorArray(OilSkyGroundId, s_groundBuf);
            Shader.SetGlobalVectorArray(OilSkySunDiscId, s_sunDiscBuf);
            Shader.SetGlobalVectorArray(OilSkySunGlowId, s_sunGlowBuf);
            Shader.SetGlobalVectorArray(OilSkyMoonDiscId, s_moonDiscBuf);
            Shader.SetGlobalVectorArray(OilSkyMoonGlowId, s_moonGlowBuf);
            Shader.SetGlobalVectorArray(OilSkySunParamsId, s_sunParamsBuf);
            Shader.SetGlobalVectorArray(OilSkyMoonParamsId, s_moonParamsBuf);
            Shader.SetGlobalFloatArray(OilSkyStarIntensityId, s_starBuf);
            Shader.SetGlobalFloat(OilSkyLookReadyId, 1f);
            Shader.SetGlobalFloat(OilSkySunriseId, 0f);
            NotifyPlanarIfSkyChanged(HoursToPeriod(s_clockHours));
        }

        /// <summary>
        /// Idle planar mirror reuses its RT; cold-start TOD / look jumps must bust that
        /// cache or water keeps a day-blue reflection under a night skybox.
        /// </summary>
        static void NotifyPlanarIfSkyChanged(float period)
        {
            period = Mathf.Repeat(period, 4f);
            bool periodJump;
            if (float.IsNaN(s_mirrorSkyPeriod))
            {
                periodJump = true;
            }
            else
            {
                // Circular distance on the 0–4 period ring (night↔dawn wrap).
                float delta = Mathf.Abs(period - s_mirrorSkyPeriod);
                float wrap = Mathf.Min(delta, 4f - delta);
                periodJump = wrap > MirrorSkyPeriodEpsilon;
            }

            bool lookJump = s_mirrorSkyLook != s_activeLookId;
            bool readyJump = !s_mirrorSkyReady;
            if (!periodJump && !lookJump && !readyJump)
            {
                return;
            }

            s_mirrorSkyPeriod = period;
            s_mirrorSkyLook = s_activeLookId;
            s_mirrorSkyReady = true;
            OilPlanarReflection.InvalidateSky();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void ResetMirrorSkyTracking()
        {
            s_mirrorSkyPeriod = float.NaN;
            s_mirrorSkyLook = (OilSkyLookId)(-1);
            s_mirrorSkyReady = false;
            Shader.SetGlobalFloat(OilSkyLookReadyId, 0f);
        }

        /// <summary>
        /// Seed _OilSkyPeriod + look anchors from the skybox material before the first
        /// water draw. Without this, analytic sky falls back while the skybox already
        /// shows the serialized night period — open water goes neon day-blue.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void SeedWaterSkyGlobals()
        {
            Material sky = RenderSettings.skybox;
            if (sky != null && sky.HasProperty(PeriodId))
            {
                float period = sky.GetFloat(PeriodId);
                Shader.SetGlobalFloat(OilSkyPeriodId, period);
                s_clockHours = PeriodToHours(period);
            }

            PushLookGlobals(s_activeLookId);
            // PushLookGlobals arms mirror tracking + InvalidateSky. That is only a
            // pigment seed — OilSkyboxTime.OnEnable has not bound the runtime skybox
            // / sun yet. Reset tracking so the real Apply still counts as readyJump
            // (critical at noon: Seed period already equals prefab timeOfDay=12, so
            // periodJump would be false and a wrong early planar RT would stick).
            s_mirrorSkyPeriod = float.NaN;
            s_mirrorSkyLook = (OilSkyLookId)(-1);
            s_mirrorSkyReady = false;
        }

        /// <summary>
        /// Push 4-anchor sky pigments. Sunrise is a separate <c>_OilSkySunrise</c> weight so the
        /// shader fades it on the same curve as the main light, not across the whole dawn anchor.
        /// </summary>
        public static void PushLookGlobals(OilSkyLookId lookId)
        {
            if (s_overrideColors)
            {
                PushOverrideLookGlobals();
                return;
            }

            if (LookBlendOpen)
            {
                WriteLookAnchors(s_blendFrom, 0f, overwrite: true);
                // t = 0 must stay on the source look. lerpT 0 is a blend, not a replace.
                WriteLookAnchors(s_blendTo, s_blendT, overwrite: false);
            }
            else
            {
                WriteLookAnchors(lookId, 0f, overwrite: true);
            }

            UploadLookAnchors();
            Shader.SetGlobalFloat(OilSkySunriseId, CurrentSunriseWeight(s_clockHours));
        }

        /// <summary><paramref name="overwrite"/> replaces the buffers; otherwise lerps toward this look by <paramref name="lerpT"/>.</summary>
        static void WriteLookAnchors(OilSkyLookId lookId, float lerpT, bool overwrite)
        {
            var table = OilSkyLookTables.Get(lookId);
            for (int i = 0; i < 4; i++)
            {
                var sky = table.sky[i];
                Vector4 zenith = sky.zenith;
                Vector4 mid = sky.mid;
                Vector4 horizon = sky.horizon;
                Vector4 ground = sky.ground;
                Vector4 sunDisc = sky.sunDisc;
                Vector4 sunGlow = sky.sunGlow;
                Vector4 moonDisc = sky.moonDisc;
                Vector4 moonGlow = sky.moonGlow;
                var sunParams = new Vector4(sky.sunSize, sky.sunIntensity, sky.sunGlowAmt, 0f);
                var moonParams = new Vector4(sky.moonSize, sky.moonIntensity, sky.moonGlowAmt, 0f);
                if (overwrite)
                {
                    s_zenithBuf[i] = zenith;
                    s_midBuf[i] = mid;
                    s_horizonBuf[i] = horizon;
                    s_groundBuf[i] = ground;
                    s_sunDiscBuf[i] = sunDisc;
                    s_sunGlowBuf[i] = sunGlow;
                    s_moonDiscBuf[i] = moonDisc;
                    s_moonGlowBuf[i] = moonGlow;
                    s_sunParamsBuf[i] = sunParams;
                    s_moonParamsBuf[i] = moonParams;
                    s_starBuf[i] = sky.starIntensity;
                }
                else
                {
                    s_zenithBuf[i] = Vector4.Lerp(s_zenithBuf[i], zenith, lerpT);
                    s_midBuf[i] = Vector4.Lerp(s_midBuf[i], mid, lerpT);
                    s_horizonBuf[i] = Vector4.Lerp(s_horizonBuf[i], horizon, lerpT);
                    s_groundBuf[i] = Vector4.Lerp(s_groundBuf[i], ground, lerpT);
                    s_sunDiscBuf[i] = Vector4.Lerp(s_sunDiscBuf[i], sunDisc, lerpT);
                    s_sunGlowBuf[i] = Vector4.Lerp(s_sunGlowBuf[i], sunGlow, lerpT);
                    s_moonDiscBuf[i] = Vector4.Lerp(s_moonDiscBuf[i], moonDisc, lerpT);
                    s_moonGlowBuf[i] = Vector4.Lerp(s_moonGlowBuf[i], moonGlow, lerpT);
                    s_sunParamsBuf[i] = Vector4.Lerp(s_sunParamsBuf[i], sunParams, lerpT);
                    s_moonParamsBuf[i] = Vector4.Lerp(s_moonParamsBuf[i], moonParams, lerpT);
                    s_starBuf[i] = Mathf.Lerp(s_starBuf[i], sky.starIntensity, lerpT);
                }
            }
        }

        static void UploadLookAnchors()
        {
            Shader.SetGlobalVectorArray(OilSkyZenithId, s_zenithBuf);
            Shader.SetGlobalVectorArray(OilSkyMidId, s_midBuf);
            Shader.SetGlobalVectorArray(OilSkyHorizonId, s_horizonBuf);
            Shader.SetGlobalVectorArray(OilSkyGroundId, s_groundBuf);
            Shader.SetGlobalVectorArray(OilSkySunDiscId, s_sunDiscBuf);
            Shader.SetGlobalVectorArray(OilSkySunGlowId, s_sunGlowBuf);
            Shader.SetGlobalVectorArray(OilSkyMoonDiscId, s_moonDiscBuf);
            Shader.SetGlobalVectorArray(OilSkyMoonGlowId, s_moonGlowBuf);
            Shader.SetGlobalVectorArray(OilSkySunParamsId, s_sunParamsBuf);
            Shader.SetGlobalVectorArray(OilSkyMoonParamsId, s_moonParamsBuf);
            Shader.SetGlobalFloatArray(OilSkyStarIntensityId, s_starBuf);
            Shader.SetGlobalFloat(OilSkyLookReadyId, 1f);
            NotifyPlanarIfSkyChanged(HoursToPeriod(s_clockHours));
        }

        void SyncSkyMaterial(Material mat, float period, bool force)
        {
            // Stars live on the shared skybox material — only drive _Period from the clock.
            if (!force && Mathf.Abs(period - _lastSyncedPeriod) < 1e-5f)
            {
                return;
            }

            _lastSyncedPeriod = period;

            mat.SetFloat(PeriodId, period);
            Shader.SetGlobalFloat(OilSkyPeriodId, period);
            PushLookGlobals(ActiveLookId);
            // PushLookGlobals notifies from clock hours; re-check with the period
            // actually written so a mid-lerp clock and material stay aligned.
            NotifyPlanarIfSkyChanged(period);
            EnsureFogCanvasWeave();
            PushFogCanvasGlobal();

            ResolveCelestialDirections(period, out Vector3 sunDir, out Vector3 moonDir);
            PushCelestialGlobals(sunDir, moonDir);
        }

        /// <summary>
        /// Soft OilCanvas weave as the shared fog dry-brush (not OilBrush dabs).
        /// Skybox material no longer carries brush slots — use fogCanvasWeave / shared asset.
        /// </summary>
        const string FogCanvasAssetPath = "Assets/CustomSRP/Textures/OilCanvas.png";
        static Texture s_fogCanvasTex;

        void EnsureFogCanvasWeave()
        {
            if (fogCanvasWeave != null)
            {
                s_fogCanvasTex = fogCanvasWeave;
                return;
            }

#if UNITY_EDITOR
            fogCanvasWeave = AssetDatabase.LoadAssetAtPath<Texture>(FogCanvasAssetPath);
            if (fogCanvasWeave != null)
            {
                s_fogCanvasTex = fogCanvasWeave;
            }
#endif
        }

        static void PushFogCanvasGlobal()
        {
            if (s_fogCanvasTex == null)
            {
                Shader.SetGlobalFloat(OilFogCanvasEnableId, 0f);
                return;
            }

            Shader.SetGlobalTexture(OilFogCanvasId, s_fogCanvasTex);
            Shader.SetGlobalFloat(OilFogCanvasEnableId, 1f);
            Shader.SetGlobalVector(OilFogCanvasStId, new Vector4(0.016f, 0.016f, 0f, 0f));
        }

        /// <summary>
        /// Editor/scene setup: drive clock _Period + look globals only.
        /// Stars stay on the material asset — do not overwrite artist values.
        /// </summary>
        public static void SyncSkyMaterialStatic(Material mat, float period)
        {
            if (mat == null)
            {
                return;
            }

            mat.SetFloat(PeriodId, period);
            Shader.SetGlobalFloat(OilSkyPeriodId, period);
            PushLookGlobals(s_activeLookId);
#if UNITY_EDITOR
            if (s_fogCanvasTex == null)
            {
                s_fogCanvasTex = AssetDatabase.LoadAssetAtPath<Texture>(FogCanvasAssetPath);
            }
#endif
            PushFogCanvasGlobal();
            Shader.SetGlobalVector(OilSkySunDirId, SunDirectionAt(period));
            Shader.SetGlobalVector(OilSkyMoonDirId, MoonDirectionAt(period));
        }

        static float _lastEnvironmentPeriod = float.NaN;
        static OilCloudLayer _cachedCloudLayer;

        /// <summary>Foggy weather weight 0–1 for OilDistanceFog.Soften (set by OilAtmosphereDriver).</summary>
        static float s_foggyBlend;

        public static void SetFoggyBlend(float foggy01To1) =>
            s_foggyBlend = Mathf.Clamp01(foggy01To1);

        /// <summary>
        /// Boluo Trilight ambient (Cozy ambientLightZenith/Horizon × mul). NPR oilFill
        /// is uploaded as _OilPeriodAmbient for fog / legacy readers — far tree cross
        /// billboards share OilFoliageLighting (SH9) with near leaves, not period multiply.
        /// </summary>
        static void ApplyAmbient(float period)
        {
            EvaluateAmbient(period, ResolveCumulus(), out Color sky, out Color equator, out Color ground, out Color fill);
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = sky;
            RenderSettings.ambientEquatorColor = equator;
            RenderSettings.ambientGroundColor = ground;
            RenderSettings.ambientIntensity = 1f;
            // Vector, not Color: Linear SetGlobalColor sRGB→linear crushes night greens
            // (same pitfall as BoluoTreeBillboardAmbientSync).
            Shader.SetGlobalVector(OilPeriodAmbientId, new Vector4(fill.r, fill.g, fill.b, 1f));

            if (float.IsNaN(_lastEnvironmentPeriod) || Mathf.Abs(period - _lastEnvironmentPeriod) > 0.02f)
            {
                _lastEnvironmentPeriod = period;
                DynamicGI.UpdateEnvironment();
            }
        }

        /// <summary>
        /// Cozy ambient channels + Oil NPR fill. Cumulus dims equator/ground.
        /// </summary>
        public static void EvaluateAmbient(
            float period,
            float cumulus,
            out Color sky,
            out Color equator,
            out Color ground,
            out Color oilFill)
        {
            if (s_overrideColors)
            {
                float horizonScale = 1f - Mathf.Clamp01(cumulus) * 0.5f;
                sky = s_colorSet.ambientZenith;
                equator = s_colorSet.ambientHorizon * horizonScale;
                ground = s_colorSet.ambientHorizon * new Color(0.5f, 0.5f, 0.5f, 1f) * horizonScale;
                oilFill = new Color(
                    equator.r * 0.62f + sky.r * 0.38f,
                    equator.g * 0.62f + sky.g * 0.38f,
                    equator.b * 0.62f + sky.b * 0.38f,
                    1f);
                return;
            }

            if (LookBlendOpen)
            {
                OilSkyLookTables.EvaluateAmbient(
                    s_blendFrom, period, cumulus, out Color sky0, out Color eq0, out Color ground0, out Color fill0);
                OilSkyLookTables.EvaluateAmbient(
                    s_blendTo, period, cumulus, out sky, out equator, out ground, out oilFill);
                sky = Color.Lerp(sky0, sky, s_blendT);
                equator = Color.Lerp(eq0, equator, s_blendT);
                ground = Color.Lerp(ground0, ground, s_blendT);
                oilFill = Color.Lerp(fill0, oilFill, s_blendT);
                return;
            }

            OilSkyLookTables.EvaluateAmbient(
                s_activeLookId, period, cumulus, out sky, out equator, out ground, out oilFill);
        }

        static float ResolveCumulus()
        {
            if (_cachedCloudLayer == null)
            {
                _cachedCloudLayer = Object.FindAnyObjectByType<OilCloudLayer>();
            }

            return _cachedCloudLayer != null ? _cachedCloudLayer.coverage : 0f;
        }

        /// <summary>Main-light tint/intensity at period (same table as <see cref="ApplySun"/>).</summary>
        public static void EvaluateLight(float period, out Color color, out float intensity)
        {
            if (s_overrideColors)
            {
                color = s_colorSet.lightColor;
                intensity = Mathf.Max(0f, s_colorSet.lightIntensity);
                return;
            }

            if (LookBlendOpen)
            {
                OilSkyLookTables.EvaluateLight(s_blendFrom, period, out Color color0, out float intensity0);
                OilSkyLookTables.EvaluateLight(s_blendTo, period, out color, out intensity);
                color = Color.Lerp(color0, color, s_blendT);
                intensity = Mathf.Lerp(intensity0, intensity, s_blendT);
                return;
            }

            OilSkyLookTables.EvaluateLight(s_activeLookId, period, out color, out intensity);
        }

        /// <summary>
        /// Distance fog tint from the active look table, softened like BoluoDistanceFog.
        /// Follows the same period blend as the main light (no 21:00 snap).
        /// </summary>
        public static void EvaluateFog(float period, out Color fogColor)
        {
            if (s_overrideColors)
            {
                fogColor = s_colorSet.fogColor;
                return;
            }

            if (LookBlendOpen)
            {
                OilSkyLookTables.EvaluateFog(s_blendFrom, period, out Color a, s_foggyBlend);
                OilSkyLookTables.EvaluateFog(s_blendTo, period, out Color b, s_foggyBlend);
                fogColor = Color.Lerp(a, b, s_blendT);
                return;
            }

            OilSkyLookTables.EvaluateFog(s_activeLookId, period, out fogColor, s_foggyBlend);
        }

        /// <summary>Cozy fogDensityMultiplier scale relative to noon (preset density × this).</summary>
        public static float EvaluateFogDensityScale(float period)
        {
            if (!LookBlendOpen)
                return OilSkyLookTables.EvaluateFogDensityScale(s_activeLookId, period);
            float from = OilSkyLookTables.EvaluateFogDensityScale(s_blendFrom, period);
            float to = OilSkyLookTables.EvaluateFogDensityScale(s_blendTo, period);
            return Mathf.Lerp(from, to, s_blendT);
        }

        /// <summary>
        /// Boluo SkyWeatherController.ApplyCelestialOrbits — Cozy sun pivot.
        /// parent(0, sunDirection, sunPitch) + localX = dayT·360 − 90; dir = −child.forward.
        /// </summary>
        public static Vector3 SunDirectionFromDayT(
            float dayT,
            float sunDirectionDeg = DefaultSunDirectionDeg,
            float sunPitchDeg = DefaultSunPitchDeg)
        {
            float t = Mathf.Clamp01(Mathf.Repeat(dayT, 1f));
            Quaternion pivot = Quaternion.Euler(0f, sunDirectionDeg, sunPitchDeg);
            Quaternion child = Quaternion.Euler(t * 360f - 90f, 0f, 0f);
            Vector3 dir = -(pivot * child * Vector3.forward);
            return dir.sqrMagnitude > 1e-8f ? dir.normalized : Vector3.up;
        }

        /// <summary>
        /// Boluo moon satellite: orbitOffset=-95, satelliteDirection=11.5, satellitePitch=-30,
        /// rotationPeriod=28 at AbsoluteDay=0 (SatRotationAtDay0 keeps midnight continuous).
        /// </summary>
        public static Vector3 MoonDirectionFromDayT(
            float dayT,
            float sunDirectionDeg = DefaultSunDirectionDeg,
            float sunPitchDeg = DefaultSunPitchDeg)
        {
            float t = Mathf.Clamp01(Mathf.Repeat(dayT, 1f));
            float moonLocalX = 360f * t + SatRotationAtDay0Deg + MoonOrbitOffsetDeg - 90f;
            Quaternion pivot = Quaternion.Euler(
                0f, sunDirectionDeg + MoonSatelliteDirectionDeg, sunPitchDeg + MoonSatellitePitchDeg);
            Quaternion child = Quaternion.Euler(moonLocalX, 0f, 0f);
            Vector3 dir = -(pivot * child * Vector3.forward);
            return dir.sqrMagnitude > 1e-8f ? dir.normalized : Vector3.up;
        }

        public static Vector3 SunDirectionAt(float period) =>
            SunDirectionFromDayT(PeriodToHours(period) / 24f);

        public static Vector3 MoonDirectionAt(float period) =>
            MoonDirectionFromDayT(PeriodToHours(period) / 24f);

        /// <summary>
        /// Boluo / Cozy night handoff: sun height −0.03 → −0.23 blends to moon.
        /// Ambient stays on the clock period; only the main-light direction follows the visible disc.
        /// </summary>
        public static float MoonBlendAt(float period)
        {
            Vector3 sunDir = SunDirectionAt(period);
            return Mathf.Clamp01((-0.03f - sunDir.y) * 5f);
        }

        public static float MoonBlendFromSun(Vector3 sunDir) =>
            Mathf.Clamp01((-0.03f - sunDir.y) * 5f);

        /// <summary>
        /// Main light points at the visible celestial body (sun by day, moon after sunset).
        /// </summary>
        public static Vector3 CelestialLightDirectionAt(float period)
        {
            return CelestialLightDirection(SunDirectionAt(period), MoonDirectionAt(period));
        }

        public static Vector3 CelestialLightDirection(Vector3 sunDir, Vector3 moonDir)
        {
            float moonBlend = MoonBlendFromSun(sunDir);
            Vector3 lerped = Vector3.Lerp(sunDir, moonDir, moonBlend);
            float mag = lerped.magnitude;
            return mag > 1e-6f ? lerped / mag : sunDir;
        }

        [System.Serializable]
        public struct OilSkyColorSet
        {
            public Color zenith;
            public Color mid;
            public Color horizon;
            public Color ground;
            public Color sunDisc;
            public Color sunGlow;
            public Color moonDisc;
            public Color moonGlow;
            public Color lightColor;
            public float lightIntensity;
            public Color fogColor;
            public Color ambientZenith;
            public Color ambientHorizon;

            /// <summary>Camera_1 sky + fog + ambient @ 12:00 (ctime 32768).</summary>
            public static OilSkyColorSet DefaultDay => new OilSkyColorSet
            {
                zenith = new Color(0.0685f, 0.4793f, 1f),
                mid = new Color(0.2739f, 0.5619f, 0.9154f),
                horizon = new Color(0.4793f, 0.6445f, 0.8308f),
                ground = new Color(0.8392f, 0.7686f, 0.7020f),
                sunDisc = new Color(1f, 0.9804f, 0.8235f),
                sunGlow = new Color(1f, 0.9804f, 0.8235f),
                moonDisc = Color.black,
                moonGlow = Color.black,
                lightColor = new Color(1f, 0.9804f, 0.8235f),
                lightIntensity = 1.5f,
                fogColor = new Color(0.4252f, 0.6011f, 0.8585f),
                ambientZenith = new Color(0.5922f, 0.7098f, 0.4824f),
                ambientHorizon = new Color(0.4824f, 0.7098f, 0.6628f),
            };
        }

        public static Vector3 DirectionFromAngles(float elevationDeg, float azimuthDeg)
        {
            float el = elevationDeg * Mathf.Deg2Rad;
            float az = azimuthDeg * Mathf.Deg2Rad;
            float c = Mathf.Cos(el);
            return new Vector3(Mathf.Sin(az) * c, Mathf.Sin(el), Mathf.Cos(az) * c);
        }
    }
}
