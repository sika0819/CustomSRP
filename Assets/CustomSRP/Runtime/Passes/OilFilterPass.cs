using Unity.Profiling;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using static CustomSRP.PostFXStack;

namespace CustomSRP
{
    /// <summary>
    /// Edge wet-oil after Bloom, before color grading.
    /// Mobile: half-res QuadPick + full-res sharp compose (UltraLite). Desktop / Editor: HQ.
    /// Params follow PostFX Settings only — no per-device / material oil strategies.
    /// </summary>
    public class OilFilterPass
    {
        static readonly ProfilingSampler
            Sampler = new ProfilingSampler("Oil Filter"),
            HalfSampler = new ProfilingSampler("Oil Filter Half"),
            ComposeSampler = new ProfilingSampler("Oil Filter Compose"),
            HqSampler = new ProfilingSampler("Oil Filter HQ");

        static readonly ProfilerMarker MarkerRender = new ProfilerMarker("OilFilter.Render");

        /// <summary>
        /// Wet-oil texel radius is authored for this buffer short side.
        /// Larger buffers scale radius down so the smear stays sharp.
        /// </summary>
        const float RadiusRefShortSide = 1080f;

        static readonly int
            TexelSizeId = Shader.PropertyToID("_OilFilterTexelSize"),
            HasDepthId = Shader.PropertyToID("_OilFilterHasDepth"),
            RadiusScaleId = Shader.PropertyToID("_OilFilterRadiusScale"),
            StrengthId = Shader.PropertyToID("_OilFilterStrength"),
            InteriorFloorId = Shader.PropertyToID("_OilFilterInteriorFloor"),
            DepthCopyId = Shader.PropertyToID("_CameraDepthTexture");

        enum Stage { Half, Compose, HQ, FullUltraLite }

        TextureHandle _source;
        TextureHandle _oilHalf;
        TextureHandle _result;
        TextureHandle _depth;
        PostFXStack _stack;
        bool _hasDepth;
        bool _ultraLite;
        Vector2Int _bufferSize;
        float _strength;
        float _interiorFloor;
        float _radiusMul;
        Stage _stage;

        /// <summary>
        /// Map buffer short side → radius scale.
        /// </summary>
        public static float ComputeRadiusScale(int bufferWidth, int bufferHeight)
        {
            int shortSide = Mathf.Max(1, Mathf.Min(bufferWidth, bufferHeight));
            float scale = RadiusRefShortSide / shortSide;
            return Mathf.Clamp(scale, 0.45f, 1.15f);
        }

        /// <summary>
        /// HQ keeps a higher floor so Retina / 1440p+ Game 视图仍能看出笔触。
        /// </summary>
        public static float ComputeRadiusScaleHQ(int bufferWidth, int bufferHeight)
        {
            int shortSide = Mathf.Max(1, Mathf.Min(bufferWidth, bufferHeight));
            float scale = RadiusRefShortSide / shortSide;
            return Mathf.Clamp(scale, 0.72f, 1.25f);
        }

        public static bool UseUltraLite => Application.isMobilePlatform;

        public readonly struct OilParams
        {
            public readonly float Strength;
            public readonly float InteriorFloor;
            public readonly float RadiusMul;
            public readonly bool HasDepth;
            public readonly TextureHandle Depth;

            public OilParams(
                float strength,
                float interiorFloor,
                float radiusMul,
                bool hasDepth,
                TextureHandle depth)
            {
                Strength = strength;
                InteriorFloor = interiorFloor;
                RadiusMul = radiusMul;
                HasDepth = hasDepth;
                Depth = depth;
            }
        }

        public static bool TryGetParams(
            PostFXStack stack,
            in CameraRendererTextures textures,
            out OilParams oilParams)
        {
            oilParams = default;
            if (!OilFilter.IsEnabled(stack.Settings))
                return false;

            PostFXSettings.OilFilterSettings oil = stack.Settings.OilFilter;
            float strength = oil.strength > 0f ? oil.strength : 1f;
            float interiorFloor = Mathf.Clamp(oil.interiorFloor, 0f, 0.25f);
            float radiusMul = oil.radiusScale > 1e-4f
                ? Mathf.Clamp(oil.radiusScale, 0.5f, 1.5f)
                : 1f;

            bool hasDepth = textures.depthCopy.IsValid();
            TextureHandle depth = hasDepth ? textures.depthCopy : textures.depthAttachment;
            hasDepth = depth.IsValid();
            oilParams = new OilParams(
                Mathf.Clamp(strength, 0f, 1.5f),
                interiorFloor,
                radiusMul,
                hasDepth,
                depth);
            return true;
        }

        public static void BindGlobals(
            UnsafeCommandBuffer buffer,
            Vector2Int bufferSize,
            bool ultraLite,
            in OilParams oilParams)
        {
            int w = Mathf.Max(1, bufferSize.x);
            int h = Mathf.Max(1, bufferSize.y);
            buffer.SetGlobalVector(TexelSizeId, new Vector4(1f / w, 1f / h, w, h));
            buffer.SetGlobalFloat(HasDepthId, oilParams.HasDepth ? 1f : 0f);
            float autoRadius = ultraLite
                ? ComputeRadiusScale(w, h)
                : ComputeRadiusScaleHQ(w, h);
            buffer.SetGlobalFloat(RadiusScaleId, autoRadius * oilParams.RadiusMul);
            buffer.SetGlobalFloat(StrengthId, oilParams.Strength);
            buffer.SetGlobalFloat(InteriorFloorId, oilParams.InteriorFloor);
            if (oilParams.HasDepth)
                buffer.SetGlobalTexture(DepthCopyId, oilParams.Depth);
        }

        void FillFrom(PostFXStack stack, TextureHandle source, in OilParams oilParams)
        {
            _stack = stack;
            _source = source;
            _bufferSize = stack.BufferSize;
            _ultraLite = UseUltraLite;
            _strength = oilParams.Strength;
            _interiorFloor = oilParams.InteriorFloor;
            _radiusMul = oilParams.RadiusMul;
            _hasDepth = oilParams.HasDepth;
            _depth = oilParams.Depth;
        }

        void Render(UnsafeGraphContext context)
        {
            using (MarkerRender.Auto())
            {
                UnsafeCommandBuffer buffer = context.cmd;
                BindGlobals(
                    buffer,
                    _bufferSize,
                    _ultraLite,
                    new OilParams(
                        _strength, _interiorFloor, _radiusMul, _hasDepth, _depth));

                switch (_stage)
                {
                    case Stage.Half:
                        _stack.Draw(buffer, _source, _oilHalf, Pass.OilFilterHalf);
                        break;
                    case Stage.Compose:
                        buffer.SetGlobalTexture(fxSource2Id, _oilHalf);
                        _stack.Draw(buffer, _source, _result, Pass.OilFilterCompose);
                        break;
                    case Stage.HQ:
                        _stack.Draw(buffer, _source, _result, Pass.OilFilterHQ);
                        break;
                    case Stage.FullUltraLite:
                        _stack.Draw(buffer, _source, _oilHalf, Pass.OilFilterHalf);
                        buffer.SetGlobalTexture(fxSource2Id, _oilHalf);
                        _stack.Draw(buffer, _source, _result, Pass.OilFilterCompose);
                        break;
                }
            }
        }

        static GraphicsFormat ColorFormat(PostFXStack stack) =>
            SystemInfo.GetGraphicsFormat(
                stack.BufferSettings.allowHDR ? DefaultFormat.HDR : DefaultFormat.LDR);

        static void UseDepth(
            IUnsafeRenderGraphBuilder builder, in OilParams oilParams)
        {
            if (oilParams.HasDepth)
                builder.UseTexture(oilParams.Depth);
        }

        /// <summary>UltraLite pass 1 only — returns half-res oil color.</summary>
        public static TextureHandle RecordHalf(
            RenderGraph renderGraph,
            PostFXStack stack,
            TextureHandle colorSource,
            in OilParams oilParams)
        {
            using IUnsafeRenderGraphBuilder builder = renderGraph.AddUnsafePass(
                HalfSampler.name, out OilFilterPass pass, HalfSampler);
            pass.FillFrom(stack, colorSource, oilParams);
            pass._stage = Stage.Half;
            builder.UseTexture(colorSource);
            UseDepth(builder, oilParams);

            int halfW = Mathf.Max(1, stack.BufferSize.x / 2);
            int halfH = Mathf.Max(1, stack.BufferSize.y / 2);
            var halfDesc = new TextureDesc(halfW, halfH)
            {
                colorFormat = ColorFormat(stack),
                name = "Oil Filter Half",
                filterMode = FilterMode.Bilinear
            };
            pass._oilHalf = renderGraph.CreateTexture(halfDesc);
            builder.UseTexture(pass._oilHalf, AccessFlags.WriteAll);
            builder.AllowPassCulling(false);
            builder.SetRenderFunc<OilFilterPass>(
                static (p, context) => p.Render(context));
            return pass._oilHalf;
        }

        /// <summary>UltraLite pass 2 — full-res compose to an intermediate RT.</summary>
        public static TextureHandle RecordCompose(
            RenderGraph renderGraph,
            PostFXStack stack,
            TextureHandle colorSource,
            TextureHandle oilHalf,
            in OilParams oilParams)
        {
            using IUnsafeRenderGraphBuilder builder = renderGraph.AddUnsafePass(
                ComposeSampler.name, out OilFilterPass pass, ComposeSampler);
            pass.FillFrom(stack, colorSource, oilParams);
            pass._stage = Stage.Compose;
            pass._oilHalf = oilHalf;
            builder.UseTexture(colorSource);
            builder.UseTexture(oilHalf);
            UseDepth(builder, oilParams);

            var desc = new TextureDesc(stack.BufferSize.x, stack.BufferSize.y)
            {
                colorFormat = ColorFormat(stack),
                name = "Oil Filter",
                filterMode = FilterMode.Bilinear
            };
            pass._result = renderGraph.CreateTexture(desc);
            builder.UseTexture(pass._result, AccessFlags.WriteAll);
            builder.AllowPassCulling(false);
            builder.SetRenderFunc<OilFilterPass>(
                static (p, context) => p.Render(context));
            return pass._result;
        }

        /// <summary>Desktop / Editor HQ wet-oil to an intermediate RT.</summary>
        public static TextureHandle RecordHQ(
            RenderGraph renderGraph,
            PostFXStack stack,
            TextureHandle colorSource,
            in OilParams oilParams)
        {
            using IUnsafeRenderGraphBuilder builder = renderGraph.AddUnsafePass(
                HqSampler.name, out OilFilterPass pass, HqSampler);
            pass.FillFrom(stack, colorSource, oilParams);
            pass._stage = Stage.HQ;
            builder.UseTexture(colorSource);
            UseDepth(builder, oilParams);

            var desc = new TextureDesc(stack.BufferSize.x, stack.BufferSize.y)
            {
                colorFormat = ColorFormat(stack),
                name = "Oil Filter",
                filterMode = FilterMode.Bilinear
            };
            pass._result = renderGraph.CreateTexture(desc);
            builder.UseTexture(pass._result, AccessFlags.WriteAll);
            builder.AllowPassCulling(false);
            builder.SetRenderFunc<OilFilterPass>(
                static (p, context) => p.Render(context));
            return pass._result;
        }

        /// <summary>
        /// Legacy single-pass UltraLite (Half+Compose) or HQ to intermediate.
        /// Used when FXAA keeps the old Final path.
        /// </summary>
        public static TextureHandle Record(
            RenderGraph renderGraph,
            PostFXStack stack,
            TextureHandle colorSource,
            in CameraRendererTextures textures)
        {
            if (!TryGetParams(stack, textures, out OilParams oilParams))
                return colorSource;

            if (UseUltraLite)
            {
                using IUnsafeRenderGraphBuilder builder = renderGraph.AddUnsafePass(
                    Sampler.name, out OilFilterPass pass, Sampler);
                pass.FillFrom(stack, colorSource, oilParams);
                pass._stage = Stage.FullUltraLite;
                builder.UseTexture(colorSource);
                UseDepth(builder, oilParams);

                int halfW = Mathf.Max(1, stack.BufferSize.x / 2);
                int halfH = Mathf.Max(1, stack.BufferSize.y / 2);
                var halfDesc = new TextureDesc(halfW, halfH)
                {
                    colorFormat = ColorFormat(stack),
                    name = "Oil Filter Half",
                    filterMode = FilterMode.Bilinear
                };
                pass._oilHalf = renderGraph.CreateTexture(halfDesc);
                builder.UseTexture(pass._oilHalf, AccessFlags.ReadWrite);

                var desc = new TextureDesc(stack.BufferSize.x, stack.BufferSize.y)
                {
                    colorFormat = ColorFormat(stack),
                    name = "Oil Filter",
                    filterMode = FilterMode.Bilinear
                };
                pass._result = renderGraph.CreateTexture(desc);
                builder.UseTexture(pass._result, AccessFlags.WriteAll);
                builder.AllowPassCulling(false);
                builder.SetRenderFunc<OilFilterPass>(
                    static (p, context) => p.Render(context));
                return pass._result;
            }

            return RecordHQ(renderGraph, stack, colorSource, oilParams);
        }
    }
}
