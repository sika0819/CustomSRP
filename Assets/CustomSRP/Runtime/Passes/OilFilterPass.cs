using Unity.Profiling;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using static CustomSRP.PostFXStack;

namespace CustomSRP
{
    /// <summary>
    /// Low-cost edge wet-oil after Bloom, before color grading (fwidth + 4 taps).
    /// </summary>
    public class OilFilterPass
    {
        static readonly ProfilingSampler Sampler = new ProfilingSampler("Oil Filter");
        static readonly ProfilerMarker MarkerRender = new ProfilerMarker("OilFilter.Render");

        static readonly int
            TexelSizeId = Shader.PropertyToID("_OilFilterTexelSize"),
            HasDepthId = Shader.PropertyToID("_OilFilterHasDepth"),
            DepthCopyId = Shader.PropertyToID("_CameraDepthTexture");

        TextureHandle _source;
        TextureHandle _result;
        TextureHandle _depth;
        PostFXStack _stack;
        bool _hasDepth;
        Vector2Int _bufferSize;

        void Render(UnsafeGraphContext context)
        {
            using (MarkerRender.Auto())
            {
                UnsafeCommandBuffer buffer = context.cmd;
                int w = Mathf.Max(1, _bufferSize.x);
                int h = Mathf.Max(1, _bufferSize.y);
                buffer.SetGlobalVector(TexelSizeId, new Vector4(1f / w, 1f / h, w, h));
                buffer.SetGlobalFloat(HasDepthId, _hasDepth ? 1f : 0f);
                if (_hasDepth)
                    buffer.SetGlobalTexture(DepthCopyId, _depth);

                _stack.Draw(buffer, _source, _result, Pass.OilFilter);
            }
        }

        public static TextureHandle Record(
            RenderGraph renderGraph,
            PostFXStack stack,
            TextureHandle colorSource,
            in CameraRendererTextures textures)
        {
            if (!OilFilter.IsEnabled(stack.Settings))
                return colorSource;

            using IUnsafeRenderGraphBuilder builder = renderGraph.AddUnsafePass(
                Sampler.name, out OilFilterPass pass, Sampler);
            pass._stack = stack;
            pass._source = colorSource;
            pass._bufferSize = stack.BufferSize;
            builder.UseTexture(colorSource);

            bool hasDepth = textures.depthCopy.IsValid();
            TextureHandle depth = hasDepth ? textures.depthCopy : textures.depthAttachment;
            hasDepth = depth.IsValid();
            pass._hasDepth = hasDepth;
            if (hasDepth)
            {
                pass._depth = depth;
                builder.UseTexture(depth);
            }

            var desc = new TextureDesc(stack.BufferSize.x, stack.BufferSize.y)
            {
                colorFormat = SystemInfo.GetGraphicsFormat(
                    stack.BufferSettings.allowHDR ? DefaultFormat.HDR : DefaultFormat.LDR),
                name = "Oil Filter",
                filterMode = FilterMode.Bilinear
            };
            pass._result = renderGraph.CreateTexture(desc);
            builder.UseTexture(pass._result, AccessFlags.WriteAll);
            builder.AllowPassCulling(false);
            builder.SetRenderFunc<OilFilterPass>(
                static (pass, context) => pass.Render(context));
            return pass._result;
        }
    }
}
