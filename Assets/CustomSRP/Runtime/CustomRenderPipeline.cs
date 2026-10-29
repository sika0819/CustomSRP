using System;
using System.Collections.Generic;
using Game;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace CustomSRP
{
    public partial class CustomRenderPipeline : RenderPipeline
    {
        RenderGraph _renderGraph;
        readonly CustomRenderPipelineSettings _settings;
        readonly CameraRenderer _cameraRenderer;
        bool _uiOverlayReady;

        public CustomRenderPipeline(CustomRenderPipelineSettings settings)
        {
            _settings = settings;
            GraphicsSettings.useScriptableRenderPipelineBatching = settings.useSRPBatcher;
            GraphicsSettings.lightsUseLinearIntensity = true;
            _cameraRenderer = new CameraRenderer(
                settings.cameraRendererShader, settings.cameraDebuggerShader);
            _renderGraph = new RenderGraph("Custom SRP Render Graph");
            // UI Toolkit runtime panels are painted by CameraRenderer via
            // ScriptableRenderContext.DrawUIOverlay. The engine overlay pass does
            // not run for a custom render pipeline, so the pipeline has to claim
            // the UI overlay on every platform — Editor and player alike.
            EnsureUIOverlay();
            InitializeForEditor();
        }

        /// <summary>
        /// Claims the UI overlay for this pipeline so <c>DrawUIOverlay</c> is honoured
        /// in players as well. <c>SupportedRenderingFeatures.active</c> is created by
        /// the engine when the pipeline is enabled, so this is retried until it sticks.
        /// </summary>
        void EnsureUIOverlay()
        {
            if (_uiOverlayReady)
                return;

            SupportedRenderingFeatures features = SupportedRenderingFeatures.active;
            if (features == null)
                return;

            features.rendersUIOverlay = true;
            _uiOverlayReady = true;
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            DisposeForEditor();
            _cameraRenderer.Dispose();
            _renderGraph.Cleanup();
        }

        /// <summary>
        /// 退登后把池里的整屏目标还给驱动。Cleanup 会注销这张图，所以立刻换一张空的。
        /// </summary>
        public static void FlushGpuPool()
        {
            if (RenderPipelineManager.currentPipeline is not CustomRenderPipeline pipeline)
                return;
            pipeline._renderGraph.Cleanup();
            pipeline._renderGraph = new RenderGraph("Custom SRP Render Graph");
        }

        partial void InitializeForEditor();

        partial void DisposeForEditor();

        /// <summary>
        /// 上一帧命令缓冲在 ClearGlobalBindings 之后才绑上这些纹理。
        /// 退登改走 CoverOnly 后不再覆盖，全局槽会把整屏和阴影图留在显存里。
        /// </summary>
        public static void ReleasePinnedGlobals()
        {
            Shader.SetGlobalTexture(Shader.PropertyToID("_CameraColorTexture"), null);
            Shader.SetGlobalTexture(Shader.PropertyToID("_CameraDepthTexture"), null);
            Shader.SetGlobalTexture(Shader.PropertyToID("_CameraDepthTexture_ForRain"), null);
            Shader.SetGlobalTexture(Shader.PropertyToID("_DirectionalShadowAtlas"), null);
            Shader.SetGlobalTexture(Shader.PropertyToID("_OtherShadowAtlas"), null);
            Shader.SetGlobalTexture(Shader.PropertyToID("_PostFXSource"), null);
            Shader.SetGlobalTexture(Shader.PropertyToID("_PostFXSource2"), null);
            Shader.SetGlobalTexture(Shader.PropertyToID("_SourceTexture"), null);
            Shader.SetGlobalBuffer(Shader.PropertyToID("_DirectionalLightData"), (ComputeBuffer)null);
            Shader.SetGlobalBuffer(Shader.PropertyToID("_OtherLightData"), (ComputeBuffer)null);
            Shader.SetGlobalBuffer(Shader.PropertyToID("_ForwardPlusTiles"), (ComputeBuffer)null);
            Shader.SetGlobalBuffer(Shader.PropertyToID("_DirectionalShadowCascades"), (ComputeBuffer)null);
            Shader.SetGlobalBuffer(Shader.PropertyToID("_DirectionalShadowMatrices"), (ComputeBuffer)null);
            Shader.SetGlobalBuffer(Shader.PropertyToID("_OtherShadowData"), (ComputeBuffer)null);
        }

        protected override void Render(ScriptableRenderContext context, List<Camera> cameras)
        {
            EnsureUIOverlay();
            BeginContextRendering(context, cameras);
            try
            {
                for (int i = 0; i < cameras.Count; i++)
                {
                    Camera camera = cameras[i];
                    // 铺装和退登只留主相机清屏 + 加载 UI，镜像相机会把整岛再画一遍。
                    if (!EnvItemStreamerLite.PrefillSuppressRender && !IslandGpuReclaim.CoverOnly)
                    {
                        OilPlanarReflection.RenderBefore(
                            context, camera, _renderGraph, _cameraRenderer, _settings);
                    }
                    BeginCameraRendering(context, camera);
                    try
                    {
                        _cameraRenderer.Render(_renderGraph, context, camera, _settings);
                    }
                    finally
                    {
                        EndCameraRendering(context, camera);
                    }
                }
            }
            catch (Exception e)
            {
                if (_renderGraph.ResetGraphAndLogException(e))
                {
                    throw;
                }
            }
            finally
            {
                EndContextRendering(context, cameras);
            }

            // CoverOnly / Prefill 只清屏，不录 RenderGraph。再 EndFrame 会把上一帧
            // 池里的整屏和阴影纹理继续挂在 GfxDevice 上，退登后 kgsl 降不下来。
            if (!EnvItemStreamerLite.PrefillSuppressRender && !IslandGpuReclaim.CoverOnly)
                _renderGraph.EndFrame();
        }
    }
}
