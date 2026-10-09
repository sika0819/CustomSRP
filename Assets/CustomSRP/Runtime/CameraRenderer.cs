using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

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
                Debug.LogError(
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

            CameraBufferSettings bufferSettings = settings.cameraBuffer;
            PostFXSettings postFXSettings = settings.postFXSettings;
            ShadowSettings shadowSettings = settings.shadows;

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

            bool useColorTexture;
            bool useDepthTexture;
            if (camera.cameraType == CameraType.Reflection)
            {
                useColorTexture = bufferSettings.copyColorReflection;
                useDepthTexture = bufferSettings.copyDepthReflection;
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
                postFXSettings != null &&
                postFXSettings.Material != null &&
                PostFXSettings.AreApplicableTo(camera);

            float renderScale = cameraSettings.GetRenderScale(bufferSettings.renderScale);
            bool useScaledRendering = renderScale < 0.99f || renderScale > 1.01f;

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

            cullingParameters.shadowDistance =
                Mathf.Min(shadowSettings.maxDistance, camera.farClipPlane);
            CullingResults cullingResults = context.Cull(ref cullingParameters);

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

                    _shadows.Setup(shadowSettings);
                    var lightResources = new LightResources(
                        LightingPass.Record(
                            renderGraph,
                            cullingResults,
                            bufferSize,
                            settings.forwardPlus,
                            _shadows,
                            lightMask),
                        ShadowsPass.Record(
                            renderGraph, cullingResults, _shadows, context));

                    CameraRendererTextures textures = SetupPass.Record(
                        renderGraph,
                        useColorTexture,
                        useDepthTexture,
                        bufferSettings.allowHDR,
                        bufferSize,
                        camera);

                    GeometryPass.Record(
                        renderGraph,
                        camera,
                        cullingResults,
                        (uint)cameraSettings.renderingLayerMask,
                        true,
                        textures,
                        lightResources);

                    SkyboxPass.Record(renderGraph, camera, textures);

                    CloudPass.Record(renderGraph, camera, bufferSize, textures);

                    CopyAttachmentsPass.Record(
                        renderGraph,
                        useColorTexture,
                        useDepthTexture,
                        copier,
                        textures);

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

                    TextureHandle colorLUT;
                    if (hasActivePostFX)
                    {
                        _postFXStack.BufferSettings = bufferSettings;
                        _postFXStack.BufferSize = bufferSize;
                        _postFXStack.Camera = camera;
                        _postFXStack.FinalBlendMode = cameraSettings.finalBlendMode;
                        _postFXStack.Settings = postFXSettings;
                        colorLUT = PostFXPass.Record(
                            renderGraph, _postFXStack,
                            (int)settings.colorLUTResolution,
                            cameraSettings.keepAlpha, textures);
                    }
                    else
                    {
                        colorLUT = default;
                        FinalPass.Record(renderGraph, copier, textures);
                    }

                    DebugPass.Record(
                        renderGraph, settings, camera,
                        (int)settings.colorLUTResolution, colorLUT, lightResources);
                    GizmosPass.Record(renderGraph, copier, textures);
                }

                renderGraph.EndRecordingAndExecute();
                context.ExecuteCommandBuffer(commandBuffer);
                context.Submit();
            }
            finally
            {
                CommandBufferPool.Release(commandBuffer);
            }
        }
    }
}
