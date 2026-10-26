#ifndef CUSTOM_OIL_SHADOW_BRUSH_INCLUDED
#define CUSTOM_OIL_SHADOW_BRUSH_INCLUDED

// Mid soft-restore under OIL_MOBILE_PERF (0=hard cut, 0.5=half brush). Set by DeviceGraphicsPolicy.
#ifndef CUSTOM_OIL_BRUSH_MOBILE_SCALE_DECLARED
#define CUSTOM_OIL_BRUSH_MOBILE_SCALE_DECLARED
float _OilBrushMobileScale;
#endif

// Caller declares _OutlineBrushMap, sampler_OutlineBrushMap,
// _OutlineBrushMap_ST, and _ShadowBrushScale.
// worldFrequency is UV per meter. Plane and terrain receivers both use 0.25.
half SampleOilShadowBrush(float3 positionWS, float2 uv, float worldFrequency)
{
#if defined(SHADER_STAGE_FRAGMENT)
    float2 brushUV =
        uv * _OutlineBrushMap_ST.xy * _ShadowBrushScale +
        _OutlineBrushMap_ST.zw +
        positionWS.xz * (_ShadowBrushScale * worldFrequency);

    // Hold a little detail at distance, then let mips soften instead of staying sharp noise.
    float2 dx = ddx(brushUV);
    float2 dy = ddy(brushUV);
    float span = max(length(dx), length(dy));
    float lodScale = max(span * 10.0, 1.0);
    dx /= lodScale;
    dy /= lodScale;

    half a = (half)SAMPLE_TEXTURE2D_GRAD(
        _OutlineBrushMap, sampler_OutlineBrushMap, brushUV, dx, dy).r;
    float2 uv2 = brushUV * 1.73 + float2(0.37, 0.11);
    half b = (half)SAMPLE_TEXTURE2D_GRAD(
        _OutlineBrushMap, sampler_OutlineBrushMap, uv2, dx * 1.73, dy * 1.73).r;
    return saturate(a * 0.65h + b * 0.45h);
#else
    return 0.5h;
#endif
}

// Full-screen receivers (terrain). One hardware-filtered tap; the mask already holds the stroke.
half SampleOilShadowBrushCheap(float3 positionWS, float2 uv, float worldFrequency)
{
#if defined(SHADER_STAGE_FRAGMENT)
    float2 brushUV =
        uv * _OutlineBrushMap_ST.xy * _ShadowBrushScale +
        _OutlineBrushMap_ST.zw +
        positionWS.xz * (_ShadowBrushScale * worldFrequency);
    return (half)SAMPLE_TEXTURE2D(_OutlineBrushMap, sampler_OutlineBrushMap, brushUV).r;
#else
    return 0.5h;
#endif
}

#endif
