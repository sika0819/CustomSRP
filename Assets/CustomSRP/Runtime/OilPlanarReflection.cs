using Game;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace CustomSRP
{
    /// <summary>
    /// Planar water reflection via a dedicated mirror camera.
    /// Borrowing the main camera moved its transform / type every frame and made
    /// nearby trees, tents, and LOD thrash.
    /// </summary>
    public sealed class OilPlanarReflection : MonoBehaviour
    {
        /// <summary>
        /// 泄漏对照关着倒影时不建镜像相机、不分配反射 RT。
        /// 打开 <see cref="IslandLeakProbe.Module.Reflect"/> 后下一帧才会建。
        /// 闺蜜机仍画海面倒影；标准版走路时镜面更新更稀。
        /// </summary>
        public static bool TemporarilyDisabled =>
            !IslandLeakProbe.Allow(IslandLeakProbe.Module.Reflect);

        // Soft oil glaze: tiny RT + shader multi-tap blur (no mips — SRP blit
        // into autoGenerateMips RT leaves higher LODs empty / black).
        public const float Scale = 0.12f;
        public const float MobileScale = 0.1f;
        public const int MinSize = 48;
        public const int MaxSize = 192;
        const float ClipPlaneOffset = 0.04f;

        // Throttle: a full mirror render (cull + opaque + skybox) is expensive.
        // The water shader samples _OilPlanarReflectTex with *current* screen UV,
        // so a stale RT immediately reads as slide / shake / "one frame behind".
        // Rules (pose vs previous frame):
        //   any meaningful turn              -> every frame (never throttle yaw/pitch)
        //   except MirrorTurnGraceFrames     -> reuse previous RT (render scale just changed)
        //   walking on mobile, little turn   -> every WalkMirrorInterval frames
        //   fast move (standing / desktop)   -> every frame
        //   slow creep, no turn              -> even frames only
        //   stationary (below Still*)        -> never (reuse RT)
        const float SlowMoveThresholdSq = 0.0225f; // 0.15 m²
        // Below these: treat as fully still — do not re-render (even-frame path
        // used to fire on zero delta and waste a mirror pass every other frame).
        const float StillMoveThresholdSq = 1e-6f;    // ~1 mm²
        const float StillTurnAngle = 0.05f;          // degrees
        /// <summary>Mobile walk + little turn: re-render mirror every N frames.</summary>
        const int WalkMirrorInterval = 6;

        static readonly int ReflectTexId = Shader.PropertyToID("_OilPlanarReflectTex");
        static readonly int ReflectOnId = Shader.PropertyToID("_OilPlanarReflectOn");
        static readonly int ReflectTexelId = Shader.PropertyToID("_OilPlanarReflectTex_TexelSize");

        static OilPlanarReflection _instance;
        static bool _rendering;
        static int _updatedFrame = -1;
        static bool _suppressed;

        Camera _mirrorCam;
        RenderTexture _reflectionRt;
        int _rtW;
        int _rtH;
        float _planeY;
        bool _hasPlane;
        Matrix4x4 _cachedView;
        Matrix4x4 _cachedProj;
        bool _hasCachedMatrices;
        // Throttle bookkeeping: source camera pose at the previous frame, plus the
        // pose we last actually rendered from (needed to tell "moved" from "turned").
        Vector3 _prevFramePos;
        Quaternion _prevFrameRot;
        bool _hasPrevFramePose;
        // Sky TOD / look changed while the camera is idle — force one mirror redraw
        // so a day-blue RT captured at boot cannot stick over the night skybox.
        bool _skyDirty;
        // InvalidateSky can run from AfterSceneLoad before Ensure() creates the
        // instance (SeedWaterSkyGlobals vs Boot order is undefined). A static bit
        // keeps the request alive so noon cold-start does not freeze a wrong RT.
        static bool _pendingSkyDirty;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void ResetPendingSkyDirty()
        {
            _pendingSkyDirty = false;
            _updatedFrame = -1;
            // Game 不能引用本程序集，退登从这里收回 _OilPlanarReflectTex。
            BoluoLogout.ReleasePlanarReflectTex = ReleaseForLeave;
        }

        /// <summary>
        /// 宿主在 Launch 里，退登不拆。全局贴图和镜面相机都会按住 RT，不解开就留到下次进岛。
        /// </summary>
        public static void ReleaseForLeave()
        {
            _pendingSkyDirty = false;
            _updatedFrame = -1;
            _rendering = false;
            if (_instance != null)
            {
                _instance._hasPrevFramePose = false;
                _instance._hasCachedMatrices = false;
                _instance._skyDirty = false;
                _instance._hasPlane = false;
                _instance.ReleaseMirrorCam();
                _instance.ReleaseReflectionRt();
            }

            Shader.SetGlobalTexture(ReflectTexId, null);
            Shader.SetGlobalFloat(ReflectOnId, 0f);
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            if (TemporarilyDisabled)
            {
                Suppress();
                Debug.Log("[OilPlanarReflection] sea reflection temporarily disabled (leak hunt).");
                return;
            }

            Ensure();
        }

        public static void Ensure()
        {
            if (TemporarilyDisabled)
            {
                Suppress();
                return;
            }

            if (_instance != null)
            {
                return;
            }

            // 启动时建在 Launch 里。Launch 不卸载，HideAndDontSave 也不会跟 Island 一起被拆。
            var go = new GameObject("OilPlanarReflection")
            {
                hideFlags = HideFlags.HideAndDontSave
            };
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

        void OnEnable() => Shader.SetGlobalFloat(ReflectOnId, 0f);

        void OnDisable()
        {
            Shader.SetGlobalFloat(ReflectOnId, 0f);
            ReleaseReflectionRt();
        }

        public static void RenderBefore(
            ScriptableRenderContext context,
            Camera camera,
            RenderGraph renderGraph,
            CameraRenderer renderer,
            CustomRenderPipelineSettings settings)
        {
            if (TemporarilyDisabled)
            {
                Suppress();
                return;
            }

            Ensure();
            _instance?.RenderMirror(context, camera, renderGraph, renderer, settings);
        }

        /// <summary>
        /// Drop the mirror camera and reflection RT, and keep the water shader
        /// off the planar sample. Called every frame while <see cref="TemporarilyDisabled"/>.
        /// </summary>
        static void Suppress()
        {
            if (_suppressed && _instance == null)
            {
                return;
            }

            if (_instance != null)
            {
                _instance._hasPrevFramePose = false;
                _instance._hasCachedMatrices = false;
                _instance._skyDirty = false;
                _instance._hasPlane = false;
                _instance.ReleaseMirrorCam();
                _instance.ReleaseReflectionRt();
            }

            _pendingSkyDirty = false;
            _rendering = false;
            _updatedFrame = -1;
            Shader.SetGlobalTexture(ReflectTexId, null);
            Shader.SetGlobalFloat(ReflectOnId, 0f);
            _suppressed = _instance == null;
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

            CachePlane();
            if (!_hasPlane)
            {
                ReleaseReflectionRt();
                Shader.SetGlobalFloat(ReflectOnId, 0f);
                return;
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
        /// otherwise leak into the water. Afterwards pose delta decides; turn
        /// always wins over walk-interval / even-frame throttle.
        /// </summary>
        bool ShouldRenderThisFrame(Camera source)
        {
            if (_pendingSkyDirty)
            {
                _skyDirty = true;
            }

            if (_reflectionRt == null || !_hasPrevFramePose)
                return true;

            // Keep _skyDirty set until RenderMirror finishes a real capture.
            if (_skyDirty)
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

            // Screen-UV planar sample + stale RT = shake / lag. Never skip on look.
            // 走路/站立切换 renderScale 的头两帧例外：那一帧已经在重建主相机 RT，
            // 再强制整遍倒影就是刚进岛边转边走的那一下。宽限内先复用上一张倒影。
            if (turn > StillTurnAngle)
            {
                if (IslandLocomotionState.MirrorTurnGraceFrames > 0)
                    return false;
                return true;
            }

            // Mobile walk, mostly translation: interval reuse (tiny RT hides walk parallax).
            // 闺蜜机标准版再稀一点：倒影本就糊，少画镜面相机几乎看不出。4K 与手机同间隔。
            if (Application.isMobilePlatform && IslandLocomotionState.IsWalking)
            {
                int interval = Game.BoluoApi.BoluoDeviceInfo.IsSkyworthGmjStandard()
                    ? WalkMirrorInterval + 4
                    : WalkMirrorInterval;
                return (Time.frameCount % interval) == 0;
            }

            if (moveSq > SlowMoveThresholdSq)
                return true;

            // Truly idle: reuse the previous mirror RT (no cull / opaque / skybox).
            if (moveSq <= StillMoveThresholdSq)
                return false;

            // Slow creep, no turn: render on even frames only.
            return (Time.frameCount & 1) == 0;
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
                CoreUtils.Destroy(_mirrorCam.gameObject);
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
            // Sky pixels stay alpha 0. Water keeps analytic sky there and only
            // composites pixels the mirror actually drew (alpha > 0).
            mirror.clearFlags = CameraClearFlags.SolidColor;
            mirror.backgroundColor = Color.clear;
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

            Camera main = IslandJoystickCamera.ActiveView;
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

        void CachePlane()
        {
            ISeaLevel sea = IslandSceneComponent.Active?.Sea;
            if (sea == null)
            {
                _hasPlane = false;
                return;
            }

            _planeY = sea.Level;
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

            w = Mathf.Clamp(Mathf.RoundToInt(srcW * scale), MinSize, MaxSize);
            h = Mathf.Max(MinSize, Mathf.RoundToInt(w / aspect));
            if (h > MaxSize)
            {
                h = MaxSize;
                w = Mathf.Max(MinSize, Mathf.RoundToInt(h * aspect));
            }

            return true;
        }

        void EnsureReflectionRt(int srcW, int srcH, float srcAspect)
        {
            // Same math as DesiredRtSize so the throttle gate and the rebuild
            // never disagree; srcAspect is passed in for the degenerate case.
            float aspect = srcAspect > 1e-4f ? srcAspect : srcW / (float)Mathf.Max(1, srcH);
            float scale = Application.isMobilePlatform ? MobileScale : Scale;

            int w = Mathf.Clamp(Mathf.RoundToInt(srcW * scale), MinSize, MaxSize);
            int h = Mathf.Max(MinSize, Mathf.RoundToInt(w / aspect));
            if (h > MaxSize)
            {
                h = MaxSize;
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
            if (_reflectionRt == null)
            {
                _rtW = 0;
                _rtH = 0;
                return;
            }

            // 全局属性和 targetTexture 都持有引用，只 Destroy 字段卸不掉 GPU 内存。
            if (_mirrorCam != null)
                _mirrorCam.targetTexture = null;
            Shader.SetGlobalTexture(ReflectTexId, null);
            Shader.SetGlobalFloat(ReflectOnId, 0f);

            _reflectionRt.Release();
            // Scene view renders this in edit mode; Object.Destroy is illegal there.
            CoreUtils.Destroy(_reflectionRt);
            _reflectionRt = null;
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
    }
}
