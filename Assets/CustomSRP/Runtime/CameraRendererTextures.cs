using UnityEngine.Rendering.RenderGraphModule;

namespace CustomSRP
{
    /// <summary>
    /// Camera texture handles shared while recording a single camera's render graph.
    /// Ref struct so it cannot escape <see cref="CameraRenderer.Render"/>.
    /// </summary>
    public readonly ref struct CameraRendererTextures
    {
        public readonly TextureHandle colorAttachment;
        public readonly TextureHandle depthAttachment;
        public readonly TextureHandle colorCopy;
        public readonly TextureHandle depthCopy;
        public readonly TextureHandle cameraTarget;

        public CameraRendererTextures(
            TextureHandle colorAttachment,
            TextureHandle depthAttachment,
            TextureHandle colorCopy,
            TextureHandle depthCopy,
            TextureHandle cameraTarget)
        {
            this.colorAttachment = colorAttachment;
            this.depthAttachment = depthAttachment;
            this.colorCopy = colorCopy;
            this.depthCopy = depthCopy;
            this.cameraTarget = cameraTarget;
        }
    }
}
