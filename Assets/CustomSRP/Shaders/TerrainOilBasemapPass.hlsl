#ifndef CUSTOM_TERRAIN_OIL_BASEMAP_PASS_INCLUDED
#define CUSTOM_TERRAIN_OIL_BASEMAP_PASS_INCLUDED

#include "OilNPRLighting.hlsl"

TEXTURE2D(_MainTex);
SAMPLER(sampler_MainTex);

TEXTURE2D(_CanvasMap);
SAMPLER(sampler_CanvasMap);

TEXTURE2D(_OutlineBrushMap);
SAMPLER(sampler_OutlineBrushMap);

CBUFFER_START(UnityPerMaterial)
    float4 _MainTex_ST;
    float4 _BaseColor;
    float4 _CanvasMap_ST;
    float4 _OutlineBrushMap_ST;
    float4 _AmbientColor;
    float4 _ShadowTint;
    float4 _ShadowWarm;
    float4 _SpecularColor;
    float _CanvasStrength;
    float _PaintThickness;
    float _ShadeSteps;
    float _ShadeLift;
    float _ShadowLift;
    float _ShadowWobble;
    float _ShadowBrushScale;
    float _SpecularThreshold;
CBUFFER_END

#include "OilShadowBrush.hlsl"

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
    float2 uv : VAR_BASE_UV;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

Varyings TerrainOilBasemapVertex(Attributes input)
{
    Varyings output;
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_TRANSFER_INSTANCE_ID(input, output);
    output.positionWS = TransformObjectToWorld(input.positionOS);
    output.positionCS_SS = TransformWorldToHClip(output.positionWS);
    output.normalWS = TransformObjectToWorldNormal(input.normalOS);
    output.uv = TRANSFORM_TEX(input.texcoord, _MainTex);
    return output;
}

half4 TerrainOilBasemapFragment(Varyings input) : SV_TARGET
{
    UNITY_SETUP_INSTANCE_ID(input);

    half3 albedo = (half3)(SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv).rgb * _BaseColor.rgb);
    half3 geometricNormal = normalize((half3)input.normalWS);
    half3 normalWS = geometricNormal;
    half thicknessMask = 0.5h;

#if defined(_CANVAS_ON)
    float2 canvasUV = input.uv * _CanvasMap_ST.xy + _CanvasMap_ST.zw;
    half4 canvas = (half4)SAMPLE_TEXTURE2D(_CanvasMap, sampler_CanvasMap, canvasUV);
    half2 nxy = canvas.rg * 2.0h - 1.0h;
    half strength = min((half)_CanvasStrength, 0.5h);
    half3 normalTS = normalize(half3(nxy * strength, 1.0h));
    float3 n = normalize(input.normalWS);
    float3 up = abs(n.y) < 0.999 ? float3(0.0, 1.0, 0.0) : float3(1.0, 0.0, 0.0);
    float3 t = normalize(cross(up, n));
    float3 b = cross(n, t);
    float3 canvasN = normalize(t * normalTS.x + b * normalTS.y + n * normalTS.z);
    normalWS = normalize(lerp(geometricNormal, (half3)canvasN, 0.55h));
    thicknessMask = canvas.b;
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

    half brush = SampleOilShadowBrush(input.positionWS, input.uv, 0.008);
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

    return half4(color, 1.0h);
}

#endif
