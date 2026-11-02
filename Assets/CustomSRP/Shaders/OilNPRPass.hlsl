#ifndef CUSTOM_OIL_NPR_PASS_INCLUDED
#define CUSTOM_OIL_NPR_PASS_INCLUDED

#include "OilNPRInput.hlsl"
#include "OilNPRLighting.hlsl"
#include "OilImpastoCore.hlsl"

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

    // 镜子里活下来的岛上树像素写成不透明，水面才按 alpha 叠出浅倒影。
    if (_OilMirrorCapture > 0.5)
        alpha = 1.0h;

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
        half3 paintedBb = ImpastoDeviation(albedo, brush, 0.55h);
        surface.color = (float3)paintedBb;
        half3 bb = OilTreeCrossBillboardShade(
            paintedBb, bakedN, (half)_NormalStrength, surface, input.positionOS,
            config.fragment.screenUV);
#if defined(OIL_MOBILE_PERF)
        bb = (half3)ApplyOilFog((float3)bb, surface.depth, brush);
#else
        bb = OilWeaveTint(bb, input.positionWS.xz * 0.15);
        bb = (half3)ApplyOilFog((float3)bb, surface.depth, brush, input.positionWS);
#endif
        return half4(bb, alpha);
    }

#if defined(OIL_Y_BILLBOARD)
    // Y 片树：全时段植被光照 + 与近树叶同一套 object-Y 油画色带。
    float yBbFrag = _OilYBillboard;
    if (yBbFrag > 0.5)
    {
        half3 bakedN = (half3)SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, config.baseUV).rgb;
        half3 paintedBb = ImpastoDeviation(albedo, brush, 0.55h);
        surface.color = (float3)paintedBb;
        half3 bb = OilTreeCrossBillboardShade(
            paintedBb, bakedN, (half)_NormalStrength, surface, input.positionOS,
            config.fragment.screenUV);
#if defined(OIL_MOBILE_PERF)
        bb = (half3)ApplyOilFog((float3)bb, surface.depth, brush);
#else
        bb = OilWeaveTint(bb, input.positionWS.xz * 0.15);
        bb = (half3)ApplyOilFog((float3)bb, surface.depth, brush, input.positionWS);
#endif
        return half4(bb, alpha);
    }
#endif

    // Near leaves: same vegetation lighting + volume paint as Y-billboard.
    if (_OilFoliage > 0.5)
    {
        half3 painted = OilFoliagePaintAlbedo(albedo, input.positionOS, normalWS);
        painted = ImpastoDeviation(painted, brush, 0.72h);
        surface.color = (float3)painted;
        half3 foliage = OilFoliageLighting(
            surface, painted, (half)_ShadeSteps, config.fragment.screenUV);
#if defined(OIL_MOBILE_PERF)
        foliage = (half3)ApplyOilFog((float3)foliage, surface.depth, brush);
#else
        foliage = OilWeaveTint(foliage, input.positionWS.xz * 0.15);
        foliage = (half3)ApplyOilFog((float3)foliage, surface.depth, brush, input.positionWS);
#endif
        return half4(foliage, alpha);
    }

    // 树干：暖赭体积色 + 更硬油画色块（圆柱明暗），不走叶片绿体积。
    if (_OilBark > 0.5)
    {
#if defined(OIL_MOBILE_PERF)
        surface.dither = InterleavedGradientNoise(config.fragment.positionSS, 0);
#endif
        half3 painted = OilBarkPaintAlbedo(albedo, input.positionOS, normalWS);
        painted = ImpastoDeviation(painted, brush, 0.58h);
        surface.color = (float3)painted;
#if !defined(OIL_MOBILE_PERF)
        half brushDx = (half)ddx((float)brush);
        half brushDz = (half)ddy((float)brush);
        normalWS = ImpastoTiltNormal(normalWS, brushDx, brushDz, 0.45h);
        surface.normal = (float3)normalWS;
#endif
        half3 bark = OilBarkLighting(
            surface, painted, (half)_ShadeSteps, config.fragment.screenUV);
#if !defined(OIL_MOBILE_PERF)
        if (GetDirectionalLightCount() > 0)
        {
            half3 L = (half3)GetDirectionalLightData(0).directionAndMask.xyz;
            half3 H = normalize(L + viewDir);
            bark += ImpastoRidgeSpec(brush, saturate(dot(normalWS, H)), 0.58h)
                * half3(1.0h, 0.92h, 0.78h);
        }
        bark = OilWeaveTint(bark, input.positionWS.xz * 0.15);
        bark = (half3)ApplyOilFog((float3)bark, surface.depth, brush, input.positionWS);
#else
        bark = (half3)ApplyOilFog((float3)bark, surface.depth, brush);
#endif
        return half4(bark, alpha);
    }

    // Wood props / rocks: 恢复改前颜色路径 — 只做轻量 Impasto，不做 PaintStack 压黑。

    albedo = ImpastoDeviation(albedo, brush, 0.52h);
    surface.color = (float3)albedo;
#if !defined(OIL_MOBILE_PERF)
    {
        half brushDx = (half)ddx((float)brush);
        half brushDz = (half)ddy((float)brush);
        normalWS = ImpastoTiltNormal(normalWS, brushDx, brushDz, 0.40h);
        surface.normal = (float3)normalWS;
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
    if (GetDirectionalLightCount() > 0)
    {
        half3 L = (half3)GetDirectionalLightData(0).directionAndMask.xyz;
        half3 H = normalize(L + viewDir);
        color += ImpastoRidgeSpec(brush, saturate(dot(normalWS, H)), 0.52h)
            * lerp(half3(1.0h, 0.96h, 0.88h), (half3)GetDirectionalLightData(0).color.rgb, 0.3h);
    }
    color = OilWeaveTint(color, input.positionWS.xz * 0.15);
    color = (half3)ApplyOilFog((float3)color, surface.depth, brush, input.positionWS);
#endif

    return half4(color, alpha);
}

#endif
