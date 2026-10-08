#ifndef CUSTOM_OIL_NPR_PASS_INCLUDED
#define CUSTOM_OIL_NPR_PASS_INCLUDED

#include "OilNPRKuwahara.hlsl"
#include "OilNPRLighting.hlsl"

struct Attributes
{
    float3 positionOS : POSITION;
    float3 normalOS : NORMAL;
    float4 tangentOS : TANGENT;
    float2 baseUV : TEXCOORD0;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct Varyings
{
    float4 positionCS_SS : SV_POSITION;
    float3 positionWS : VAR_POSITION;
    float3 normalWS : VAR_NORMAL;
    float4 tangentWS : VAR_TANGENT;
    float2 baseUV : VAR_BASE_UV;
    float2 canvasUV : VAR_CANVAS_UV;
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
    output.tangentWS = float4(
        TransformObjectToWorldDir(input.tangentOS.xyz), input.tangentOS.w);
    output.baseUV = TransformBaseUV(input.baseUV);
    output.canvasUV = TransformCanvasUV(input.baseUV);
    return output;
}

half4 OilNPRPassFragment(Varyings input) : SV_TARGET
{
    UNITY_SETUP_INSTANCE_ID(input);

    InputConfig config = GetInputConfig(
        input.positionCS_SS, input.baseUV, input.canvasUV);

#if defined(_KUWAHARA_ON)
    half3 albedo = SampleKuwaharaUV(
        config.baseUV, (half)_KuwaharaRadius).rgb * (half3)_BaseColor.rgb;
    half alpha = (half)GetBaseRaw(config.baseUV).a * (half)_BaseColor.a;
#else
    half4 base = (half4)GetBase(config);
    half3 albedo = base.rgb;
    half alpha = base.a;
#endif

#if defined(_CLIPPING)
    clip(alpha - (half)GetCutoff(config));
#endif

    half3 geometricNormal = normalize((half3)input.normalWS);
    half3 normalWS = geometricNormal;
    half thicknessMask = 0.5h;

#if defined(_CANVAS_ON)
    half4 canvas = SampleCanvas(config.canvasUV);
    half3 normalTS = GetCanvasNormalTS(canvas, (half)_CanvasStrength);
    normalWS = normalize((half3)NormalTangentToWorld(
        (float3)normalTS, input.normalWS, input.tangentWS));
    normalWS = normalize(lerp(geometricNormal, normalWS, 0.55h));
    thicknessMask = GetCanvasThicknessMask(canvas);
#endif

    half3 viewDir = normalize((half3)(_WorldSpaceCameraPos - input.positionWS));
    half ndotV = saturate(dot(normalWS, viewDir));
    half paintRim = (1.0h - ndotV) * (half)_PaintThickness * thicknessMask;

    Surface surface;
    surface.position = input.positionWS;
    surface.normal = (float3)normalWS;
    surface.interpolatedNormal = input.normalWS;
    surface.viewDirection = (float3)viewDir;
    surface.depth = -TransformWorldToView(input.positionWS).z;
    surface.color = (float3)albedo;
    surface.alpha = (float)alpha;
    surface.metallic = 0.0;
    surface.occlusion = 1.0;
    surface.smoothness = 0.0;
    surface.fresnelStrength = 0.0;
    surface.dither = InterleavedGradientNoise(config.fragment.positionSS, 0);
    surface.renderingLayerMask = asuint(unity_RenderingLayer.x);

    half brush = SampleShadowBrush(input.positionWS, config.baseUV);
#if defined(_CANVAS_ON)
    brush = saturate(brush * 0.75h + thicknessMask * 0.35h);
#endif

    half3 color = OilNPRLighting(
        surface,
        albedo,
        (half3)_AmbientColor.rgb,
        (half3)_ShadowTint.rgb,
        (half3)_ShadowWarm.rgb,
        (half3)_SpecularColor.rgb,
        (half)_SpecularThreshold,
        (half)_ShadeSteps,
        paintRim,
        (half)_ShadowLift,
        (half)_ShadeLift,
        brush,
        (half)_ShadowWobble);

#if defined(_INTERNAL_EDGE_ON)
    half depthEdge = fwidth((half)surface.depth);
    half normalEdge = length(fwidth(geometricNormal));
    half edge = saturate(depthEdge * 1.25h + normalEdge * 1.25h) * (half)_EdgeStrength;
    edge = smoothstep(0.15h, 0.85h, edge);
    color = lerp(color, (half3)_EdgeColor.rgb, edge * 0.65h);
#endif

    return half4(color, alpha);
}

#endif
