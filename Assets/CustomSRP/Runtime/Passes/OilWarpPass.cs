using Unity.Profiling;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using static CustomSRP.PostFXStack;

namespace CustomSRP
{
    /// <summary>
    /// Immersive full-screen oil UV warp after OilFilter, before color grading.
    /// </summary>
    public class OilWarpPass
    {
        static readonly ProfilingSampler Sampler = new ProfilingSampler("Oil Warp");
        static readonly ProfilerMarker MarkerRender = new ProfilerMarker("OilWarp.Render");

        static readonly int
            StrengthId = Shader.PropertyToID("_OilWarpStrength"),
            ScaleId = Shader.PropertyToID("_OilWarpScale"),
            MaxTexelsId = Shader.PropertyToID("_OilWarpMaxTexels"),
            TexelSizeId = Shader.PropertyToID("_OilWarpTexelSize");

        TextureHandle _source;
        TextureHandle _result;
        PostFXStack _stack;
        float _strength;
        float _scale;
        float _maxTexels;

        public static bool IsActive(PostFXStack stack)
        {
            if (!OilWarpDriver.IsActive)
                return false;
            PostFXSettings settings = stack != null ? stack.Settings : null;
            if (settings == null)
                return true;
            return settings.OilWarp.enabled;
        }

        public static TextureHandle Record(
            RenderGraph renderGraph,
            PostFXStack stack,
            TextureHandle colorSource)
        {
            if (!IsActive(stack) || !colorSource.IsValid())
                return colorSource;

            PostFXSettings.OilWarpSettings warp = stack.Settings.OilWarp;
            float strength = OilWarpDriver.Strength * Mathf.Clamp01(warp.maxStrength);
            if (strength < 1e-4f)
                return colorSource;

            using IUnsafeRenderGraphBuilder builder = renderGraph.AddUnsafePass(
                Sampler.name, out OilWarpPass pass, Sampler);
            pass._stack = stack;
            pass._source = colorSource;
            pass._strength = strength;
            pass._scale = warp.scale > 1e-4f ? warp.scale : 1f;
            // Same 3 taps either way. Do not shrink the dab on mobile — half-res
            // already makes a reduced texel count disappear after upscale.
            pass._maxTexels = Mathf.Clamp(warp.maxTexels, 3f, 14f);
            builder.UseTexture(colorSource);

            var desc = new TextureDesc(stack.BufferSize.x, stack.BufferSize.y)
            {
                colorFormat = SystemInfo.GetGraphicsFormat(
                    stack.BufferSettings.allowHDR ? DefaultFormat.HDR : DefaultFormat.LDR),
                name = "Oil Warp",
                filterMode = FilterMode.Bilinear
            };
            pass._result = renderGraph.CreateTexture(desc);
            builder.UseTexture(pass._result, AccessFlags.WriteAll);
            builder.AllowPassCulling(false);
            builder.AllowGlobalStateModification(true);
            builder.SetRenderFunc<OilWarpPass>(
                static (p, context) => p.Render(context));
            return pass._result;
        }

        void Render(UnsafeGraphContext context)
        {
            using (MarkerRender.Auto())
            {
                UnsafeCommandBuffer buffer = context.cmd;
                int w = Mathf.Max(1, _stack.BufferSize.x);
                int h = Mathf.Max(1, _stack.BufferSize.y);
                buffer.SetGlobalFloat(StrengthId, _strength);
                buffer.SetGlobalFloat(ScaleId, _scale);
                buffer.SetGlobalFloat(MaxTexelsId, _maxTexels);
                buffer.SetGlobalVector(TexelSizeId, new Vector4(1f / w, 1f / h, w, h));
                _stack.Draw(buffer, _source, _result, Pass.OilWarp);
            }
        }
    }
}
