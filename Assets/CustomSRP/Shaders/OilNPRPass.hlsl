#ifndef CUSTOM_OIL_NPR_PASS_INCLUDED
#define CUSTOM_OIL_NPR_PASS_INCLUDED

#include "OilNPRLighting.hlsl"

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
    float3 normalWS : VAR_NORMAL;
    float2 baseUV : VAR_BASE_UV;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

Varyings OilNPRPassVertex(Attributes input)
{
    Varyings output;
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_TRANSFER_INSTANCE_ID(input, output);

    output.positionWS = TransformObjectToWorld(input.positionOS);
    output.positionCS_SS = TransformWorldToHClip(output.positionWS);
    output.normalWS = TransformObjectToWorldNormal(input.normalOS);
    output.baseUV = TransformBaseUV(input.baseUV);
    return output;
}

half4 OilNPRPassFragment(Varyings input, bool isFrontFace : SV_IsFrontFace) : SV_TARGET
{
    UNITY_SETUP_INSTANCE_ID(input);

    InputConfig config = GetInputConfig(input.positionCS_SS, input.baseUV);

    half4 base = (half4)GetBase(config);
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
    surface.smoothness = 0.0;
    surface.fresnelStrength = 0.0;
#if defined(OIL_MOBILE_PERF)
    surface.dither = 0.5;
#else
    surface.dither = InterleavedGradientNoise(config.fragment.positionSS, 0);
#endif
    surface.renderingLayerMask = asuint(unity_RenderingLayer.x);

    half brush = SampleShadowBrush(input.positionWS, config.baseUV);

#if defined(OIL_MOBILE_PERF)
    // Quantize bands only — no dither / rim / weave / fog-canvas.
    half3 color = OilNPRLighting(
        surface,
        albedo,
        OilPeriodAmbient((half3)_AmbientColor.rgb),
        half3(0.42h, 0.48h, 0.72h),
        half3(0.62h, 0.45h, 0.32h),
        half3(0.0h, 0.0h, 0.0h),
        0.0h,
        (half)_ShadeSteps,
        0.0h,
        (half)_ShadowLift,
        (half)_ShadeLift,
        brush,
        (half)_ShadowWobble,
        0.0h,
        config.fragment.screenUV);
    color = (half3)ApplyOilFog((float3)color, surface.depth, brush);
#else
    half3 color = OilNPRLighting(
        surface,
        albedo,
        OilPeriodAmbient((half3)_AmbientColor.rgb),
        half3(0.42h, 0.48h, 0.72h),
        half3(0.62h, 0.45h, 0.32h),
        half3(0.0h, 0.0h, 0.0h),
        0.0h,
        (half)_ShadeSteps,
        paintRim,
        (half)_ShadowLift,
        (half)_ShadeLift,
        brush,
        (half)_ShadowWobble,
        0.2h,
        config.fragment.screenUV);
    color = OilWeaveTint(color, input.positionWS.xz * 0.15);
    color = (half3)ApplyOilFog((float3)color, surface.depth, brush, input.positionWS);
#endif
    return half4(color, alpha);
}

#endif
