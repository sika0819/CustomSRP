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

struct TerrainOilSample
{
    half3 albedo;
    half3 normalWS;
    half occlusion;
    half smoothness;
};

// Control once, each land-cover albedo once, ridge rock once. UV wobble is a stable cell hash.
// Layer normals and masks share those UVs so the bump and the height blend sit on the paint.
TerrainOilSample SampleTerrainCheap(float2 controlUV, float3 positionWS, float3 geomNormalWS)
{
    TerrainOilSample s;
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

    half4 m0 = SampleLayerMask(TEXTURE2D_ARGS(_Mask0, sampler_Splat0), uv0, _LayerHasMask0, _MaskMapRemapScale0, _MaskMapRemapOffset0);
    half4 m1 = SampleLayerMask(TEXTURE2D_ARGS(_Mask1, sampler_Splat0), uv1, _LayerHasMask1, _MaskMapRemapScale1, _MaskMapRemapOffset1);
    half4 m2 = SampleLayerMask(TEXTURE2D_ARGS(_Mask2, sampler_Splat0), uv2, _LayerHasMask2, _MaskMapRemapScale2, _MaskMapRemapOffset2);
    half4 m3 = SampleLayerMask(TEXTURE2D_ARGS(_Mask3, sampler_Splat0), uv3, _LayerHasMask3, _MaskMapRemapScale3, _MaskMapRemapOffset3);

    control = TerrainHeightBlend(control, float4(m0.b, m1.b, m2.b, m3.b));

    half ao0 = _LayerHasMask0 > 0.5 ? m0.g : (half)(_MaskMapRemapScale0.g + _MaskMapRemapOffset0.g);
    half ao1 = _LayerHasMask1 > 0.5 ? m1.g : (half)(_MaskMapRemapScale1.g + _MaskMapRemapOffset1.g);
    half ao2 = _LayerHasMask2 > 0.5 ? m2.g : (half)(_MaskMapRemapScale2.g + _MaskMapRemapOffset2.g);
    half ao3 = _LayerHasMask3 > 0.5 ? m3.g : (half)(_MaskMapRemapScale3.g + _MaskMapRemapOffset3.g);
    half sm0 = _LayerHasMask0 > 0.5 ? m0.a : (half)_Smoothness0;
    half sm1 = _LayerHasMask1 > 0.5 ? m1.a : (half)_Smoothness1;
    half sm2 = _LayerHasMask2 > 0.5 ? m2.a : (half)_Smoothness2;
    half sm3 = _LayerHasMask3 > 0.5 ? m3.a : (half)_Smoothness3;

    half3 albedo =
        (half3)SAMPLE_TEXTURE2D_BIAS(_Splat0, sampler_Splat0, uv0, TERRAIN_ALBEDO_BIAS).rgb * (half)control.r +
        (half3)SAMPLE_TEXTURE2D_BIAS(_Splat1, sampler_Splat0, uv1, TERRAIN_ALBEDO_BIAS).rgb * (half)control.g +
        (half3)SAMPLE_TEXTURE2D_BIAS(_Splat2, sampler_Splat0, uv2, TERRAIN_ALBEDO_BIAS).rgb * (half)control.b +
        (half3)SAMPLE_TEXTURE2D_BIAS(_Splat3, sampler_Splat0, uv3, TERRAIN_ALBEDO_BIAS).rgb * (half)control.a;

    float3 geomN = normalize(geomNormalWS);
    float slope = 1.0 - saturate(geomN.y);
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
    s.occlusion =
        ao0 * (half)control.r +
        ao1 * (half)control.g +
        ao2 * (half)control.b +
        ao3 * (half)control.a;
    s.smoothness =
        sm0 * (half)control.r +
        sm1 * (half)control.g +
        sm2 * (half)control.b +
        sm3 * (half)control.a;
    albedo *= s.occlusion;

    half3 nTS =
        (half3)DecodeNormal(SAMPLE_TEXTURE2D(_Normal0, sampler_Splat0, uv0), _NormalScale0) * (half)control.r +
        (half3)DecodeNormal(SAMPLE_TEXTURE2D(_Normal1, sampler_Splat0, uv1), _NormalScale1) * (half)control.g +
        (half3)DecodeNormal(SAMPLE_TEXTURE2D(_Normal2, sampler_Splat0, uv2), _NormalScale2) * (half)control.b +
        (half3)DecodeNormal(SAMPLE_TEXTURE2D(_Normal3, sampler_Splat0, uv3), _NormalScale3) * (half)control.a;
    nTS.z += 1e-4h;
    s.normalWS = (half3)TerrainDetailToWorld(normalize(nTS), geomN);
    s.albedo = albedo * (half3)_BaseColor.rgb;
    return s;
}

half4 TerrainOilNPRPassFragment(Varyings input) : SV_TARGET
{
    UNITY_SETUP_INSTANCE_ID(input);

    TerrainOilSample terrain = SampleTerrainCheap(input.controlUV, input.positionWS, input.normalWS);
    half3 albedo = terrain.albedo;
    half3 normalWS = normalize(terrain.normalWS);
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
    surface.occlusion = (float)terrain.occlusion;
    surface.smoothness = (float)terrain.smoothness;
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

    if (terrain.smoothness > 0.02h && GetDirectionalLightCount() > 0)
    {
        half3 L = (half3)_DirectionalLightData[0].directionAndMask.xyz;
        half3 H = normalize(L + viewDir);
        half spec = pow(saturate(dot(normalWS, H)), lerp(24.0h, 96.0h, terrain.smoothness));
        color += spec * terrain.smoothness * (half3)_SpecularColor.rgb * 0.28h;
    }

    color = OilWeaveTint(color, input.positionWS.xz * 0.15);
    return half4(saturate(color), 1.0h);
}

#endif
