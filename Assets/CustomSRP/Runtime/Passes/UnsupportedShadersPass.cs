using System.Diagnostics;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RendererUtils;

namespace CustomSRP
{
    public class UnsupportedShadersPass
    {
#if UNITY_EDITOR
        static readonly ProfilingSampler Sampler = new ProfilingSampler("Unsupported Shaders");

        static readonly ShaderTagId[] ShaderTagIds =
        {
            new ShaderTagId("Always"),
            new ShaderTagId("ForwardBase"),
            new ShaderTagId("PrepassBase"),
            new ShaderTagId("Vertex"),
            new ShaderTagId("VertexLMRGBM"),
            new ShaderTagId("VertexLM")
        };

        static Material _errorMaterial;

        RendererListHandle _list;

        void Render(UnsafeGraphContext context)
        {
            context.cmd.DrawRendererList(_list);
        }
#endif

        [Conditional("UNITY_EDITOR")]
        public static void Record(
            RenderGraph renderGraph,
            Camera camera,
            CullingResults cullingResults,
            in CameraRendererTextures textures)
        {
#if UNITY_EDITOR
            using IUnsafeRenderGraphBuilder builder = renderGraph.AddUnsafePass(
                Sampler.name, out UnsupportedShadersPass pass, Sampler);

            if (_errorMaterial == null)
            {
                _errorMaterial = new Material(Shader.Find("Hidden/InternalErrorShader"));
            }

            pass._list = renderGraph.CreateRendererList(
                new RendererListDesc(ShaderTagIds, cullingResults, camera)
                {
                    overrideMaterial = _errorMaterial,
                    renderQueueRange = RenderQueueRange.all,
                    layerMask = -1
                });
            builder.UseRendererList(pass._list);
            builder.UseTexture(textures.colorAttachment, AccessFlags.ReadWrite);
            builder.UseTexture(textures.depthAttachment, AccessFlags.ReadWrite);
            builder.AllowPassCulling(false);
            builder.SetRenderFunc<UnsupportedShadersPass>(
                static (pass, context) => pass.Render(context));
#endif
        }
    }
}
