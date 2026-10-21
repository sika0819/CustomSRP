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

        public CustomRenderPipeline(CustomRenderPipelineSettings settings)
        {
            _settings = settings;
            GraphicsSettings.useScriptableRenderPipelineBatching = settings.useSRPBatcher;
            GraphicsSettings.lightsUseLinearIntensity = true;
            _cameraRenderer = new CameraRenderer(
                settings.cameraRendererShader, settings.cameraDebuggerShader);
            _renderGraph = new RenderGraph("Custom SRP Render Graph");
            InitializeForEditor();
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
