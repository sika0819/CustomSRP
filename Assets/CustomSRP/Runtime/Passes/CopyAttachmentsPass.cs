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
        Vector2Int _bufferSize;
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
                _copier.Copy(buffer, _colorAttachment, _colorCopy, false, _bufferSize);
                buffer.SetGlobalTexture(ColorCopyId, _colorCopy);
            }

            if (_copyDepth)
            {
                _copier.Copy(buffer, _depthAttachment, _depthCopy, true, _bufferSize);
                buffer.SetGlobalTexture(DepthCopyId, _depthCopy);
            }

            buffer.SetRenderTarget(
                _colorAttachment,
                RenderBufferLoadAction.Load, RenderBufferStoreAction.Store,
                _depthAttachment,
                RenderBufferLoadAction.Load, RenderBufferStoreAction.Store);
            // 恢复中间 RT 视口，避免后续 Transparent 仍用 pixelRect。
            buffer.SetViewport(new Rect(0f, 0f, _bufferSize.x, _bufferSize.y));
        }

        public static void Record(
            RenderGraph renderGraph,
            bool copyColor,
            bool copyDepth,
            CameraRendererCopier copier,
            in CameraRendererTextures textures,
            Vector2Int bufferSize)
        {
            if (!copyColor && !copyDepth)
            {
                return;
            }

            using IUnsafeRenderGraphBuilder builder = renderGraph.AddUnsafePass(
                Sampler.name, out CopyAttachmentsPass pass, Sampler);
            pass._copyColor = copyColor;
            pass._copyDepth = copyDepth;
            pass._bufferSize = bufferSize;
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
