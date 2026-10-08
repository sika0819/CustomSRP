using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace CustomSRP
{
    public class FinalPass
    {
        static readonly ProfilingSampler Sampler = new ProfilingSampler("Final");

        CameraRendererCopier _copier;
        TextureHandle _colorAttachment;
        TextureHandle _cameraTarget;

        void Render(UnsafeGraphContext context)
        {
            _copier.CopyToCameraTarget(
                context.cmd, _colorAttachment, _cameraTarget);
        }

        public static void Record(
            RenderGraph renderGraph,
            CameraRendererCopier copier,
            in CameraRendererTextures textures)
        {
            using IUnsafeRenderGraphBuilder builder = renderGraph.AddUnsafePass(
                Sampler.name, out FinalPass pass, Sampler);
            pass._copier = copier;
            pass._colorAttachment = textures.colorAttachment;
            builder.UseTexture(textures.colorAttachment);
            pass._cameraTarget = textures.cameraTarget;
            builder.UseTexture(pass._cameraTarget, AccessFlags.ReadWrite);
            builder.SetRenderFunc<FinalPass>(
                static (pass, context) => pass.Render(context));
        }
    }
}
