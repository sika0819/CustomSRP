using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace CustomSRP
{
    /// <summary>
    /// Drives both flipbook-based rain effects on the GPU:
    ///
    ///  - Rain ripples: uploads the ripple normal atlas once per frame as a set of
    ///    global uniforms. Water shaders read it via OilRainRippleFlipbook.hlsl and
    ///    perturb their shading normal. No objects, no blit, no extra draw call.
    ///
    ///  - Splashes: a fixed pool of horizontal quads drawn in ONE instanced call
    ///    via Graphics.RenderMeshInstanced, each carrying its own animation phase
    ///    through the instance struct (no CPU per-particle state beyond one float).
    /// </summary>
    [DisallowMultipleComponent]
    public class OilRainFlipbookDriver : MonoBehaviour
    {
        // Per-instance data. Graphics.RenderMeshInstanced maps every field beyond
        // objectToWorld onto the matching instanced shader property.
        struct SplashInstance
        {
            public Matrix4x4 objectToWorld;
            public Vector4 splashInstData; // x = progress, y = seed, z = alpha scale
        }

        [Header("References")]
        [SerializeField] OilRainSettings _settings;
        [SerializeField] Camera _targetCamera;

        [Header("Spawning")]
        [Tooltip("Splashes per second at full intensity.")]
        [SerializeField] float _splashRate = 18f;

        [Tooltip("Height above the follow target where splashes land.")]
        [SerializeField] float _splashPlaneY = 0.05f;

        static readonly int kRippleAtlasId = Shader.PropertyToID("_OilRainRippleAtlas");
        static readonly int kRippleParamsId = Shader.PropertyToID("_OilRainRippleParams");
        static readonly int kRippleGridId = Shader.PropertyToID("_OilRainRippleGrid");
        static readonly int kRippleTexelId = Shader.PropertyToID("_OilRainRippleTexel");
        static readonly int kRippleEnabledId = Shader.PropertyToID("_OilRainRippleEnabled");
        static readonly int kSplashAtlasId = Shader.PropertyToID("_SplashFlipbook");
        static readonly int kSplashParamsId = Shader.PropertyToID("_SplashFlipbookParams");
        static readonly int kSplashTexelId = Shader.PropertyToID("_SplashFlipbookTexel");

        struct Splash
        {
            public Vector3 pos;
            public float t;
            public float seed;
        }

        readonly List<Splash> _splashes = new List<Splash>(64);

        Mesh _quad;
        Material _splashMaterial;
        MaterialPropertyBlock _mpb;
        SplashInstance[] _instances = new SplashInstance[64];

        Texture2D _generatedRipple;
        Texture2D _generatedSplash;
        Vector3 _followTarget;
        float _spawnAccum;

        void OnEnable()
        {
            if (!Game.IslandLeakProbe.Allow(Game.IslandLeakProbe.Module.Rain))
            {
                enabled = false;
                return;
            }

            if (_settings == null) _settings = OilRainSettings.Current;
            if (_targetCamera == null) _targetCamera = Camera.main;
            _followTarget = transform.position;
            EnsureResources();
            PushRippleGlobals();
        }

        /// <summary>退登清掉全局雨水贴图，并拆掉运行时生成的 atlas / quad / 材质。</summary>
        public static void ReleaseSession()
        {
            Shader.SetGlobalTexture(kRippleAtlasId, null);
            Shader.SetGlobalTexture(kSplashAtlasId, null);
            Shader.SetGlobalFloat(kRippleEnabledId, 0f);
            OilRainFlipbookDriver[] drivers = FindObjectsByType<OilRainFlipbookDriver>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            for (int i = 0; i < drivers.Length; i++)
                drivers[i].ReleaseOwned();
        }

        void OnDisable()
        {
            Shader.SetGlobalTexture(kRippleAtlasId, null);
            Shader.SetGlobalTexture(kSplashAtlasId, null);
            Shader.SetGlobalFloat(kRippleEnabledId, 0f);
            ReleaseGenerated();
        }

        void LateUpdate()
        {
            if (!Game.IslandLeakProbe.Allow(Game.IslandLeakProbe.Module.Rain))
                return;
            if (Game.IslandGpuReclaim.CoverOnly)
                return;
            if (_settings == null) return;
            if (_targetCamera == null) _targetCamera = Camera.main;

            float intensity = _settings.intensity;

            PushRippleGlobals();

            if (intensity <= 0.001f || !_settings.splashFlipbookEnabled)
            {
                _splashes.Clear();
                return;
            }

            UpdateFollowTarget();
            SpawnSplashes(intensity);
            UpdateSplashes();
            DrawSplashes();
        }

        // ---------------------------------------------------------------------
        // Ripples: one texture upload per frame, consumed by water shaders
        // ---------------------------------------------------------------------
        void PushRippleGlobals()
        {
            if (_settings == null) return;

            if (!_settings.rippleFlipbookEnabled || _settings.intensity <= 0.001f)
            {
                Shader.SetGlobalFloat(kRippleEnabledId, 0f);
                return;
            }

            Texture2D atlas = _settings.rippleFlipbook != null
                ? _settings.rippleFlipbook
                : GetGeneratedRipple();

            if (atlas == null)
            {
                Shader.SetGlobalFloat(kRippleEnabledId, 0f);
                return;
            }

            Shader.SetGlobalTexture(kRippleAtlasId, atlas);
            Shader.SetGlobalVector(kRippleParamsId, new Vector4(
                _settings.rippleFlipbookWorldScale,
                _settings.rippleFlipbookSpeed,
                _settings.rippleFlipbookStrength * Mathf.Clamp01(_settings.intensity),
                _settings.rippleFlipbookFrames));
            Shader.SetGlobalVector(kRippleGridId, new Vector2(
                _settings.rippleFlipbookColumns,
                _settings.rippleFlipbookRows));
            Shader.SetGlobalVector(kRippleTexelId, new Vector2(
                1f / Mathf.Max(1, atlas.width),
                1f / Mathf.Max(1, atlas.height)));
            Shader.SetGlobalFloat(kRippleEnabledId, 1f);
        }

        // ---------------------------------------------------------------------
        // Splashes
        // ---------------------------------------------------------------------
        void UpdateFollowTarget()
        {
            if (_targetCamera != null) _followTarget = _targetCamera.transform.position;
        }

        void SpawnSplashes(float intensity)
        {
            int capacity = Mathf.Max(1, _settings.splashQuadPoolSize);
            if (_splashes.Count >= capacity) return;

            _spawnAccum += _splashRate * intensity * Time.deltaTime;

            float radius = _settings.splashSpawnRadius;
            while (_spawnAccum >= 1f && _splashes.Count < capacity)
            {
                _spawnAccum -= 1f;

                // Uniform disc sampling so splashes do not cluster at the centre.
                float ang = Random.value * Mathf.PI * 2f;
                float rad = radius * Mathf.Sqrt(Random.value);

                _splashes.Add(new Splash
                {
                    pos = new Vector3(
                        _followTarget.x + Mathf.Cos(ang) * rad,
                        _splashPlaneY,
                        _followTarget.z + Mathf.Sin(ang) * rad),
                    t = 0f,
                    seed = Random.value
                });
            }
        }

        void UpdateSplashes()
        {
            float frames = Mathf.Max(1, _settings.splashFlipbookFrames);
            float lifetime = frames / Mathf.Max(0.01f, _settings.splashFlipbookSpeed);

            for (int i = _splashes.Count - 1; i >= 0; i--)
            {
                var s = _splashes[i];
                s.t += Time.deltaTime;
                if (s.t >= lifetime) { _splashes.RemoveAt(i); continue; }
                _splashes[i] = s;
            }
        }

        void DrawSplashes()
        {
            int count = _splashes.Count;
            if (count == 0 || _splashMaterial == null) return;

            int capacity = Mathf.Max(1, _settings.splashQuadPoolSize);
            if (_instances.Length < capacity) _instances = new SplashInstance[capacity];

            float frames = Mathf.Max(1, _settings.splashFlipbookFrames);
            float lifetime = frames / Mathf.Max(0.01f, _settings.splashFlipbookSpeed);
            float size = _settings.splashQuadSize;

            for (int i = 0; i < count; i++)
            {
                var s = _splashes[i];
                float progress = Mathf.Clamp01(s.t / lifetime);

                _instances[i].objectToWorld = Matrix4x4.TRS(
                    s.pos, Quaternion.identity, new Vector3(size, size, size));
                _instances[i].splashInstData = new Vector4(progress, s.seed, 1f, 0f);
            }

            Texture2D atlas = _settings.splashFlipbook != null
                ? _settings.splashFlipbook
                : GetGeneratedSplash();

            _mpb.SetTexture(kSplashAtlasId, atlas);
            _mpb.SetVector(kSplashParamsId, new Vector4(
                _settings.splashFlipbookColumns,
                _settings.splashFlipbookRows,
                frames,
                _settings.splashOpacity));
            _mpb.SetVector(kSplashTexelId, new Vector2(
                1f / Mathf.Max(1, atlas.width),
                1f / Mathf.Max(1, atlas.height)));

            var rp = new RenderParams(_splashMaterial)
            {
                worldBounds = new Bounds(_followTarget, Vector3.one * (_settings.splashSpawnRadius * 4f + 50f)),
                matProps = _mpb,
                shadowCastingMode = ShadowCastingMode.Off,
                receiveShadows = false,
                layer = gameObject.layer,
                camera = _targetCamera,
                lightProbeUsage = LightProbeUsage.Off,
            };

            Graphics.RenderMeshInstanced(rp, _quad, 0, _instances, count, 0);
        }

        /// <summary>Spawn a splash immediately at an explicit world position.</summary>
        public void SpawnSplash(Vector3 worldPos)
        {
            int capacity = Mathf.Max(1, _settings != null ? _settings.splashQuadPoolSize : 1);
            if (_splashes.Count >= capacity) return;
            _splashes.Add(new Splash
            {
                pos = new Vector3(worldPos.x, _splashPlaneY, worldPos.z),
                t = 0f,
                seed = Random.value
            });
        }

        // ---------------------------------------------------------------------
        // Resources / placeholders
        // ---------------------------------------------------------------------
        void EnsureResources()
        {
            if (_quad == null) _quad = BuildQuad();
            if (_mpb == null) _mpb = new MaterialPropertyBlock();

            if (_splashMaterial == null)
            {
                var shader = Shader.Find("CustomSRP/OilRainSplash");
                if (shader == null) { enabled = false; return; }
                _splashMaterial = new Material(shader)
                {
                    name = "OilRainSplash_Shared",
                    hideFlags = HideFlags.HideAndDontSave
                };
                _splashMaterial.enableInstancing = true;
            }
        }

        static Mesh BuildQuad()
        {
            // Unit quad on the XZ plane, normal +Y, so it lies flat on the water.
            var mesh = new Mesh
            {
                name = "OilRainSplashQuad",
                hideFlags = HideFlags.HideAndDontSave,
                vertices = new[]
                {
                    new Vector3(-0.5f, 0f, -0.5f),
                    new Vector3( 0.5f, 0f, -0.5f),
                    new Vector3( 0.5f, 0f,  0.5f),
                    new Vector3(-0.5f, 0f,  0.5f),
                },
                uv = new[]
                {
                    new Vector2(0f, 0f), new Vector2(1f, 0f),
                    new Vector2(1f, 1f), new Vector2(0f, 1f),
                },
                triangles = new[] { 0, 2, 1, 0, 3, 2 },
            };
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        Texture2D GetGeneratedRipple()
        {
            if (_generatedRipple != null) return _generatedRipple;
            _generatedRipple = RippleFlipbookGenerator.Generate(
                Mathf.Max(1, _settings.rippleFlipbookColumns),
                Mathf.Max(1, _settings.rippleFlipbookRows),
                Mathf.Max(1, _settings.rippleFlipbookFrames));
            return _generatedRipple;
        }

        Texture2D GetGeneratedSplash()
        {
            if (_generatedSplash != null) return _generatedSplash;
            _generatedSplash = RippleFlipbookGenerator.GenerateSplash(
                Mathf.Max(1, _settings.splashFlipbookColumns),
                Mathf.Max(1, _settings.splashFlipbookRows),
                Mathf.Max(1, _settings.splashFlipbookFrames));
            return _generatedSplash;
        }

        void ReleaseOwned()
        {
            ReleaseGenerated();
            if (_quad != null)
                DestroyImmediate(_quad);
            _quad = null;
            if (_splashMaterial != null)
                DestroyImmediate(_splashMaterial);
            _splashMaterial = null;
            _mpb = null;
        }

        void ReleaseGenerated()
        {
            if (_generatedRipple != null) DestroyImmediate(_generatedRipple);
            if (_generatedSplash != null) DestroyImmediate(_generatedSplash);
            _generatedRipple = null;
            _generatedSplash = null;
        }
    }
}