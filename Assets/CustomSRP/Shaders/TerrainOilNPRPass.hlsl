#ifndef CUSTOM_TERRAIN_OIL_NPR_PASS_INCLUDED
#define CUSTOM_TERRAIN_OIL_NPR_PASS_INCLUDED

#include "OilNPRLighting.hlsl"
#include "OilPaintNoise.hlsl"
#include "TerrainOilNPRInput.hlsl"
#include "TerrainInstancing.hlsl"

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
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

Varyings TerrainOilNPRPassVertex(Attributes input)
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
    output.controlUV = TRANSFORM_TEX(texcoord, _Control);
    return output;
}

// Coarser mip. Scenic cameras sit hundreds of meters up; full-res splats only burn bandwidth.
#define TERRAIN_ALBEDO_BIAS 1.0

// Control once, each land-cover albedo once, ridge rock once. UV wobble is a stable cell hash.
half3 SampleTerrainCheap(float2 controlUV, float3 positionWS, float3 normalWS)
{
    float4 control = SAMPLE_TEXTURE2D(_Control, sampler_Control, controlUV);
    float sum4 = dot(control, 1.0);
    float rockW = saturate((1.0 - sum4) * _RidgeAmount);
    control /= max(sum4, 1e-5);

    float n = OilHash21(floor(positionWS.xz * 0.02));
    float2 wobble = (float2(n, frac(n * 13.7)) - 0.5) * 0.16;

    float2 uv0 = TransformTerrainUV(controlUV, _Splat0_ST) + wobble;
    float2 uv1 = TransformTerrainUV(controlUV, _Splat1_ST) + wobble.yx;
    float2 uv2 = TransformTerrainUV(controlUV, _Splat2_ST) + wobble;
    float2 uv3 = TransformTerrainUV(controlUV, _Splat3_ST) + wobble.yx;

    half3 albedo =
        (half3)SAMPLE_TEXTURE2D_BIAS(_Splat0, sampler_Splat0, uv0, TERRAIN_ALBEDO_BIAS).rgb * (half)control.r +
        (half3)SAMPLE_TEXTURE2D_BIAS(_Splat1, sampler_Splat0, uv1, TERRAIN_ALBEDO_BIAS).rgb * (half)control.g +
        (half3)SAMPLE_TEXTURE2D_BIAS(_Splat2, sampler_Splat0, uv2, TERRAIN_ALBEDO_BIAS).rgb * (half)control.b +
        (half3)SAMPLE_TEXTURE2D_BIAS(_Splat3, sampler_Splat0, uv3, TERRAIN_ALBEDO_BIAS).rgb * (half)control.a;

    float slope = 1.0 - saturate(normalize(normalWS).y);
    float height01 = saturate(positionWS.y / max(_TerrainHeight, 1.0));
    float ridge = smoothstep(0.2, 0.55, slope);
    ridge = max(ridge, smoothstep(0.42, 0.72, height01) * smoothstep(0.1, 0.28, slope));
    ridge *= _RidgeAmount * saturate(control.g + control.b);
    ridge = smoothstep(0.08, 0.92, ridge);

    float2 uvR = controlUV * _RidgeRockMap_ST.xy + _RidgeRockMap_ST.zw + wobble;
    half3 rock = (half3)SAMPLE_TEXTURE2D_BIAS(_RidgeRockMap, sampler_RidgeRockMap, uvR, TERRAIN_ALBEDO_BIAS).rgb;
    albedo = lerp(albedo, rock, (half)saturate(ridge + rockW));

    half climate = (half)OilHash21(floor(positionWS.xz * 0.002));
    half3 warm = albedo * half3(1.06h, 1.02h, 0.9h);
    half3 cool = albedo * half3(0.9h, 0.97h, 1.05h);
    albedo = lerp(cool, warm, climate);
    return albedo * (half3)_BaseColor.rgb;
}

half4 TerrainOilNPRPassFragment(Varyings input) : SV_TARGET
{
    UNITY_SETUP_INSTANCE_ID(input);

    half3 albedo = SampleTerrainCheap(input.controlUV, input.positionWS, input.normalWS);
    half3 normalWS = normalize((half3)input.normalWS);
    half3 viewDir = normalize((half3)(_WorldSpaceCameraPos - input.positionWS));

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

    half brush = (half)OilHash21(floor(input.positionWS.xz * 0.05));
    half3 color = OilNPRLighting(
        surface,
        albedo,
        OilPeriodAmbient((half3)_AmbientColor.rgb),
        (half3)_ShadowTint.rgb,
        (half3)_ShadowWarm.rgb,
        (half3)_SpecularColor.rgb,
        (half)_SpecularThreshold,
        (half)_ShadeSteps,
        0.0h,
        (half)_ShadowLift,
        (half)_ShadeLift,
        brush,
        (half)_ShadowWobble,
        (half)_OilDetail);

    color = OilWeaveTint(color, input.positionWS.xz * 0.15);
    return half4(saturate(color), 1.0h);
}

#endif
