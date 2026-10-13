using System.Collections.Generic;
using GameFramework.Event;
using UnityEngine;
using UnityEngine.Rendering;
using UnityGameFramework.Runtime;

namespace Game
{
    /// <summary>
    /// 篝火：Dead-Rabbit/UnityFire 噪声火焰平面（CustomSRP/NoiseFire）+ Point Light。
    /// 订 <see cref="CampfireSuppressChangedEventArgs"/>，过渡在 Update 中推进（不依赖裸 C# 事件 / 协程）。
    /// </summary>
    public sealed class FireController : MonoBehaviour
    {
        const string FlameRootName = "NoiseFire";
        const string LegacyOilRootName = "OilFlames";
        // Local scale; Huodui root ~0.6 → world flame ~2.0×2.6 (UnityFire Plane≈10×scale).
        static readonly Vector3 CardScale = new Vector3(3.3f, 4.35f, 1f);

        static readonly int ColorScaleId = Shader.PropertyToID("_ColorScale");
        static readonly int AlphaScaleId = Shader.PropertyToID("_AlphaScale");
        static readonly int Time0Id = Shader.PropertyToID("_Time0");
        static readonly int DisplaceStrengthId = Shader.PropertyToID("_DisplaceStrength");

        [SerializeField] Material flameMaterial;
        [SerializeField] float igniteDuration = 1f;
        [SerializeField] float extinguishDuration = 1f;
        [SerializeField] AnimationCurve transitionCurve = AnimationCurve.EaseInOut(0, 0, 1, 1);
        [SerializeField] bool forceToggleLightEnabled = true;
        [SerializeField] float minRangeOnIgnite = 12f;
        [SerializeField] float lightMaxIntensity = 6.5f;
        [SerializeField] float lightRange = 14f;
        [SerializeField] Color lightColor = new Color(1f, 0.42f, 0.08f, 1f);
        [SerializeField] bool crossedCards = true;
        [SerializeField] bool billboardY = true;

        readonly List<Light> _lights = new List<Light>(2);
        readonly List<MeshRenderer> _flameRenderers = new List<MeshRenderer>(2);
        MaterialPropertyBlock _mpb;

        Transform _flameRoot;
        Light _fireLight;
        bool _isIgnited;
        float _factor;
        Camera _billboardCam;
        bool _eventSubscribed;

        bool _transitionActive;
        bool _transitionToIgnite;
        float _transitionDuration;
        float _transitionElapsed;
        float _transitionStart;
        float _transitionEnd;

        static Material _runtimeMat;
        static Mesh _quadMesh;

        EntityId ControllerId => GetEntityId();

        void Awake()
        {
            EnsureMpb();
            DisableFakeGlowPlanes();
            MuteLegacyFireEmitter();
            StripLegacyOilFlames();
            EnsureNoiseFire();
            EnsurePointLight();
            ApplyFactor(0f);
        }

        void OnEnable()
        {
            EnsureMpb();
            if (_flameRoot == null)
            {
                MuteLegacyFireEmitter();
                StripLegacyOilFlames();
                EnsureNoiseFire();
                EnsurePointLight();
            }

            TrySubscribe();
            SyncIgniteToGate();
        }

        void Start() => TrySubscribe();

        void OnDisable()
        {
            Unsubscribe();
            CampfireWeatherGate.SetVisualIgnited(ControllerId, false);
            _transitionActive = false;
        }

        void Update()
        {
            if (!_transitionActive)
                return;

            _transitionElapsed += Time.deltaTime;
            float u = Mathf.Clamp01(_transitionElapsed / _transitionDuration);
            ApplyFactor(Mathf.Lerp(_transitionStart, _transitionEnd, transitionCurve.Evaluate(u)));
            if (u < 1f)
                return;

            ApplyFactor(_transitionEnd);
            if (!_transitionToIgnite)
                SetFlameVisible(false);
            _transitionActive = false;
        }

        void LateUpdate()
        {
            if (!billboardY || _flameRoot == null || !_isIgnited)
                return;
            Camera cam = _billboardCam != null ? _billboardCam : Camera.main;
            if (cam == null)
                return;
            _billboardCam = cam;
            Vector3 toCam = cam.transform.position - _flameRoot.position;
            toCam.y = 0f;
            if (toCam.sqrMagnitude < 1e-6f)
                return;
            _flameRoot.rotation = Quaternion.LookRotation(toCam.normalized, Vector3.up);
        }

        void TrySubscribe()
        {
            if (_eventSubscribed || GameEntry.Event == null)
                return;
            GameEntry.Event.Subscribe(CampfireSuppressChangedEventArgs.EventId, OnIgniteSuppressChanged);
            _eventSubscribed = true;
        }

        void Unsubscribe()
        {
            if (!_eventSubscribed || GameEntry.Event == null)
                return;
            GameEntry.Event.Unsubscribe(CampfireSuppressChangedEventArgs.EventId, OnIgniteSuppressChanged);
            _eventSubscribed = false;
        }

        void OnIgniteSuppressChanged(object sender, GameEventArgs e)
        {
            if (!(e is CampfireSuppressChangedEventArgs args))
                return;
            if (args.Suppress)
                Extinguish();
            else
                Ignite();
        }

        public void Ignite()
        {
            if (CampfireWeatherGate.SuppressIgnite)
                return;
            StartTransition(true, Mathf.Max(0.0001f, igniteDuration));
        }

        public void Extinguish() =>
            StartTransition(false, Mathf.Max(0.0001f, extinguishDuration));

        public void SetInstant(bool ignite)
        {
            _transitionActive = false;
            _isIgnited = ignite;
            CampfireWeatherGate.SetVisualIgnited(ControllerId, ignite);
            ApplyFactor(ignite ? 1f : 0f);
            SetFlameVisible(ignite);
        }

        public void EnsureRuntimeBindings()
        {
            EnsureMpb();
            DisableFakeGlowPlanes();
            MuteLegacyFireEmitter();
            StripLegacyOilFlames();
            EnsureNoiseFire();
            EnsurePointLight();
            TrySubscribe();
            SyncIgniteToGate();
        }

        void SyncIgniteToGate() =>
            SetInstant(!CampfireWeatherGate.SuppressIgnite);

        void DisableFakeGlowPlanes()
        {
            Transform[] all = GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < all.Length; i++)
            {
                string n = all[i].name;
                bool fake = n.IndexOf("CoreGlow", System.StringComparison.OrdinalIgnoreCase) >= 0
                            || n.IndexOf("GroundGlow", System.StringComparison.OrdinalIgnoreCase) >= 0;
                if (!fake)
                    continue;
                all[i].gameObject.SetActive(false);
                var r = all[i].GetComponent<Renderer>();
                if (r != null)
                    r.enabled = false;
            }
        }

        void MuteLegacyFireEmitter()
        {
            Transform fire = transform.Find("Fire");
            if (fire == null)
                return;

            var ps = fire.GetComponent<ParticleSystem>();
            if (ps != null)
            {
                var emission = ps.emission;
                emission.enabled = false;
                ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            }

            var renderer = fire.GetComponent<ParticleSystemRenderer>();
            if (renderer != null)
                renderer.enabled = false;

            var mrs = fire.GetComponentsInChildren<MeshRenderer>(true);
            for (int i = 0; i < mrs.Length; i++)
            {
                if (mrs[i] == null)
                    continue;
                string n = mrs[i].name;
                if (n.IndexOf("Glow", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    mrs[i].enabled = false;
            }
        }

        void StripLegacyOilFlames()
        {
            Transform oil = transform.Find(LegacyOilRootName);
            if (oil != null)
                DestroyGo(oil.gameObject);
        }

        void EnsureNoiseFire()
        {
            _flameRenderers.Clear();
            _flameRoot = transform.Find(FlameRootName);
            if (_flameRoot == null)
            {
                var go = new GameObject(FlameRootName);
                go.transform.SetParent(transform, false);
                go.transform.localPosition = new Vector3(0f, 0.75f, 0f);
                go.transform.localRotation = Quaternion.identity;
                go.transform.localScale = Vector3.one;
                _flameRoot = go.transform;
            }

            Material mat = ResolveFlameMaterial();
            EnsureCard(_flameRoot, "FireCard0", Quaternion.identity, mat);
            if (crossedCards)
                EnsureCard(_flameRoot, "FireCard1", Quaternion.Euler(0f, 90f, 0f), mat);
            else
            {
                Transform extra = _flameRoot.Find("FireCard1");
                if (extra != null)
                    DestroyGo(extra.gameObject);
            }

            for (int i = _flameRoot.childCount - 1; i >= 0; i--)
            {
                Transform child = _flameRoot.GetChild(i);
                if (child.name == "FireCard0" || child.name == "FireCard1")
                    continue;
                DestroyGo(child.gameObject);
            }
        }

        void EnsureCard(Transform root, string name, Quaternion localRot, Material mat)
        {
            Transform t = root.Find(name);
            GameObject go;
            if (t == null)
            {
                go = new GameObject(name);
                go.transform.SetParent(root, false);
            }
            else
            {
                go = t.gameObject;
            }

            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = localRot;
            go.transform.localScale = CardScale;

            var filter = go.GetComponent<MeshFilter>();
            if (filter == null)
                filter = go.AddComponent<MeshFilter>();
            filter.sharedMesh = GetQuadMesh();

            var renderer = go.GetComponent<MeshRenderer>();
            if (renderer == null)
                renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = mat;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.lightProbeUsage = LightProbeUsage.Off;
            renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            renderer.enabled = _factor > 0.0001f;
            _flameRenderers.Add(renderer);
        }

        static Mesh GetQuadMesh()
        {
            if (_quadMesh != null)
                return _quadMesh;
            var tmp = GameObject.CreatePrimitive(PrimitiveType.Quad);
            _quadMesh = tmp.GetComponent<MeshFilter>().sharedMesh;
            if (Application.isPlaying)
                Object.Destroy(tmp);
            else
                Object.DestroyImmediate(tmp);
            return _quadMesh;
        }

        static void DestroyGo(GameObject go)
        {
#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                Object.DestroyImmediate(go);
                return;
            }
#endif
            Object.Destroy(go);
        }

        void EnsurePointLight()
        {
            _lights.Clear();
            GetComponentsInChildren(true, _lights);
            _fireLight = null;
            for (int i = 0; i < _lights.Count; i++)
            {
                if (_lights[i] != null && _lights[i].type == LightType.Point)
                {
                    _fireLight = _lights[i];
                    break;
                }
            }

            if (_fireLight == null)
            {
                var go = new GameObject("CampfirePointLight");
                go.transform.SetParent(transform, false);
                go.transform.localPosition = new Vector3(0f, 0.9f, 0f);
                _fireLight = go.AddComponent<Light>();
                _lights.Add(_fireLight);
            }

            _fireLight.type = LightType.Point;
            _fireLight.color = lightColor;
            _fireLight.intensity = 0f;
            _fireLight.range = Mathf.Max(minRangeOnIgnite, lightRange);
            _fireLight.shadows = LightShadows.Soft;
            _fireLight.renderMode = LightRenderMode.Auto;
            _fireLight.enabled = false;
            lightMaxIntensity = Mathf.Max(lightMaxIntensity, 4f);
        }

        Material ResolveFlameMaterial()
        {
            if (flameMaterial != null
                && flameMaterial.shader != null
                && flameMaterial.shader.name == "CustomSRP/NoiseFire")
                return flameMaterial;

            Material baked = TryLoadBakedFlameMaterial();
            if (baked != null)
            {
                flameMaterial = baked;
                return baked;
            }

            if (_runtimeMat != null)
                return _runtimeMat;

            Shader shader = Shader.Find("CustomSRP/NoiseFire");
            if (shader == null)
            {
                Log.Warning("[FireController] Shader CustomSRP/NoiseFire not found.");
                return flameMaterial;
            }

            _runtimeMat = new Material(shader) { name = "Campfire_NoiseFire_Runtime" };
            _runtimeMat.SetFloat(ColorScaleId, 1f);
            _runtimeMat.SetFloat(AlphaScaleId, 1f);
            _runtimeMat.SetFloat(Time0Id, 0f);
            _runtimeMat.SetFloat(DisplaceStrengthId, 0.1f);
            return _runtimeMat;
        }

        static Material TryLoadBakedFlameMaterial()
        {
#if UNITY_EDITOR
            return UnityEditor.AssetDatabase.LoadAssetAtPath<Material>(
                "Assets/GameMain/Environment/FX/UnityFire/Materials/Campfire_NoiseFire.mat");
#else
            return null;
#endif
        }

        void StartTransition(bool toIgnite, float duration)
        {
            _isIgnited = toIgnite;
            CampfireWeatherGate.SetVisualIgnited(ControllerId, toIgnite);
            if (toIgnite)
                SetFlameVisible(true);

            _transitionActive = true;
            _transitionToIgnite = toIgnite;
            _transitionDuration = duration;
            _transitionElapsed = 0f;
            _transitionStart = _factor;
            _transitionEnd = toIgnite ? 1f : 0f;
        }

        void SetFlameVisible(bool visible)
        {
            for (int i = 0; i < _flameRenderers.Count; i++)
            {
                if (_flameRenderers[i] != null)
                    _flameRenderers[i].enabled = visible;
            }

            if (_flameRoot != null && _flameRoot.gameObject.activeSelf != visible && visible)
                _flameRoot.gameObject.SetActive(true);
        }

        void EnsureMpb()
        {
            if (_mpb == null)
                _mpb = new MaterialPropertyBlock();
        }

        void ApplyFactor(float f)
        {
            _factor = Mathf.Clamp01(f);
            EnsureMpb();
            float baseColor = 1f;
            float baseAlpha = 1f;
            Material mat = ResolveFlameMaterial();
            if (mat != null)
            {
                baseColor = mat.GetFloat(ColorScaleId);
                baseAlpha = mat.GetFloat(AlphaScaleId);
            }

            _mpb.Clear();
            _mpb.SetFloat(ColorScaleId, Mathf.Lerp(0.15f * baseColor, baseColor, _factor));
            _mpb.SetFloat(AlphaScaleId, baseAlpha * _factor);

            for (int i = 0; i < _flameRenderers.Count; i++)
            {
                MeshRenderer r = _flameRenderers[i];
                if (r == null)
                    continue;
                r.SetPropertyBlock(_mpb);
                r.enabled = _factor > 0.0001f;
            }

            if (_fireLight == null && _lights.Count > 0)
                _fireLight = _lights[0];
            if (_fireLight == null)
                return;

            float target = lightMaxIntensity * _factor;
            if (forceToggleLightEnabled)
            {
                if (_factor > 0f && !_fireLight.enabled)
                    _fireLight.enabled = true;
                if (_factor <= 0.0001f && _fireLight.enabled)
                    _fireLight.enabled = false;
            }

            _fireLight.type = LightType.Point;
            _fireLight.color = lightColor;
            if (_factor > 0f)
                _fireLight.range = Mathf.Max(minRangeOnIgnite, lightRange);
            _fireLight.intensity = target;
        }
    }
}
