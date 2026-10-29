using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace CustomSRP
{
    public class CloudPass
    {
        static readonly ProfilingSampler Sampler = new ProfilingSampler("Oil Cloud");
        static readonly int AtmosphereFogAmountId = Shader.PropertyToID("_OilAtmosphereFogAmount");

        Material _material;
        OilCloudLayer.Frame _frame;
        Camera _camera;
        TextureHandle _colorAttachment;
        TextureHandle _depthAttachment;
        Vector2Int _bufferSize;

        void Render(UnsafeGraphContext context)
        {
            UnsafeCommandBuffer cmd = context.cmd;
            if (OilCloudLayer.Current == null || _frame.mesh == null)
            {
                return;
            }

            OilCloudLayer.ApplyFrame(cmd, _frame);
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
            // Fog dye amount after camera props — clouds share TOD fog with the skybox.
            cmd.SetGlobalFloat(AtmosphereFogAmountId, 1f);
            // Low triangles first, high ring after. Overlap is painter's order:
            // both layers sit at sky depth with ZWrite off.
            cmd.DrawMesh(_frame.mesh, _frame.low, _material, 0, 0);
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
            OilCloudLayer layer = OilCloudLayer.Current;
            if (layer == null)
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

            // Bake the ring mesh here, while the graph is recorded, not inside execute.
            OilCloudLayer.Frame frame = layer.Evaluate(camera);
            if (frame.mesh == null)
            {
                return;
            }

            using IUnsafeRenderGraphBuilder builder = renderGraph.AddUnsafePass(
                Sampler.name, out CloudPass pass, Sampler);
            pass._material = material;
            pass._frame = frame;
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
