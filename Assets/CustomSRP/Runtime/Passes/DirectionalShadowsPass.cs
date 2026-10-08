using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace CustomSRP
{
    public class DirectionalShadowsPass
    {
        static readonly ProfilingSampler Sampler =
            new ProfilingSampler("Directional Shadows");

        DirectionalShadows _shadows;

        void Render(UnsafeGraphContext context) =>
            _shadows.RenderDirectionalShadows(context.cmd);

        public static DirectionalShadows.Handles Record(
            RenderGraph renderGraph,
            CullingResults cullingResults,
            ShadowCastersCullingInfos cullingInfos,
            Shadows shadows)
        {
            using IUnsafeRenderGraphBuilder builder = renderGraph.AddUnsafePass(
                Sampler.name, out DirectionalShadowsPass pass, Sampler);
            pass._shadows = shadows.directionalShadows;
            builder.SetRenderFunc<DirectionalShadowsPass>(
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
