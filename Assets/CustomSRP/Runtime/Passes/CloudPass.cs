using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace CustomSRP
{
    public class CloudPass
    {
        static readonly ProfilingSampler Sampler = new ProfilingSampler("Oil Cloud");

        Material _material;
        Mesh _mesh;
        Camera _camera;
        TextureHandle _colorAttachment;
        TextureHandle _depthAttachment;
        Vector2Int _bufferSize;

        void Render(UnsafeGraphContext context)
        {
            UnsafeCommandBuffer cmd = context.cmd;
            OilCloudLayer layer = OilCloudLayer.Current;
            if (layer == null || _mesh == null)
            {
                return;
            }

            OilCloudLayer.Frame frame = layer.Evaluate(_camera);
            OilCloudLayer.ApplyFrame(cmd, frame);
            // Restore the camera matrices, then bind the intermediate targets again.
            // SetupCameraProperties also switches to the camera target.
            cmd.SetupCameraProperties(_camera);
            cmd.SetRenderTarget(
                _colorAttachment,
                RenderBufferLoadAction.Load,
                RenderBufferStoreAction.Store,
                _depthAttachment,
                RenderBufferLoadAction.Load,
                RenderBufferStoreAction.Store);
            cmd.SetViewport(new Rect(0f, 0f, _bufferSize.x, _bufferSize.y));
            cmd.DrawMesh(_mesh, frame.low, _material, 0, 0);
            if (frame.drawHigh)
            {
                OilCloudLayer.SetRingBias(cmd, frame.highSdfBias);
                cmd.DrawMesh(_mesh, frame.high, _material, 0, 0);
                OilCloudLayer.SetRingBias(cmd, 0f);
            }
        }

        static Material _fallback;

        static Material ResolveMaterial()
        {
            Material material = OilCloudLayer.ActiveMaterial;
            if (material != null && material.shader != null)
            {
                return material;
            }

            if (_fallback != null)
            {
                return _fallback;
            }

            Shader shader = Shader.Find(OilCloudLayer.ShaderName);
            if (shader == null)
            {
                return null;
            }

            _fallback = new Material(shader)
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            return _fallback;
        }

        public static void Record(
            RenderGraph renderGraph,
            Camera camera,
            Vector2Int bufferSize,
            in CameraRendererTextures textures)
        {
            if (OilCloudLayer.Current == null)
            {
                return;
            }

            Material material = ResolveMaterial();
            if (material == null)
            {
                return;
            }

            if (camera.clearFlags != CameraClearFlags.Skybox)
            {
                return;
            }

            Mesh mesh = OilCloudLayer.SharedRing;
            if (mesh == null)
            {
                return;
            }

            using IUnsafeRenderGraphBuilder builder = renderGraph.AddUnsafePass(
                Sampler.name, out CloudPass pass, Sampler);
            pass._material = material;
            pass._mesh = mesh;
            pass._camera = camera;
            pass._bufferSize = bufferSize;
            pass._colorAttachment = textures.colorAttachment;
            pass._depthAttachment = textures.depthAttachment;

            builder.UseTexture(textures.colorAttachment, AccessFlags.ReadWrite);
            builder.UseTexture(textures.depthAttachment, AccessFlags.ReadWrite);
            builder.AllowGlobalStateModification(true);
            builder.AllowPassCulling(false);
            builder.SetRenderFunc<CloudPass>(
                static (pass, context) => pass.Render(context));
        }
    }
}
