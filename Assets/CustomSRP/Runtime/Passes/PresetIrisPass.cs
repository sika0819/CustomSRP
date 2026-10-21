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
        static readonly int AspectId = Shader.PropertyToID("_PresetIrisAspect");

        static Material s_material;
        static Shader s_shader;
        static bool s_missingShaderLogged;

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
                {
                    if (!s_missingShaderLogged)
                    {
                        s_missingShaderLogged = true;
                        Debug.LogError(
                            "[PresetIris] Shader \"Hidden/CustomSRP/Preset Iris\" not found. " +
                            "It is stripped from player builds unless it is listed in " +
                            "Project Settings > Graphics > Always Included Shaders.");
                    }
                    return null;
                }

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
            float aspect = 1f;
            if (_camera != null)
            {
                buffer.SetViewport(_camera.pixelRect);
                // Match iris circle to camera pixels (not UV square) — landscape phones.
                aspect = _camera.pixelWidth / (float)Mathf.Max(1, _camera.pixelHeight);
            }
            buffer.SetGlobalFloat(AspectId, aspect);
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
