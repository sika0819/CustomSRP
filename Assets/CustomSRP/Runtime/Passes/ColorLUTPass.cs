using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using static CustomSRP.PostFXSettings;

namespace CustomSRP
{
    public class ColorLUTPass
    {
        static readonly ProfilingSampler Sampler = new ProfilingSampler("Color LUT");

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

        static readonly GraphicsFormat ColorFormat =
            SystemInfo.GetGraphicsFormat(DefaultFormat.HDR);

        PostFXStack _stack;
        ComputeShader _shader;
        int _colorLUTResolution;
        TextureHandle _colorLUT;

        void ConfigureColorAdjustments(ComputeCommandBuffer buffer, PostFXSettings settings)
        {
            ColorAdjustmentsSettings colorAdjustments = settings.ColorAdjustments;
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
            buffer.SetComputeVectorParam(
                _shader, whiteBalanceId,
                ColorUtils.ColorBalanceToLMSCoeffs(
                    whiteBalance.temperature, whiteBalance.tint));
        }

        void ConfigureSplitToning(ComputeCommandBuffer buffer, PostFXSettings settings)
        {
            SplitToningSettings splitToning = settings.SplitToning;
            Color splitColor = splitToning.shadows;
            splitColor.a = splitToning.balance * 0.01f;
            buffer.SetComputeVectorParam(_shader, splitToningShadowsId, splitColor);
            buffer.SetComputeVectorParam(
                _shader, splitToningHighlightsId, splitToning.highlights);
        }

        void ConfigureChannelMixer(ComputeCommandBuffer buffer, PostFXSettings settings)
        {
            ChannelMixerSettings channelMixer = settings.ChannelMixer;
            buffer.SetComputeVectorParam(_shader, channelMixerRedId, channelMixer.red);
            buffer.SetComputeVectorParam(_shader, channelMixerGreenId, channelMixer.green);
            buffer.SetComputeVectorParam(_shader, channelMixerBlueId, channelMixer.blue);
        }

        void ConfigureShadowsMidtonesHighlights(
            ComputeCommandBuffer buffer, PostFXSettings settings)
        {
            ShadowsMidtonesHighlightsSettings smh = settings.ShadowsMidtonesHighlights;
            buffer.SetComputeVectorParam(_shader, smhShadowsId, smh.shadows.linear);
            buffer.SetComputeVectorParam(_shader, smhMidtonesId, smh.midtones.linear);
            buffer.SetComputeVectorParam(_shader, smhHighlightsId, smh.highlights.linear);
            buffer.SetComputeVectorParam(_shader, smhRangeId, new Vector4(
                smh.shadowsStart, smh.shadowsEnd, smh.highlightsStart, smh.highLightsEnd));
        }

        void Render(ComputeGraphContext context)
        {
            PostFXSettings settings = _stack.Settings;
            ComputeCommandBuffer buffer = context.cmd;
            ConfigureColorAdjustments(buffer, settings);
            ConfigureWhiteBalance(buffer, settings);
            ConfigureSplitToning(buffer, settings);
            ConfigureChannelMixer(buffer, settings);
            ConfigureShadowsMidtonesHighlights(buffer, settings);

            ToneMappingSettings.Mode mode = settings.ToneMapping.mode;
            var lutParameters = new Vector4(
                // X: Whether LUT is in LogC space.
                _stack.BufferSettings.allowHDR &&
                mode != ToneMappingSettings.Mode.None
                    ? 1f
                    : 0f,
                // YZ: Arguments for ApplyLut3D, for sampling LUT.
                1f / _colorLUTResolution,
                _colorLUTResolution - 1f,
                // W: Linear ID to color conversion, for filling LUT.
                1f / (_colorLUTResolution - 1f));
            buffer.SetGlobalVector(colorGradingLUTParametersId, lutParameters);
            buffer.SetComputeVectorParam(
                _shader, colorGradingLUTParametersId, lutParameters);

            int kernel = (int)mode;
            buffer.SetComputeTextureParam(
                _shader, kernel, colorGradingLUTId, _colorLUT);
            int groups = _colorLUTResolution / 4;
            buffer.DispatchCompute(_shader, kernel, groups, groups, groups);
            buffer.SetGlobalTexture(colorGradingLUTId, _colorLUT);
        }

        public static TextureHandle Record(
            RenderGraph renderGraph,
            PostFXStack stack,
            int colorLUTResolution)
        {
            ComputeShader compute = stack.Settings.ColorLUTComputeShader;
            if (compute == null)
            {
                Debug.LogError(
                    "PostFXSettings.colorLUTComputeShader is not assigned. " +
                    "Assign Assets/CustomSRP/Shaders/ColorLUT.compute, or run " +
                    "CustomSRP → Fix Color LUT Compute References.");
                return TextureHandle.nullHandle;
            }

            using IComputeRenderGraphBuilder builder = renderGraph.AddComputePass(
                Sampler.name, out ColorLUTPass pass, Sampler);
            pass._stack = stack;
            pass._shader = compute;
            pass._colorLUTResolution = Mathf.Max(16, colorLUTResolution);

            var desc = new TextureDesc(pass._colorLUTResolution, pass._colorLUTResolution)
            {
                dimension = TextureDimension.Tex3D,
                slices = pass._colorLUTResolution,
                enableRandomWrite = true,
                colorFormat = ColorFormat,
                name = "Color LUT",
                filterMode = FilterMode.Bilinear
            };
            pass._colorLUT = renderGraph.CreateTexture(desc);
            builder.UseTexture(pass._colorLUT, AccessFlags.WriteAll);
            builder.AllowGlobalStateModification(true);
            builder.SetRenderFunc<ColorLUTPass>(
                static (pass, context) => pass.Render(context));
            return pass._colorLUT;
        }
    }
}
