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
        static readonly int FogEnabledId = Shader.PropertyToID("_CustomFogEnabled");
        static readonly int FogModeId = Shader.PropertyToID("_CustomFogMode");
        static readonly int FogColorId = Shader.PropertyToID("_CustomFogColor");
        static readonly int FogParamsId = Shader.PropertyToID("_CustomFogParams");
        static readonly int FogSkyPowerId = Shader.PropertyToID("_CustomFogSkyPower");
        static readonly int AtmosphereFogColorId = Shader.PropertyToID("_OilAtmosphereFogColor");
        static readonly int AtmosphereFogAmountId = Shader.PropertyToID("_OilAtmosphereFogAmount");

        TextureHandle _colorAttachment;
        TextureHandle _depthAttachment;
        Vector2Int _attachmentSize;
        Camera _camera;
        CameraClearFlags _clearFlags;
        FogSettings.ShaderData _fog;

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
            // SetupCameraProperties 会把视口设成 pixelRect；Render Scale 下中间 RT
            // 更小，必须改回 attachment 尺寸，否则 Opaque Depth 与 _CameraBufferSize UV 错位，
            // Copy Depth / 河岸白边会整条失效。
            cmd.SetViewport(new Rect(
                0f, 0f, _attachmentSize.x, _attachmentSize.y));
            cmd.SetGlobalVector(AttachmentSizeId, new Vector4(
                1f / _attachmentSize.x, 1f / _attachmentSize.y,
                _attachmentSize.x, _attachmentSize.y));
            cmd.SetGlobalFloat(FogEnabledId, _fog.enabled);
            cmd.SetGlobalFloat(FogModeId, _fog.mode);
            cmd.SetGlobalVector(FogColorId, _fog.color);
            cmd.SetGlobalVector(FogParamsId, _fog.parameters);
            cmd.SetGlobalFloat(FogSkyPowerId, _fog.skyPower);
            // Skybox / cloud dye — same TOD color, amount follows pipeline fog enable.
            cmd.SetGlobalVector(AtmosphereFogColorId, _fog.color);
            cmd.SetGlobalFloat(AtmosphereFogAmountId, _fog.enabled > 0.5f ? 1f : 0f);
        }

        public static CameraRendererTextures Record(
            RenderGraph renderGraph,
            bool copyColor,
            bool copyDepth,
            bool useHDR,
            Vector2Int attachmentSize,
            Camera camera,
            FogSettings.ShaderData fog)
        {
            using IUnsafeRenderGraphBuilder builder = renderGraph.AddUnsafePass(
                Sampler.name, out SetupPass pass, Sampler);

            pass._attachmentSize = attachmentSize;
            pass._camera = camera;
            pass._clearFlags = camera.clearFlags;
            pass._fog = fog;

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

            // Fog + buffer-size globals must stick for Skybox / Geometry.
            builder.AllowGlobalStateModification(true);
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
