using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RendererUtils;

namespace CustomSRP
{
    public class GeometryPass
    {
        static readonly ProfilingSampler SamplerOpaque =
            new ProfilingSampler("Opaque Geometry");
        static readonly ProfilingSampler SamplerTransparent =
            new ProfilingSampler("Transparent Geometry");

        static readonly ShaderTagId[] ShaderTagIds =
        {
            new ShaderTagId("SRPDefaultUnlit"),
            new ShaderTagId("CustomLit")
        };

        RendererListHandle _list;
        bool _invertCulling;

        void Render(UnsafeGraphContext context)
        {
            // Absolute, not a toggle. The mirror pass must flip winding or
            // Cull Back drops the island and trees. The main camera passes false.
            context.cmd.SetInvertCulling(_invertCulling);
            context.cmd.DrawRendererList(_list);
        }

        public static void Record(
            RenderGraph renderGraph,
            Camera camera,
            CullingResults cullingResults,
            uint renderingLayerMask,
            bool opaque,
            in CameraRendererTextures textures,
            in LightResources lightResources,
            bool invertCulling = false)
        {
            ProfilingSampler sampler = opaque ? SamplerOpaque : SamplerTransparent;
            using IUnsafeRenderGraphBuilder builder = renderGraph.AddUnsafePass(
                sampler.name, out GeometryPass pass, sampler);
            pass._invertCulling = invertCulling;

            pass._list = renderGraph.CreateRendererList(
                new RendererListDesc(ShaderTagIds, cullingResults, camera)
                {
                    sortingCriteria = opaque
                        ? SortingCriteria.CommonOpaque
                        : SortingCriteria.CommonTransparent,
                    rendererConfiguration =
                        PerObjectData.ReflectionProbes |
                        PerObjectData.Lightmaps |
                        PerObjectData.ShadowMask |
                        PerObjectData.LightProbe |
                        PerObjectData.OcclusionProbe,
                    renderQueueRange = opaque
                        ? RenderQueueRange.opaque
                        : RenderQueueRange.transparent,
                    layerMask = camera.cullingMask,
                    renderingLayerMask = renderingLayerMask
                });
            builder.UseRendererList(pass._list);
            builder.UseTexture(textures.colorAttachment, AccessFlags.ReadWrite);
            builder.UseTexture(textures.depthAttachment, AccessFlags.ReadWrite);
            lightResources.Use(builder);
            if (!opaque)
            {
                if (textures.colorCopy.IsValid())
                {
                    builder.UseTexture(textures.colorCopy);
                }

                if (textures.depthCopy.IsValid())
                {
                    builder.UseTexture(textures.depthCopy);
                }
            }

            builder.AllowPassCulling(false);
            builder.SetRenderFunc<GeometryPass>(
                static (pass, context) => pass.Render(context));
        }
    }
}
