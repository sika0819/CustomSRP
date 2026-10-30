#ifndef CUSTOM_OIL_NPR_PASS_INCLUDED
#define CUSTOM_OIL_NPR_PASS_INCLUDED

#include "OilNPRInput.hlsl"
#include "OilNPRLighting.hlsl"

// Billboard wind: object-space sway so cards stay attached to the trunk pivot.
float3 OilBillboardWindDisplace(float3 positionOS)
{
    float wind = _OilGrassWindStrength;
    if (wind < 0.001)
        return positionOS;

    float gust = 0.7 + 0.3 * sin(_Time.y * 1.5 + positionOS.x * 0.06 + positionOS.z * 0.05);
    float sway = sin(_Time.y * 2.0 + positionOS.x * 0.1 + positionOS.z * 0.08) * 0.3;
    float amp = wind * sway * gust * 0.15;
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
    float crossBb = _OilCrossBillboard;
    float yBb = _OilYBillboard;
    if (crossBb > 0.5)
    {
        positionOS = OilTreeCrossBillboardRotateYaw(
            input.positionOS, input.normalOS, normalOS, normalWS, keep);
    }
    else
    {
#if defined(OIL_Y_BILLBOARD)
        if (yBb > 0.5)
        {
            positionOS = OilTreeYBillboardRotateYaw(
                input.positionOS, input.normalOS, normalOS, normalWS);
        }
        else
#endif
        {
            normalWS = TransformObjectToWorldNormal(input.normalOS);
        }
    }

    if (crossBb > 0.5 || yBb > 0.5)
    {
        positionOS = OilBillboardWindDisplace(positionOS);
    }

    float3 positionWS = TransformObjectToWorld(positionOS);

    output.positionWS = positionWS;
    output.positionOS = positionOS;
    output.positionCS_SS = TransformWorldToHClip(output.positionWS);
    output.normalWS = normalWS;
    output.normalOS = normalOS;
    output.baseUV = TransformBaseUV(input.baseUV);
    output.keepCard = keep;
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
    // and crown RGB doesn't wash to grey (Boluo SAMPLE_TEXTURE2D_BIAS).
    // Far Y-billboard proxies need the same bias — without it, 120m+ trees clip away entirely.
    // Stronger than -0.65: full-rect Y cards rely on clip; soft mips still sparkle while walking.
    float crossBbEarly = _OilCrossBillboard;
    float yBbEarly = _OilYBillboard;
    half4 base;
    if (crossBbEarly > 0.5 || yBbEarly > 0.5)
        base = (half4)SAMPLE_TEXTURE2D_BIAS(_BaseMap, sampler_BaseMap, config.baseUV, -1.0)
            * (half4)_BaseColor;
    else
        base = (half4)GetBase(config);
    half3 albedo = base.rgb;
    half alpha = base.a;

#if defined(_CLIPPING)
    half cutoff = (half)GetCutoff(config);
    if (crossBbEarly > 0.5 || yBbEarly > 0.5)
    {
        // Cap the derivative widen. An uncapped +fwidth kept the whole mip bleed
        // ring once the card was small, and that ring is the distant grey outline.
        half aa = min(max(fwidth(alpha), (half)1e-4), (half)0.08);
        clip(alpha - cutoff + aa);
    }
    else
    {
        clip(alpha - cutoff);
    }
#endif

    // 片树烘焙：贴图颜色 = 近树 _BaseMap * _BaseColor，光照留到运行时同一套模型。
#if defined(OIL_BILLBOARD_ALBEDO_CAPTURE)
    return half4(albedo, alpha);
#endif

    half3 geometricNormal = normalize((half3)input.normalWS);
    // Cull Off (e.g. tent canvas): flip so underside lighting faces the viewer.
    if (!isFrontFace)
    {
        geometricNormal = -geometricNormal;
    }
    half3 normalWS = geometricNormal;

#if defined(OIL_MOBILE_PERF)
    half3 viewDir = half3(0.0h, 0.0h, 1.0h);
#else
    half3 viewDir = normalize((half3)(_WorldSpaceCameraPos - input.positionWS));
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
    float crossBbFrag = _OilCrossBillboard;

    // Far cross-card: vegetation lighting (BoluoUnity LitAlbedoObject).
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

    half yPeriodW = 0.0h;
#if defined(OIL_Y_BILLBOARD)
    // 片树专用变体。朝上烘焙法线；晨昏夜走 OilTreeCrossBillboardShade（植被光照）。
    // 移动端时段收成 0/1，同一像素不叠 NPR 和植被光照。过渡 lerp 只留非移动端。
    float yBbFrag = _OilYBillboard;
    if (yBbFrag > 0.5)
    {
        yPeriodW = (half)OilFoliagePeriodWeight(_OilSkyPeriod);
#if defined(OIL_MOBILE_PERF)
        yPeriodW = yPeriodW >= 0.5h ? 1.0h : 0.0h;
#endif
        half3 bakedN = (half3)SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, config.baseUV).rgb;
        if (yPeriodW >= 0.999h)
        {
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

        half3 litN = OilTreeBillboardLitNormal(bakedN, (half)_NormalStrength);
        surface.normal = (float3)litN;
        surface.interpolatedNormal = (float3)litN;
    }
#endif

#if defined(OIL_MOBILE_PERF)
    surface.dither = InterleavedGradientNoise(config.fragment.positionSS, 0);
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
        0.2h,
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
        0.0h,
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

#if defined(OIL_Y_BILLBOARD) && !defined(OIL_MOBILE_PERF)
    // Mid period (2s dial blend): lerp vegetation ↔ NPR.
    if (yPeriodW > 0.001h)
    {
        half3 bakedN = (half3)SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, config.baseUV).rgb;
        half3 foliage = OilTreeCrossBillboardShade(
            albedo, bakedN, (half)_NormalStrength, surface, config.fragment.screenUV);
        foliage = OilWeaveTint(foliage, input.positionWS.xz * 0.15);
        foliage = (half3)ApplyOilFog((float3)foliage, surface.depth, brush, input.positionWS);
        color = lerp(color, foliage, yPeriodW);
    }
#endif

    return half4(color, alpha);
}

#endif
