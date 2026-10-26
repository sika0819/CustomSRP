#ifndef CUSTOM_OIL_OUTLINE_PASS_INCLUDED
#define CUSTOM_OIL_OUTLINE_PASS_INCLUDED

#include "OilNPRInput.hlsl"
#include "OilPaintNoise.hlsl"

// Inverted-hull ink: extrude OS normal, Cull Front, brush break / noise / wobble.
// Foliage cards: hard discard when _Clipping>0 or _Outline=0 (no keyword variant).

struct Attributes
{
    float3 positionOS : POSITION;
    float3 normalOS : NORMAL;
    float2 baseUV : TEXCOORD0;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct Varyings
{
    float4 positionCS_SS : SV_POSITION;
    float3 positionWS : VAR_POSITION;
    float2 baseUV : VAR_BASE_UV;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

float3 OilOutlineSafeNormalOS(float3 normalOS)
{
    float len2 = dot(normalOS, normalOS);
    return len2 > 1e-8 ? normalOS * rsqrt(len2) : float3(0.0, 1.0, 0.0);
}

float3 OilOutlineExtrudeOS(float3 positionOS, float3 normalOS)
{
    float w = _OutlineWidth;
    if (_Outline < 0.5 || w < 1e-5)
        return positionOS;

    float3 n = OilOutlineSafeNormalOS(normalOS);
    float3 positionWS = TransformObjectToWorld(positionOS);

    // 提按：噪声调制线宽。
    float noiseScale = max(_OutlineNoiseScale, 1e-3);
    float noise = OilValueNoise(positionWS.xz * noiseScale);
    float widthMul = 1.0 + (noise - 0.5) * 2.0 * saturate(_OutlineNoise);
    w *= max(widthMul, 0.08);

    // 远距略涨，屏上墨线不至于细成 1px 闪烁（不碰 _ScaledScreenParams）。
    float dist = max(length(_WorldSpaceCameraPos.xyz - positionWS), 1.0);
    float screenW = dist * 0.0032;
    w = min(max(w, screenW), 0.85);

    float3 extruded = positionOS + n * w;

    // 书法侧向抖动：沿切向偏移。
    if (_OutlineWobble > 1e-4)
    {
        float3 up = abs(n.y) < 0.99 ? float3(0.0, 1.0, 0.0) : float3(1.0, 0.0, 0.0);
        float3 tangent = normalize(cross(n, up));
        float wob = OilValueNoise(positionWS.xz * noiseScale * 1.37 + float2(17.0, 9.0));
        wob = (wob - 0.5) * 2.0;
        extruded += tangent * (wob * _OutlineWobble * w * 0.4);
    }

    return extruded;
}

Varyings OilOutlinePassVertex(Attributes input)
{
    Varyings output;
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_TRANSFER_INSTANCE_ID(input, output);

    float3 positionOS = OilOutlineExtrudeOS(input.positionOS, input.normalOS);
    float3 positionWS = TransformObjectToWorld(positionOS);
    output.positionWS = positionWS;
    output.positionCS_SS = TransformWorldToHClip(positionWS);
    output.baseUV = TransformBaseUV(input.baseUV);
    return output;
}

half4 OilOutlinePassFragment(Varyings input) : SV_TARGET
{
    UNITY_SETUP_INSTANCE_ID(input);
    OilBakeClipAboveWater(input.positionWS);

    // Pass toggle / leaf cards — float checks, no keyword dependency.
    if (_Outline < 0.5)
        clip(-1);
    if (_Clipping > 0.5)
        clip(-1);
#if defined(_CLIPPING)
    // Bundle/reimport may re-enable the Pass; keyword path still hard-kills ink.
    clip(-1);
#endif
    if (_OilCrossBillboard > 0.5 || _OilYBillboard > 0.5)
        clip(-1);
    if (_OutlineWidth < 1e-5)
        clip(-1);

    // 断续墨线：笔触遮罩低于 Break 则丢弃。
    float brushScale = max(_OutlineBrushScale, 1e-3);
    float2 brushUV =
        input.baseUV * _OutlineBrushMap_ST.xy * brushScale +
        _OutlineBrushMap_ST.zw +
        input.positionWS.xz * (brushScale * 0.15);
    half brush = (half)SAMPLE_TEXTURE2D(_OutlineBrushMap, sampler_OutlineBrushMap, brushUV).r;
    clip(brush - (half)_OutlineBreak);

    half3 color = (half3)_OutlineColor.rgb;
    float depth = -TransformWorldToView(input.positionWS).z;
#if defined(OIL_MOBILE_PERF)
    color = (half3)ApplyOilFog((float3)color, depth, brush);
#else
    color = (half3)ApplyOilFog((float3)color, depth, brush, input.positionWS);
#endif
    return half4(color, 1.0h);
}

#endif
