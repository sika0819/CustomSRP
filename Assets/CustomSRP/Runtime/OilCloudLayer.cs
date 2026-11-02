using System.Runtime.InteropServices;
using Game;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace CustomSRP
{
    /// <summary>
    /// Camera-following cloud ring (XZ). Low-ring belly anchors to sea level.
    /// The optional high ring is baked into the same mesh (low triangles first,
    /// SDF bias on UV1) so the cloud pass draws once. Pigment follows the oil sky.
    /// </summary>
    [DisallowMultipleComponent]
    public class OilCloudLayer : MonoBehaviour
    {
        public const string ShaderName = "CustomSRP/OilCloudNPR";

        public const string CloudMapPath = "Assets/CustomSRP/Textures/Cloud/CloudMap_SDF.png";
        public const string NoiseMapPath = "Assets/CustomSRP/Textures/Cloud/CloudNoise.png";
        public const string BrushPath = "Assets/CustomSRP/Textures/Cloud/Cloud_A.png";

        const float RingRadius = 400f;
        // Local mesh Y. Bottom (MeshY0) is the belly; MeshY1 raised for taller clouds.
        // ringHeight lifts both rings off the sea (zhanweitu1). Low belly should sit at/above
        // DawnSunRingHeight so the first ring clears the 清晨起床 sun disc.
        const float MeshY0 = -62.5f;
        const float MeshY1 = 110f;
        const float HighLocalY = 140f;
        const float DefaultRingScale = 1.85f; // shared by ringScale default + DawnSunRingHeight
        /// <summary>清晨起床太阳在默认云环半径上的世界 Y（第一层肚皮下限）。</summary>
        public static readonly float DawnSunRingHeight =
            Mathf.Tan(OilSkyboxTime.DawnSunElevationDeg * Mathf.Deg2Rad)
            * RingRadius * DefaultRingScale;
        const float HighScaleMul = 0.68f;
        const float HighYawDegrees = 70f;
        const float HighSdfBias = 0.08f;
        static readonly Quaternion HighYaw = Quaternion.Euler(0f, HighYawDegrees, 0f);
        const float WindBlendSeconds = 0.85f;
        const float CalmYawRad = 0f;
        const float StormYawRad = 16f * Mathf.Deg2Rad;
        // Rest yaw (0) lays a cloud belly across the pinned moon for 放空发呆 and 准备入睡.
        // +12° puts both discs in the atlas gap. Other presets keep the authored ring.
        const float MoonGapYawDegrees = 12f;
        const int IdlePresetId = 6;
        const int SleepPresetId = 10;

        public Material material;

        [Range(0f, 1f)]
        [Tooltip("0.12 sparse, 0.30 broken, 1 overcast. Shaped before the SDF threshold.")]
        public float coverage = 0.85f;

        [Range(0f, 1f)]
        [Tooltip("0 holds the ring. 1 turns it at 16 degrees per second.")]
        public float wind = 0.2f;

        [Tooltip("Boluo FBX ring used 1.85 on a ~400 m mesh.")]
        public float ringScale = DefaultRingScale;

        [Tooltip("When on, low-ring belly tracks OilOceanSurface world Y.")]
        public bool followSeaLevel = true;

        [Tooltip("World Y of the low-ring belly when followSeaLevel is off, or no ocean exists.")]
        public float seaLevel = 0f;

        [Tooltip("Extra meters to lift both rings after anchoring. 0 = belly on water; ≥ DawnSunRingHeight (~91) so the low ring sits at/above 清晨起床 sun.")]
        public float ringHeight = 180f;

        public bool highRing = true;

        [Header("Shape")]
        [Range(0f, 0.2f)] public float uvDisturbance = 0.028f;
        [Range(0.01f, 0.5f)] public float sdfSoftness = 0.08f;
        [Range(0.003f, 1.5f)] public float sdfMin = 0.48f;
        [Range(0.003f, 1.5f)] public float sdfMax = 0.90f;

        [Header("Light")]
        [Range(0f, 1f)] public float topShadow = 1f;
        [Range(0f, 1f)] public float topHighlight = 1f;
        [Range(0f, 3f)] public float edgeIntensity = 2.2f;

        public static OilCloudLayer Current { get; private set; }

        public static Material ActiveMaterial =>
            Current != null ? Current.material : null;

        public static Mesh SharedRing => _ring ??= BuildRing();

        /// <summary>退登丢掉运行时云环。静态网格会一直占着，下一趟登录不会自己换掉。
        /// 必须 DestroyImmediate：延迟 Destroy 在卸岛帧里会被丢掉，网格 GPU 副本留在 GfxDevice。</summary>
        public static void ReleaseSharedMeshes()
        {
            if (_ring != null)
            {
                Object.DestroyImmediate(_ring);
                _ring = null;
            }

            if (_combined != null)
            {
                Object.DestroyImmediate(_combined);
                _combined = null;
            }

            _combinedScale = float.NaN;
        }

        static Mesh _ring;
        static Mesh _combined;
        static float _combinedScale = float.NaN;

        // Position, atlas UV, one-float SDF bias. No normals: the cloud shader never reads them.
        static readonly VertexAttributeDescriptor[] CloudRingLayout =
        {
            new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord1, VertexAttributeFormat.Float32, 1)
        };

        const MeshUpdateFlags CloudRingUploadFlags =
            MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices;

        [StructLayout(LayoutKind.Sequential)]
        struct CloudRingVertex
        {
            public float PositionX;
            public float PositionY;
            public float PositionZ;
            public float UvX;
            public float UvY;
            public float Bias;
        }
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

        public struct Frame
        {
            public Matrix4x4 low;
            public Mesh mesh;
            public bool drawHigh;
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
            float period = sky != null ? sky.VisualPeriod() : 1f;
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

            float sunrise = OilSkyboxTime.CurrentSunriseWeight(OilSkyboxTime.PeriodToHours(period));
            float shaped = ShapeCoverage(coverage);
            if (_windIntensity > 0.01f)
            {
                shaped *= Mathf.Lerp(1f, 0.35f, _windIntensity);
            }

            Vector3 origin = camera != null ? camera.transform.position : Vector3.zero;
            float scale = Mathf.Max(0.2f, ringScale);
            // Pin low-ring belly (local MeshY0) to sea, then apply optional lift.
            origin.y = ResolveSeaLevel() - MeshY0 * scale + ringHeight;
            float yawRad = MoonGapYawActive() ? MoonGapYawDegrees * Mathf.Deg2Rad : _yaw;
            Quaternion yaw = Quaternion.Euler(0f, yawRad * Mathf.Rad2Deg, 0f);
            return new Frame
            {
                low = Matrix4x4.TRS(origin, yaw, Vector3.one * scale),
                mesh = highRing ? CombinedRing(scale) : SharedRing,
                drawHigh = highRing,
                sunDir = sunDir,
                moonDir = moonDir,
                body = body,
                highlight = highlight,
                zenith = zenith,
                horizon = horizon,
                ambientSky = ambientSky,
                ambientEquator = ambientEquator,
                ambientGround = ambientGround,
                cloudParams = new Vector4(moonBlend, shaped, 1f, sunrise),
                windVector = new Vector4(
                    Mathf.Lerp(CalmYawRad, StormYawRad, _windIntensity),
                    yawRad,
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
        }

        /// <summary>
        /// 放空发呆 / 准备入睡 pin the moon in view. The rest ring covers that disc;
        /// those two presets draw at <see cref="MoonGapYawDegrees"/> instead.
        /// </summary>
        static bool MoonGapYawActive()
        {
            PresetService presets = GameEntry.Presets;
            if (presets == null)
                return false;

            int id = presets.CurrentId;
            return id == IdlePresetId || id == SleepPresetId;
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
            WeatherService weather = GameEntry.Weather;
            if (weather == null)
                return 0f;
            // VisualWind01 already eases across a weather change (0 when not wind).
            return weather.VisualWind01;
        }

        float ResolveSeaLevel()
        {
            if (followSeaLevel)
            {
                ISeaLevel sea = IslandSceneComponent.Active?.Sea;
                if (sea != null)
                    return sea.Level;
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
            // Night presets at 19:00–20:00 are still before the 21:00 clock gate.
            if (OilSkyboxTime.NightColdHoldsPigment(hours))
                night = 1f;
            float day = 1f - night;

            // Same period ambient channels as OilSkyboxTime.EvaluateAmbient.
            OilSkyboxTime.EvaluateAmbient(
                period, cumulus, out ambientSky, out ambientEquator, out ambientGround, out _);
            horizon = look.horizon;

            float wrapped = Mathf.Repeat(period, 4f);
            float dist = Mathf.Min(wrapped, 4f - wrapped);
            dawn = Mathf.SmoothStep(0.4f, 0.02f, dist);
            // Period dawn peaks at 06:00 and is already weak by 清晨起床 (07:30).
            float sunrise = OilSkyboxTime.CurrentSunriseWeight(hours);
            dawn = Mathf.Max(dawn, sunrise);
            // Clock night holds through 07:00 and would ink the sunrise clouds black.
            night *= 1f - sunrise;

            // Body stays closer to sky mid/horizon pigment — chalk wash kills 色块 contrast.
            body = Color.Lerp(look.horizon, look.mid, 0.55f);
            body = Color.Lerp(body, new Color(0.90f, 0.91f, 0.94f), 0.08f * day);
            if (!OilSkyboxTime.ColorOverrideActive && sunrise > 0.001f)
                body = Color.Lerp(body, OilSkyLookTables.SunriseCloud, sunrise);
            // Knife highlight: cream + horizon warmth (zhanweitu1 peach slabs).
            Color cloudHilite = Color.Lerp(
                new Color(0.99f, 0.96f, 0.90f),
                look.horizon,
                0.30f * Mathf.Max(dawn, OilSkyboxTime.ColorOverrideActive ? 0.45f : 0f));
            // Dawn mid is dark blue. Skip that pull while the sunrise peach is up.
            body = Color.Lerp(body, Color.Lerp(look.mid, cloudHilite, 0.35f), 0.22f * dawn * (1f - sunrise));
            // Night clouds stay on the shipped ink (canonical anchors + #0A1828).
            // The sky dome can move; this endpoint does not follow the new night fog.
            Color onlineNightZ = new Color(0x06 / 255f, 0x12 / 255f, 0x1C / 255f, 1f);
            Color onlineNightH = new Color(0x0A / 255f, 0x1C / 255f, 0x2A / 255f, 1f);
            Color onlineNightFog = new Color(0x0A / 255f, 0x18 / 255f, 0x28 / 255f, 1f);
            Color onlineMid = Color.Lerp(onlineNightZ, onlineNightH, 0.42f);
            Color nightBody = Color.Lerp(onlineMid, onlineNightFog, 0.78f);
            body = Color.Lerp(body, nightBody, night);
            // Highlight must sit clearly above body so slab gates read as separate paint.
            Color sunHighlight = Color.Lerp(look.horizon, cloudHilite, 0.78f);
            sunHighlight = Color.Lerp(sunHighlight, Color.white, 0.12f * day);
            // Shipped moon lift: dim cool grey, not a near-black edge.
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

            // Shipped far-side: dark ring inked 70% toward the night body.
            Color nightRing = Color.Lerp(onlineNightH, onlineNightZ, 0.22f);
            Color nightZenith = Color.Lerp(nightRing, nightBody, 0.7f);
            zenith = Color.Lerp(zenith, nightZenith, night);
            horizon = Color.Lerp(look.horizon, onlineNightH, night);

            // Sky zenith/mid stay blue (#0128CD / #4868B8). Clouds do not inherit them.
            float cloudW = Mathf.SmoothStep(0.15f, 0.45f, sunrise);
            if (!OilSkyboxTime.ColorOverrideActive && cloudW > 0.001f)
            {
                body = Color.Lerp(body, OilSkyLookTables.SunriseCloud, cloudW);
                highlight = Color.Lerp(highlight, new Color(1f, 0.97f, 0.92f, 1f), cloudW);
                zenith = Color.Lerp(zenith, OilSkyLookTables.SunriseCloudShade, cloudW);
                horizon = Color.Lerp(horizon, new Color(0.93f, 0.70f, 0.40f, 1f), cloudW);
            }
        }

        static Mesh BuildRing()
        {
            var mesh = new Mesh
            {
                name = "OilCloudRing",
                hideFlags = HideFlags.HideAndDontSave
            };
            FillRingMesh(mesh, false, 1f);
            return mesh;
        }

        // High ring baked into low-ring object space. Rebuilt when ringScale changes:
        // the +80 m lift is world-space, so the local offset is 80 / scale.
        static Mesh CombinedRing(float scale)
        {
            scale = Mathf.Max(0.2f, scale);
            if (_combined != null && Mathf.Approximately(_combinedScale, scale))
            {
                return _combined;
            }

            _combined ??= new Mesh
            {
                name = "OilCloudRingBoth",
                hideFlags = HideFlags.HideAndDontSave
            };
            FillRingMesh(_combined, true, scale);
            _combinedScale = scale;
            return _combined;
        }

        static void FillRingMesh(Mesh mesh, bool includeHigh, float scale)
        {
            const int cards = 8;
            const int segU = 8;
            const int segV = 5;
            int vertsPerRing = cards * (segU + 1) * (segV + 1);
            int indicesPerRing = cards * segU * segV * 6;
            int rings = includeHigh ? 2 : 1;
            int vertexCount = vertsPerRing * rings;
            int indexCount = indicesPerRing * rings;

            mesh.indexFormat = IndexFormat.UInt16;
            Mesh.MeshDataArray dataArray = Mesh.AllocateWritableMeshData(1);
            Mesh.MeshData data = dataArray[0];
            data.SetVertexBufferParams(vertexCount, CloudRingLayout);
            data.SetIndexBufferParams(indexCount, IndexFormat.UInt16);
            NativeArray<CloudRingVertex> vertices = data.GetVertexData<CloudRingVertex>();
            NativeArray<ushort> indices = data.GetIndexData<ushort>();

            Vector3 boundsMin = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
            Vector3 boundsMax = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
            WriteRing(vertices, indices, 0, 0, 0f, false, scale, ref boundsMin, ref boundsMax);
            if (includeHigh)
            {
                WriteRing(
                    vertices, indices, vertsPerRing, indicesPerRing,
                    HighSdfBias, true, scale, ref boundsMin, ref boundsMax);
            }

            var bounds = new Bounds((boundsMin + boundsMax) * 0.5f, boundsMax - boundsMin);
            data.subMeshCount = 1;
            data.SetSubMesh(
                0,
                new SubMeshDescriptor(0, indexCount, MeshTopology.Triangles)
                {
                    bounds = bounds,
                    vertexCount = vertexCount
                },
                CloudRingUploadFlags);
            Mesh.ApplyAndDisposeWritableMeshData(dataArray, mesh, CloudRingUploadFlags);
            mesh.bounds = bounds;
        }

        static void WriteRing(
            NativeArray<CloudRingVertex> vertices,
            NativeArray<ushort> indices,
            int vertexBase,
            int indexBase,
            float bias,
            bool bakeHigh,
            float scale,
            ref Vector3 boundsMin,
            ref Vector3 boundsMax)
        {
            const int cards = 8;
            const int segU = 8;
            const int segV = 5;
            const float arc = 48f * Mathf.Deg2Rad;
            // Belly at MeshY0; top at MeshY1. Evaluate() pins MeshY0 to sea level.
            float highLift = HighLocalY / Mathf.Max(0.2f, scale);
            int v = vertexBase;
            int t = indexBase;

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
                        var position = new Vector3(
                            Mathf.Sin(ang) * radius,
                            height,
                            Mathf.Cos(ang) * radius);
                        if (bakeHigh)
                        {
                            position = HighYaw * (position * HighScaleMul);
                            position.y += highLift;
                        }

                        boundsMin = Vector3.Min(boundsMin, position);
                        boundsMax = Vector3.Max(boundsMax, position);
                        // Stay inside the atlas cell. V is flipped: the painted belly hangs down.
                        float uInset = Mathf.Lerp(0.01f, 0.99f, u01);
                        float vInset = Mathf.Lerp(0.98f, 0.02f, v01);
                        vertices[v++] = new CloudRingVertex
                        {
                            PositionX = position.x,
                            PositionY = position.y,
                            PositionZ = position.z,
                            UvX = (col + uInset) / 2f,
                            UvY = (row + vInset) / 4f,
                            Bias = bias
                        };
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
                        indices[t++] = (ushort)i00;
                        indices[t++] = (ushort)i11;
                        indices[t++] = (ushort)i10;
                        indices[t++] = (ushort)i00;
                        indices[t++] = (ushort)i01;
                        indices[t++] = (ushort)i11;
                    }
                }
            }
        }
    }
}
