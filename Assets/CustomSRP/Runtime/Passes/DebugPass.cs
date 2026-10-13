using System.Diagnostics;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace CustomSRP
{
    public class DebugPass
    {
        static readonly ProfilingSampler Sampler = new ProfilingSampler("Debug");

        int _colorLUTResolution;

        [Conditional("DEVELOPMENT_BUILD"), Conditional("UNITY_EDITOR")]
        public static void Record(
            RenderGraph renderGraph,
            CustomRenderPipelineSettings settings,
            Camera camera,
            int colorLUTResolution,
            TextureHandle colorLUT,
            in LightResources lightResources)
        {
            if (!CameraDebugger.IsActive ||
                camera.cameraType > CameraType.SceneView)
            {
                return;
            }

            using IUnsafeRenderGraphBuilder builder = renderGraph.AddUnsafePass(
                Sampler.name, out DebugPass pass, Sampler);
            if (lightResources.lightHandles.tilesBuffer.IsValid())
            {
                builder.UseBuffer(lightResources.lightHandles.tilesBuffer);
            }

            if (colorLUT.IsValid())
            {
                pass._colorLUTResolution = colorLUTResolution;
                builder.UseTexture(colorLUT);
            }
            else
            {
                pass._colorLUTResolution = 0;
            }

            builder.AllowPassCulling(false);
            builder.SetRenderFunc<DebugPass>(
                static (pass, context) => CameraDebugger.Render(
                    context, pass._colorLUTResolution));
        }
    }
}
