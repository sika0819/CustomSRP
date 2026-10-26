#ifndef CUSTOM_TERRAIN_OIL_BASEMAP_PASS_INCLUDED
#define CUSTOM_TERRAIN_OIL_BASEMAP_PASS_INCLUDED

#include "OilNPRLighting.hlsl"
#include "OilPaintNoise.hlsl"
#include "TerrainInstancing.hlsl"

TEXTURE2D(_MainTex);
SAMPLER(sampler_MainTex);

TEXTURE2D(_OutlineBrushMap);
SAMPLER(sampler_OutlineBrushMap);

CBUFFER_START(UnityPerMaterial)
    float4 _MainTex_ST;
    float4 _BaseColor;
    float4 _OutlineBrushMap_ST;
    float4 _AmbientColor;
    float _PaintThickness;
    float _ShadeSteps;
    float _ShadeLift;
    float _ShadowLift;
    float _ShadowWobble;
    float _ShadowBrushScale;
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
    float3 positionOS = input.positionOS;
    float3 normalOS = input.normalOS;
    float2 texcoord = input.texcoord;
    ApplyTerrainInstancing(positionOS, normalOS, texcoord);
    output.positionWS = TransformObjectToWorld(positionOS);
    output.positionCS_SS = TransformWorldToHClip(output.positionWS);
    output.normalWS = TransformObjectToWorldNormal(normalOS);
    output.uv = TRANSFORM_TEX(texcoord, _MainTex);
    return output;
}

half4 TerrainOilBasemapFragment(Varyings input) : SV_TARGET
{
    UNITY_SETUP_INSTANCE_ID(input);
    OilBakeClipAboveWater(input.positionWS);

    half3 albedo = (half3)(SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv).rgb * _BaseColor.rgb);
    half3 geometricNormal = normalize((half3)input.normalWS);
    half slopeShade = saturate((1.0h - geometricNormal.y) * 0.35h);
    albedo *= lerp(1.0h, 0.72h, slopeShade);
    half3 normalWS = geometricNormal;
    half thicknessMask = 0.5h;

    half3 viewDir = normalize((half3)(_WorldSpaceCameraPos - input.positionWS));
    half ndotV = saturate(dot(normalWS, viewDir));
    half paintRim = (1.0h - ndotV) * (half)_PaintThickness * thicknessMask;
#if defined(OIL_MOBILE_PERF)
    paintRim *= (half)_OilBrushMobileScale * 0.5h;
#endif

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
    surface.dither = InterleavedGradientNoise(input.positionCS_SS.xy, 0);
    surface.renderingLayerMask = asuint(unity_RenderingLayer.x);

#if defined(OIL_MOBILE_PERF)
    half brushScale = (half)_OilBrushMobileScale;
    half brush = 0.5h;
    if (brushScale > 0.001h)
    {
        brush = lerp(
            0.5h,
            SampleOilShadowBrushCheap(input.positionWS, input.uv, 0.25),
            brushScale);
    }
#else
    half brush = SampleOilShadowBrushCheap(input.positionWS, input.uv, 0.25);
#endif

    Fragment fragment = GetFragmentScreen(input.positionCS_SS);
    half3 color = OilNPRLighting(
        surface,
        albedo,
        half3(0.36h, 0.46h, 0.62h),
        half3(0.58h, 0.48h, 0.3h),
        half3(0.0h, 0.0h, 0.0h),
        0.0h,
        (half)_ShadeSteps,
        paintRim,
        brush,
        0.2h,
        0.0h,
        fragment.screenUV);

    // Soft day floor only — a fixed pigment max() was lifting night cast umbra to chalk.
    half lightLumBm = 0.0h;
    if (GetDirectionalLightCount() > 0)
    {
        lightLumBm = OilVegMainLightLuma((half3)_DirectionalLightData[0].color.rgb);
    }
    half nightBm = OilVegetationNightAmount(lightLumBm);
    half3 shadowFloor = albedo * half3(0.18h, 0.12h, 0.26h) + half3(0.015h, 0.01h, 0.03h);
    color = max(color, shadowFloor * (1.0h - nightBm * 0.92h));
    color = min(color, albedo * 1.2h + half3(0.06h, 0.05h, 0.04h));
#if !defined(OIL_MOBILE_PERF)
    color = OilWeaveTint(color, input.positionWS.xz * 0.15);
    color = (half3)ApplyOilFog((float3)color, surface.depth, brush, input.positionWS);
#else
    color = OilWeaveTintMobile(color, input.positionWS.xz * 0.15);
    color = (half3)ApplyOilFog((float3)color, surface.depth, brush);
#endif
    return half4(saturate(color), 1.0h);
}

#endif
