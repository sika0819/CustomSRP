using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace CustomSRP
{
    public readonly struct CameraRendererCopier
    {
        static readonly int SourceTextureId = Shader.PropertyToID("_SourceTexture");
        static readonly int SrcBlendId = Shader.PropertyToID("_CameraSrcBlend");
        static readonly int DstBlendId = Shader.PropertyToID("_CameraDstBlend");
        static readonly Rect FullViewRect = new Rect(0f, 0f, 1f, 1f);

        readonly Material _material;
        readonly Camera _camera;
        readonly CameraSettings.FinalBlendMode _finalBlendMode;

        public Camera Camera => _camera;

        public CameraRendererCopier(
            Material material,
            Camera camera,
            CameraSettings.FinalBlendMode finalBlendMode)
        {
            _material = material;
            _camera = camera;
            _finalBlendMode = finalBlendMode;
        }

        public void Copy(
            UnsafeCommandBuffer buffer,
            TextureHandle from,
            TextureHandle to,
            bool isDepth)
        {
            if (_material == null)
            {
                return;
            }

            buffer.SetGlobalFloat(SrcBlendId, 1f);
            buffer.SetGlobalFloat(DstBlendId, 0f);
            buffer.SetGlobalTexture(SourceTextureId, from);
            buffer.SetRenderTarget(
                to, RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store);
            buffer.SetViewport(_camera.pixelRect);
            buffer.DrawProcedural(
                Matrix4x4.identity, _material, isDepth ? 1 : 0,
                MeshTopology.Triangles, 3);
        }

        public void CopyToCameraTarget(
            UnsafeCommandBuffer buffer,
            TextureHandle from,
            TextureHandle cameraTarget)
        {
            if (_material == null)
            {
                return;
            }

            buffer.SetGlobalFloat(SrcBlendId, (float)_finalBlendMode.source);
            buffer.SetGlobalFloat(DstBlendId, (float)_finalBlendMode.destination);
            buffer.SetGlobalTexture(SourceTextureId, from);
            buffer.SetRenderTarget(
                cameraTarget,
                _finalBlendMode.destination == BlendMode.Zero && _camera.rect == FullViewRect
                    ? RenderBufferLoadAction.DontCare
                    : RenderBufferLoadAction.Load,
                RenderBufferStoreAction.Store);
            buffer.SetViewport(_camera.pixelRect);
            buffer.DrawProcedural(
                Matrix4x4.identity, _material, 0, MeshTopology.Triangles, 3);
            buffer.SetGlobalFloat(SrcBlendId, 1f);
            buffer.SetGlobalFloat(DstBlendId, 0f);
        }
    }
}
