#ifndef CUSTOM_TERRAIN_OIL_NPR_PASS_INCLUDED
#define CUSTOM_TERRAIN_OIL_NPR_PASS_INCLUDED

#include "OilNPRLighting.hlsl"
#include "TerrainOilNPRInput.hlsl"

struct Attributes
{
    float3 positionOS : POSITION;
    float3 normalOS : NORMAL;
    float2 texcoord : TEXCOORD0;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct Varyings
{
    float4 positionCS_SS : SV_POSITION;
    float3 positionWS : VAR_POSITION;
    float3 normalWS : VAR_NORMAL;
    float2 controlUV : VAR_CONTROL_UV;
    float2 canvasUV : VAR_CANVAS_UV;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

Varyings TerrainOilNPRPassVertex(Attributes input)
{
    Varyings output;
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_TRANSFER_INSTANCE_ID(input, output);

    output.positionWS = TransformObjectToWorld(input.positionOS);
    output.positionCS_SS = TransformWorldToHClip(output.positionWS);
    output.normalWS = TransformObjectToWorldNormal(input.normalOS);
    output.controlUV = TRANSFORM_TEX(input.texcoord, _Control);
    output.canvasUV = TransformCanvasUV(input.texcoord);
    return output;
}

half4 TerrainOilNPRPassFragment(Varyings input) : SV_TARGET
{
    UNITY_SETUP_INSTANCE_ID(input);

#if defined(_KUWAHARA_ON)
    half3 albedo = SampleKuwaharaTerrain(
        input.controlUV, (half)_KuwaharaRadius);
#else
    half3 albedo = SampleTerrainAlbedo(input.controlUV);
#endif

    half3 geometricNormal = normalize((half3)input.normalWS);
    half3 normalWS = geometricNormal;
    half thicknessMask = 0.5h;

#if defined(_CANVAS_ON)
    half4 canvas = SampleCanvas(input.canvasUV);
    half3 normalTS = GetCanvasNormalTS(canvas, (half)_CanvasStrength);
    float3 canvasN;
    TerrainCanvasToWorld(normalTS, input.normalWS, canvasN);
    normalWS = normalize(lerp(geometricNormal, (half3)canvasN, 0.55h));
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
    surface.alpha = 1.0;
    surface.metallic = 0.0;
    surface.occlusion = 1.0;
    surface.smoothness = 0.0;
    surface.fresnelStrength = 0.0;
    surface.dither = InterleavedGradientNoise(
        GetFragment(input.positionCS_SS).positionSS, 0);
    surface.renderingLayerMask = asuint(unity_RenderingLayer.x);

    half brush = SampleShadowBrush(input.positionWS, input.controlUV);
#if defined(_CANVAS_ON)
    brush = saturate(brush * 0.75h + thicknessMask * 0.35h);
#endif
    OffsetSurfaceForOilShadow(surface, brush, (half)_ShadowWobble);

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
    color = lerp(color, (half3)_EdgeColor.rgb, edge * 0.55h);
#endif

    return half4(color, 1.0h);
}

#endif
