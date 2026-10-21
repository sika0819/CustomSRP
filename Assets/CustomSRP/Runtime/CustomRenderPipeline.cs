using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace CustomSRP
{
    public partial class CustomRenderPipeline : RenderPipeline
    {
        readonly RenderGraph _renderGraph;
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

        partial void InitializeForEditor();

        partial void DisposeForEditor();

        protected override void Render(ScriptableRenderContext context, List<Camera> cameras)
        {
            EnsureUIOverlay();
            BeginContextRendering(context, cameras);
            try
            {
                for (int i = 0; i < cameras.Count; i++)
                {
                    Camera camera = cameras[i];
                    OilPlanarReflection.RenderBefore(
                        context, camera, _renderGraph, _cameraRenderer, _settings);
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

            _renderGraph.EndFrame();
        }
    }
}
