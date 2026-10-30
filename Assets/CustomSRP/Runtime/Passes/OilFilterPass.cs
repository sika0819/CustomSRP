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
            StrengthId = Shader.PropertyToID("_OilFilterStrength"),
            DepthCopyId = Shader.PropertyToID("_CameraDepthTexture");

        TextureHandle _source;
        TextureHandle _result;
        TextureHandle _depth;
        PostFXStack _stack;
        bool _hasDepth;
        Vector2Int _bufferSize;

        /// <summary>
        /// Map buffer short side → texel radius. Large TVs shrink so the smear stays
        /// sharp. Phone standstill is half-res (short side ~540): a scale of 1 still
        /// paints about 2× the 1080 reference on screen, so those buffers cap under 1.
        /// </summary>
        public static float ComputeRadiusScale(int bufferWidth, int bufferHeight)
        {
            int shortSide = ShortSide(bufferWidth, bufferHeight);
            float scale = RadiusRefShortSide / shortSide;
            if (shortSide < RadiusRefShortSide)
                scale = Mathf.Min(scale, SmallBufferCap(shortSide));
            return Mathf.Clamp(scale, 0.45f, 1f);
        }

        /// <summary>
        /// Edge blend. 1080+ stays authored; phone half-res eases so standstill
        /// wet-oil does not replace the edge. Short side 540 → 0.73, 1080 → 1.
        /// </summary>
        public static float ComputeStrength(int bufferWidth, int bufferHeight)
        {
            int shortSide = ShortSide(bufferWidth, bufferHeight);
            if (shortSide >= RadiusRefShortSide)
                return 1f;
            return SmallBufferCap(shortSide);
        }

        static int ShortSide(int bufferWidth, int bufferHeight)
        {
            return Mathf.Max(1, Mathf.Min(bufferWidth, bufferHeight));
        }

        // Short side 540 → 0.73, 1080 → 1. Continuous so a resolution step does not pop.
        static float SmallBufferCap(int shortSide)
        {
            float t = shortSide / RadiusRefShortSide;
            return Mathf.Lerp(0.45f, 1f, t);
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
                float authored = _stack.Settings != null
                    ? Mathf.Clamp(_stack.Settings.OilFilter.strength, 0.05f, 1f)
                    : 0.45f;
                buffer.SetGlobalFloat(StrengthId, ComputeStrength(w, h) * authored);
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
