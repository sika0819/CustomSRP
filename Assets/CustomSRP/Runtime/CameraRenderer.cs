using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

using UnityGameFramework.Runtime;
namespace CustomSRP
{
    public class CameraRenderer
    {
        public const float renderScaleMin = 0.1f, renderScaleMax = 2f;

        static readonly CameraSettings DefaultCameraSettings = new CameraSettings();

#if !(UNITY_EDITOR || DEVELOPMENT_BUILD)
        static readonly ProfilingSampler DefaultReleaseBuildProfilingSampler =
            new ProfilingSampler("Other Camera");
#endif

        readonly Shadows _shadows = new Shadows();
        readonly PostFXStack _postFXStack = new PostFXStack();
        readonly Material _material;

        public CameraRenderer(Shader shader, Shader cameraDebuggerShader)
        {
            if (shader != null)
            {
                _material = CoreUtils.CreateEngineMaterial(shader);
            }
            else
            {
                Log.Error(
                    "Custom SRP Camera Renderer shader is missing. " +
                    "Assign Hidden/CustomSRP/Camera Renderer on the pipeline asset.");
            }

            CameraDebugger.Initialize(cameraDebuggerShader);
        }

        public void Dispose()
        {
            CoreUtils.Destroy(_material);
            CameraDebugger.Cleanup();
            LightingPass.Cleanup();
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        static ProfilingSampler GetDefaultProfileSampler(Camera camera) =>
            ProfilingSampler.Get(camera.cameraType);
#else
        static ProfilingSampler GetDefaultProfileSampler(Camera camera) =>
            DefaultReleaseBuildProfilingSampler;
#endif

        public void Render(
            RenderGraph renderGraph,
            ScriptableRenderContext context,
            Camera camera,
            CustomRenderPipelineSettings settings)
        {
            if (camera.pixelWidth <= 0 || camera.pixelHeight <= 0)
            {
                return;
            }

            // Push rain globals once per frame BEFORE any draw, so NPR shaders
            // can read _OilRainWetStrength / _OilRainIntensity without per-material
            // property overrides. Falls back to OilRainManager.LateUpdate if active.
            if (settings.rainSettings != null)
            {
                float rainI = settings.rainSettings.intensity;
                Shader.SetGlobalFloat("_OilRainWetStrength", settings.rainSettings.wetStrength * rainI);
                Shader.SetGlobalFloat("_OilRainWetSmoothnessBoost", settings.rainSettings.wetSmoothnessBoost * rainI);
                Shader.SetGlobalFloat("_OilRainWetDarkening", settings.rainSettings.wetDarkening * rainI);
                Shader.SetGlobalFloat("_OilRainIntensity", rainI);
                Shader.SetGlobalColor("_OilRainAmbientColor",
                    new Color(0.3f, 0.6f, 0.8f, 1f));
            }

            CameraBufferSettings bufferSettings = settings.cameraBuffer;
            PostFXSettings postFXSettings = settings.postFXSettings;
            ShadowSettings shadowSettings = settings.shadows;
            // Mobile: reduce shadow draw distance, cascades, and atlas size to keep the
            // shadow rendering affordable on mid-tier tile-based GPUs.
            if (Application.isMobilePlatform)
            {
                shadowSettings = shadowSettings.ClonedForMobile();
                shadowSettings.maxDistance = Mathf.Min(shadowSettings.maxDistance, 25f);
            }

            ProfilingSampler cameraSampler;
            CameraSettings cameraSettings;
            if (camera.TryGetComponent(out CustomRenderPipelineCamera crpCamera))
            {
                cameraSampler = crpCamera.Sampler;
                cameraSettings = crpCamera.Settings;
            }
            else
            {
                cameraSampler = GetDefaultProfileSampler(camera);
                cameraSettings = DefaultCameraSettings;
            }

            bool isReflection = camera.cameraType == CameraType.Reflection;

            bool useColorTexture;
            bool useDepthTexture;
            if (isReflection)
            {
                // Planar water mirror: color/depth copies are unused and expensive.
                useColorTexture = false;
                useDepthTexture = false;
            }
            else
            {
                useColorTexture = bufferSettings.copyColor && cameraSettings.copyColor;
                useDepthTexture = bufferSettings.copyDepth && cameraSettings.copyDepth;
            }

            if (cameraSettings.overridePostFX)
            {
                postFXSettings = cameraSettings.postFXSettings;
            }

            bool hasActivePostFX =
                !isReflection &&
                postFXSettings != null &&
                postFXSettings.Material != null &&
                PostFXSettings.AreApplicableTo(camera);

            // Oil filter needs a depth copy for silhouette edges (falls back to luma-only).
            if (hasActivePostFX && OilFilter.IsEnabled(postFXSettings))
                useDepthTexture = true;

            float renderScale = cameraSettings.GetRenderScale(bufferSettings.renderScale);
            // Mobile mid-tier: nudge render scale down unless the asset explicitly overrode it.
            // ~28% fewer fragments on a 0.85 scale, with bicubic rescale preserving edges.
            if (!isReflection &&
                Application.isMobilePlatform &&
                cameraSettings.renderScaleMode == CameraSettings.RenderScaleMode.Inherit &&
                renderScale >= 0.99f)
            {
                renderScale = 0.85f;
            }
            // Reflection cameras already target a low-res RT — do not scale again.
            bool useScaledRendering = !isReflection &&
                (renderScale < 0.99f || renderScale > 1.01f);

#if UNITY_EDITOR
            if (camera.cameraType == CameraType.SceneView)
            {
                ScriptableRenderContext.EmitWorldGeometryForSceneView(camera);
                useScaledRendering = false;
            }
#endif

            Transform t = camera.transform;
            Quaternion r = t.localRotation;
            if (camera.cameraType == CameraType.SceneView
                || camera.cameraType == CameraType.Preview
                || Mathf.Abs(Quaternion.Dot(r, r) - 1f) > 1e-5f)
            {
                t.localRotation = Quaternion.Normalize(r);
            }

            if (!camera.TryGetCullingParameters(out ScriptableCullingParameters cullingParameters))
            {
                return;
            }

            // Cull may rebuild matrices from transform — restore planar reflection view.
            if (isReflection)
            {
                OilPlanarReflection.ReapplyMatrices(camera);
            }

            cullingParameters.shadowDistance = isReflection
                ? 0f
                : Mathf.Min(shadowSettings.maxDistance, camera.farClipPlane);
            CullingResults cullingResults = context.Cull(ref cullingParameters);

            if (isReflection)
            {
                OilPlanarReflection.ReapplyMatrices(camera);
            }

            bufferSettings.allowHDR &= camera.allowHDR;
            Vector2Int bufferSize = default;
            if (useScaledRendering)
            {
                renderScale = Mathf.Clamp(renderScale, renderScaleMin, renderScaleMax);
                bufferSize.x = Mathf.Max(1, (int)(camera.pixelWidth * renderScale));
                bufferSize.y = Mathf.Max(1, (int)(camera.pixelHeight * renderScale));
            }
            else
            {
                bufferSize.x = camera.pixelWidth;
                bufferSize.y = camera.pixelHeight;
            }

            bufferSettings.fxaa.enabled &= cameraSettings.allowFXAA;

            var copier = new CameraRendererCopier(
                _material, camera, cameraSettings.finalBlendMode);

            CommandBuffer commandBuffer = CommandBufferPool.Get();
            var renderGraphParameters = new RenderGraphParameters
            {
                commandBuffer = commandBuffer,
                currentFrameIndex = Time.frameCount,
                executionId = camera.GetEntityId(),
                generateDebugData =
                    camera.cameraType != CameraType.Preview &&
                    !camera.isProcessingRenderRequest,
                scriptableRenderContext = context
            };

            try
            {
                renderGraph.BeginRecording(renderGraphParameters);
                using (new RenderGraphProfilingScope(renderGraph, cameraSampler))
                {
                    int lightMask = cameraSettings.maskLights
                        ? unchecked((int)(uint)cameraSettings.renderingLayerMask)
                        : -1;

                    // Shadows.Setup only stores the settings struct and resets
                    // _lightCount counters; atlas memory is allocated lazily by
                    // DirectionalShadows.GetHandles when _lightCount > 0. The
                    // reflection path keeps _lightCount at 0 (LightingPass.SetupLights
                    // skips ReserveShadows for isReflection, and ShadowsPass.Record
                    // calls ResetHasLights) so no atlas is allocated — but
                    // ShadowsPass still records the keyword pass that reads
                    // _settings.filterQuality, so Setup must run unconditionally.
                    _shadows.Setup(shadowSettings);

                    LightResources lightResources = new LightResources(
                        LightingPass.Record(
                            renderGraph,
                            cullingResults,
                            bufferSize,
                            settings.forwardPlus,
                            _shadows,
                            lightMask,
                            isReflection),
                        ShadowsPass.Record(
                            renderGraph, cullingResults, _shadows, context,
                            isReflection));

                    CameraRendererTextures textures = SetupPass.Record(
                        renderGraph,
                        useColorTexture,
                        useDepthTexture,
                        bufferSettings.allowHDR,
                        bufferSize,
                        camera,
                        FogSettings.Evaluate(settings.fog, cameraSettings.ignoreFog));

                    GeometryPass.Record(
                        renderGraph,
                        camera,
                        cullingResults,
                        (uint)cameraSettings.renderingLayerMask,
                        true,
                        textures,
                        lightResources);

                    SkyboxPass.Record(renderGraph, camera, textures);

                    if (!isReflection)
                    {
                        CloudPass.Record(renderGraph, camera, bufferSize, textures);
                    }

                    CopyAttachmentsPass.Record(
                        renderGraph,
                        useColorTexture,
                        useDepthTexture,
                        copier,
                        textures,
                        bufferSize);

                    if (!isReflection)
                    {
                        GeometryPass.Record(
                            renderGraph,
                            camera,
                            cullingResults,
                            (uint)cameraSettings.renderingLayerMask,
                            false,
                            textures,
                            lightResources);

                        UnsupportedShadersPass.Record(
                            renderGraph, camera, cullingResults, textures);
                    }

                    TextureHandle colorLUT;
                    if (hasActivePostFX)
                    {
                        _postFXStack.BufferSettings = bufferSettings;
                        _postFXStack.BufferSize = bufferSize;
                        _postFXStack.Camera = camera;
                        _postFXStack.FinalBlendMode = cameraSettings.finalBlendMode;
                        _postFXStack.Settings = postFXSettings;
                        // OilRain screen-space ripples run AFTER OilFilter (in PostFXPass.Record)
                        // when rain settings exist. We do nothing here — the post pass owns the
                        // integration point so it sees the OilFilter output.
                        colorLUT = PostFXPass.Record(
                            renderGraph, _postFXStack,
                            (int)settings.colorLUTResolution,
                            cameraSettings.keepAlpha, textures,
                            settings.rainSettings);
                    }
                    else
                    {
                        colorLUT = default;
                        bool useBicubic = useScaledRendering && FinalPass.ShouldUseBicubic(
                            bufferSettings, bufferSize, camera);
                        FinalPass.Record(
                            renderGraph, copier, textures, useBicubic, bufferSize);
                    }

                    // Iris overlays the resolved camera target (covers world + rain curtain).
                    if (!isReflection)
                        PresetIrisPass.Record(renderGraph, textures, camera);

                    if (!isReflection)
                    {
                        DebugPass.Record(
                            renderGraph, settings, camera,
                            (int)settings.colorLUTResolution, colorLUT, lightResources);
                        GizmosPass.Record(renderGraph, copier, textures);
                    }
                }

                renderGraph.EndRecordingAndExecute();
                context.ExecuteCommandBuffer(commandBuffer);
                if (camera.cameraType == CameraType.Game)
                {
                    context.DrawUIOverlay(camera);
                }
                context.Submit();
            }
            finally
            {
                CommandBufferPool.Release(commandBuffer);
            }
        }
    }
}
