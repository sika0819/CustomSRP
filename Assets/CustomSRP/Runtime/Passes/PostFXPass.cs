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
            fxaaConfigId = Shader.PropertyToID("_FXAAConfig"),
            canvasGrainId = Shader.PropertyToID("_CanvasGrain");

        static readonly GlobalKeyword
            fxaaLowKeyword = GlobalKeyword.Create("FXAA_QUALITY_LOW"),
            fxaaMediumKeyword = GlobalKeyword.Create("FXAA_QUALITY_MEDIUM");

        static readonly GraphicsFormat ColorFormat =
            SystemInfo.GetGraphicsFormat(DefaultFormat.LDR);

        PostFXStack _stack;
        bool _keepAlpha;
        bool _applyFxaa;

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
            PostFXSettings.CanvasSettings canvas = _stack.Settings.Canvas;
            // zw.x = canvas cell in backbuffer pixels. 0 keeps the authored 3px tooth.
            // Phone pixels are too small for that tooth; bin to ~1/80 of the short side.
            float canvasCellPx = 0f;
            if (canvas.enabled && Application.isMobilePlatform)
            {
                float shortSide = Mathf.Min(
                    _stack.Camera.pixelWidth, _stack.Camera.pixelHeight);
                canvasCellPx = Mathf.Max(shortSide / 80f, 6f);
            }

            buffer.SetGlobalVector(
                canvasGrainId,
                canvas.enabled
                    ? new Vector4(canvas.grain, canvas.weave, canvasCellPx, 0f)
                    : Vector4.zero);

            if (_scaleMode != ScaleMode.None)
            {
                buffer.SetGlobalFloat(
                    copyBicubicId, _scaleMode == ScaleMode.Bicubic ? 1f : 0f);
                if (_applyFxaa)
                {
                    ConfigureFXAA(buffer);
                    _stack.Draw(
                        buffer, _colorSource, _scaledResult,
                        _keepAlpha ? Pass.FXAA : Pass.FXAAWithLuma);
                    _stack.DrawFinal(
                        buffer, _scaledResult, _cameraTarget, Pass.FinalRescale);
                }
                else
                {
                    _stack.DrawFinal(
                        buffer, _colorSource, _cameraTarget, Pass.FinalRescale);
                }

                return;
            }

            TextureHandle finalSource;
            Pass finalPass;
            if (_applyFxaa)
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

            _stack.DrawFinal(buffer, finalSource, _cameraTarget, finalPass);
        }

        public static TextureHandle Record(
            RenderGraph renderGraph,
            PostFXStack stack,
            int colorLUTResolution,
            bool keepAlpha,
            in CameraRendererTextures textures,
            OilRainSettings rainSettings = null)
        {
            using var _ = new RenderGraphProfilingScope(renderGraph, GroupSampler);

            TextureHandle colorSource = BloomPass.Record(renderGraph, stack, textures);
            colorSource = OilFilterPass.Record(renderGraph, stack, colorSource, textures);
            // Immersive oil UV warp: after the edge filter, before rain and grading.
            // Inactive (strength 0) returns the same handle and allocates nothing.
            colorSource = OilWarpPass.Record(renderGraph, stack, colorSource);

            // OilRain screen-space water ripples run AFTER OilFilter (which likes our
            // strong colour edges), BEFORE the colour grading pass (which would shift hue).
            // Only runs when rain settings present AND post ripples enabled.
            if (rainSettings != null && rainSettings.postRippleEnabled)
            {
                colorSource = OilRainPostPass.Record(
                    renderGraph, stack, colorSource, textures, rainSettings);
            }

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
            // Mobile: FXAA is expensive on tile GPUs; rely on render-scale + bicubic instead.
            if (Application.isMobilePlatform)
                applyFXAA = false;
            pass._applyFxaa = applyFXAA;
            // Scaled output grades inside FinalRescale. An intermediate is only
            // needed when FXAA must run before that upscale.
            if (applyFXAA)
            {
                var desc = new TextureDesc(stack.BufferSize.x, stack.BufferSize.y)
                {
                    colorFormat = ColorFormat,
                    filterMode = FilterMode.Bilinear
                };
                if (pass._scaleMode == ScaleMode.None)
                {
                    desc.name = "Color Grading Result";
                    pass._colorGradingResult = builder.CreateTransientTexture(desc);
                }
                else
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
