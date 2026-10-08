using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace CustomSRP
{
    public class OtherShadowsPass
    {
        static readonly ProfilingSampler Sampler =
            new ProfilingSampler("Other Shadows");

        OtherShadows _shadows;

        void Render(UnsafeGraphContext context) =>
            _shadows.RenderOtherShadows(context.cmd);

        public static OtherShadows.Handles Record(
            RenderGraph renderGraph,
            CullingResults cullingResults,
            ShadowCastersCullingInfos cullingInfos,
            Shadows shadows)
        {
            using IUnsafeRenderGraphBuilder builder = renderGraph.AddUnsafePass(
                Sampler.name, out OtherShadowsPass pass, Sampler);
            pass._shadows = shadows.otherShadows;
            builder.SetRenderFunc<OtherShadowsPass>(
                static (pass, context) => pass.Render(context));

            if (pass._shadows.HasLights)
            {
                pass._shadows.BuildRendererLists(
                    renderGraph, builder, cullingResults, cullingInfos);
            }

            return pass._shadows.GetHandles(renderGraph, builder);
        }
    }
}
