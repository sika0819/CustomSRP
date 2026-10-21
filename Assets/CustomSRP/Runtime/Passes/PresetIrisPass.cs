using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace CustomSRP
{
    /// <summary>
    /// Alpha-blends the circular iris onto the camera target after Post FX / Final.
    /// Driven by globals from Game.PresetIrisOverlay; idle early-outs.
    /// </summary>
    public class PresetIrisPass
    {
        static readonly ProfilingSampler Sampler = new ProfilingSampler("Preset Iris");
        static readonly int ActiveId = Shader.PropertyToID("_PresetIrisActive");

        static Material s_material;
        static Shader s_shader;

        TextureHandle _cameraTarget;
        Camera _camera;

        static Material Material
        {
            get
            {
                if (s_material != null)
                    return s_material;
                if (s_shader == null)
                    s_shader = Shader.Find("Hidden/CustomSRP/Preset Iris");
                if (s_shader == null)
                    return null;
                s_material = CoreUtils.CreateEngineMaterial(s_shader);
                return s_material;
            }
        }

        void Render(UnsafeGraphContext context)
        {
            Material mat = Material;
            if (mat == null)
                return;

            UnsafeCommandBuffer buffer = context.cmd;
            buffer.SetRenderTarget(
                _cameraTarget,
                RenderBufferLoadAction.Load,
                RenderBufferStoreAction.Store);
            if (_camera != null)
                buffer.SetViewport(_camera.pixelRect);
            buffer.DrawProcedural(
                Matrix4x4.identity, mat, 0,
                MeshTopology.Triangles, 3);
        }

        public static void Record(
            RenderGraph renderGraph,
            in CameraRendererTextures textures,
            Camera camera)
        {
            if (Shader.GetGlobalFloat(ActiveId) < 0.5f)
                return;

            if (Material == null)
                return;

            using IUnsafeRenderGraphBuilder builder = renderGraph.AddUnsafePass(
                Sampler.name, out PresetIrisPass pass, Sampler);
            pass._cameraTarget = textures.cameraTarget;
            pass._camera = camera;
            builder.UseTexture(textures.cameraTarget, AccessFlags.ReadWrite);
            builder.AllowPassCulling(false);
            builder.SetRenderFunc<PresetIrisPass>(
                static (p, context) => p.Render(context));
        }
    }
}
