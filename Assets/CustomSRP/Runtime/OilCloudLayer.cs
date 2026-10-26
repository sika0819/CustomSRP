using Game;
using UnityEngine;

namespace CustomSRP
{
    /// <summary>
    /// Camera-following cloud ring (XZ). Low-ring belly anchors to sea level;
    /// the cloud pass draws a low ring and an optional smaller high ring.
    /// Pigment follows the oil sky.
    /// </summary>
    [DisallowMultipleComponent]
    public class OilCloudLayer : MonoBehaviour
    {
        public const string ShaderName = "CustomSRP/OilCloudNPR";

        public const string CloudMapPath = "Assets/CustomSRP/Textures/Cloud/CloudMap_SDF.png";
        public const string NoiseMapPath = "Assets/CustomSRP/Textures/Cloud/CloudNoise.png";
        public const string BrushPath = "Assets/CustomSRP/Textures/Cloud/Cloud_A.png";

        const float RingRadius = 400f;
        // Local mesh Y. Bottom (MeshY0) sits on sea; MeshY1 raised for taller clouds.
        const float MeshY0 = -62.5f;
        const float MeshY1 = 110f;
        const float HighLocalY = 80f;
        const float HighScaleMul = 0.68f;
        const float HighYawDegrees = 70f;
        const float HighSdfBias = 0.08f;
        const float WindBlendSeconds = 0.85f;
        const float CalmYawRad = 0f;
        const float StormYawRad = 16f * Mathf.Deg2Rad;

        public Material material;

        [Range(0f, 1f)]
        [Tooltip("0.12 sparse, 0.30 broken, 1 overcast. Shaped before the SDF threshold.")]
        public float coverage = 0.85f;

        [Range(0f, 1f)]
        [Tooltip("0 holds the ring. 1 turns it at 16 degrees per second.")]
        public float wind = 0.2f;

        [Tooltip("Boluo FBX ring used 1.85 on a ~400 m mesh.")]
        public float ringScale = 1.85f;

        [Tooltip("When on, low-ring belly tracks OilOceanSurface world Y.")]
        public bool followSeaLevel = true;

        [Tooltip("World Y of the low-ring belly when followSeaLevel is off, or no ocean exists.")]
        public float seaLevel = 0f;

        [Tooltip("Extra meters to lift the ring after anchoring the belly to sea. ~65 clears island ridges.")]
        public float ringHeight = 65f;

        public bool highRing = true;

        [Header("Shape")]
        [Range(0f, 0.2f)] public float uvDisturbance = 0.04f;
        [Range(0.01f, 0.5f)] public float sdfSoftness = 0.14f;
        [Range(0.003f, 1.5f)] public float sdfMin = 0.48f;
        [Range(0.003f, 1.5f)] public float sdfMax = 0.90f;

        [Header("Light")]
        [Range(0f, 1f)] public float topShadow = 1f;
        [Range(0f, 1f)] public float topHighlight = 0.92f;
        [Range(0f, 3f)] public float edgeIntensity = 1.55f;

        public static OilCloudLayer Current { get; private set; }

        public static Material ActiveMaterial =>
            Current != null ? Current.material : null;

        public static Mesh SharedRing => _ring ??= BuildRing();

        static Mesh _ring;
        static readonly int UvDisturbanceId = Shader.PropertyToID("_UVDisturbance");
        static readonly int SdfSoftnessId = Shader.PropertyToID("_SdfSoftness");
        static readonly int SdfMinId = Shader.PropertyToID("_SdfMin");
        static readonly int SdfMaxId = Shader.PropertyToID("_SdfMax");
        static readonly int TopShadowId = Shader.PropertyToID("_TopShadow");
        static readonly int TopHighlightId = Shader.PropertyToID("_TopHighlight");
        static readonly int EdgeIntensityId = Shader.PropertyToID("_EdgeIntensity");

        static readonly int SunDirId = Shader.PropertyToID("_OilCloudSunDir");
        static readonly int MoonDirId = Shader.PropertyToID("_OilCloudMoonDir");
        static readonly int ColorId = Shader.PropertyToID("_OilCloudColor");
        static readonly int HighlightId = Shader.PropertyToID("_OilCloudHighlight");
        static readonly int ZenithId = Shader.PropertyToID("_OilCloudZenith");
        static readonly int HorizonId = Shader.PropertyToID("_OilCloudHorizon");
        static readonly int AmbientSkyId = Shader.PropertyToID("_OilCloudAmbientSky");
        static readonly int AmbientEquatorId = Shader.PropertyToID("_OilCloudAmbientEquator");
        static readonly int AmbientGroundId = Shader.PropertyToID("_OilCloudAmbientGround");
        static readonly int ParamsId = Shader.PropertyToID("_OilCloudParams");
        static readonly int WindId = Shader.PropertyToID("_OilCloudWind");
        static readonly int DawnId = Shader.PropertyToID("_OilCloudDawn");
        static readonly int NightId = Shader.PropertyToID("_OilCloudNight");
        static readonly int RingBiasId = Shader.PropertyToID("_OilCloudRingBias");

        EntityId _appliedMaterialId;
        float _appliedDisturbance;
        float _appliedSoftness;
        float _appliedSdfMin;
        float _appliedSdfMax;
        float _appliedTopShadow;
        float _appliedTopHighlight;
        float _appliedEdge;
        bool _hasApplied;

        float _windIntensity;
        float _yaw;
        int _windFrame = -1;
        OilOceanSurface _ocean;

        /// <summary>Cloud ring yaw in degrees (wind advances this over time).</summary>
        public float YawDegrees
        {
            get => _yaw * Mathf.Rad2Deg;
            set => _yaw = value * Mathf.Deg2Rad;
        }

        /// <summary>
        /// Ring-local azimuth of the seam between card 7 and card 0 (emptiest sky gap).
        /// Dawn/sleep presets snap this seam toward the sun/moon so opaque cards sit off-axis.
        /// </summary>
        public const float GapLocalDegrees = 0f;

        public struct Frame
        {
            public Matrix4x4 low;
            public Matrix4x4 high;
            public bool drawHigh;
            public float highSdfBias;
            public Vector3 sunDir;
            public Vector3 moonDir;
            public Color body;
            public Color highlight;
            public Color zenith;
            public Color horizon;
            public Color ambientSky;
            public Color ambientEquator;
            public Color ambientGround;
            public Vector4 cloudParams;
            public Vector4 windVector;
            public float dawn;
            public float night;
        }

        void OnEnable()
        {
            Current = this;
            _hasApplied = false;
            Apply();
        }

        void OnDisable()
        {
            if (Current == this)
            {
                Current = null;
            }
        }

        void Update()
        {
            Apply();
        }

        void OnValidate()
        {
            ringScale = Mathf.Max(0.2f, ringScale);
            _hasApplied = false;
            Apply();
        }

        public void Apply()
        {
            if (material == null)
            {
                return;
            }

            EntityId materialId = material.GetEntityId();
            if (_hasApplied &&
                materialId == _appliedMaterialId &&
                _appliedDisturbance == uvDisturbance &&
                _appliedSoftness == sdfSoftness &&
                _appliedSdfMin == sdfMin &&
                _appliedSdfMax == sdfMax &&
                _appliedTopShadow == topShadow &&
                _appliedTopHighlight == topHighlight &&
                _appliedEdge == edgeIntensity)
            {
                return;
            }

            material.SetFloat(UvDisturbanceId, uvDisturbance);
            material.SetFloat(SdfSoftnessId, sdfSoftness);
            material.SetFloat(SdfMinId, sdfMin);
            material.SetFloat(SdfMaxId, sdfMax);
            material.SetFloat(TopShadowId, topShadow);
            material.SetFloat(TopHighlightId, topHighlight);
            material.SetFloat(EdgeIntensityId, edgeIntensity);

            _appliedMaterialId = materialId;
            _appliedDisturbance = uvDisturbance;
            _appliedSoftness = sdfSoftness;
            _appliedSdfMin = sdfMin;
            _appliedSdfMax = sdfMax;
            _appliedTopShadow = topShadow;
            _appliedTopHighlight = topHighlight;
            _appliedEdge = edgeIntensity;
            _hasApplied = true;
        }

        public Frame Evaluate(Camera camera)
        {
            TickWind();
            var sky = GetComponent<OilSkyboxTime>();
            float period = sky != null ? sky.CurrentPeriod() : 1f;
            Vector3 orbitSun;
            Vector3 orbitMoon;
            if (sky != null)
                sky.ResolveCelestialDirections(period, out orbitSun, out orbitMoon);
            else
            {
                orbitSun = OilSkyboxTime.SunDirectionAt(period);
                orbitMoon = OilSkyboxTime.MoonDirectionAt(period);
            }

            EvaluatePigment(
                period,
                coverage,
                orbitSun,
                orbitMoon,
                out Vector3 sunDir,
                out Vector3 moonDir,
                out float moonBlend,
                out float dawn,
                out float night,
                out Color body,
                out Color highlight,
                out Color zenith,
                out Color horizon,
                out Color ambientSky,
                out Color ambientEquator,
                out Color ambientGround);

            float shaped = ShapeCoverage(coverage);
            if (_windIntensity > 0.01f)
            {
                shaped *= Mathf.Lerp(1f, 0.35f, _windIntensity);
            }

            Vector3 origin = camera != null ? camera.transform.position : Vector3.zero;
            float scale = Mathf.Max(0.2f, ringScale);
            // Pin low-ring belly (local MeshY0) to sea, then apply optional lift.
            origin.y = ResolveSeaLevel() - MeshY0 * scale + ringHeight;
            Quaternion yaw = Quaternion.Euler(0f, _yaw * Mathf.Rad2Deg, 0f);
            return new Frame
            {
                low = Matrix4x4.TRS(origin, yaw, Vector3.one * scale),
                high = Matrix4x4.TRS(
                    origin + Vector3.up * HighLocalY,
                    yaw * Quaternion.Euler(0f, HighYawDegrees, 0f),
                    Vector3.one * (scale * HighScaleMul)),
                drawHigh = highRing,
                highSdfBias = HighSdfBias,
                sunDir = sunDir,
                moonDir = moonDir,
                body = body,
                highlight = highlight,
                zenith = zenith,
                horizon = horizon,
                ambientSky = ambientSky,
                ambientEquator = ambientEquator,
                ambientGround = ambientGround,
                cloudParams = new Vector4(moonBlend, shaped, 1f, 0f),
                windVector = new Vector4(
                    Mathf.Lerp(CalmYawRad, StormYawRad, _windIntensity),
                    _yaw,
                    Mathf.Lerp(0f, 0.014f, _windIntensity),
                    _windIntensity),
                dawn = dawn,
                night = night,
            };
        }

        public static void ApplyFrame(UnityEngine.Rendering.UnsafeCommandBuffer cmd, in Frame frame)
        {
            cmd.SetGlobalVector(SunDirId, frame.sunDir);
            cmd.SetGlobalVector(MoonDirId, frame.moonDir);
            cmd.SetGlobalColor(ColorId, frame.body);
            cmd.SetGlobalColor(HighlightId, frame.highlight);
            cmd.SetGlobalColor(ZenithId, frame.zenith);
            cmd.SetGlobalColor(HorizonId, frame.horizon);
            cmd.SetGlobalColor(AmbientSkyId, frame.ambientSky);
            cmd.SetGlobalColor(AmbientEquatorId, frame.ambientEquator);
            cmd.SetGlobalColor(AmbientGroundId, frame.ambientGround);
            cmd.SetGlobalVector(ParamsId, frame.cloudParams);
            cmd.SetGlobalVector(WindId, frame.windVector);
            cmd.SetGlobalFloat(DawnId, frame.dawn);
            cmd.SetGlobalFloat(NightId, frame.night);
            cmd.SetGlobalFloat(RingBiasId, 0f);
        }

        public static void SetRingBias(UnityEngine.Rendering.UnsafeCommandBuffer cmd, float bias)
        {
            cmd.SetGlobalFloat(RingBiasId, bias);
        }

        void TickWind()
        {
            if (_windFrame == Time.frameCount)
            {
                return;
            }

            _windFrame = Time.frameCount;
            float dt = Mathf.Max(0f, Time.deltaTime);
            // Get target wind from weather system. When weather is not Wind, clouds should not move.
            float target = GetWeatherWindIntensity();
            _windIntensity = Mathf.MoveTowards(_windIntensity, target, dt / WindBlendSeconds);
            float yawSpeed = Mathf.Lerp(CalmYawRad, StormYawRad, _windIntensity);
            _yaw += dt * yawSpeed;
        }

        /// <summary>
        /// Get wind intensity from weather system. When weather is not Wind type, returns 0
        /// so clouds don't move without wind.
        /// </summary>
        float GetWeatherWindIntensity()
        {
            var weather = WeatherService.Ensure();
            return weather.VisualWind01;
        }

        float ResolveSeaLevel()
        {
            if (followSeaLevel)
            {
                if (_ocean == null)
                {
                    _ocean = Object.FindAnyObjectByType<OilOceanSurface>();
                }

                if (_ocean != null)
                {
                    return _ocean.transform.position.y;
                }
            }

            return seaLevel;
        }

        /// <summary>
        /// Same steps as Boluo Cloud_1 / Cloud_2 / Cloud_3 so the three stops stay apart.
        /// </summary>
        public static float ShapeCoverage(float cumulus)
        {
            float c = Mathf.Clamp01(cumulus);
            if (c <= 0.12f)
            {
                return Mathf.Lerp(0.16f, 0.34f, c / 0.12f);
            }

            if (c <= 0.30f)
            {
                return Mathf.Lerp(0.34f, 0.70f, (c - 0.12f) / 0.18f);
            }

            return Mathf.Lerp(0.70f, 1f, (c - 0.30f) / 0.70f);
        }

        static void EvaluatePigment(
            float period,
            float cumulus,
            Vector3 sunDirIn,
            Vector3 moonDirIn,
            out Vector3 sunDir,
            out Vector3 moonDir,
            out float moonBlend,
            out float dawn,
            out float night,
            out Color body,
            out Color highlight,
            out Color zenith,
            out Color horizon,
            out Color ambientSky,
            out Color ambientEquator,
            out Color ambientGround)
        {
            OilSkyboxTime.SkyLookPreset look = OilSkyboxTime.EvaluateSkyLook(period);
            sunDir = sunDirIn;
            moonDir = moonDirIn;
            moonBlend = OilSkyboxTime.MoonBlendFromSun(sunDir);
            // Clock night (21–06, same as EvaluateFog) — moonBlend alone can lag at dusk.
            float hours = OilSkyboxTime.PeriodToHours(period);
            float clockNight = hours >= 21f || hours < 6f
                ? 1f
                : (hours >= 19.5f ? Mathf.InverseLerp(19.5f, 21f, hours)
                    : hours < 7f ? Mathf.InverseLerp(7f, 6f, hours) : 0f);
            night = Mathf.Max(moonBlend, clockNight);
            float day = 1f - night;

            // Same period ambient channels as OilSkyboxTime.EvaluateAmbient.
            OilSkyboxTime.EvaluateAmbient(
                period, cumulus, out ambientSky, out ambientEquator, out ambientGround, out _);
            horizon = look.horizon;

            float wrapped = Mathf.Repeat(period, 4f);
            float dist = Mathf.Min(wrapped, 4f - wrapped);
            dawn = Mathf.SmoothStep(0.4f, 0.02f, dist);

            body = Color.Lerp(look.horizon, look.mid, 0.45f);
            body = Color.Lerp(body, new Color(0.93f, 0.94f, 0.96f), 0.28f * day);
            // boluo-main Camera_1 @ 清晨起床：云 #BC7273。
            float sunrise = OilSkyboxTime.CurrentSunriseWeight(hours);
            if (!OilSkyboxTime.ColorOverrideActive && sunrise > 0.001f)
                body = Color.Lerp(body, OilSkyLookTables.SunriseCloud, sunrise);
            // 云高光 #FBF3E9 — pale cream, keep impasto readable under fog.
            Color cloudHilite = new Color(0.984f, 0.953f, 0.914f);
            body = Color.Lerp(body, cloudHilite, 0.22f * dawn);
            // Night: lock body to NightFog (#36363C) ink — not lit chalk.
            Color nightBody = Color.Lerp(look.mid, OilSkyboxTime.NightFogColor, 0.78f);
            body = Color.Lerp(body, nightBody, night);
            Color sunHighlight = Color.Lerp(look.horizon, cloudHilite, 0.62f);
            // Moon lift stays dim cool grey, not bright blue-white.
            Color moonHighlight = Color.Lerp(nightBody, new Color(0.22f, 0.24f, 0.30f), 0.35f);
            highlight = Color.Lerp(sunHighlight, moonHighlight, night);

            Color ring = Color.Lerp(look.horizon, look.zenith, 0.22f);
            zenith = ring;
            float zMax = Mathf.Max(ring.r, Mathf.Max(ring.g, ring.b));
            float zMin = Mathf.Min(ring.r, Mathf.Min(ring.g, ring.b));
            float sat = zMax > 1e-4f ? (zMax - zMin) / zMax : 0f;
            bool blue = ring.b > ring.r + 0.08f && ring.b > ring.g;
            if (blue)
            {
                float wash = day * Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.18f, 0.55f, sat));
                Color washed = Color.Lerp(look.horizon, Color.white, 0.62f);
                zenith = Color.Lerp(ring, Color.Lerp(body, washed, 0.72f), wash);
            }

            // Night far-side must stay inked — blue zenith wash looked like white sheets.
            zenith = Color.Lerp(zenith, nightBody, night * 0.7f);
        }

        static Mesh BuildRing()
        {
            const int cards = 8;
            const int segU = 8;
            const int segV = 5;
            const float arc = 48f * Mathf.Deg2Rad;
            // Belly at MeshY0; top at MeshY1. Evaluate() pins MeshY0 to sea level.

            int vertsPerCard = (segU + 1) * (segV + 1);
            var vertices = new Vector3[cards * vertsPerCard];
            var uvs = new Vector2[vertices.Length];
            var indices = new int[cards * segU * segV * 6];

            int v = 0;
            int t = 0;
            for (int card = 0; card < cards; card++)
            {
                int col = card % 2;
                int row = card / 2;
                float center = (card + 0.5f) * (Mathf.PI * 2f / cards);
                int cardVert = v;

                for (int y = 0; y <= segV; y++)
                {
                    float v01 = y / (float)segV;
                    float height = Mathf.Lerp(MeshY0, MeshY1, v01);
                    float bow = Mathf.Sin(v01 * Mathf.PI);
                    float radius = RingRadius + bow * 36f;
                    for (int x = 0; x <= segU; x++)
                    {
                        float u01 = x / (float)segU;
                        float ang = center + (u01 - 0.5f) * arc;
                        vertices[v] = new Vector3(
                            Mathf.Sin(ang) * radius,
                            height,
                            Mathf.Cos(ang) * radius);
                        // Stay inside the atlas cell. V is flipped: the painted belly hangs down.
                        float uInset = Mathf.Lerp(0.01f, 0.99f, u01);
                        float vInset = Mathf.Lerp(0.98f, 0.02f, v01);
                        uvs[v] = new Vector2(
                            (col + uInset) / 2f,
                            (row + vInset) / 4f);
                        v++;
                    }
                }

                int stride = segU + 1;
                for (int y = 0; y < segV; y++)
                {
                    for (int x = 0; x < segU; x++)
                    {
                        int i00 = cardVert + y * stride + x;
                        int i10 = i00 + 1;
                        int i01 = i00 + stride;
                        int i11 = i01 + 1;
                        // Front face points inward. The camera sits inside the ring.
                        indices[t++] = i00;
                        indices[t++] = i11;
                        indices[t++] = i10;
                        indices[t++] = i00;
                        indices[t++] = i01;
                        indices[t++] = i11;
                    }
                }
            }

            var mesh = new Mesh
            {
                name = "OilCloudRing",
                hideFlags = HideFlags.HideAndDontSave
            };
            mesh.SetVertices(vertices);
            mesh.SetUVs(0, uvs);
            mesh.SetIndices(indices, MeshTopology.Triangles, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
