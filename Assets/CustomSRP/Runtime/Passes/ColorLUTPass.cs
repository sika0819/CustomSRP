using Unity.Profiling;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityGameFramework.Runtime;
using static CustomSRP.PostFXSettings;

namespace CustomSRP
{
    public class ColorLUTPass
    {
        static readonly ProfilingSampler Sampler = new ProfilingSampler("Color LUT");
        static readonly ProfilerMarker MarkerRebuild = new ProfilerMarker("ColorLUT.Rebuild");
        static readonly ProfilerMarker MarkerBindCached = new ProfilerMarker("ColorLUT.BindCached");

        static readonly int
            colorGradingLUTId = Shader.PropertyToID("_ColorGradingLUT"),
            colorGradingLUTParametersId = Shader.PropertyToID("_ColorGradingLUTParameters"),
            colorAdjustmentsId = Shader.PropertyToID("_ColorAdjustments"),
            colorFilterId = Shader.PropertyToID("_ColorFilter"),
            whiteBalanceId = Shader.PropertyToID("_WhiteBalance"),
            splitToningHighlightsId = Shader.PropertyToID("_SplitToningHighlights"),
            splitToningShadowsId = Shader.PropertyToID("_SplitToningShadows"),
            channelMixerRedId = Shader.PropertyToID("_ChannelMixerRed"),
            channelMixerGreenId = Shader.PropertyToID("_ChannelMixerGreen"),
            channelMixerBlueId = Shader.PropertyToID("_ChannelMixerBlue"),
            smhShadowsId = Shader.PropertyToID("_SMHShadows"),
            smhMidtonesId = Shader.PropertyToID("_SMHMidtones"),
            smhHighlightsId = Shader.PropertyToID("_SMHHighlights"),
            smhRangeId = Shader.PropertyToID("_SMHRange");

        static RTHandle s_CachedLut;
        static int s_CachedHash;
        static int s_CachedResolution;
        static GraphicsFormat s_CachedFormat;
        static Vector4 s_CachedLutParameters;
        static bool s_CacheValid;

        static GraphicsFormat LutColorFormat
        {
            get
            {
                GraphicsFormat hdr = SystemInfo.GetGraphicsFormat(DefaultFormat.HDR);
                if (SystemInfo.IsFormatSupported(hdr, GraphicsFormatUsage.LoadStore))
                    return hdr;
                GraphicsFormat ldr = SystemInfo.GetGraphicsFormat(DefaultFormat.LDR);
                if (SystemInfo.IsFormatSupported(ldr, GraphicsFormatUsage.LoadStore))
                    return ldr;
                return GraphicsFormat.R8G8B8A8_UNorm;
            }
        }

        PostFXStack _stack;
        ComputeShader _shader;
        int _colorLUTResolution;
        TextureHandle _colorLUT;
        Vector4 _lutParameters;

        void ConfigureColorAdjustments(ComputeCommandBuffer buffer, PostFXSettings settings)
        {
            ColorAdjustmentsSettings colorAdjustments = settings.ColorAdjustments;
            if (!colorAdjustments.enabled)
            {
                buffer.SetComputeVectorParam(_shader, colorAdjustmentsId, new Vector4(1f, 1f, 0f, 1f));
                buffer.SetComputeVectorParam(_shader, colorFilterId, Color.white.linear);
                return;
            }

            buffer.SetComputeVectorParam(_shader, colorAdjustmentsId, new Vector4(
                Mathf.Pow(2f, colorAdjustments.postExposure),
                colorAdjustments.contrast * 0.01f + 1f,
                colorAdjustments.hueShift * (1f / 360f),
                colorAdjustments.saturation * 0.01f + 1f));
            buffer.SetComputeVectorParam(
                _shader, colorFilterId, colorAdjustments.colorFilter.linear);
        }

        void ConfigureWhiteBalance(ComputeCommandBuffer buffer, PostFXSettings settings)
        {
            WhiteBalanceSettings whiteBalance = settings.WhiteBalance;
            float temperature = whiteBalance.enabled ? whiteBalance.temperature : 0f;
            float tint = whiteBalance.enabled ? whiteBalance.tint : 0f;
            buffer.SetComputeVectorParam(
                _shader, whiteBalanceId,
                ColorUtils.ColorBalanceToLMSCoeffs(temperature, tint));
        }

        void ConfigureSplitToning(ComputeCommandBuffer buffer, PostFXSettings settings)
        {
            SplitToningSettings splitToning = settings.SplitToning;
            if (!splitToning.enabled)
            {
                buffer.SetComputeVectorParam(_shader, splitToningShadowsId, new Vector4(0.5f, 0.5f, 0.5f, 0f));
                buffer.SetComputeVectorParam(_shader, splitToningHighlightsId, new Vector4(0.5f, 0.5f, 0.5f, 1f));
                return;
            }

            Color splitColor = splitToning.shadows;
            splitColor.a = splitToning.balance * 0.01f;
            buffer.SetComputeVectorParam(_shader, splitToningShadowsId, splitColor);
            buffer.SetComputeVectorParam(
                _shader, splitToningHighlightsId, splitToning.highlights);
        }

        void ConfigureChannelMixer(ComputeCommandBuffer buffer, PostFXSettings settings)
        {
            ChannelMixerSettings channelMixer = settings.ChannelMixer;
            if (!channelMixer.enabled)
            {
                buffer.SetComputeVectorParam(_shader, channelMixerRedId, Vector3.right);
                buffer.SetComputeVectorParam(_shader, channelMixerGreenId, Vector3.up);
                buffer.SetComputeVectorParam(_shader, channelMixerBlueId, Vector3.forward);
                return;
            }

            buffer.SetComputeVectorParam(_shader, channelMixerRedId, channelMixer.red);
            buffer.SetComputeVectorParam(_shader, channelMixerGreenId, channelMixer.green);
            buffer.SetComputeVectorParam(_shader, channelMixerBlueId, channelMixer.blue);
        }

        void ConfigureShadowsMidtonesHighlights(
            ComputeCommandBuffer buffer, PostFXSettings settings)
        {
            ShadowsMidtonesHighlightsSettings smh = settings.ShadowsMidtonesHighlights;
            if (!smh.enabled)
            {
                buffer.SetComputeVectorParam(_shader, smhShadowsId, Color.white.linear);
                buffer.SetComputeVectorParam(_shader, smhMidtonesId, Color.white.linear);
                buffer.SetComputeVectorParam(_shader, smhHighlightsId, Color.white.linear);
                buffer.SetComputeVectorParam(_shader, smhRangeId, new Vector4(0f, 0.3f, 0.55f, 1f));
                return;
            }

            buffer.SetComputeVectorParam(_shader, smhShadowsId, smh.shadows.linear);
            buffer.SetComputeVectorParam(_shader, smhMidtonesId, smh.midtones.linear);
            buffer.SetComputeVectorParam(_shader, smhHighlightsId, smh.highlights.linear);
            buffer.SetComputeVectorParam(_shader, smhRangeId, new Vector4(
                smh.shadowsStart, smh.shadowsEnd, smh.highlightsStart, smh.highLightsEnd));
        }

        static int HashSettings(PostFXStack stack, int resolution, GraphicsFormat format)
        {
            unchecked
            {
                PostFXSettings settings = stack.Settings;
                int hash = resolution * 397;
                hash = hash * 397 ^ (int)format;
                hash = hash * 397 ^ (stack.BufferSettings.allowHDR ? 1 : 0);

                ToneMappingSettings.Mode mode = settings.ToneMapping.enabled
                    ? settings.ToneMapping.mode
                    : ToneMappingSettings.Mode.None;
                hash = hash * 397 ^ (int)mode;

                ColorAdjustmentsSettings ca = settings.ColorAdjustments;
                hash = hash * 397 ^ (ca.enabled ? 1 : 0);
                if (ca.enabled)
                {
                    hash = hash * 397 ^ ca.postExposure.GetHashCode();
                    hash = hash * 397 ^ ca.contrast.GetHashCode();
                    hash = hash * 397 ^ ca.hueShift.GetHashCode();
                    hash = hash * 397 ^ ca.saturation.GetHashCode();
                    hash = hash * 397 ^ ca.colorFilter.GetHashCode();
                }

                WhiteBalanceSettings wb = settings.WhiteBalance;
                hash = hash * 397 ^ (wb.enabled ? 1 : 0);
                if (wb.enabled)
                {
                    hash = hash * 397 ^ wb.temperature.GetHashCode();
                    hash = hash * 397 ^ wb.tint.GetHashCode();
                }

                SplitToningSettings st = settings.SplitToning;
                hash = hash * 397 ^ (st.enabled ? 1 : 0);
                if (st.enabled)
                {
                    hash = hash * 397 ^ st.shadows.GetHashCode();
                    hash = hash * 397 ^ st.highlights.GetHashCode();
                    hash = hash * 397 ^ st.balance.GetHashCode();
                }

                ChannelMixerSettings cm = settings.ChannelMixer;
                hash = hash * 397 ^ (cm.enabled ? 1 : 0);
                if (cm.enabled)
                {
                    hash = hash * 397 ^ cm.red.GetHashCode();
                    hash = hash * 397 ^ cm.green.GetHashCode();
                    hash = hash * 397 ^ cm.blue.GetHashCode();
                }

                ShadowsMidtonesHighlightsSettings smh = settings.ShadowsMidtonesHighlights;
                hash = hash * 397 ^ (smh.enabled ? 1 : 0);
                if (smh.enabled)
                {
                    hash = hash * 397 ^ smh.shadows.GetHashCode();
                    hash = hash * 397 ^ smh.midtones.GetHashCode();
                    hash = hash * 397 ^ smh.highlights.GetHashCode();
                    hash = hash * 397 ^ smh.shadowsStart.GetHashCode();
                    hash = hash * 397 ^ smh.shadowsEnd.GetHashCode();
                    hash = hash * 397 ^ smh.highlightsStart.GetHashCode();
                    hash = hash * 397 ^ smh.highLightsEnd.GetHashCode();
                }

                return hash;
            }
        }

        static void EnsurePersistentLut(int resolution, GraphicsFormat format)
        {
            if (s_CachedLut != null
                && s_CachedResolution == resolution
                && s_CachedFormat == format
                && s_CachedLut.rt != null
                && s_CachedLut.rt.IsCreated())
                return;

            if (s_CachedLut != null)
            {
                RTHandles.Release(s_CachedLut);
                s_CachedLut = null;
            }

            s_CachedLut = RTHandles.Alloc(
                resolution,
                resolution,
                slices: resolution,
                colorFormat: format,
                filterMode: FilterMode.Bilinear,
                wrapMode: TextureWrapMode.Clamp,
                dimension: TextureDimension.Tex3D,
                enableRandomWrite: true,
                name: "ColorLUT_Cache");
            s_CachedResolution = resolution;
            s_CachedFormat = format;
            s_CacheValid = false;
        }

        static Vector4 BuildLutParameters(PostFXStack stack, int resolution)
        {
            ToneMappingSettings.Mode mode = stack.Settings.ToneMapping.enabled
                ? stack.Settings.ToneMapping.mode
                : ToneMappingSettings.Mode.None;
            return new Vector4(
                stack.BufferSettings.allowHDR && mode != ToneMappingSettings.Mode.None
                    ? 1f
                    : 0f,
                1f / resolution,
                resolution - 1f,
                1f / (resolution - 1f));
        }

        void RenderRebuild(ComputeGraphContext context)
        {
            using (MarkerRebuild.Auto())
            {
                PostFXSettings settings = _stack.Settings;
                ComputeCommandBuffer buffer = context.cmd;
                ConfigureColorAdjustments(buffer, settings);
                ConfigureWhiteBalance(buffer, settings);
                ConfigureSplitToning(buffer, settings);
                ConfigureChannelMixer(buffer, settings);
                ConfigureShadowsMidtonesHighlights(buffer, settings);

                ToneMappingSettings.Mode mode = settings.ToneMapping.enabled
                    ? settings.ToneMapping.mode
                    : ToneMappingSettings.Mode.None;
                buffer.SetGlobalVector(colorGradingLUTParametersId, _lutParameters);
                buffer.SetComputeVectorParam(
                    _shader, colorGradingLUTParametersId, _lutParameters);

                int kernel = (int)mode;
                buffer.SetComputeTextureParam(
                    _shader, kernel, colorGradingLUTId, _colorLUT);
                int groups = _colorLUTResolution / 4;
                buffer.DispatchCompute(_shader, kernel, groups, groups, groups);
                buffer.SetGlobalTexture(colorGradingLUTId, _colorLUT);
            }
        }

        void RenderBindCached(UnsafeGraphContext context)
        {
            using (MarkerBindCached.Auto())
            {
                UnsafeCommandBuffer buffer = context.cmd;
                buffer.SetGlobalVector(colorGradingLUTParametersId, _lutParameters);
                buffer.SetGlobalTexture(colorGradingLUTId, _colorLUT);
            }
        }

        public static TextureHandle Record(
            RenderGraph renderGraph,
            PostFXStack stack,
            int colorLUTResolution)
        {
            ComputeShader compute = stack.Settings.ColorLUTComputeShader;
            if (compute == null)
            {
                Log.Error(
                    "PostFXSettings.colorLUTComputeShader is not assigned. " +
                    "Assign Assets/CustomSRP/Shaders/ColorLUT.compute, or run " +
                    "CustomSRP → Fix Color LUT Compute References.");
                return TextureHandle.nullHandle;
            }

            int resolution = Mathf.Max(16, colorLUTResolution);
            // Mobile: cap LUT resolution to 24 to cut the compute kernel and storage.
            // Visual difference is imperceptible; saves 8³→6³ = 56% of dispatch threads.
            if (Application.isMobilePlatform && resolution > 24)
            {
                resolution = 24;
            }
            GraphicsFormat format = LutColorFormat;
            EnsurePersistentLut(resolution, format);
            Vector4 lutParameters = BuildLutParameters(stack, resolution);
            int hash = HashSettings(stack, resolution, format);
            bool cacheHit = s_CacheValid && s_CachedHash == hash && s_CachedLut != null;
            TextureHandle lutHandle = renderGraph.ImportTexture(s_CachedLut);

            if (cacheHit)
            {
                using IUnsafeRenderGraphBuilder bindBuilder = renderGraph.AddUnsafePass(
                    Sampler.name, out ColorLUTPass bindPass, Sampler);
                bindPass._lutParameters = s_CachedLutParameters;
                bindPass._colorLUT = lutHandle;
                bindBuilder.UseTexture(lutHandle);
                bindBuilder.AllowGlobalStateModification(true);
                bindBuilder.AllowPassCulling(false);
                bindBuilder.SetRenderFunc<ColorLUTPass>(
                    static (pass, context) => pass.RenderBindCached(context));
                return lutHandle;
            }

            using (IComputeRenderGraphBuilder builder = renderGraph.AddComputePass(
                       Sampler.name, out ColorLUTPass pass, Sampler))
            {
                pass._stack = stack;
                pass._shader = compute;
                pass._colorLUTResolution = resolution;
                pass._lutParameters = lutParameters;
                pass._colorLUT = lutHandle;
                builder.UseTexture(lutHandle, AccessFlags.WriteAll);
                builder.AllowGlobalStateModification(true);
                builder.SetRenderFunc<ColorLUTPass>(
                    static (p, context) => p.RenderRebuild(context));
            }

            s_CachedHash = hash;
            s_CachedLutParameters = lutParameters;
            s_CacheValid = true;
            return lutHandle;
        }
    }
}
