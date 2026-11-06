using System.Collections.Generic;
using Game;
using Game.Native;
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

#if !(UNITY_INCLUDE_INSTRUMENTATION)
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

#if UNITY_INCLUDE_INSTRUMENTATION
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

            // 铺装/退登时不画世界：全场景每帧更重，Present 会把主线程停死。
            // 仍清屏并画 UI，Game 视图才认这台摄像机在渲染，加载条才能往前走。
            // 有 targetTexture 的离线相机（远树 Billboard 烘贴等）必须继续画世界，
            // 否则会把 CoverOnly 黑屏 +「正在进入世界」写进 Albedo。
            bool coverGate = EnvItemStreamerLite.PrefillSuppressRender || IslandGpuReclaim.CoverOnly;
            if (!coverGate)
                ClearCoverBypassLogs();
            if (coverGate && camera.targetTexture == null)
            {
                if (camera.cameraType == CameraType.Game)
                    RenderCoverOnly(context, camera);
                return;
            }

            // 铺装门闩开着但仍画世界：begin 打在绘制之前，卡住时最后一行没有 end。
            float bypassStart = 0f;
            bool bypassPair = false;
            bool bypassWatch = false;
            if (coverGate && camera.targetTexture != null)
            {
                bypassPair = NoteCoverBypassBegin(camera);
                bypassStart = Time.realtimeSinceStartup;
                bypassWatch = true;
            }

            // Push rain globals once per frame BEFORE any draw, so NPR shaders
            // can read _OilRainWetStrength / _OilRainIntensity without per-material
            // property overrides. Falls back to OilRainManager.LateUpdate if active.
            if (camera.cameraType == CameraType.Game)
            {
                if (!IslandLeakProbe.Allow(IslandLeakProbe.Module.Rain))
                {
                    Shader.SetGlobalFloat("_OilRainWetStrength", 0f);
                    Shader.SetGlobalFloat("_OilRainWetSmoothnessBoost", 0f);
                    Shader.SetGlobalFloat("_OilRainWetDarkening", 0f);
                    Shader.SetGlobalFloat("_OilRainIntensity", 0f);
                }
                else if (settings.rainSettings != null)
                {
                    float rainI = settings.rainSettings.intensity;
                    Shader.SetGlobalFloat("_OilRainWetStrength", settings.rainSettings.wetStrength * rainI);
                    Shader.SetGlobalFloat("_OilRainWetSmoothnessBoost", settings.rainSettings.wetSmoothnessBoost * rainI);
                    Shader.SetGlobalFloat("_OilRainWetDarkening", settings.rainSettings.wetDarkening * rainI);
                    Shader.SetGlobalFloat("_OilRainIntensity", rainI);
                    Shader.SetGlobalColor("_OilRainAmbientColor",
                        new Color(0.3f, 0.6f, 0.8f, 1f));
                }
            }
            else if (IslandLeakProbe.Allow(IslandLeakProbe.Module.Rain) && settings.rainSettings != null)
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
                // Asset 写死 26。平板直接抬到 40；手机与闺蜜机仍收到 25。
                shadowSettings.maxDistance = IslandMobileTune.UseTabletShadowDistance
                    ? IslandMobileTune.TabletShadowDistance
                    : Mathf.Min(shadowSettings.maxDistance, 25f);
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

            // Bloom / 调色全档开；湿油另由 AllowsOilFilter。两者任一即可走 PostFX 栈。
            bool hasActivePostFX =
                (IslandGraphicsTier.AllowsPostFx || IslandGraphicsTier.AllowsOilFilter) &&
                IslandLeakProbe.Allow(IslandLeakProbe.Module.PostFx) &&
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
                NoteCoverBypassEnd(camera, bypassStart, bypassPair, bypassWatch);
                return;
            }

            // Cull may rebuild matrices from transform — restore planar reflection view.
            if (isReflection)
            {
                OilPlanarReflection.ReapplyMatrices(camera);
            }

            cullingParameters.shadowDistance = isReflection
                || !IslandLeakProbe.Allow(IslandLeakProbe.Module.Shadow)
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

            if (!isReflection && camera.cameraType == CameraType.Game)
            {
                IslandLocomotionState.BufferWidth = bufferSize.x;
                IslandLocomotionState.BufferHeight = bufferSize.y;
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
                        lightResources,
                        isReflection);

                    // Mirror RT is scenery only. Water already has analytic sky, so the
                    // reflection camera must not draw the skybox again.
                    if (!isReflection && IslandLeakProbe.Allow(IslandLeakProbe.Module.Sky))
                        SkyboxPass.Record(renderGraph, camera, textures);

                    if (!isReflection && IslandLeakProbe.Allow(IslandLeakProbe.Module.Cloud))
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
                // Instanced trees are emitted at Submit, after the mirror's
                // geometry pass. Put winding back before the next camera.
                if (isReflection)
                {
                    commandBuffer.Clear();
                    commandBuffer.SetInvertCulling(false);
                    context.ExecuteCommandBuffer(commandBuffer);
                    context.Submit();
                }
            }
            finally
            {
                CommandBufferPool.Release(commandBuffer);
            }

            NoteCoverBypassEnd(camera, bypassStart, bypassPair, bypassWatch);
        }

        static readonly HashSet<EntityId> BypassBegan = new HashSet<EntityId>();
        static int _bypassSlowLogs;
        const int BypassSlowLogCap = 8;

        static void ClearCoverBypassLogs()
        {
            if (BypassBegan.Count == 0 && _bypassSlowLogs == 0)
                return;
            BypassBegan.Clear();
            _bypassSlowLogs = 0;
        }

        /// <summary>每台相机每次铺装只打一次 begin。返回 true 时本次绘制要配一条 end。</summary>
        static bool NoteCoverBypassBegin(Camera camera)
        {
            if (!BypassBegan.Add(camera.GetEntityId()))
                return false;
            IslandEntryLog.Wait("coverBypass", "begin", "cam=" + CoverCamName(camera));
            return true;
        }

        static void NoteCoverBypassEnd(Camera camera, float startedAt, bool paired, bool watch)
        {
            if (!watch)
                return;
            int ms = (int)((Time.realtimeSinceStartup - startedAt) * 1000f);
            if (paired)
            {
                IslandEntryLog.Ok(
                    "coverBypass",
                    "end",
                    "cam=" + CoverCamName(camera) + " ms=" + ms);
            }

            if (ms < 300 || _bypassSlowLogs >= BypassSlowLogCap)
                return;
            _bypassSlowLogs++;
            IslandEntryLog.Wait(
                "coverBypass",
                "slow",
                "cam=" + CoverCamName(camera) + " ms=" + ms);
        }

        static string CoverCamName(Camera camera)
        {
            if (camera == null || string.IsNullOrEmpty(camera.name))
                return "none";
            return camera.name.Replace(' ', '_');
        }

        static void RenderCoverOnly(ScriptableRenderContext context, Camera camera)
        {
            CommandBuffer cmd = CommandBufferPool.Get("Prefill Cover");
            try
            {
                cmd.SetupCameraProperties(camera);
                cmd.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
                cmd.ClearRenderTarget(true, true, new Color(0.09f, 0.07f, 0.05f, 1f));
                context.ExecuteCommandBuffer(cmd);
                context.DrawUIOverlay(camera);
                bool logSubmit = EnvItemStreamerLite.TakeTailCoverSubmitLog();
                if (logSubmit)
                    Log.Info("[EnvItemStreamerLite] prefill cover submit");
                context.Submit();
                if (logSubmit)
                    Log.Info("[EnvItemStreamerLite] prefill cover submit done");
            }
            finally
            {
                CommandBufferPool.Release(cmd);
            }
        }
    }
}
