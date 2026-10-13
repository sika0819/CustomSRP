using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace CustomSRP
{
    public readonly struct CameraRendererCopier
    {
        static readonly int SourceTextureId = Shader.PropertyToID("_SourceTexture");
        static readonly int SourceTextureTexelSizeId = Shader.PropertyToID("_SourceTexture_TexelSize");
        static readonly int CopyBicubicId = Shader.PropertyToID("_CopyBicubic");
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

        /// <summary>
        /// Intermediate RT copy. destinationSize must match the target attachment
        /// (bufferSize under render scale) — never camera.pixelRect, or depth/color
        /// copies are wrong and shore foam / soft particles die.
        /// </summary>
        public void Copy(
            UnsafeCommandBuffer buffer,
            TextureHandle from,
            TextureHandle to,
            bool isDepth,
            Vector2Int destinationSize)
        {
            if (_material == null)
            {
                return;
            }

            buffer.SetGlobalFloat(SrcBlendId, 1f);
            buffer.SetGlobalFloat(DstBlendId, 0f);
            buffer.SetGlobalFloat(CopyBicubicId, 0f);
            buffer.SetGlobalTexture(SourceTextureId, from);
            buffer.SetRenderTarget(
                to, RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store);
            int w = Mathf.Max(1, destinationSize.x);
            int h = Mathf.Max(1, destinationSize.y);
            buffer.SetViewport(new Rect(0f, 0f, w, h));
            buffer.DrawProcedural(
                Matrix4x4.identity, _material, isDepth ? 1 : 0,
                MeshTopology.Triangles, 3);
        }

        public void CopyToCameraTarget(
            UnsafeCommandBuffer buffer,
            TextureHandle from,
            TextureHandle cameraTarget,
            bool useBicubic = false,
            Vector2Int sourceSize = default)
        {
            if (_material == null)
            {
                return;
            }

            buffer.SetGlobalFloat(SrcBlendId, (float)_finalBlendMode.source);
            buffer.SetGlobalFloat(DstBlendId, (float)_finalBlendMode.destination);
            buffer.SetGlobalFloat(CopyBicubicId, useBicubic ? 1f : 0f);
            buffer.SetGlobalTexture(SourceTextureId, from);
            if (useBicubic && sourceSize.x > 0 && sourceSize.y > 0)
            {
                buffer.SetGlobalVector(
                    SourceTextureTexelSizeId,
                    new Vector4(
                        1f / sourceSize.x,
                        1f / sourceSize.y,
                        sourceSize.x,
                        sourceSize.y));
            }

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
            buffer.SetGlobalFloat(CopyBicubicId, 0f);
        }
    }
}
