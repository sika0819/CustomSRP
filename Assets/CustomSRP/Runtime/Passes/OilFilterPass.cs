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

        /// <summary>
        /// Wet-oil texel radius is authored for this buffer short side.
        /// Larger buffers (闺蜜机 4K @ scale 1) scale radius down so the smear stays sharp.
        /// </summary>
        const float RadiusRefShortSide = 1080f;

        static readonly int
            TexelSizeId = Shader.PropertyToID("_OilFilterTexelSize"),
            HasDepthId = Shader.PropertyToID("_OilFilterHasDepth"),
            RadiusScaleId = Shader.PropertyToID("_OilFilterRadiusScale"),
            DepthCopyId = Shader.PropertyToID("_CameraDepthTexture");

        TextureHandle _source;
        TextureHandle _result;
        TextureHandle _depth;
        PostFXStack _stack;
        bool _hasDepth;
        Vector2Int _bufferSize;

        /// <summary>
        /// Map buffer short side → radius scale. Large TVs stay capped so the smear
        /// does not go soft. Phone half-res (short side under 720) gets a higher cap
        /// so the same 4 taps still read as a dab after bilinear upscale.
        /// </summary>
        public static float ComputeRadiusScale(int bufferWidth, int bufferHeight)
        {
            int shortSide = Mathf.Max(1, Mathf.Min(bufferWidth, bufferHeight));
            float scale = RadiusRefShortSide / shortSide;
            float maxScale = shortSide < 720 ? 1.65f : 1.15f;
            return Mathf.Clamp(scale, 0.45f, maxScale);
        }

        void Render(UnsafeGraphContext context)
        {
            using (MarkerRender.Auto())
            {
                UnsafeCommandBuffer buffer = context.cmd;
                int w = Mathf.Max(1, _bufferSize.x);
                int h = Mathf.Max(1, _bufferSize.y);
                buffer.SetGlobalVector(TexelSizeId, new Vector4(1f / w, 1f / h, w, h));
                buffer.SetGlobalFloat(HasDepthId, _hasDepth ? 1f : 0f);
                buffer.SetGlobalFloat(RadiusScaleId, ComputeRadiusScale(w, h));
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
