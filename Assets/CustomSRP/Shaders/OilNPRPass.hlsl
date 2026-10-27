#ifndef CUSTOM_OIL_NPR_PASS_INCLUDED
#define CUSTOM_OIL_NPR_PASS_INCLUDED

#include "OilNPRInput.hlsl"
#include "OilNPRLighting.hlsl"

// 树叶风力偏移函数：使用局部空间偏移，树叶只在原地摇摆不离树干。
float3 OilFoliageWindDisplace(float3 positionOS, float3 normalOS, float foliageFlag)
{
    if (foliageFlag < 0.5)
        return positionOS;

    float wind = _OilGrassWindStrength;
    if (wind < 0.001)
        return positionOS;

    // 轻微摆动：只在局部 XZ 方向做小幅偏移，保持与树干的连接。
    float gust = 0.7 + 0.3 * sin(_Time.y * 1.5 + positionOS.x * 0.06 + positionOS.z * 0.05);
    float sway = sin(_Time.y * 2.0 + positionOS.x * 0.1 + positionOS.z * 0.08) * 0.3;
    float amp = wind * sway * gust * 0.15; // 偏移量很小，保持与树干连接
    return positionOS + float3(amp, 0.0, amp * 0.5);
}

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
    float3 positionOS : VAR_POSITION_OS;
    float3 normalWS : VAR_NORMAL;
    float3 normalOS : VAR_NORMAL_OS;
    float2 baseUV : VAR_BASE_UV;
    float keepCard : VAR_KEEP_CARD;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

Varyings OilNPRPassVertex(Attributes input)
{
    Varyings output;
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_TRANSFER_INSTANCE_ID(input, output);

    float3 positionOS = input.positionOS;
    float3 normalOS = input.normalOS;
    float3 normalWS;
    float keep = 1.0;
    // Metal stage-split: flags must be read in vertex or fragment-only
    // CBUFFER members are stripped while this file is still parsed.
    float foliageFlag = OilEffectiveFoliageFlag(_OilFoliage, _OilFoliageSkipDay);
    float crossBb = _OilCrossBillboard;
    float yBb = _OilYBillboard;
    if (crossBb > 0.5)
    {
        positionOS = OilTreeCrossBillboardRotateYaw(
            input.positionOS, input.normalOS, normalOS, normalWS, keep);
    }
    else if (yBb > 0.5)
    {
        positionOS = OilTreeYBillboardRotateYaw(
            input.positionOS, input.normalOS, normalOS, normalWS);
    }
    else
    {
        normalWS = TransformObjectToWorldNormal(input.normalOS);
    }

    // 风力偏移：树叶在局部空间摇摆，不离树干。
    if (foliageFlag > 0.5 || crossBb > 0.5 || yBb > 0.5)
    {
        positionOS = OilFoliageWindDisplace(positionOS, input.normalOS, foliageFlag);
    }

    float3 positionWS = TransformObjectToWorld(positionOS);

    output.positionWS = positionWS;
    output.positionOS = positionOS;
    output.positionCS_SS = TransformWorldToHClip(output.positionWS);
    output.normalWS = normalWS;
    output.normalOS = normalOS;
    output.baseUV = TransformBaseUV(input.baseUV);
    output.keepCard = keep + foliageFlag * 0.0;
    return output;
}

half4 OilNPRPassFragment(Varyings input, bool isFrontFace : SV_IsFrontFace) : SV_TARGET
{
    UNITY_SETUP_INSTANCE_ID(input);
    clip(input.keepCard - 0.5);
    OilBakeClipAboveWater(input.positionWS);

    InputConfig config = GetInputConfig(
        input.positionCS_SS,
        input.baseUV,
        input.positionWS,
        input.positionOS,
        input.normalWS,
        input.normalOS);

    // Billboard atlas: bias before clip so mip-averaged alpha doesn't punch holes
    // and crown RGB doesn't wash to grey (Boluo SAMPLE_TEXTURE2D_BIAS -0.65).
    // Far Y-billboard proxies need the same bias — without it, 120m+ trees clip away entirely.
    float crossBbEarly = _OilCrossBillboard;
    float yBbEarly = _OilYBillboard;
    half4 base;
    if (crossBbEarly > 0.5 || yBbEarly > 0.5)
        base = (half4)SAMPLE_TEXTURE2D_BIAS(_BaseMap, sampler_BaseMap, config.baseUV, -0.65)
            * (half4)_BaseColor;
    else
        base = (half4)GetBase(config);
    half3 albedo = base.rgb;
    half alpha = base.a;

#if defined(_CLIPPING)
    clip(alpha - (half)GetCutoff(config));
#endif

    half3 geometricNormal = normalize((half3)input.normalWS);
    // Cull Off (e.g. tent canvas): flip so underside lighting faces the viewer.
    if (!isFrontFace)
    {
        geometricNormal = -geometricNormal;
    }
    half3 normalWS = geometricNormal;

#if defined(OIL_MOBILE_PERF)
    half paintRim = 0.0h;
    half3 viewDir = half3(0.0h, 0.0h, 1.0h);
#else
    half3 viewDir = normalize((half3)(_WorldSpaceCameraPos - input.positionWS));
    half ndotV = saturate(dot(normalWS, viewDir));
    half paintRim = (1.0h - ndotV) * (half)_PaintThickness * 0.5h;
#endif

    Surface surface;
    surface.position = input.positionWS;
    surface.normal = (float3)normalWS;
    surface.interpolatedNormal = isFrontFace ? input.normalWS : -input.normalWS;
    surface.viewDirection = (float3)viewDir;
    surface.depth = -TransformWorldToView(input.positionWS).z;
    surface.color = (float3)albedo;
    surface.alpha = (float)alpha;
    surface.metallic = 0.0;
    surface.occlusion = 1.0;
    surface.smoothness = (half)_Smoothness;
    surface.fresnelStrength = 0.0;
#if defined(OIL_MOBILE_PERF)
    surface.dither = 0.5;
#else
    surface.dither = InterleavedGradientNoise(config.fragment.positionSS, 0);
#endif
    surface.renderingLayerMask = asuint(unity_RenderingLayer.x);

    half brush = SampleShadowBrush(input.positionWS, config.baseUV);

    // Keep material flags live on Metal fragment stage (vertex-only reads get stripped).
    float foliageFlagFrag = OilEffectiveFoliageFlag(_OilFoliage, _OilFoliageSkipDay);
    float crossBbFrag = _OilCrossBillboard;

    // Far cross-card: same OilFoliageLighting as near leaves (BoluoUnity LitAlbedoObject).
    if (crossBbFrag > 0.5)
    {
#if !defined(OIL_MOBILE_PERF)
        half3 viewBb = normalize((half3)(_WorldSpaceCameraPos - input.positionWS));
        half ndotv = abs(dot(normalWS, viewBb));
        clip(ndotv - (half)_EdgeFade);
#endif
        half3 bakedN = (half3)SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, config.baseUV).rgb;
        half3 bb = OilTreeCrossBillboardShade(
            albedo, bakedN, (half)_NormalStrength, surface, config.fragment.screenUV);
#if defined(OIL_MOBILE_PERF)
        bb = (half3)ApplyOilFog((float3)bb, surface.depth, brush);
#else
        bb = OilWeaveTint(bb, input.positionWS.xz * 0.15);
        bb = (half3)ApplyOilFog((float3)bb, surface.depth, brush, input.positionWS);
#endif
        return half4(bb, alpha);
    }

    // Foliage weight 0..1 (billboard SkipDay follows period; near leaves stay 0/1).
    // Steady 0/1 = single path; mid values lerp foliage↔NPR across the 2s dial blend.
    half foliageW = (half)saturate(foliageFlagFrag);

    if (foliageW >= 0.999h)
    {
        half3 foliage = OilFoliageLighting(
            surface, albedo, (half)_ShadeSteps, config.fragment.screenUV);
#if defined(OIL_MOBILE_PERF)
        foliage = (half3)ApplyOilFog((float3)foliage, surface.depth, brush);
#else
        foliage = OilWeaveTint(foliage, input.positionWS.xz * 0.15);
        foliage = (half3)ApplyOilFog((float3)foliage, surface.depth, brush, input.positionWS);
#endif
        return half4(foliage, alpha);
    }

#if defined(OIL_MOBILE_PERF)
    half3 color = OilNPRLighting(
        surface,
        albedo,
        half3(0.42h, 0.48h, 0.72h),
        half3(0.62h, 0.45h, 0.32h),
        (half3)_SpecColor.rgb,
        0.0h,
        (half)_ShadeSteps,
        0.0h,
        (half)_ShadowLift,
        (half)_ShadeLift,
        brush,
        (half)_ShadowWobble,
        0.0h,
        surface.smoothness,
        config.fragment.screenUV);
    color = (half3)ApplyOilFog((float3)color, surface.depth, brush);
#else
    half3 color = OilNPRLighting(
        surface,
        albedo,
        half3(0.42h, 0.48h, 0.72h),
        half3(0.62h, 0.45h, 0.32h),
        (half3)_SpecColor.rgb,
        0.0h,
        (half)_ShadeSteps,
        paintRim,
        (half)_ShadowLift,
        (half)_ShadeLift,
        brush,
        (half)_ShadowWobble,
        0.2h,
        surface.smoothness,
        config.fragment.screenUV);
    color = OilWeaveTint(color, input.positionWS.xz * 0.15);
    color = (half3)ApplyOilFog((float3)color, surface.depth, brush, input.positionWS);
#endif

    if (foliageW > 0.001h)
    {
        half3 foliage = OilFoliageLighting(
            surface, albedo, (half)_ShadeSteps, config.fragment.screenUV);
#if defined(OIL_MOBILE_PERF)
        foliage = (half3)ApplyOilFog((float3)foliage, surface.depth, brush);
#else
        foliage = OilWeaveTint(foliage, input.positionWS.xz * 0.15);
        foliage = (half3)ApplyOilFog((float3)foliage, surface.depth, brush, input.positionWS);
#endif
        color = lerp(color, foliage, foliageW);
    }

    return half4(color, alpha);
}

#endif
