using Game;
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
            FinalSampler = new ProfilingSampler("Final Post FX"),
            ComposeFinalSampler = new ProfilingSampler("Oil Compose Final"),
            HqFinalSampler = new ProfilingSampler("Oil HQ Final"),
            RainFinalSampler = new ProfilingSampler("Oil Rain Final");

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

        enum MergedMode { None, ComposeFinal, HQFinal, RainFinal }

        ScaleMode _scaleMode;
        MergedMode _mergedMode;
        TextureHandle _colorSource;
        TextureHandle _oilHalf;
        TextureHandle _colorGradingResult;
        TextureHandle _scaledResult;
        TextureHandle _cameraTarget;
        TextureHandle _rainDepth;
        OilFilterPass.OilParams _oilParams;
        OilRainSettings _rainSettings;

        void ConfigureFXAA(UnsafeCommandBuffer buffer)
        {
            PostFXSettings.FXAASettings fxaa = _stack.Settings.FXAA;
            if (fxaa.quality == PostFXSettings.FXAASettings.Quality.Low)
            {
                buffer.SetKeyword(fxaaLowKeyword, true);
                buffer.SetKeyword(fxaaMediumKeyword, false);
            }
            else if (fxaa.quality == PostFXSettings.FXAASettings.Quality.Medium)
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

        void BindCanvasAndScale(UnsafeCommandBuffer buffer)
        {
            buffer.SetGlobalFloat(finalSrcBlendId, 1f);
            buffer.SetGlobalFloat(finalDstBlendId, 0f);
            PostFXSettings.CanvasSettings canvas = _stack.Settings.Canvas;
            float grain = canvas.grain;
            float weave = canvas.weave;
            if (canvas.enabled
                && Application.isMobilePlatform
                && DeviceGraphicsPolicy.Current == DeviceGraphicsClass.Low)
            {
                grain *= 0.75f;
                weave *= 0.75f;
            }

            buffer.SetGlobalVector(
                canvasGrainId,
                canvas.enabled
                    ? new Vector4(grain, weave, 0f, 0f)
                    : Vector4.zero);

            buffer.SetGlobalFloat(
                copyBicubicId, _scaleMode == ScaleMode.Bicubic ? 1f : 0f);
        }

        void RenderMerged(UnsafeGraphContext context)
        {
            UnsafeCommandBuffer buffer = context.cmd;
            ColorLUTPass.BindGlobals(buffer);
            BindCanvasAndScale(buffer);

            switch (_mergedMode)
            {
                case MergedMode.ComposeFinal:
                    OilFilterPass.BindGlobals(
                        buffer, _stack.BufferSize, ultraLite: true, _oilParams);
                    buffer.SetGlobalTexture(fxSource2Id, _oilHalf);
                    _stack.DrawFinal(
                        buffer, _colorSource, _cameraTarget, Pass.OilFilterComposeFinal);
                    break;
                case MergedMode.HQFinal:
                    OilFilterPass.BindGlobals(
                        buffer, _stack.BufferSize, ultraLite: false, _oilParams);
                    _stack.DrawFinal(
                        buffer, _colorSource, _cameraTarget, Pass.OilFilterHQFinal);
                    break;
                case MergedMode.RainFinal:
                    OilRainPostPass.BindGlobals(
                        buffer, _stack.BufferSize, _rainDepth, _rainSettings);
                    _stack.DrawFinal(
                        buffer, _colorSource, _cameraTarget, Pass.OilRainFinal);
                    break;
            }
        }

        void Render(UnsafeGraphContext context)
        {
            UnsafeCommandBuffer buffer = context.cmd;
            ColorLUTPass.BindGlobals(buffer);
            BindCanvasAndScale(buffer);

            if (_scaleMode != ScaleMode.None)
            {
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

        static ScaleMode ResolveScaleMode(PostFXStack stack)
        {
            if (stack.BufferSize.x == stack.Camera.pixelWidth)
                return ScaleMode.None;

            return stack.BufferSettings.bicubicRescaling ==
                   CameraBufferSettings.BicubicRescalingMode.UpAndDown ||
                   (stack.BufferSettings.bicubicRescaling ==
                    CameraBufferSettings.BicubicRescalingMode.UpOnly &&
                    stack.BufferSize.x < stack.Camera.pixelWidth)
                ? ScaleMode.Bicubic
                : ScaleMode.Linear;
        }

        static void RecordMergedFinal(
            RenderGraph renderGraph,
            PostFXStack stack,
            ScaleMode scaleMode,
            MergedMode mode,
            TextureHandle colorSource,
            TextureHandle oilHalf,
            TextureHandle colorLUT,
            in CameraRendererTextures textures,
            in OilFilterPass.OilParams oilParams,
            OilRainSettings rainSettings,
            TextureHandle rainDepth)
        {
            ProfilingSampler sampler = mode switch
            {
                MergedMode.ComposeFinal => ComposeFinalSampler,
                MergedMode.HQFinal => HqFinalSampler,
                _ => RainFinalSampler
            };

            using IUnsafeRenderGraphBuilder builder = renderGraph.AddUnsafePass(
                sampler.name, out PostFXPass pass, sampler);
            pass._stack = stack;
            pass._scaleMode = scaleMode;
            pass._mergedMode = mode;
            pass._colorSource = colorSource;
            pass._oilHalf = oilHalf;
            pass._oilParams = oilParams;
            pass._rainSettings = rainSettings;
            pass._rainDepth = rainDepth;
            pass._cameraTarget = textures.cameraTarget;
            builder.UseTexture(colorSource);
            if (oilHalf.IsValid())
                builder.UseTexture(oilHalf);
            if (colorLUT.IsValid())
                builder.UseTexture(colorLUT);
            if (oilParams.HasDepth)
                builder.UseTexture(oilParams.Depth);
            if (rainDepth.IsValid())
                builder.UseTexture(rainDepth);
            builder.UseTexture(pass._cameraTarget, AccessFlags.ReadWrite);
            builder.AllowGlobalStateModification(true);
            builder.SetRenderFunc<PostFXPass>(
                static (p, context) => p.RenderMerged(context));
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

            bool applyFXAA =
                stack.Settings.FXAA.enabled && stack.AllowFXAA;
            // Mobile: FXAA is expensive on tile GPUs; rely on render-scale + bicubic instead.
            if (Application.isMobilePlatform)
                applyFXAA = false;

            bool rain = OilRainPostPass.IsActive(rainSettings);
            // Merge Oil/Rain into Final only when FXAA is off (avoid multi-LUT neighbor fetches).
            bool mergeFinal = !applyFXAA;
            bool warp = OilWarpPass.IsActive(stack);
            ScaleMode scaleMode = ResolveScaleMode(stack);

            TextureHandle bloomSource = BloomPass.Record(renderGraph, stack, textures);
            TextureHandle colorSource = bloomSource;
            TextureHandle oilHalf = default;
            bool oilEnabled = OilFilterPass.TryGetParams(
                stack, textures, out OilFilterPass.OilParams oilParams);
            bool ultraLite = OilFilterPass.UseUltraLite;

            // OilWarp needs a resolved color RT after OilFilter; skip oil→Final merge when warping.
            bool oilMergeOk = mergeFinal && !warp;

            if (oilEnabled)
            {
                if (oilMergeOk)
                {
                    if (ultraLite)
                    {
                        oilHalf = OilFilterPass.RecordHalf(
                            renderGraph, stack, bloomSource, oilParams);
                        if (rain)
                        {
                            colorSource = OilFilterPass.RecordCompose(
                                renderGraph, stack, bloomSource, oilHalf, oilParams);
                        }
                    }
                    else if (rain)
                    {
                        colorSource = OilFilterPass.RecordHQ(
                            renderGraph, stack, bloomSource, oilParams);
                    }
                }
                else
                {
                    colorSource = OilFilterPass.Record(
                        renderGraph, stack, bloomSource, textures);
                }
            }

            if (warp)
                colorSource = OilWarpPass.Record(renderGraph, stack, colorSource);

            // FXAA path keeps a dedicated rain intermediate before Final.
            if (!mergeFinal && rain)
            {
                colorSource = OilRainPostPass.Record(
                    renderGraph, stack, colorSource, textures, rainSettings);
            }

            TextureHandle colorLUT = ColorLUTPass.Record(
                renderGraph, stack, colorLUTResolution);

            if (mergeFinal)
            {
                if (oilEnabled && !rain && !warp)
                {
                    if (ultraLite)
                    {
                        RecordMergedFinal(
                            renderGraph, stack, scaleMode, MergedMode.ComposeFinal,
                            bloomSource, oilHalf, colorLUT, textures,
                            oilParams, null, default);
                        return colorLUT;
                    }

                    RecordMergedFinal(
                        renderGraph, stack, scaleMode, MergedMode.HQFinal,
                        bloomSource, default, colorLUT, textures,
                        oilParams, null, default);
                    return colorLUT;
                }

                if (rain)
                {
                    TextureHandle rainDepth = OilRainPostPass.ResolveDepth(textures);
                    RecordMergedFinal(
                        renderGraph, stack, scaleMode, MergedMode.RainFinal,
                        colorSource, default, colorLUT, textures,
                        oilParams, rainSettings, rainDepth);
                    return colorLUT;
                }
            }

            using IUnsafeRenderGraphBuilder builder = renderGraph.AddUnsafePass(
                FinalSampler.name, out PostFXPass pass, FinalSampler);
            pass._keepAlpha = keepAlpha;
            pass._stack = stack;
            pass._colorSource = colorSource;
            pass._mergedMode = MergedMode.None;
            pass._scaleMode = scaleMode;
            pass._applyFxaa = applyFXAA;
            builder.UseTexture(colorSource);
            if (colorLUT.IsValid())
                builder.UseTexture(colorLUT);

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
            builder.AllowGlobalStateModification(true);
            builder.SetRenderFunc<PostFXPass>(
                static (p, context) => p.Render(context));

            return colorLUT;
        }
    }
}
