using Game;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace CustomSRP
{
    /// <summary>
    /// Planar water reflection via a dedicated mirror camera (never touches Camera.main).
    /// Borrowing the main camera moved its transform / type every frame and made
    /// nearby trees, tents, and LOD thrash.
    /// </summary>
    public sealed class OilPlanarReflection : MonoBehaviour, IOilPlanarReflectionControl
    {
        // Soft oil glaze: tiny RT + shader multi-tap blur (no mips — SRP blit
        // into autoGenerateMips RT leaves higher LODs empty / black).
        public const float Scale = 0.12f;
        public const float MobileScale = 0.1f;
        public const int MinSize = 48;
        public const int MaxSize = 192;
        /// <summary>Gmj 4K：低于桌面 Max，带宽优先。</summary>
        public const int GmjMaxSize = 64;
        const float RescanSeconds = 1f;
        const float ClipPlaneOffset = 0.04f;

        // Throttle: a full mirror render (cull + opaque + skybox) is expensive.
        // The water shader samples _OilPlanarReflectTex with *current* screen UV,
        // so a stale RT immediately reads as slide / shake / "one frame behind".
        // Rules (pose vs previous frame):
        //   walking / any translation        -> every frame (never interval — slides)
        //   meaningful turn (non-Gmj)        -> every frame
        //   Gmj stand turn                   -> every GmjTurnMirrorInterval frames,
        //                                      or sooner if yaw vs last mirror RT
        //                                      exceeds GmjTurnStaleDegrees
        //   stationary (below Still*)        -> never (reuse RT)
        // Below these: treat as fully still — do not re-render.
        const float StillMoveThresholdSq = 1e-6f;    // ~1 mm²
        const float StillTurnAngle = 0.05f;          // degrees
        /// <summary>Preset yaw snap — bypass Gmj turn interval the same way.</summary>
        const float SnapTurnAngle = 12f;
        /// <summary>
        /// Gmj 站立转头隔帧：4K 站立每帧双相机（主 + mirror）是「刚启动右转」尖峰主因。
        /// 走路 / 位移不走这条——screen-UV + 隔帧 RT 会整块抖。
        /// </summary>
        const int GmjTurnMirrorInterval = 2;
        /// <summary>
        /// Max yaw (deg) the reused mirror RT may lag behind the live camera on Gmj.
        /// Continuous look forces a redraw once this accumulates, even mid-interval.
        /// </summary>
        const float GmjTurnStaleDegrees = 1.5f;

        static readonly int ReflectTexId = Shader.PropertyToID("_OilPlanarReflectTex");
        static readonly int ReflectOnId = Shader.PropertyToID("_OilPlanarReflectOn");
        static readonly int ReflectTexelId = Shader.PropertyToID("_OilPlanarReflectTex_TexelSize");

        static OilPlanarReflection _instance;
        static bool _rendering;
        static int _updatedFrame = -1;

        Camera _mirrorCam;
        RenderTexture _reflectionRt;
        int _rtW;
        int _rtH;
        float _nextBind;
        float _planeY;
        float _lastRenderedPlaneY;
        bool _hasPlane;
        bool _planeDirty;
        // Cached water planes (ocean + rivers). Screen-UV planar sampling is only
        // correct on the chosen Y — locking to ocean while looking at an elevated
        // river stretches sky into stripes and drops nearby mountain reflections.
        const int MaxWaterPlanes = 24;
        struct WaterPlane
        {
            public float y;
            public Vector3 center;
            public float radiusXZ;
        }

        readonly WaterPlane[] _waterPlanes = new WaterPlane[MaxWaterPlanes];
        int _waterPlaneCount;
        Matrix4x4 _cachedView;
        Matrix4x4 _cachedProj;
        bool _hasCachedMatrices;
        // Throttle bookkeeping: source camera pose at the previous frame, plus the
        // pose we last actually rendered from (needed to tell "moved" from "turned").
        Vector3 _prevFramePos;
        Quaternion _prevFrameRot;
        bool _hasPrevFramePose;
        /// <summary>Source rotation at the last successful mirror capture (skip path must not overwrite).</summary>
        Quaternion _lastMirrorRot;
        bool _hasLastMirrorRot;
        // Sky TOD / look changed while the camera is idle — force one mirror redraw
        // so a day-blue RT captured at boot cannot stick over the night skybox.
        bool _skyDirty;
        // InvalidateSky can run from AfterSceneLoad before Ensure() creates the
        // instance (SeedWaterSkyGlobals vs Boot order is undefined). A static bit
        // keeps the request alive so noon cold-start does not freeze a wrong RT.
        static bool _pendingSkyDirty;
        /// <summary>Idle immersive: RT released; RenderMirror early-outs until Resume.</summary>
        bool _immersiveSuspended;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void ResetPendingSkyDirty()
        {
            _pendingSkyDirty = false;
            _updatedFrame = -1;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot() => Ensure();

        public static void Ensure()
        {
            if (_instance != null)
            {
                return;
            }

            var go = new GameObject("OilPlanarReflection")
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            if (Application.isPlaying)
            {
                DontDestroyOnLoad(go);
            }

            _instance = go.AddComponent<OilPlanarReflection>();
            if (_pendingSkyDirty)
            {
                _instance._skyDirty = true;
            }
        }

        /// <summary>
        /// Sky period / look globals changed. Idle throttle would otherwise keep a
        /// stale mirror RT (startup day-blue over a night skybox). Drop ReflectOn
        /// immediately so water falls back to analytic sky until the next mirror draw.
        /// </summary>
        public static void InvalidateSky()
        {
            Shader.SetGlobalFloat(ReflectOnId, 0f);
            _pendingSkyDirty = true;
            if (_instance != null)
            {
                _instance._skyDirty = true;
            }
        }

        void OnEnable()
        {
            OilPlanarReflectionControl.Driver = this;
            Shader.SetGlobalFloat(ReflectOnId, 0f);
        }

        void OnDisable()
        {
            if (ReferenceEquals(OilPlanarReflectionControl.Driver, this))
                OilPlanarReflectionControl.Driver = null;
            Shader.SetGlobalFloat(ReflectOnId, 0f);
            ReleaseReflectionRt();
        }

        public void SuspendForImmersive()
        {
            _immersiveSuspended = true;
            Shader.SetGlobalFloat(ReflectOnId, 0f);
            ReleaseReflectionRt();
        }

        public void ResumeFromImmersive()
        {
            _immersiveSuspended = false;
        }

        public static void RenderBefore(
            ScriptableRenderContext context,
            Camera camera,
            RenderGraph renderGraph,
            CameraRenderer renderer,
            CustomRenderPipelineSettings settings)
        {
            Ensure();
            _instance?.RenderMirror(context, camera, renderGraph, renderer, settings);
        }

        /// <summary>
        /// Cull / SetupCameraProperties can rebuild matrices from transform.
        /// Re-apply the cached mirror view after cull on the dedicated camera only.
        /// </summary>
        public static void ReapplyMatrices(Camera camera)
        {
            if (_instance == null || !_rendering || camera == null ||
                camera != _instance._mirrorCam)
            {
                return;
            }

            _instance.ApplyCachedMatrices();
        }

        void OnDestroy()
        {
            if (_instance == this)
            {
                _instance = null;
            }

            if (ReferenceEquals(OilPlanarReflectionControl.Driver, this))
                OilPlanarReflectionControl.Driver = null;

            ReleaseMirrorCam();
            ReleaseReflectionRt();
        }

        void RenderMirror(
            ScriptableRenderContext context,
            Camera source,
            RenderGraph renderGraph,
            CameraRenderer renderer,
            CustomRenderPipelineSettings settings)
        {
            IslandLocomotionState.MirrorRenderedThisFrame = false;

            if (_immersiveSuspended)
            {
                Shader.SetGlobalFloat(ReflectOnId, 0f);
                return;
            }

            if (_rendering || source == null || !source.enabled)
            {
                return;
            }

            if (!IsPrimaryGameCamera(source) || _updatedFrame == Time.frameCount)
            {
                return;
            }

            if (source.pixelWidth < 8 || source.pixelHeight < 8)
            {
                return;
            }

            if (Time.unscaledTime >= _nextBind)
            {
                _nextBind = Time.unscaledTime + RescanSeconds;
                // One scene walk shared by layer bind, shore-wet Ys, and plane scan.
                Renderer[] renderers = Object.FindObjectsByType<Renderer>(
                    FindObjectsInactive.Exclude);
                OilWaterLayers.BindWaters(renderers);
                RescanWaterPlanes(renderers);
            }
            else if (_waterPlaneCount == 0)
            {
                RescanWaterPlanes(null);
            }

            // Pick ocean vs river plane *before* throttle — wrong-Y RT must not be reused.
            SelectPlaneForCamera(source);
            if (!_hasPlane)
            {
                return;
            }

            if (Mathf.Abs(_planeY - _lastRenderedPlaneY) > 0.35f)
            {
                _planeDirty = true;
            }

            // Throttle: if the camera hasn't moved or rotated meaningfully since
            // the last successful mirror capture, reuse the existing RT. Water
            // shader still samples the same _OilPlanarReflectTex, so the user
            // sees a stable reflection and we save a full mirror camera render
            // (cull + opaque + skybox on a reflection probe = expensive).
            if (!ShouldRenderThisFrame(source))
            {
                // Re-emit globals only; the cached RT content already matches.
                if (_reflectionRt != null && _rtW > 0 && _rtH > 0)
                {
                    Shader.SetGlobalTexture(ReflectTexId, _reflectionRt);
                    Shader.SetGlobalFloat(ReflectOnId, 1f);
                    Shader.SetGlobalVector(
                        ReflectTexelId,
                        new Vector4(1f / _rtW, 1f / _rtH, _rtW, _rtH));
                }
                _updatedFrame = Time.frameCount;
                _prevFramePos = source.transform.position;
                _prevFrameRot = source.transform.rotation;
                _hasPrevFramePose = true;
                return;
            }

            int srcW = source.pixelWidth;
            int srcH = source.pixelHeight;
            EnsureReflectionRt(srcW, srcH, srcW / (float)srcH);
            if (_reflectionRt == null)
            {
                return;
            }

            Camera mirror = EnsureMirrorCam();
            if (mirror == null)
            {
                return;
            }

            _rendering = true;
            _updatedFrame = Time.frameCount;
            bool prevInvert = GL.invertCulling;
            GL.invertCulling = true;
            try
            {
                ConfigureMirror(mirror, source, _planeY);
                renderer.Render(renderGraph, context, mirror, settings);
            }
            finally
            {
                GL.invertCulling = prevInvert;
                _hasCachedMatrices = false;
                _rendering = false;
            }

            IslandLocomotionState.MirrorRenderedThisFrame = true;
            _prevFramePos = source.transform.position;
            _prevFrameRot = source.transform.rotation;
            _hasPrevFramePose = true;
            _lastMirrorRot = _prevFrameRot;
            _hasLastMirrorRot = true;
            _lastRenderedPlaneY = _planeY;
            _planeDirty = false;
            // Only clear after a real draw — early outs (!_hasPlane / no RT) must
            // keep the dirty bit so the next frame still redraws.
            _skyDirty = false;
            _pendingSkyDirty = false;

            Shader.SetGlobalTexture(ReflectTexId, _reflectionRt);
            Shader.SetGlobalFloat(ReflectOnId, 1f);
            Shader.SetGlobalVector(
                ReflectTexelId,
                new Vector4(1f / _rtW, 1f / _rtH, _rtW, _rtH));
        }

        /// <summary>
        /// Frame budget gate for the mirror render. First frame and any frame
        /// after the RT was (re)created must draw — a stale or empty RT would
        /// otherwise leak into the water. Afterwards pose delta decides.
        /// Translation / walking always draws; Gmj may still throttle stand look.
        /// </summary>
        bool ShouldRenderThisFrame(Camera source)
        {
            if (_pendingSkyDirty)
            {
                _skyDirty = true;
            }

            if (_reflectionRt == null || !_hasPrevFramePose)
                return true;

            // Cross-assembly one-shot (Game warmup / TOD) — draw even under Gmj turn throttle.
            if (IslandLocomotionState.ForceMirrorRedraw)
            {
                IslandLocomotionState.ForceMirrorRedraw = false;
                return true;
            }

            // Keep _skyDirty set until RenderMirror finishes a real capture.
            if (_skyDirty)
                return true;

            // Ocean ↔ river plane switch — previous RT is mirrored on the wrong Y.
            if (_planeDirty)
                return true;

            // A resolution flip (orientation change / render-scale tweak) resizes the
            // RT in EnsureReflectionRt below, which this gate runs before. Draw this
            // frame so the water never samples a stale-sized mirror for a frame.
            if (DesiredRtSize(source, out int w, out int h)
                && (w != _rtW || h != _rtH))
            {
                return true;
            }

            Vector3 pos = source.transform.position;
            float dx = pos.x - _prevFramePos.x;
            float dy = pos.y - _prevFramePos.y;
            float dz = pos.z - _prevFramePos.z;
            float moveSq = dx * dx + dy * dy + dz * dz;
            float turn = Quaternion.Angle(source.transform.rotation, _prevFrameRot);
            bool gmj = DeviceGraphicsPolicy.Current == DeviceGraphicsClass.Gmj;

            // Locomotion: always lock screen-UV to a fresh mirror. Interval reuse
            // while walking was the "水面倒影剧烈抖动" path.
            if (IslandLocomotionState.IsWalking || moveSq > StillMoveThresholdSq)
                return true;

            // Non-Gmj: never skip on look (screen-UV + stale RT = shake).
            // Gmj stand turn: share hitch budget; large yaw snaps (preset) draw;
            // continuous look redraws once past GmjTurnStaleDegrees.
            if (turn > StillTurnAngle)
            {
                if (gmj && turn < SnapTurnAngle)
                {
                    if (_hasLastMirrorRot
                        && Quaternion.Angle(source.transform.rotation, _lastMirrorRot)
                            >= GmjTurnStaleDegrees)
                        return true;
                    return (Time.frameCount % GmjTurnMirrorInterval) == 0;
                }
                return true;
            }

            // Truly idle: reuse the previous mirror RT (no cull / opaque / skybox).
            return false;
        }

        Camera EnsureMirrorCam()
        {
            if (_mirrorCam != null)
            {
                return _mirrorCam;
            }

            var go = new GameObject("OilPlanarMirrorCam")
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            go.transform.SetParent(transform, false);
            _mirrorCam = go.AddComponent<Camera>();
            // Disabled so Unity never puts it in the pipeline camera list.
            _mirrorCam.enabled = false;
            _mirrorCam.cameraType = CameraType.Reflection;
            _mirrorCam.allowHDR = false;
            _mirrorCam.allowMSAA = false;
            _mirrorCam.useOcclusionCulling = false;
            return _mirrorCam;
        }

        void ReleaseMirrorCam()
        {
            if (_mirrorCam != null)
            {
                var go = _mirrorCam.gameObject;
                if (Application.isPlaying)
                {
                    Destroy(go);
                }
                else
                {
                    DestroyImmediate(go);
                }

                _mirrorCam = null;
            }
        }

        void ConfigureMirror(Camera mirror, Camera source, float planeY)
        {
            Vector3 normal = Vector3.up;
            Vector3 planePoint = new Vector3(0f, planeY, 0f);
            float d = -Vector3.Dot(normal, planePoint);
            Matrix4x4 reflection = ReflectionMatrix(new Vector4(normal.x, normal.y, normal.z, d));

            Matrix4x4 sourceView = source.worldToCameraMatrix;
            Matrix4x4 sourceProj = source.projectionMatrix;

            mirror.cameraType = CameraType.Reflection;
            mirror.targetTexture = _reflectionRt;
            mirror.allowHDR = false;
            mirror.allowMSAA = false;
            mirror.clearFlags = CameraClearFlags.Skybox;
            mirror.backgroundColor = Color.black;
            // Mirror camera culling: inherit the source culling mask, but drop
            // Water (sea doesn't reflect inside itself) and ReflectionSkip
            // (grass / clutter / small rocks — mainCameraOnly via native
            // instancing already excludes grass & clutter; small rocks that
            // bypass instancing because of SkipNative now land on the
            // ReflectionSkip layer and get filtered here too).
            mirror.cullingMask =
                OilWaterLayers.PlanarReflectionCullingMask & source.cullingMask;
            mirror.nearClipPlane = Mathf.Max(0.3f, source.nearClipPlane);
            mirror.farClipPlane = source.farClipPlane;
            mirror.fieldOfView = source.fieldOfView;
            mirror.orthographic = source.orthographic;
            mirror.orthographicSize = source.orthographicSize;
            mirror.aspect = source.aspect;

            Vector3 reflectedPos = reflection.MultiplyPoint(source.transform.position);
            Vector3 euler = source.transform.eulerAngles;
            mirror.transform.SetPositionAndRotation(
                reflectedPos, Quaternion.Euler(-euler.x, euler.y, euler.z));

            _cachedView = sourceView * reflection;
            mirror.worldToCameraMatrix = _cachedView;

            Vector4 clipPlane = CameraSpacePlane(mirror, planePoint, normal, 1f);
            mirror.ResetProjectionMatrix();
            mirror.projectionMatrix = sourceProj;
            _cachedProj = mirror.CalculateObliqueMatrix(clipPlane);
            mirror.projectionMatrix = _cachedProj;
            mirror.cullingMatrix = _cachedProj * _cachedView;
            _hasCachedMatrices = true;
        }

        static bool IsPrimaryGameCamera(Camera camera)
        {
            if (camera.cameraType != CameraType.Game || camera.targetTexture != null)
            {
                return false;
            }

            Camera main = Camera.main;
            if (main != null && main.isActiveAndEnabled && main.targetTexture == null)
            {
                return camera == main;
            }

            Camera[] all = Camera.allCameras;
            Camera best = null;
            float depth = float.MinValue;
            for (int i = 0; i < all.Length; i++)
            {
                Camera c = all[i];
                if (c == null || !c.isActiveAndEnabled ||
                    c.targetTexture != null || c.cameraType != CameraType.Game)
                {
                    continue;
                }

                if (c.depth >= depth)
                {
                    depth = c.depth;
                    best = c;
                }
            }

            return best != null && camera == best;
        }

        void RescanWaterPlanes(Renderer[] cachedRenderers)
        {
            _waterPlaneCount = 0;

            OilOceanSurface[] oceans = Object.FindObjectsByType<OilOceanSurface>(
                FindObjectsInactive.Exclude);
            for (int i = 0; i < oceans.Length && _waterPlaneCount < MaxWaterPlanes; i++)
            {
                OilOceanSurface ocean = oceans[i];
                if (ocean == null)
                {
                    continue;
                }

                float radius = Mathf.Max(ocean.gridScale * 0.5f, 64f);
                _waterPlanes[_waterPlaneCount++] = new WaterPlane
                {
                    y = ocean.transform.position.y,
                    center = ocean.transform.position,
                    radiusXZ = radius
                };
            }

            Renderer[] renderers = cachedRenderers
                ?? Object.FindObjectsByType<Renderer>(FindObjectsInactive.Exclude);
            for (int i = 0; i < renderers.Length && _waterPlaneCount < MaxWaterPlanes; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null || !IsOilRiver(renderer))
                {
                    continue;
                }

                Bounds b = renderer.bounds;
                float radius = Mathf.Max(Mathf.Max(b.extents.x, b.extents.z), 8f);
                _waterPlanes[_waterPlaneCount++] = new WaterPlane
                {
                    y = b.center.y,
                    center = b.center,
                    radiusXZ = radius
                };
            }

            _hasPlane = _waterPlaneCount > 0;
        }

        /// <summary>
        /// Screen-UV planar sampling is only valid on one horizontal plane.
        /// Pick the water the camera is looking at (ray ∩ plane near that water's
        /// XZ bounds) so inland rivers aren't forced onto the ocean mirror Y.
        /// </summary>
        void SelectPlaneForCamera(Camera source)
        {
            if (_waterPlaneCount <= 0)
            {
                _hasPlane = false;
                return;
            }

            if (source == null)
            {
                _planeY = _waterPlanes[0].y;
                _hasPlane = true;
                return;
            }

            Vector3 camPos = source.transform.position;
            Vector3 fwd = source.transform.forward;
            float bestScore = float.MaxValue;
            float bestY = _waterPlanes[0].y;

            for (int i = 0; i < _waterPlaneCount; i++)
            {
                WaterPlane p = _waterPlanes[i];
                float score;
                if (Mathf.Abs(fwd.y) > 1e-4f)
                {
                    float t = (p.y - camPos.y) / fwd.y;
                    if (t > 0.4f && t < 900f)
                    {
                        Vector3 hit = camPos + fwd * t;
                        float dx = hit.x - p.center.x;
                        float dz = hit.z - p.center.z;
                        float dist = Mathf.Sqrt(dx * dx + dz * dz);
                        score = dist;
                        if (dist > p.radiusXZ)
                        {
                            score += (dist - p.radiusXZ) * 3f;
                        }
                    }
                    else
                    {
                        float dx = camPos.x - p.center.x;
                        float dz = camPos.z - p.center.z;
                        score = Mathf.Sqrt(dx * dx + dz * dz) + 250f;
                    }
                }
                else
                {
                    float dx = camPos.x - p.center.x;
                    float dz = camPos.z - p.center.z;
                    score = Mathf.Sqrt(dx * dx + dz * dz) + 250f;
                }

                // Prefer local water near the camera height (river valley vs distant sea).
                score += Mathf.Abs(camPos.y - p.y) * 0.08f;

                if (score < bestScore)
                {
                    bestScore = score;
                    bestY = p.y;
                }
            }

            _planeY = bestY;
            _hasPlane = true;
        }

        /// <summary>Mirror RT size for a source resolution. Mirrors
        /// <see cref="EnsureReflectionRt"/> exactly so the throttle gate can predict
        /// a resize before the RT is actually rebuilt.</summary>
        static bool DesiredRtSize(Camera source, out int w, out int h)
        {
            w = h = 0;
            int srcW = source.pixelWidth;
            int srcH = source.pixelHeight;
            if (srcW < 8 || srcH < 8)
                return false;

            float scale = Application.isMobilePlatform ? MobileScale : Scale;
            float aspect = srcW / (float)srcH;
            if (aspect <= 1e-4f)
                return false;

            int maxSize = EffectiveMaxSize();
            w = Mathf.Clamp(Mathf.RoundToInt(srcW * scale), MinSize, maxSize);
            h = Mathf.Max(MinSize, Mathf.RoundToInt(w / aspect));
            if (h > maxSize)
            {
                h = maxSize;
                w = Mathf.Max(MinSize, Mathf.RoundToInt(h * aspect));
            }

            return true;
        }

        static int EffectiveMaxSize() =>
            DeviceGraphicsPolicy.Current == DeviceGraphicsClass.Gmj ? GmjMaxSize : MaxSize;

        void EnsureReflectionRt(int srcW, int srcH, float srcAspect)
        {
            // Same math as DesiredRtSize so the throttle gate and the rebuild
            // never disagree; srcAspect is passed in for the degenerate case.
            float aspect = srcAspect > 1e-4f ? srcAspect : srcW / (float)Mathf.Max(1, srcH);
            float scale = Application.isMobilePlatform ? MobileScale : Scale;
            int maxSize = EffectiveMaxSize();

            int w = Mathf.Clamp(Mathf.RoundToInt(srcW * scale), MinSize, maxSize);
            int h = Mathf.Max(MinSize, Mathf.RoundToInt(w / aspect));
            if (h > maxSize)
            {
                h = maxSize;
                w = Mathf.Max(MinSize, Mathf.RoundToInt(h * aspect));
            }

            if (_reflectionRt != null &&
                _rtW == w &&
                _rtH == h &&
                _reflectionRt.IsCreated() &&
                !_reflectionRt.useMipMap)
            {
                return;
            }

            ReleaseReflectionRt();

            var desc = new RenderTextureDescriptor(w, h)
            {
                graphicsFormat = GraphicsFormat.R8G8B8A8_UNorm,
                depthBufferBits = 16,
                msaaSamples = 1,
                sRGB = QualitySettings.activeColorSpace == ColorSpace.Linear,
                useMipMap = false,
                autoGenerateMips = false
            };
            _reflectionRt = new RenderTexture(desc)
            {
                name = "ReflectionRT",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave,
                anisoLevel = 0
            };
            _reflectionRt.Create();
            _rtW = w;
            _rtH = h;
        }

        void ReleaseReflectionRt()
        {
            if (_reflectionRt != null)
            {
                _reflectionRt.Release();
                if (Application.isPlaying)
                {
                    Destroy(_reflectionRt);
                }
                else
                {
                    DestroyImmediate(_reflectionRt);
                }

                _reflectionRt = null;
            }

            _rtW = 0;
            _rtH = 0;
        }

        void ApplyCachedMatrices()
        {
            if (!_hasCachedMatrices || _mirrorCam == null)
            {
                return;
            }

            _mirrorCam.worldToCameraMatrix = _cachedView;
            _mirrorCam.projectionMatrix = _cachedProj;
            _mirrorCam.cullingMatrix = _cachedProj * _cachedView;
        }

        static Matrix4x4 ReflectionMatrix(Vector4 plane)
        {
            Matrix4x4 m = Matrix4x4.identity;
            m.m00 = 1f - 2f * plane.x * plane.x;
            m.m01 = -2f * plane.x * plane.y;
            m.m02 = -2f * plane.x * plane.z;
            m.m03 = -2f * plane.w * plane.x;
            m.m10 = -2f * plane.y * plane.x;
            m.m11 = 1f - 2f * plane.y * plane.y;
            m.m12 = -2f * plane.y * plane.z;
            m.m13 = -2f * plane.w * plane.y;
            m.m20 = -2f * plane.z * plane.x;
            m.m21 = -2f * plane.z * plane.y;
            m.m22 = 1f - 2f * plane.z * plane.z;
            m.m23 = -2f * plane.w * plane.z;
            m.m30 = 0f;
            m.m31 = 0f;
            m.m32 = 0f;
            m.m33 = 1f;
            return m;
        }

        static Vector4 CameraSpacePlane(Camera cam, Vector3 pos, Vector3 normal, float sideSign)
        {
            Vector3 offsetPos = pos + normal * ClipPlaneOffset;
            Matrix4x4 m = cam.worldToCameraMatrix;
            Vector3 cPos = m.MultiplyPoint(offsetPos);
            Vector3 cNormal = m.MultiplyVector(normal).normalized * sideSign;
            return new Vector4(cNormal.x, cNormal.y, cNormal.z, -Vector3.Dot(cPos, cNormal));
        }

        static bool IsOilRiver(Renderer renderer)
        {
            Material[] materials = renderer.sharedMaterials;
            for (int i = 0; i < materials.Length; i++)
            {
                Material material = materials[i];
                if (material != null &&
                    material.shader != null &&
                    material.shader.name == "CustomSRP/OilRiverNPR")
                {
                    return true;
                }
            }

            return false;
        }
    }
}
