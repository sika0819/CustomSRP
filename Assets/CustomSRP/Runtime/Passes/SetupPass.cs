using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace CustomSRP
{
    public class SetupPass
    {
        static readonly ProfilingSampler Sampler = new ProfilingSampler("Setup");
        static readonly int AttachmentSizeId = Shader.PropertyToID("_CameraBufferSize");

        TextureHandle _colorAttachment;
        TextureHandle _depthAttachment;
        Vector2Int _attachmentSize;
        Camera _camera;
        CameraClearFlags _clearFlags;

        void Render(UnsafeGraphContext context)
        {
            UnsafeCommandBuffer cmd = context.cmd;
            cmd.SetupCameraProperties(_camera);
            cmd.SetRenderTarget(
                _colorAttachment,
                RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store,
                _depthAttachment,
                RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store);

            cmd.ClearRenderTarget(
                _clearFlags <= CameraClearFlags.Depth,
                _clearFlags <= CameraClearFlags.Color,
                _clearFlags == CameraClearFlags.Color
                    ? _camera.backgroundColor.linear
                    : Color.clear);
            cmd.SetGlobalVector(AttachmentSizeId, new Vector4(
                1f / _attachmentSize.x, 1f / _attachmentSize.y,
                _attachmentSize.x, _attachmentSize.y));
        }

        public static CameraRendererTextures Record(
            RenderGraph renderGraph,
            bool copyColor,
            bool copyDepth,
            bool useHDR,
            Vector2Int attachmentSize,
            Camera camera)
        {
            using IUnsafeRenderGraphBuilder builder = renderGraph.AddUnsafePass(
                Sampler.name, out SetupPass pass, Sampler);

            pass._attachmentSize = attachmentSize;
            pass._camera = camera;
            pass._clearFlags = camera.clearFlags;

            if (pass._clearFlags > CameraClearFlags.Color)
            {
                pass._clearFlags = CameraClearFlags.Color;
            }

            TextureHandle colorCopy = default;
            TextureHandle depthCopy = default;

            var desc = new TextureDesc(attachmentSize.x, attachmentSize.y)
            {
                colorFormat = SystemInfo.GetGraphicsFormat(
                    useHDR ? DefaultFormat.HDR : DefaultFormat.LDR),
                name = "Color Attachment",
                filterMode = FilterMode.Bilinear
            };
            TextureHandle colorAttachment = pass._colorAttachment =
                renderGraph.CreateTexture(desc);
            builder.UseTexture(colorAttachment, AccessFlags.WriteAll);

            if (copyColor)
            {
                desc.name = "Color Copy";
                colorCopy = renderGraph.CreateTexture(desc);
            }

            desc.depthBufferBits = DepthBits.Depth32;
            desc.name = "Depth Attachment";
            desc.filterMode = FilterMode.Point;
            TextureHandle depthAttachment = pass._depthAttachment =
                renderGraph.CreateTexture(desc);
            builder.UseTexture(depthAttachment, AccessFlags.WriteAll);

            if (copyDepth)
            {
                desc.name = "Depth Copy";
                depthCopy = renderGraph.CreateTexture(desc);
            }

            builder.AllowPassCulling(false);
            builder.SetRenderFunc<SetupPass>(
                static (pass, context) => pass.Render(context));

            RenderTexture targetTexture = camera.targetTexture;
            var cameraTargetInfo = new RenderTargetInfo
            {
                width = camera.pixelWidth,
                height = camera.pixelHeight,
                volumeDepth = 1,
                msaaSamples = 1,
                format = targetTexture
                    ? targetTexture.graphicsFormat
                    : GraphicsFormat.R8G8B8A8_UNorm
            };
            TextureHandle cameraTarget = renderGraph.ImportBackbuffer(
                targetTexture
                    ? targetTexture
                    : BuiltinRenderTextureType.CameraTarget,
                cameraTargetInfo);

            return new CameraRendererTextures(
                colorAttachment, depthAttachment, colorCopy, depthCopy,
                cameraTarget);
        }
    }
}
