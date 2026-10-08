using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using static CustomSRP.PostFXStack;

namespace CustomSRP
{
    public class PostFXPass
    {
        static readonly ProfilingSampler
            GroupSampler = new ProfilingSampler("Post FX"),
            FinalSampler = new ProfilingSampler("Final Post FX");

        static readonly int
            copyBicubicId = Shader.PropertyToID("_CopyBicubic"),
            fxaaConfigId = Shader.PropertyToID("_FXAAConfig");

        static readonly GlobalKeyword
            fxaaLowKeyword = GlobalKeyword.Create("FXAA_QUALITY_LOW"),
            fxaaMediumKeyword = GlobalKeyword.Create("FXAA_QUALITY_MEDIUM");

        static readonly GraphicsFormat ColorFormat =
            SystemInfo.GetGraphicsFormat(DefaultFormat.LDR);

        PostFXStack _stack;
        bool _keepAlpha;

        enum ScaleMode { None, Linear, Bicubic }

        ScaleMode _scaleMode;
        TextureHandle _colorSource;
        TextureHandle _colorGradingResult;
        TextureHandle _scaledResult;
        TextureHandle _cameraTarget;

        void ConfigureFXAA(UnsafeCommandBuffer buffer)
        {
            CameraBufferSettings.FXAA fxaa = _stack.BufferSettings.fxaa;
            if (fxaa.quality == CameraBufferSettings.FXAA.Quality.Low)
            {
                buffer.SetKeyword(fxaaLowKeyword, true);
                buffer.SetKeyword(fxaaMediumKeyword, false);
            }
            else if (fxaa.quality == CameraBufferSettings.FXAA.Quality.Medium)
            {
                buffer.SetKeyword(fxaaLowKeyword, false);
                buffer.SetKeyword(fxaaMediumKeyword, true);
            }
            else
            {
                buffer.SetKeyword(fxaaLowKeyword, false);
                buffer.SetKeyword(fxaaMediumKeyword, false);
            }

            buffer.SetGlobalVector(fxaaConfigId, new Vector4(
                fxaa.fixedThreshold, fxaa.relativeThreshold, fxaa.subpixelBlending));
        }

        void Render(UnsafeGraphContext context)
        {
            UnsafeCommandBuffer buffer = context.cmd;
            buffer.SetGlobalFloat(finalSrcBlendId, 1f);
            buffer.SetGlobalFloat(finalDstBlendId, 0f);

            TextureHandle finalSource;
            Pass finalPass;
            if (_stack.BufferSettings.fxaa.enabled)
            {
                finalSource = _colorGradingResult;
                finalPass = _keepAlpha ? Pass.FXAA : Pass.FXAAWithLuma;
                ConfigureFXAA(buffer);
                _stack.Draw(
                    buffer, _colorSource, finalSource,
                    _keepAlpha ? Pass.ApplyColorGrading : Pass.ApplyColorGradingWithLuma);
            }
            else
            {
                finalSource = _colorSource;
                finalPass = Pass.ApplyColorGrading;
            }

            if (_scaleMode == ScaleMode.None)
            {
                _stack.DrawFinal(buffer, finalSource, _cameraTarget, finalPass);
            }
            else
            {
                _stack.Draw(buffer, finalSource, _scaledResult, finalPass);
                buffer.SetGlobalFloat(
                    copyBicubicId, _scaleMode == ScaleMode.Bicubic ? 1f : 0f);
                _stack.DrawFinal(
                    buffer, _scaledResult, _cameraTarget, Pass.FinalRescale);
            }
        }

        public static TextureHandle Record(
            RenderGraph renderGraph,
            PostFXStack stack,
            int colorLUTResolution,
            bool keepAlpha,
            in CameraRendererTextures textures)
        {
            using var _ = new RenderGraphProfilingScope(renderGraph, GroupSampler);

            TextureHandle colorSource = BloomPass.Record(renderGraph, stack, textures);
            TextureHandle colorLUT = ColorLUTPass.Record(
                renderGraph, stack, colorLUTResolution);

            using IUnsafeRenderGraphBuilder builder = renderGraph.AddUnsafePass(
                FinalSampler.name, out PostFXPass pass, FinalSampler);
            pass._keepAlpha = keepAlpha;
            pass._stack = stack;
            pass._colorSource = colorSource;
            builder.UseTexture(colorSource);
            if (colorLUT.IsValid())
            {
                builder.UseTexture(colorLUT);
            }

            if (stack.BufferSize.x == stack.Camera.pixelWidth)
            {
                pass._scaleMode = ScaleMode.None;
            }
            else
            {
                pass._scaleMode =
                    stack.BufferSettings.bicubicRescaling ==
                    CameraBufferSettings.BicubicRescalingMode.UpAndDown ||
                    (stack.BufferSettings.bicubicRescaling ==
                     CameraBufferSettings.BicubicRescalingMode.UpOnly &&
                     stack.BufferSize.x < stack.Camera.pixelWidth)
                        ? ScaleMode.Bicubic
                        : ScaleMode.Linear;
            }

            bool applyFXAA = stack.BufferSettings.fxaa.enabled;
            if (applyFXAA || pass._scaleMode != ScaleMode.None)
            {
                var desc = new TextureDesc(stack.BufferSize.x, stack.BufferSize.y)
                {
                    colorFormat = ColorFormat,
                    filterMode = FilterMode.Bilinear
                };
                if (applyFXAA)
                {
                    desc.name = "Color Grading Result";
                    pass._colorGradingResult = builder.CreateTransientTexture(desc);
                }

                if (pass._scaleMode != ScaleMode.None)
                {
                    desc.name = "Scaled Result";
                    pass._scaledResult = builder.CreateTransientTexture(desc);
                }
            }

            pass._cameraTarget = textures.cameraTarget;
            builder.UseTexture(pass._cameraTarget, AccessFlags.ReadWrite);
            builder.SetRenderFunc<PostFXPass>(
                static (pass, context) => pass.Render(context));

            return colorLUT;
        }
    }
}
