using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace CustomSRP
{
    public class CopyAttachmentsPass
    {
        static readonly ProfilingSampler Sampler = new ProfilingSampler("Copy Attachments");
        static readonly int ColorCopyId = Shader.PropertyToID("_CameraColorTexture");
        static readonly int DepthCopyId = Shader.PropertyToID("_CameraDepthTexture");

        bool _copyColor;
        bool _copyDepth;
        CameraRendererCopier _copier;
        TextureHandle _colorAttachment;
        TextureHandle _depthAttachment;
        TextureHandle _colorCopy;
        TextureHandle _depthCopy;

        void Render(UnsafeGraphContext context)
        {
            UnsafeCommandBuffer buffer = context.cmd;
            if (_copyColor)
            {
                _copier.Copy(buffer, _colorAttachment, _colorCopy, false);
                buffer.SetGlobalTexture(ColorCopyId, _colorCopy);
            }

            if (_copyDepth)
            {
                _copier.Copy(buffer, _depthAttachment, _depthCopy, true);
                buffer.SetGlobalTexture(DepthCopyId, _depthCopy);
            }

            buffer.SetRenderTarget(
                _colorAttachment,
                RenderBufferLoadAction.Load, RenderBufferStoreAction.Store,
                _depthAttachment,
                RenderBufferLoadAction.Load, RenderBufferStoreAction.Store);
        }

        public static void Record(
            RenderGraph renderGraph,
            bool copyColor,
            bool copyDepth,
            CameraRendererCopier copier,
            in CameraRendererTextures textures)
        {
            if (!copyColor && !copyDepth)
            {
                return;
            }

            using IUnsafeRenderGraphBuilder builder = renderGraph.AddUnsafePass(
                Sampler.name, out CopyAttachmentsPass pass, Sampler);
            pass._copyColor = copyColor;
            pass._copyDepth = copyDepth;
            pass._copier = copier;
            pass._colorAttachment = textures.colorAttachment;
            pass._depthAttachment = textures.depthAttachment;
            builder.UseTexture(textures.colorAttachment);
            builder.UseTexture(textures.depthAttachment);
            if (copyColor)
            {
                pass._colorCopy = textures.colorCopy;
                builder.UseTexture(textures.colorCopy, AccessFlags.WriteAll);
            }

            if (copyDepth)
            {
                pass._depthCopy = textures.depthCopy;
                builder.UseTexture(textures.depthCopy, AccessFlags.WriteAll);
            }

            builder.AllowPassCulling(false);
            builder.SetRenderFunc<CopyAttachmentsPass>(
                static (pass, context) => pass.Render(context));
        }
    }
}
