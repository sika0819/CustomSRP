using UnityEngine;
using UnityEngine.Rendering;

namespace CustomSRP
{
    /// <summary>
    /// Camera-space rain curtain: one Quad parented to the camera, UV-scroll streaks
    /// in the shader (ported from Boluo SkyMobileRain / Boluo/Mobile/Rain).
    /// Disabled when intensity ≈ 0 so it costs nothing out of rain.
    /// </summary>
    [DisallowMultipleComponent]
    public class OilRainStreakQuad : MonoBehaviour
    {
        static readonly int IntensityId = Shader.PropertyToID("_Intensity");
        static readonly int DensityId = Shader.PropertyToID("_Density");
        static readonly int RainTimeId = Shader.PropertyToID("_RainTime");
        static readonly int WindId = Shader.PropertyToID("_Wind");
        static readonly int QualityId = Shader.PropertyToID("_Quality");
        static readonly int MistId = Shader.PropertyToID("_Mist");
        static readonly int ColorId = Shader.PropertyToID("_RainColor");

        // Match SkyMobileRain density / fall / mist tables by weather intensity band.
        static readonly float[] DensityByState = { 0f, 1.05f, 1.9f, 2.85f };
        static readonly float[] FallSpeedByState = { 0f, 2.0f, 3.05f, 4.3f };
        static readonly float[] MistByState = { 0f, 0.2f, 0.38f, 0.56f };

        [Header("References")]
        [SerializeField] OilRainSettings _settings;
        [SerializeField] Camera _targetCamera;

        Transform _root;
        MeshRenderer _renderer;
        MeshFilter _filter;
        Material _material;
        MaterialPropertyBlock _block;
        float _rainTime;
        int _appliedState = -1;
        float _appliedQuality = -1f;
        bool _raining;

        void OnEnable()
        {
            ResolveSettings();
            if (_targetCamera == null)
                _targetCamera = ResolveCamera();
        }

        void OnDisable()
        {
            ClearImmediate();
            DestroyCurtain();
        }

        void LateUpdate()
        {
            ResolveSettings();
            if (_settings == null)
                return;

            if (!_settings.useSingleQuadStreaks)
            {
                ClearImmediate();
                return;
            }

            if (_targetCamera == null || !_targetCamera.isActiveAndEnabled)
                _targetCamera = ResolveCamera();
            if (_targetCamera == null)
            {
                ClearImmediate();
                return;
            }

            float intensity = _settings.intensity;
            int state = IntensityToState(intensity);
            if (state <= 0)
            {
                ClearImmediate();
                return;
            }

            EnsureAttached(_targetCamera);
            if (_material == null || _renderer == null)
                return;

            float quality = ResolveQualityScale();
            ApplyLayout(state, quality);

            _rainTime += Time.deltaTime * FallSpeedByState[state] *
                         Mathf.Max(0.25f, _settings.streakFallSpeed / 3.5f);

            float wind = Mathf.Lerp(0.04f, 0.55f, Mathf.Clamp01(_settings.windStrength));
            if (_settings.windDirection.x < 0f)
                wind = -wind;

            float density = DensityByState[state] * Mathf.Lerp(0.7f, 1f, quality) *
                            Mathf.Lerp(0.85f, 1.25f, Mathf.Clamp01(_settings.streakDensity));
            float mist = MistByState[state] * Mathf.Lerp(0.5f, 1f, quality);

            Color c = _settings.rainColor;
            if (c.a < 0.05f)
                c.a = 0.36f;
            c.a *= Mathf.Lerp(0.7f, 1f, Mathf.Clamp01(_settings.streakOpacity));

            // Material + MPB: CustomSRP sometimes ignores MPB-only overrides on
            // UnityPerMaterial CBUFFER props; writing both matches SkyMobileRain reliability.
            _material.SetFloat(RainTimeId, _rainTime);
            _material.SetFloat(IntensityId, intensity);
            _material.SetFloat(DensityId, density);
            _material.SetFloat(MistId, mist);
            _material.SetFloat(WindId, wind);
            _material.SetFloat(QualityId, quality);
            _material.SetColor(ColorId, c);

            _block ??= new MaterialPropertyBlock();
            _block.SetFloat(RainTimeId, _rainTime);
            _block.SetFloat(IntensityId, intensity);
            _block.SetFloat(DensityId, density);
            _block.SetFloat(MistId, mist);
            _block.SetFloat(WindId, wind);
            _block.SetFloat(QualityId, quality);
            _block.SetColor(ColorId, c);
            _renderer.SetPropertyBlock(_block);

            if (!_renderer.enabled)
                _renderer.enabled = true;
            _raining = true;
            _appliedState = state;
            _appliedQuality = quality;
        }

        void ResolveSettings()
        {
            if (_settings != null)
                return;
            _settings = OilRainSettings.Current;
        }

        static Camera ResolveCamera()
        {
            Camera main = Camera.main;
            if (main != null && main.isActiveAndEnabled)
                return main;

            Camera[] cams = Camera.allCameras;
            for (int i = 0; i < cams.Length; i++)
            {
                Camera c = cams[i];
                if (c != null && c.isActiveAndEnabled && c.CompareTag("MainCamera"))
                    return c;
            }

            for (int i = 0; i < cams.Length; i++)
            {
                Camera c = cams[i];
                if (c != null && c.isActiveAndEnabled && c.enabled)
                    return c;
            }

            return null;
        }

        void ClearImmediate()
        {
            if (!_raining && _appliedState <= 0)
                return;

            if (_renderer != null && _renderer.enabled)
                _renderer.enabled = false;
            if (_material != null)
            {
                _material.SetFloat(IntensityId, 0f);
                _material.SetFloat(MistId, 0f);
            }
            if (_renderer != null && _block != null)
            {
                _block.SetFloat(IntensityId, 0f);
                _block.SetFloat(MistId, 0f);
                _renderer.SetPropertyBlock(_block);
            }

            _raining = false;
            _appliedState = 0;
        }

        void EnsureAttached(Camera cam)
        {
            if (cam == null)
                return;

            if (_root != null)
            {
                if (_root.parent != cam.transform)
                    _root.SetParent(cam.transform, false);
                EnsureMaterial();
                return;
            }

            EnsureMaterial();
            if (_material == null)
                return;

            var go = new GameObject("~OilRainCurtain");
            go.transform.SetParent(cam.transform, false);
            go.transform.localRotation = Quaternion.identity;
            go.layer = 0;
            _root = go.transform;

            _filter = go.AddComponent<MeshFilter>();
            _filter.sharedMesh = BuildUnitQuad();

            _renderer = go.AddComponent<MeshRenderer>();
            _renderer.sharedMaterial = _material;
            _renderer.enabled = false;
            _renderer.shadowCastingMode = ShadowCastingMode.Off;
            _renderer.receiveShadows = false;
            _renderer.lightProbeUsage = LightProbeUsage.Off;
            _renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            _renderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
            _renderer.allowOcclusionWhenDynamic = false;
        }

        void ApplyLayout(int state, float quality)
        {
            if (_root == null)
                return;
            if (_appliedState == state && Mathf.Abs(quality - _appliedQuality) < 0.01f)
                return;

            // Same layout numbers as SkyMobileRain.
            float stateT = (state - 1) / 2f;
            float width = Mathf.Lerp(5.4f, 9.2f, quality) * Mathf.Lerp(0.82f, 1.08f, stateT);
            float height = width * 1.22f;
            float z = Mathf.Lerp(5.4f, 4.1f, quality);
            _root.localPosition = new Vector3(0f, 0.7f, z);
            _root.localScale = new Vector3(width, height, 1f);
            _root.localRotation = Quaternion.identity;
        }

        void EnsureMaterial()
        {
            if (_material != null)
                return;

            var shader = Shader.Find("CustomSRP/OilRainStreakQuad");
            if (shader == null)
            {
                Debug.LogWarning("[OilRainStreakQuad] Shader CustomSRP/OilRainStreakQuad not found.");
                enabled = false;
                return;
            }

            _material = new Material(shader)
            {
                name = "OilRainCurtain_Runtime",
                hideFlags = HideFlags.HideAndDontSave
            };
            // Transparent+50 — keep in sync with shader Tags.
            _material.renderQueue = 3050;
        }

        static int IntensityToState(float intensity)
        {
            if (intensity <= 0.005f)
                return 0;
            if (intensity < 0.5f)
                return 1;
            if (intensity < 0.85f)
                return 2;
            return 3;
        }

        static float ResolveQualityScale()
        {
            int mem = SystemInfo.systemMemorySize;
            int w = Screen.width;
            int h = Screen.height;
            long pixels = (long)w * h;
            bool highPixels = Mathf.Max(w, h) >= 2560 || pixels >= 2560L * 1440L;
            if (mem > 0 && mem < 4000)
                return 0.58f;
            if (highPixels)
                return 0.78f;
            if (mem >= 7500)
                return 1f;
            return 0.88f;
        }

        static Mesh BuildUnitQuad()
        {
            var mesh = new Mesh
            {
                name = "OilRainCurtainQuad",
                hideFlags = HideFlags.HideAndDontSave,
                vertices = new[]
                {
                    new Vector3(-0.5f, -0.5f, 0f),
                    new Vector3(0.5f, -0.5f, 0f),
                    new Vector3(0.5f, 0.5f, 0f),
                    new Vector3(-0.5f, 0.5f, 0f),
                },
                uv = new[]
                {
                    new Vector2(0f, 0f), new Vector2(1f, 0f),
                    new Vector2(1f, 1f), new Vector2(0f, 1f),
                },
                triangles = new[] { 0, 2, 1, 0, 3, 2 },
            };
            mesh.RecalculateBounds();
            return mesh;
        }

        void DestroyCurtain()
        {
            if (_root != null)
            {
                if (Application.isPlaying)
                    Destroy(_root.gameObject);
                else
                    DestroyImmediate(_root.gameObject);
                _root = null;
            }
            _renderer = null;
            _filter = null;

            if (_material != null)
            {
                if (Application.isPlaying)
                    Destroy(_material);
                else
                    DestroyImmediate(_material);
                _material = null;
            }

            _block = null;
            _appliedState = -1;
            _appliedQuality = -1f;
            _raining = false;
            _rainTime = 0f;
        }
    }
}
