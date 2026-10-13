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
            try
            {
                for (int i = 0; i < cameras.Count; i++)
                {
                    _cameraRenderer.Render(_renderGraph, context, cameras[i], _settings);
                }
            }
            catch (Exception e)
            {
                if (_renderGraph.ResetGraphAndLogException(e))
                {
                    throw;
                }
            }

            _renderGraph.EndFrame();
        }
    }
}
