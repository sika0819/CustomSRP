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

        public Material skyboxMaterial;
        public Light sun;
        public bool driveSun = true;

        [Header("Color Override")]
        [Tooltip("打开后天空、雾、环境光、主光颜色用下面这组，不再跟时间表走。日月方向仍跟时钟。")]
        public bool overrideColors;

        public OilSkyColorSet colorSet = OilSkyColorSet.DefaultDay;

        static bool s_overrideColors;
        static OilSkyColorSet s_colorSet = OilSkyColorSet.DefaultDay;

        [Header("Brush")]
        [Range(0.5f, 8f)] public float brushScale = 8f;
        [Range(0f, 1f)] public float brushStrength = 1f;
        [Range(0.5f, 4f)] public float brushContrast = 1.8f;
        [Range(0f, 1f)] public float brushRelief = 1f;

        [Header("Stars")]
        [Range(0.25f, 3f)]
        [Tooltip("夜晚星点密度。1 为默认，越大越密。")]
        public float starDensity = 1f;

        [Range(0.25f, 3f)]
        [Tooltip("单颗星角半径缩放。1 为默认，越大单颗越大。")]
        public float starSize = 1f;

        Material _runtime;
        /// <summary>Optional override that bypasses the 4-anchor LightPresets interpolation. Null = use the time-of-day table.</summary>
        Color? _lightColorOverride;
#if UNITY_EDITOR
        Material _editorPreview;
        bool _editorSunDriveQueued;
#endif

        public const string ShaderName = "CustomSRP/OilSkyboxNPR";

        static readonly int BrushScaleId = Shader.PropertyToID("_BrushScale");
        static readonly int BrushStrengthId = Shader.PropertyToID("_BrushStrength");
        static readonly int BrushContrastId = Shader.PropertyToID("_BrushContrast");
        static readonly int BrushReliefId = Shader.PropertyToID("_BrushRelief");
        static readonly int StarDensityId = Shader.PropertyToID("_StarDensity");
        static readonly int StarSizeId = Shader.PropertyToID("_StarSize");
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
        float _lastBrushScale = float.NaN;
        float _lastBrushStrength = float.NaN;
        float _lastBrushContrast = float.NaN;
        float _lastBrushRelief = float.NaN;
        float _lastStarDensity = float.NaN;
        float _lastStarSize = float.NaN;

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
                case 2: // 独自旅行 — 青白天、薄荷植被
                    set = Poster(
                        0xA9, 0xC3, 0xDD, 0x8E, 0xC8, 0xD4, 0x7E, 0xC8, 0xC8,
                        0x5A, 0xA8, 0xB8, 0x7E, 0xD0, 0xC0,
                        0xF2, 0xF7, 0xF4, 1.15f, 0x7E, 0xC8, 0xC8);
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
                moonDisc = night ? new Color(0.85f, 0.90f, 1f, 1f) : Color.black,
                moonGlow = night ? new Color(0.45f, 0.55f, 0.80f, 1f) : Color.black,
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
            if (ActiveLookId == lookId && s_activeLookId == lookId)
            {
                PushLookGlobals(lookId);
                return;
            }

            ActiveLookId = lookId;
            s_activeLookId = lookId;
            PushLookGlobals(lookId);
            Apply(markDirty: true);
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
            OilAtmosphereDriver.EnsureOnSkybox();
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
        }

        void OnDisable()
        {
#if UNITY_EDITOR
            CancelEditorSunDrive();
#endif
            if (_runtime == null)
            {
                return;
            }

            if (RenderSettings.skybox == _runtime && skyboxMaterial != null)
            {
                RenderSettings.skybox = skyboxMaterial;
            }

            if (Application.isPlaying)
            {
                Destroy(_runtime);
            }
            else
            {
                DestroyImmediate(_runtime);
            }

            _runtime = null;
        }

        void OnDestroy()
        {
#if UNITY_EDITOR
            CancelEditorSunDrive();
            if (_editorPreview != null)
            {
                if (RenderSettings.skybox == _editorPreview)
                {
                    RenderSettings.skybox = skyboxMaterial;
                }

                DestroyImmediate(_editorPreview);
                _editorPreview = null;
            }
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
            Material mat = RenderSettings.skybox;
            if (IsOilSky(mat))
            {
                return mat;
            }

            mat = ActiveSkyMaterial();
            if (mat != null)
            {
                BindSkybox();
            }

            return mat;
        }

        Material ActiveSkyMaterial()
        {
            if (!IsOilSky(skyboxMaterial))
            {
                return null;
            }

            if (Application.isPlaying)
            {
                if (_runtime == null)
                {
                    _runtime = new Material(skyboxMaterial)
                    {
                        name = skyboxMaterial.name + " Runtime"
                    };
                }

                return _runtime;
            }

#if UNITY_EDITOR
            if (_editorPreview == null || _editorPreview.shader != skyboxMaterial.shader)
            {
                if (_editorPreview != null)
                {
                    DestroyImmediate(_editorPreview);
                }

                _editorPreview = new Material(skyboxMaterial)
                {
                    name = skyboxMaterial.name + " (Editor Preview)",
                    hideFlags = HideFlags.HideAndDontSave
                };
            }

            return _editorPreview;
#else
            return skyboxMaterial;
#endif
        }

        void BindSkybox()
        {
            Material target = ActiveSkyMaterial();
            if (target == null || RenderSettings.skybox == target)
            {
                return;
            }

            RenderSettings.skybox = target;
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

            // Lighting window default / manual density (~0.1+) milks ExpSquared into a white slab.
            // Authored haze stays near 0.001; clamp only pathological values.
            if (RenderSettings.fogDensity > 0.05f)
            {
                RenderSettings.fogDensity = 0.001f;
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

        public static SkyLookPreset EvaluateSkyLook(float period) =>
            EvaluateSkyLook(s_activeLookId, period);

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
                look.sunIntensity = Mathf.Lerp(look.sunIntensity, 1.05f, sunrise);
                look.sunGlow = Mathf.Lerp(look.sunGlow, 0.28f, sunrise);
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
                moonGlow = night ? 0.16f : 0f,
                starIntensity = night ? 0.9f : 0.02f,
            };
        }

        static void PushOverrideLookGlobals()
        {
            OilSkyColorSet c = s_colorSet;
            bool night = c.lightIntensity < 0.35f;
            var sunParams = night ? new Vector4(0.02f, 0.02f, 0.02f, 0f) : new Vector4(0.07f, 1.05f, 0.22f, 0f);
            var moonParams = night ? new Vector4(0.07f, 0.55f, 0.16f, 0f) : new Vector4(0.02f, 0f, 0f, 0f);
            float stars = night ? 0.9f : 0.02f;
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
        }

        /// <summary>Push 4-anchor sky pigments to the OilSkyboxNPR shader.</summary>
        public static void PushLookGlobals(OilSkyLookId lookId)
        {
            if (s_overrideColors)
            {
                PushOverrideLookGlobals();
                return;
            }

            var table = OilSkyLookTables.Get(lookId);
            float sunrise = OilSkyLookTables.CanonicalSunriseWeight(lookId, s_clockHours);
            for (int i = 0; i < 4; i++)
            {
                var sky = table.sky[i];
                if (i == 0 && sunrise > 0.001f)
                {
                    sky.zenith = Color.Lerp(sky.zenith, OilSkyLookTables.SunriseSkyZenith, sunrise);
                    sky.mid = Color.Lerp(sky.mid, OilSkyLookTables.SunriseSkyMid, sunrise);
                    sky.horizon = Color.Lerp(sky.horizon, OilSkyLookTables.SunriseSkyHorizon, sunrise);
                    sky.sunDisc = Color.Lerp(sky.sunDisc, OilSkyLookTables.SunriseSunDisc, sunrise);
                    sky.sunGlow = Color.Lerp(sky.sunGlow, OilSkyLookTables.SunriseSun, sunrise);
                    sky.sunIntensity = Mathf.Lerp(sky.sunIntensity, 1.05f, sunrise);
                    sky.sunGlowAmt = Mathf.Lerp(sky.sunGlowAmt, 0.28f, sunrise);
                }

                s_zenithBuf[i] = sky.zenith;
                s_midBuf[i] = sky.mid;
                s_horizonBuf[i] = sky.horizon;
                s_groundBuf[i] = sky.ground;
                s_sunDiscBuf[i] = sky.sunDisc;
                s_sunGlowBuf[i] = sky.sunGlow;
                s_moonDiscBuf[i] = sky.moonDisc;
                s_moonGlowBuf[i] = sky.moonGlow;
                s_sunParamsBuf[i] = new Vector4(sky.sunSize, sky.sunIntensity, sky.sunGlowAmt, 0f);
                s_moonParamsBuf[i] = new Vector4(sky.moonSize, sky.moonIntensity, sky.moonGlowAmt, 0f);
                s_starBuf[i] = sky.starIntensity;
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
        }

        void SyncSkyMaterial(Material mat, float period, bool force)
        {
            if (!force && Mathf.Abs(period - _lastSyncedPeriod) < 1e-5f
                && Mathf.Approximately(brushScale, _lastBrushScale)
                && Mathf.Approximately(brushStrength, _lastBrushStrength)
                && Mathf.Approximately(brushContrast, _lastBrushContrast)
                && Mathf.Approximately(brushRelief, _lastBrushRelief)
                && Mathf.Approximately(starDensity, _lastStarDensity)
                && Mathf.Approximately(starSize, _lastStarSize))
            {
                return;
            }

            _lastSyncedPeriod = period;
            _lastBrushScale = brushScale;
            _lastBrushStrength = brushStrength;
            _lastBrushContrast = brushContrast;
            _lastBrushRelief = brushRelief;
            _lastStarDensity = starDensity;
            _lastStarSize = starSize;

            mat.SetFloat(PeriodId, period);
            Shader.SetGlobalFloat(OilSkyPeriodId, period);
            mat.SetFloat(BrushScaleId, brushScale);
            mat.SetFloat(BrushStrengthId, brushStrength);
            mat.SetFloat(BrushContrastId, brushContrast);
            mat.SetFloat(BrushReliefId, brushRelief);
            mat.SetFloat(StarDensityId, starDensity);
            mat.SetFloat(StarSizeId, starSize);
            PushLookGlobals(ActiveLookId);
            PushFogCanvasGlobal(mat);

            ResolveCelestialDirections(period, out Vector3 sunDir, out Vector3 moonDir);
            PushCelestialGlobals(sunDir, moonDir);
        }

        /// <summary>
        /// Soft OilCanvas weave as the shared fog dry-brush (not OilBrush dabs).
        /// Large tiles keep the haze scumbled instead of dense trypophobic holes.
        /// </summary>
        static void PushFogCanvasGlobal(Material mat)
        {
            if (mat == null)
            {
                return;
            }

            Texture canvas = mat.HasProperty("_OilCanvas") ? mat.GetTexture("_OilCanvas") : null;
            if (canvas == null)
            {
                Shader.SetGlobalFloat(OilFogCanvasEnableId, 0f);
                return;
            }

            Shader.SetGlobalTexture(OilFogCanvasId, canvas);
            Shader.SetGlobalFloat(OilFogCanvasEnableId, 1f);
            Shader.SetGlobalVector(OilFogCanvasStId, new Vector4(0.016f, 0.016f, 0f, 0f));
        }

        /// <summary>Editor setup: textures + brush/star defaults; sky colors blend in shader from _Period.</summary>
        public static void SyncSkyMaterialStatic(
            Material mat,
            float period,
            float brushScale,
            float brushStrength,
            float brushContrast,
            float brushRelief,
            float starDensity = 1f,
            float starSize = 1f)
        {
            mat.SetFloat(PeriodId, period);
            Shader.SetGlobalFloat(OilSkyPeriodId, period);
            mat.SetFloat(BrushScaleId, brushScale);
            mat.SetFloat(BrushStrengthId, brushStrength);
            mat.SetFloat(BrushContrastId, brushContrast);
            mat.SetFloat(BrushReliefId, brushRelief);
            mat.SetFloat(StarDensityId, starDensity);
            mat.SetFloat(StarSizeId, starSize);
            PushLookGlobals(s_activeLookId);
            PushFogCanvasGlobal(mat);
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
        /// Boluo Trilight ambient (Cozy ambientLightZenith/Horizon × mul). NPR uses oilFill.
        /// Also nails far-tree billboard period multiply so ApplyOilFog can melt into haze.
        /// </summary>
        static void ApplyAmbient(float period)
        {
            EvaluateAmbient(period, ResolveCumulus(), out Color sky, out Color equator, out Color ground, out Color fill);
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = sky;
            RenderSettings.ambientEquatorColor = equator;
            RenderSettings.ambientGroundColor = ground;
            RenderSettings.ambientIntensity = 1f;
            Shader.SetGlobalColor(OilPeriodAmbientId, fill);
            // Far trees now read _OilPeriodAmbient via OilFoliageLighting — same path
            // as near leaves. The legacy _OilTreeBBAmbient upload was a 4-anchor fixed
            // palette that left far trees yellow-green against the foliage.

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

            OilSkyLookTables.EvaluateLight(s_activeLookId, period, out color, out intensity);
        }

        /// <summary>
        /// Distance fog tint from the active look table, softened like BoluoDistanceFog.
        /// Night clock (21:00–06:00) uses that look's night fog.
        /// </summary>
        public static void EvaluateFog(float period, out Color fogColor)
        {
            if (s_overrideColors)
            {
                fogColor = s_colorSet.fogColor;
                return;
            }

            OilSkyLookTables.EvaluateFog(s_activeLookId, period, out fogColor, s_foggyBlend);
        }

        /// <summary>Cozy fogDensityMultiplier scale relative to noon (preset density × this).</summary>
        public static float EvaluateFogDensityScale(float period) =>
            OilSkyLookTables.EvaluateFogDensityScale(s_activeLookId, period);

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

            public static OilSkyColorSet DefaultDay => new OilSkyColorSet
            {
                zenith = new Color(0.0667f, 0.4784f, 1f),
                mid = new Color(0.361f, 0.632f, 0.965f),
                horizon = new Color(0.7216f, 0.8196f, 0.9216f),
                ground = new Color(0.8392f, 0.7686f, 0.7020f),
                sunDisc = new Color(1f, 0.9804f, 0.8235f),
                sunGlow = new Color(1f, 0.9804f, 0.8235f),
                moonDisc = Color.black,
                moonGlow = Color.black,
                lightColor = new Color(1f, 0.9804f, 0.8235f),
                lightIntensity = 1.5f,
                fogColor = new Color(0.424f, 0.600f, 0.855f),
                ambientZenith = new Color(0.592f, 0.710f, 0.482f),
                ambientHorizon = new Color(0.482f, 0.710f, 0.663f),
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
