using System.Diagnostics;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace CustomSRP
{
    public class GizmosPass
    {
#if UNITY_EDITOR
        static readonly ProfilingSampler Sampler = new ProfilingSampler("Gizmos");

        CameraRendererCopier _copier;
        TextureHandle _depthAttachment;
        TextureHandle _cameraTarget;
        RendererListHandle _preList;
        RendererListHandle _postList;

        void Render(UnsafeGraphContext context)
        {
            UnsafeCommandBuffer buffer = context.cmd;
            _copier.Copy(buffer, _depthAttachment, _cameraTarget, true);
            buffer.DrawRendererList(_preList);
            buffer.DrawRendererList(_postList);
        }
#endif

        [Conditional("UNITY_EDITOR")]
        public static void Record(
            RenderGraph renderGraph,
            CameraRendererCopier copier,
            in CameraRendererTextures textures)
        {
#if UNITY_EDITOR
            if (!UnityEditor.Handles.ShouldRenderGizmos())
            {
                return;
            }

            using IUnsafeRenderGraphBuilder builder = renderGraph.AddUnsafePass(
                Sampler.name, out GizmosPass pass, Sampler);
            pass._copier = copier;
            pass._depthAttachment = textures.depthAttachment;
            builder.UseTexture(pass._depthAttachment);
            pass._preList = renderGraph.CreateGizmoRendererList(
                copier.Camera, GizmoSubset.PreImageEffects);
            builder.UseRendererList(pass._preList);
            pass._postList = renderGraph.CreateGizmoRendererList(
                copier.Camera, GizmoSubset.PostImageEffects);
            builder.UseRendererList(pass._postList);
            pass._cameraTarget = textures.cameraTarget;
            builder.UseTexture(pass._cameraTarget, AccessFlags.WriteAll);
            builder.SetRenderFunc<GizmosPass>(
                static (pass, context) => pass.Render(context));
#endif
        }
    }
}
