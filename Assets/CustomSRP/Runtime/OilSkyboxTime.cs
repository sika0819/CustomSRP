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
    /// Parallel light uses the Moorea light table below.
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

            public static SkyLookPreset Dawn => new SkyLookPreset
            {
                zenith = new Color(0.10f, 0.18f, 0.52f),
                mid = new Color(0.34f, 0.30f, 0.46f),
                horizon = new Color(0.86f, 0.55f, 0.28f),
                ground = new Color(0.28f, 0.18f, 0.12f),
                sunSize = 0.07f,
                sunIntensity = 0.9f,
                sunGlow = 0.32f,
                moonSize = 0.05f,
                moonIntensity = 0.12f,
                moonGlow = 0.06f,
                starIntensity = 0f,
            };

            public static SkyLookPreset Day => new SkyLookPreset
            {
                zenith = new Color(0.06f, 0.24f, 0.68f),
                mid = new Color(0.26f, 0.46f, 0.70f),
                horizon = new Color(0.50f, 0.64f, 0.74f),
                ground = new Color(0.24f, 0.32f, 0.36f),
                sunSize = 0.055f,
                sunIntensity = 1f,
                sunGlow = 0.16f,
                moonSize = 0.05f,
                moonIntensity = 0f,
                moonGlow = 0f,
                starIntensity = 0f,
            };

            public static SkyLookPreset Dusk => new SkyLookPreset
            {
                zenith = new Color(0.05f, 0.06f, 0.16f),
                mid = new Color(0.28f, 0.12f, 0.14f),
                horizon = new Color(0.72f, 0.28f, 0.12f),
                ground = new Color(0.16f, 0.07f, 0.06f),
                sunSize = 0.08f,
                sunIntensity = 0.75f,
                sunGlow = 0.38f,
                moonSize = 0.055f,
                moonIntensity = 0.45f,
                moonGlow = 0.14f,
                starIntensity = 0.12f,
            };

            public static SkyLookPreset Night => new SkyLookPreset
            {
                zenith = new Color(0.07f, 0.05f, 0.18f),
                mid = new Color(0.11f, 0.07f, 0.22f),
                horizon = new Color(0.16f, 0.10f, 0.22f),
                ground = new Color(0.05f, 0.04f, 0.09f),
                sunSize = 0.05f,
                sunIntensity = 0.08f,
                sunGlow = 0.02f,
                moonSize = 0.07f,
                moonIntensity = 0.85f,
                moonGlow = 0.22f,
                starIntensity = 0.58f,
            };

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

        struct LightPreset
        {
            public float elevation;
            public float azimuth;
            public Color color;
            public float intensity;
        }

        // Moorea 17.538°S, 149.83°W, UTC−10. Equinox sun (declination ≈ 0), +Z north, azimuth from north.
        // Anchors: 06:00 / 12:00 / 18:00 / 00:00 → period 0 / 1 / 2 / 3.
        // Intensity follows sin(elevation), floored through twilight.
        // Linear color: 清晨淡金 / 白天白 / 黄昏淡橘红 / 夜晚蓝黑.
        static readonly LightPreset[] LightPresets =
        {
            new()
            {
                elevation = -1.69f, azimuth = 90.60f,
                color = new Color(1.00f, 0.78f, 0.40f), intensity = 0.47f,
            },
            new()
            {
                elevation = 72.44f, azimuth = 5.96f,
                color = Color.white, intensity = 2.0f,
            },
            new()
            {
                elevation = 1.73f, azimuth = 270.48f,
                color = new Color(1.00f, 0.48f, 0.22f), intensity = 0.57f,
            },
            new()
            {
                elevation = -72.31f, azimuth = 174.09f,
                color = new Color(0.10f, 0.16f, 0.55f), intensity = 0.18f,
            },
        };

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
        static readonly int OilPeriodAmbientId = Shader.PropertyToID("_OilPeriodAmbient");

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

        public void SetTimeOfDay(float hours)
        {
            timeOfDay = Mathf.Repeat(hours, 24f);
            timeOfDayVersion = TimeOfDayVersionHours;
            Apply(markDirty: Application.isPlaying);
        }

        /// <summary>
        /// Inspector / editor preview: sky _Period, ambient, and deferred sun drive.
        /// </summary>
        public void ApplyEditorHours(float hours)
        {
            timeOfDay = Mathf.Repeat(hours, 24f);
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
                Light[] lights = FindObjectsByType<Light>(FindObjectsSortMode.None);
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

        bool driveSunOnNextUpdate;

        void OnEnable()
        {
            // Edit mode: no auto Apply on load (Unity 6 delayCall / same-GO Transform recursion).
            // Inspector EndChangeCheck / period buttons call ApplyEditorPreview.
            if (!Application.isPlaying)
            {
                return;
            }

            ApplySkyOnly();
            driveSunOnNextUpdate = driveSun && sun != null;
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

        void Update()
        {
            if (!Application.isPlaying)
            {
                return;
            }

            if (driveSunOnNextUpdate)
            {
                driveSunOnNextUpdate = false;
                Apply(markDirty: false);
            }

            if (!animate || cycleSeconds <= 0.01f)
            {
                return;
            }

            timeOfDay = Mathf.Repeat(timeOfDay + Time.deltaTime / cycleSeconds * 24f, 24f);
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
            BindSkybox();
            Material mat = ActiveSkyMaterial();
            if (mat == null)
            {
                return;
            }

            float period = CurrentPeriod();
            SyncSkyMaterial(mat, period, force: markDirty || !Application.isPlaying);
            ApplyAmbient(period);

            if (!driveSun || sun == null)
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

        void ApplySun(float period)
        {
            Vector3 sunDir = SunDirectionAt(period);
            if (sunDir.sqrMagnitude <= 0.001f)
            {
                return;
            }

            EvaluateLight(period, out Color color, out float intensity);
            if (sun.color != color)
            {
                sun.color = color;
            }

            if (!Mathf.Approximately(sun.intensity, intensity))
            {
                sun.intensity = intensity;
            }

            Vector3 forward = -sunDir;
            Vector3 up = Mathf.Abs(Vector3.Dot(forward.normalized, Vector3.up)) > 0.98f
                ? Vector3.forward
                : Vector3.up;
            Quaternion rot = Quaternion.LookRotation(forward, up);
            if (sun.transform.rotation != rot)
            {
                sun.transform.rotation = rot;
            }
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
            float wrapped = Mathf.Repeat(period, 4f);
            int i0 = Mathf.FloorToInt(wrapped) % 4;
            int i1 = (i0 + 1) % 4;
            float t = wrapped - Mathf.Floor(wrapped);
            SkyLookPreset a = SkyLookAtIndex(i0);
            SkyLookPreset b = SkyLookAtIndex(i1);
            return SkyLookPreset.Lerp(a, b, t);
        }

        static SkyLookPreset SkyLookAtIndex(int index)
        {
            return index switch
            {
                0 => SkyLookPreset.Dawn,
                1 => SkyLookPreset.Day,
                2 => SkyLookPreset.Dusk,
                _ => SkyLookPreset.Night,
            };
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
            mat.SetFloat(BrushScaleId, brushScale);
            mat.SetFloat(BrushStrengthId, brushStrength);
            mat.SetFloat(BrushContrastId, brushContrast);
            mat.SetFloat(BrushReliefId, brushRelief);
            mat.SetFloat(StarDensityId, starDensity);
            mat.SetFloat(StarSizeId, starSize);
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
            mat.SetFloat(BrushScaleId, brushScale);
            mat.SetFloat(BrushStrengthId, brushStrength);
            mat.SetFloat(BrushContrastId, brushContrast);
            mat.SetFloat(BrushReliefId, brushRelief);
            mat.SetFloat(StarDensityId, starDensity);
            mat.SetFloat(StarSizeId, starSize);
        }

        static float _lastEnvironmentPeriod = float.NaN;

        /// <summary>
        /// Environment lighting source is the skybox. Oil NPR does not sample SH,
        /// so the fill color is the same sky pigment (horizon weighted toward zenith).
        /// </summary>
        static void ApplyAmbient(float period)
        {
            SkyLookPreset look = EvaluateSkyLook(period);
            RenderSettings.ambientMode = AmbientMode.Skybox;
            RenderSettings.ambientIntensity = 1f;
            Color fill = new Color(
                look.horizon.r * 0.62f + look.zenith.r * 0.38f,
                look.horizon.g * 0.62f + look.zenith.g * 0.38f,
                look.horizon.b * 0.62f + look.zenith.b * 0.38f,
                1f);
            Shader.SetGlobalColor(OilPeriodAmbientId, fill);

            if (float.IsNaN(_lastEnvironmentPeriod) || Mathf.Abs(period - _lastEnvironmentPeriod) > 0.02f)
            {
                _lastEnvironmentPeriod = period;
                DynamicGI.UpdateEnvironment();
            }
        }

        static void EvaluateLight(float period, out Color color, out float intensity)
        {
            float wrapped = Mathf.Repeat(period, 4f);
            int i0 = Mathf.FloorToInt(wrapped) % 4;
            int i1 = (i0 + 1) % 4;
            float t = wrapped - Mathf.Floor(wrapped);
            LightPreset a = LightPresets[i0];
            LightPreset b = LightPresets[i1];
            color = Color.Lerp(a.color, b.color, t);
            intensity = Mathf.Lerp(a.intensity, b.intensity, t);
        }

        static Vector3 DirAtIndex(int index)
        {
            LightPreset p = LightPresets[index];
            return DirectionFromAngles(p.elevation, p.azimuth);
        }

        /// <summary>
        /// Same path as OilSkyboxNPRPass LerpPeriodLook:
        /// normalize(lerp(dirA, dirB, t)) — not elevation/azimuth lerp.
        /// </summary>
        static Vector3 DirectionAtPeriod(float period)
        {
            float wrapped = Mathf.Repeat(period, 4f);
            int i0 = Mathf.FloorToInt(wrapped) % 4;
            int i1 = (i0 + 1) % 4;
            float t = wrapped - Mathf.Floor(wrapped);
            Vector3 a = DirAtIndex(i0);
            Vector3 b = DirAtIndex(i1);
            Vector3 lerped = Vector3.Lerp(a, b, t);
            float mag = lerped.magnitude;
            return mag > 1e-6f ? lerped / mag : a;
        }

        public static Vector3 SunDirectionAt(float period)
        {
            return DirectionAtPeriod(period);
        }

        /// <summary>Opposite arc anchor (period + 2) for moon placement, Boluo-style.</summary>
        public static Vector3 MoonDirectionAt(float period)
        {
            return DirectionAtPeriod(Mathf.Repeat(period + 2f, 4f));
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
