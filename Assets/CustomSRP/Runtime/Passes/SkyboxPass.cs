using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace CustomSRP
{
    public class SkyboxPass
    {
        static readonly ProfilingSampler Sampler = new ProfilingSampler("Skybox");

        RendererListHandle _list;

        void Render(UnsafeGraphContext context)
        {
            context.cmd.DrawRendererList(_list);
        }

        public static void Record(
            RenderGraph renderGraph,
            Camera camera,
            in CameraRendererTextures textures)
        {
            if (camera.clearFlags != CameraClearFlags.Skybox)
            {
                return;
            }

            using IUnsafeRenderGraphBuilder builder = renderGraph.AddUnsafePass(
                Sampler.name, out SkyboxPass pass, Sampler);
            pass._list = renderGraph.CreateSkyboxRendererList(camera);
            builder.UseRendererList(pass._list);
            builder.UseTexture(textures.colorAttachment, AccessFlags.ReadWrite);
            builder.UseTexture(textures.depthAttachment, AccessFlags.Read);
            builder.SetRenderFunc<SkyboxPass>(
                static (pass, context) => pass.Render(context));
        }
    }
}
