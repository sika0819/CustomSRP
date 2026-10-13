using UnityEngine;
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
        bool _useBicubic;
        Vector2Int _bufferSize;

        void Render(UnsafeGraphContext context)
        {
            _copier.CopyToCameraTarget(
                context.cmd, _colorAttachment, _cameraTarget, _useBicubic, _bufferSize);
        }

        public static void Record(
            RenderGraph renderGraph,
            CameraRendererCopier copier,
            in CameraRendererTextures textures,
            bool useBicubic = false,
            Vector2Int bufferSize = default)
        {
            using IUnsafeRenderGraphBuilder builder = renderGraph.AddUnsafePass(
                Sampler.name, out FinalPass pass, Sampler);
            pass._copier = copier;
            pass._colorAttachment = textures.colorAttachment;
            pass._useBicubic = useBicubic;
            pass._bufferSize = bufferSize;
            builder.UseTexture(textures.colorAttachment);
            pass._cameraTarget = textures.cameraTarget;
            builder.UseTexture(pass._cameraTarget, AccessFlags.ReadWrite);
            builder.SetRenderFunc<FinalPass>(
                static (pass, context) => pass.Render(context));
        }

        public static bool ShouldUseBicubic(
            CameraBufferSettings bufferSettings,
            Vector2Int bufferSize,
            Camera camera)
        {
            if (bufferSize.x >= camera.pixelWidth)
            {
                return bufferSettings.bicubicRescaling ==
                    CameraBufferSettings.BicubicRescalingMode.UpAndDown;
            }

            return bufferSettings.bicubicRescaling ==
                   CameraBufferSettings.BicubicRescalingMode.UpOnly ||
                   bufferSettings.bicubicRescaling ==
                   CameraBufferSettings.BicubicRescalingMode.UpAndDown;
        }
    }
}
