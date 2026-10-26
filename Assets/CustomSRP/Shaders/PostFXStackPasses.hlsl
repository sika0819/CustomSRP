#ifndef CUSTOM_POST_FX_PASSES_INCLUDED
#define CUSTOM_POST_FX_PASSES_INCLUDED

#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Color.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Filtering.hlsl"

TEXTURE2D(_PostFXSource);
TEXTURE2D(_PostFXSource2);

float4 _PostFXSource_TexelSize;

float4 GetSourceTexelSize()
{
    return _PostFXSource_TexelSize;
}

float4 GetSource(float2 screenUV)
{
    return SAMPLE_TEXTURE2D_LOD(_PostFXSource, sampler_linear_clamp, screenUV, 0);
}

float4 GetSourceBicubic(float2 screenUV)
{
    return SampleTexture2DBicubic(
        TEXTURE2D_ARGS(_PostFXSource, sampler_linear_clamp),
        screenUV,
        _PostFXSource_TexelSize.zwxy,
        1.0,
        0.0);
}

float4 GetSource2(float2 screenUV)
{
    return SAMPLE_TEXTURE2D_LOD(_PostFXSource2, sampler_linear_clamp, screenUV, 0);
}

struct Varyings
{
    float4 positionCS : SV_POSITION;
    float2 screenUV : VAR_SCREEN_UV;
};

Varyings DefaultPassVertex(uint vertexID : SV_VertexID)
{
    Varyings output;
    output.positionCS = float4(
        vertexID <= 1 ? -1.0 : 3.0,
        vertexID == 1 ? 3.0 : -1.0,
        0.0,
        1.0);
    output.screenUV = float2(
        vertexID <= 1 ? 0.0 : 2.0,
        vertexID == 1 ? 2.0 : 0.0);
    if (_ProjectionParams.x < 0.0)
    {
        output.screenUV.y = 1.0 - output.screenUV.y;
    }

    return output;
}

bool _BloomBicubicUpsampling;
float _BloomIntensity;

float4 BloomAddPassFragment(Varyings input) : SV_TARGET
{
    float3 lowRes;
    if (_BloomBicubicUpsampling)
    {
        lowRes = GetSourceBicubic(input.screenUV).rgb;
    }
    else
    {
        lowRes = GetSource(input.screenUV).rgb;
    }

    float4 highRes = GetSource2(input.screenUV);
    return float4(lowRes * _BloomIntensity + highRes.rgb, highRes.a);
}

float4 BloomHorizontalPassFragment(Varyings input) : SV_TARGET
{
    float3 color = 0.0;
    float offsets[] =
    {
        -4.0, -3.0, -2.0, -1.0, 0.0, 1.0, 2.0, 3.0, 4.0
    };
    float weights[] =
    {
        0.01621622, 0.05405405, 0.12162162, 0.19459459, 0.22702703,
        0.19459459, 0.12162162, 0.05405405, 0.01621622
    };
    for (int i = 0; i < 9; i++)
    {
        float offset = offsets[i] * 2.0 * GetSourceTexelSize().x;
        color += GetSource(input.screenUV + float2(offset, 0.0)).rgb * weights[i];
    }

    return float4(color, 1.0);
}

float4 _BloomThreshold;

float3 ApplyBloomThreshold(float3 color)
{
    float brightness = Max3(color.r, color.g, color.b);
    float soft = brightness + _BloomThreshold.y;
    soft = clamp(soft, 0.0, _BloomThreshold.z);
    soft = soft * soft * _BloomThreshold.w;
    float contribution = max(soft, brightness - _BloomThreshold.x);
    contribution /= max(brightness, 0.00001);
    return color * contribution;
}

float4 BloomPrefilterPassFragment(Varyings input) : SV_TARGET
{
    float3 color = ApplyBloomThreshold(GetSource(input.screenUV).rgb);
    return float4(color, 1.0);
}

float4 BloomPrefilterFirefliesPassFragment(Varyings input) : SV_TARGET
{
    float3 color = 0.0;
    float weightSum = 0.0;
    float2 offsets[] =
    {
        float2(0.0, 0.0),
        float2(-1.0, -1.0), float2(-1.0, 1.0), float2(1.0, -1.0), float2(1.0, 1.0)
    };
    for (int i = 0; i < 5; i++)
    {
        float3 c =
            GetSource(input.screenUV + offsets[i] * GetSourceTexelSize().xy * 2.0).rgb;
        c = ApplyBloomThreshold(c);
        float w = 1.0 / (Luminance(c) + 1.0);
        color += c * w;
        weightSum += w;
    }

    color /= weightSum;
    return float4(color, 1.0);
}

float4 BloomScatterPassFragment(Varyings input) : SV_TARGET
{
    float3 lowRes;
    if (_BloomBicubicUpsampling)
    {
        lowRes = GetSourceBicubic(input.screenUV).rgb;
    }
    else
    {
        lowRes = GetSource(input.screenUV).rgb;
    }

    float3 highRes = GetSource2(input.screenUV).rgb;
    return float4(lerp(highRes, lowRes, _BloomIntensity), 1.0);
}

float4 BloomScatterFinalPassFragment(Varyings input) : SV_TARGET
{
    float3 lowRes;
    if (_BloomBicubicUpsampling)
    {
        lowRes = GetSourceBicubic(input.screenUV).rgb;
    }
    else
    {
        lowRes = GetSource(input.screenUV).rgb;
    }

    float4 highRes = GetSource2(input.screenUV);
    lowRes += highRes.rgb - ApplyBloomThreshold(highRes.rgb);
    return float4(lerp(highRes.rgb, lowRes, _BloomIntensity), highRes.a);
}

float4 BloomVerticalPassFragment(Varyings input) : SV_TARGET
{
    float3 color = 0.0;
    float offsets[] =
    {
        -3.23076923, -1.38461538, 0.0, 1.38461538, 3.23076923
    };
    float weights[] =
    {
        0.07027027, 0.31621622, 0.22702703, 0.31621622, 0.07027027
    };
    for (int i = 0; i < 5; i++)
    {
        float offset = offsets[i] * GetSourceTexelSize().y;
        color += GetSource(input.screenUV + float2(0.0, offset)).rgb * weights[i];
    }

    return float4(color, 1.0);
}

float4 CopyPassFragment(Varyings input) : SV_TARGET
{
    return GetSource(input.screenUV);
}

float4 _ColorGradingLUTParameters;

TEXTURE3D(_ColorGradingLUT);

float3 ApplyColorGradingLUT(float3 color)
{
    return ApplyLut3D(
        TEXTURE3D_ARGS(_ColorGradingLUT, sampler_linear_clamp),
        saturate(_ColorGradingLUTParameters.x ? LinearToLogC(color) : color),
        _ColorGradingLUTParameters.yz);
}

float2 _CanvasGrain;

float CanvasHash(float2 p)
{
    float3 p3 = frac(float3(p.xyx) * 0.1031);
    p3 += dot(p3, p3.yzx + 33.33);
    return frac((p3.x + p3.y) * p3.z);
}

// Linen tooth after LUT. Old path was ~±1% and mid-gated to luma≈0.42 → invisible.
float3 ApplyCanvasWeave(float3 color, float2 uv)
{
    if (_CanvasGrain.x <= 0.0001 && _CanvasGrain.y <= 0.0001)
        return color;

    // Thread pitch ~2.2 px at native res; readable on phone without looking like noise.
    float2 px = uv * _ScreenParams.xy;
    float warp = sin(px.x * 2.85);
    float weft = sin(px.y * 2.85);
    // Cross ridges where warp/weft meet — the “canvas” read.
    float linen = warp * weft;
    float threads = (warp + weft) * 0.5;
    float n = CanvasHash(floor(px * 0.5)) * 2.0 - 1.0;

    float luma = dot(color, float3(0.299, 0.587, 0.114));
    // Broad tone coverage; only taper pure black / clipped white a little.
    float mid = saturate(luma * 4.0) * saturate((1.05 - luma) * 3.5);
    mid = max(mid, 0.45);

    float grain = n * _CanvasGrain.x;
    float weave = (linen * 0.65 + threads * 0.35) * _CanvasGrain.y;
    float delta = (grain + weave) * mid;
    // Multiply alone vanishes in shadows; add a little absolute tooth.
    return saturate(color * (1.0 + delta) + delta * 0.22);
}

float3 FinalizePostColor(float3 color, float2 uv)
{
    return ApplyCanvasWeave(ApplyColorGradingLUT(color), uv);
}

float4 ApplyColorGradingPassFragment(Varyings input) : SV_TARGET
{
    float4 color = GetSource(input.screenUV);
    color.rgb = FinalizePostColor(color.rgb, input.screenUV);
    return color;
}

float4 ApplyColorGradingWithLumaPassFragment(Varyings input) : SV_TARGET
{
    float4 color = GetSource(input.screenUV);
    color.rgb = FinalizePostColor(color.rgb, input.screenUV);
    color.a = sqrt(Luminance(color.rgb));
    return color;
}

bool _CopyBicubic;

float4 FinalPassFragmentRescale(Varyings input) : SV_TARGET
{
    float4 color = _CopyBicubic
        ? GetSourceBicubic(input.screenUV)
        : GetSource(input.screenUV);
    color.rgb = FinalizePostColor(color.rgb, input.screenUV);
    return color;
}

#include "OilFilterPass.hlsl"
#include "OilWarpPass.hlsl"

// =============================================================================
// OilRainPassFragment — screen-space water surface ripple post-process.
// Picks up world-space ripple events and renders expanding rings in screen space.
// Cheap: ~2 multiply-adds per pixel, fades out at 3 seconds.
// =============================================================================

TEXTURE2D(_CameraDepthTexture_ForRain);
SAMPLER(sampler_CameraDepthTexture_ForRain);
float4 _CameraDepthTexture_TexelSize;
float4x4 _InverseViewProjectionMatrix;
float4x4 _ViewProjectionMatrix;

#define OIL_RAIN_MAX_RIPPLES 8
float4 _OilRainRipplePositions[OIL_RAIN_MAX_RIPPLES]; // xyz = world pos, w = birth time
float  _OilRainRippleWorldSize;
float  _OilRainRippleExpandSpeed;
float  _OilRainRippleOpacity;
float  _OilRainRippleCount;

float OilRainRingAt(float2 uv, float4 ripple, float time)
{
    // Project world-space ripple centre to screen UV
    float4 clip = mul(_ViewProjectionMatrix, float4(ripple.xyz, 1.0));
    float2 rippleUV = (clip.xy / clip.w) * 0.5 + 0.5;

    // Skip behind-the-camera ripples
    if (clip.w <= 0.0)
        return 0.0;

    float dist = length(uv - rippleUV);
    float age = time - ripple.w;
    if (age < 0.0 || age > 3.0)
        return 0.0;

    // Ring radius grows over time, normalised to NDC space
    float ringRadius = clamp(age * _OilRainRippleExpandSpeed / _OilRainRippleWorldSize, 0.0, 0.5);
    float ringWidth = 0.012;

    float ring = smoothstep(ringRadius - ringWidth, ringRadius, dist)
               - smoothstep(ringRadius, ringRadius + ringWidth, dist);

    float fade = saturate(1.0 - age / 3.0);
    return ring * fade;
}

float3 ApplyOilRainRipples(float3 color, float2 uv)
{
    float rawDepth = SAMPLE_TEXTURE2D_LOD(
        _CameraDepthTexture_ForRain, sampler_CameraDepthTexture_ForRain, uv, 0).r;
    if (rawDepth >= 0.9999)
        return color;

    float time = _Time.y;
    float accum = 0.0;
    int count = (int)_OilRainRippleCount;

    [loop]
    for (int i = 0; i < OIL_RAIN_MAX_RIPPLES; i++)
    {
        if (i >= count) break;
        accum += OilRainRingAt(uv, _OilRainRipplePositions[i], time);
    }

    accum = saturate(accum) * _OilRainRippleOpacity;
    color += accum * 0.18;
    color = lerp(color, color * float3(0.85, 0.95, 1.05), accum * 0.6);
    return color;
}

float4 OilRainPassFragment(Varyings input) : SV_TARGET
{
    float4 color = GetSource(input.screenUV);
    color.rgb = ApplyOilRainRipples(color.rgb, input.screenUV);
    return color;
}

// Rain + LUT + canvas (+ optional bicubic source) → camera target.
float4 OilRainFinalPassFragment(Varyings input) : SV_TARGET
{
    float4 color = _CopyBicubic
        ? GetSourceBicubic(input.screenUV)
        : GetSource(input.screenUV);
    color.rgb = ApplyOilRainRipples(color.rgb, input.screenUV);
    color.rgb = FinalizePostColor(color.rgb, input.screenUV);
    return color;
}

#endif
