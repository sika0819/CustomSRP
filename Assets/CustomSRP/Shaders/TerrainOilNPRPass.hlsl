#ifndef CUSTOM_TERRAIN_OIL_NPR_PASS_INCLUDED
#define CUSTOM_TERRAIN_OIL_NPR_PASS_INCLUDED

#include "OilNPRLighting.hlsl"
#include "OilPaintNoise.hlsl"
#include "OilImpastoCore.hlsl"
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
// Stroke normals are broad ridges. Keep them a mip sharper than the albedo or the bevel flattens.
#define TERRAIN_NORMAL_BIAS 0.2

struct TerrainOilSample
{
    half3 albedo;
    half3 normalWS;
    half occlusion;
    half smoothness;
};

void AccumulateTerrainLayer(
    inout half3 albedo,
    inout half3 normalTS,
    inout half occlusion,
    inout half smoothness,
    float weight,
    float2 uv,
    half4 mask,
    float hasMask,
    float4 remapScale,
    float4 remapOffset,
    float smoothnessConstant,
    float normalScale,
    TEXTURE2D_PARAM(splat, samp),
    TEXTURE2D_PARAM(normalMap, normalSamp))
{
    if (weight <= 0.001)
    {
        return;
    }

    half w = (half)weight;
    albedo += (half3)SAMPLE_TEXTURE2D_BIAS(splat, samp, uv, TERRAIN_ALBEDO_BIAS).rgb * w;
#if defined(OIL_MOBILE_PERF)
    half ao = (half)(remapScale.g + remapOffset.g);
    half sm = (half)smoothnessConstant;
#else
    half ao = hasMask > 0.5 ? mask.g : (half)(remapScale.g + remapOffset.g);
    half sm = hasMask > 0.5 ? mask.a : (half)smoothnessConstant;
#endif
    occlusion += ao * w;
    smoothness += sm * w;
    // _OilDetail thickens the authored smear. Half-Lambert bands hide a scale of 1.
    float oilScale = normalScale * lerp(1.2, 1.85, saturate(_OilDetail));
    normalTS += (half3)DecodeNormal(
        SAMPLE_TEXTURE2D_BIAS(normalMap, normalSamp, uv, TERRAIN_NORMAL_BIAS), oilScale) * w;
}

// Geometric normal stays the shadow bias. This term is only the paint bevel:
// the face of a stroke toward the sun lifts, the far shoulder digs in.
half3 ApplyOilStrokeFlank(half3 color, half3 normalWS, half3 geomNormalWS)
{
    half bump = saturate((half)_OilDetail);
    if (bump < 0.001h || GetDirectionalLightCount() <= 0)
    {
        return color;
    }

    half3 geomN = normalize(geomNormalWS);
    half3 L = (half3)GetDirectionalLightData(0).directionAndMask.xyz;
    half flank = dot(normalWS - geomN, L);
    flank = clamp(flank, -0.55h, 0.55h);
    return color * max(0.0h, 1.0h + flank * bump * 1.7h);
}

// Control once. A layer is sampled only while its weight can change the blend.
// UV wobble is continuous value noise, shared by albedo, mask, and normal.
TerrainOilSample SampleTerrainCheap(float2 controlUV, float3 positionWS, float3 geomNormalWS)
{
    TerrainOilSample s;
    float4 control = SAMPLE_TEXTURE2D(_Control, sampler_Control, controlUV);
    // A 3-layer control map stores unused alpha as 1. A real fourth weight leaves RGB short of 1.
    float rgbSum = control.r + control.g + control.b;
    if (rgbSum > 0.98)
    {
        control.a = 0.0;
    }

    float sum4 = dot(control, 1.0);
    control /= max(sum4, 1e-5);

    // Continuous value noise — break splat tiling without a second brush plate.
    float n0 = OilValueNoise(positionWS.xz * 0.02);
    float n1 = OilValueNoise(positionWS.xz * 0.02 + float2(19.1, 7.3));
    float n2 = OilValueNoise(positionWS.xz * 0.007 + float2(3.7, 11.2));
    float2 wobble = (float2(n0, n1) - 0.5) * 0.22 + (float2(n2, 1.0 - n2) - 0.5) * 0.10;

    float2 uv0 = TransformTerrainUV(controlUV, _Splat0_ST) + wobble;
    float2 uv1 = TransformTerrainUV(controlUV, _Splat1_ST) + wobble.yx;
    float2 uv2 = TransformTerrainUV(controlUV, _Splat2_ST) + wobble;
    float2 uv3 = TransformTerrainUV(controlUV, _Splat3_ST) + wobble.yx;

#if defined(OIL_MOBILE_PERF)
    half4 m0 = half4(0.5h, 1.0h, 0.5h, (half)_Smoothness0);
    half4 m1 = half4(0.5h, 1.0h, 0.5h, (half)_Smoothness1);
    half4 m2 = half4(0.5h, 1.0h, 0.5h, (half)_Smoothness2);
    half4 m3 = half4(0.5h, 1.0h, 0.5h, (half)_Smoothness3);
#else
    half4 m0 = SampleLayerMaskWeighted(control.r, uv0, _LayerHasMask0, _MaskMapRemapScale0, _MaskMapRemapOffset0, TEXTURE2D_ARGS(_Mask0, sampler_Splat0));
    half4 m1 = SampleLayerMaskWeighted(control.g, uv1, _LayerHasMask1, _MaskMapRemapScale1, _MaskMapRemapOffset1, TEXTURE2D_ARGS(_Mask1, sampler_Splat0));
    half4 m2 = SampleLayerMaskWeighted(control.b, uv2, _LayerHasMask2, _MaskMapRemapScale2, _MaskMapRemapOffset2, TEXTURE2D_ARGS(_Mask2, sampler_Splat0));
    half4 m3 = SampleLayerMaskWeighted(control.a, uv3, _LayerHasMask3, _MaskMapRemapScale3, _MaskMapRemapOffset3, TEXTURE2D_ARGS(_Mask3, sampler_Splat0));

    // Height maps are full-range noise. Blending with them cuts the painted edge into a hard line.
    // _HeightTransition at 0 keeps the alphamap feather.
    if (_HeightTransition > 0.001)
    {
        control = TerrainHeightBlend(control, float4(m0.b, m1.b, m2.b, m3.b));
    }
#endif

    half3 albedo = 0.0h;
    half3 nTS = 0.0h;
    half occlusion = 0.0h;
    half smoothness = 0.0h;
    AccumulateTerrainLayer(
        albedo, nTS, occlusion, smoothness, control.r, uv0, m0,
        _LayerHasMask0, _MaskMapRemapScale0, _MaskMapRemapOffset0, _Smoothness0, _NormalScale0,
        TEXTURE2D_ARGS(_Splat0, sampler_Splat0), TEXTURE2D_ARGS(_Normal0, sampler_Splat0));
    AccumulateTerrainLayer(
        albedo, nTS, occlusion, smoothness, control.g, uv1, m1,
        _LayerHasMask1, _MaskMapRemapScale1, _MaskMapRemapOffset1, _Smoothness1, _NormalScale1,
        TEXTURE2D_ARGS(_Splat1, sampler_Splat0), TEXTURE2D_ARGS(_Normal1, sampler_Splat0));
    AccumulateTerrainLayer(
        albedo, nTS, occlusion, smoothness, control.b, uv2, m2,
        _LayerHasMask2, _MaskMapRemapScale2, _MaskMapRemapOffset2, _Smoothness2, _NormalScale2,
        TEXTURE2D_ARGS(_Splat2, sampler_Splat0), TEXTURE2D_ARGS(_Normal2, sampler_Splat0));
    AccumulateTerrainLayer(
        albedo, nTS, occlusion, smoothness, control.a, uv3, m3,
        _LayerHasMask3, _MaskMapRemapScale3, _MaskMapRemapOffset3, _Smoothness3, _NormalScale3,
        TEXTURE2D_ARGS(_Splat3, sampler_Splat0), TEXTURE2D_ARGS(_Normal3, sampler_Splat0));

    float3 geomN = normalize(geomNormalWS);
#if !defined(OIL_MOBILE_PERF)
    if (_RidgeAmount > 0.001)
    {
        float rockW = saturate((1.0 - sum4) * _RidgeAmount);
        float slope = 1.0 - saturate(geomN.y);
        float height01 = saturate(positionWS.y / max(_TerrainHeight, 1.0));
        float ridge = smoothstep(0.2, 0.55, slope);
        ridge = max(ridge, smoothstep(0.42, 0.72, height01) * smoothstep(0.1, 0.28, slope));
        ridge *= _RidgeAmount * saturate(control.g + control.b);
        ridge = smoothstep(0.08, 0.92, ridge);
        float ridgeWeight = saturate(ridge + rockW);
        if (ridgeWeight > 0.001)
        {
            float2 uvR = controlUV * _RidgeRockMap_ST.xy + _RidgeRockMap_ST.zw + wobble;
            half3 rock = (half3)SAMPLE_TEXTURE2D_BIAS(_RidgeRockMap, sampler_RidgeRockMap, uvR, TERRAIN_ALBEDO_BIAS).rgb;
            albedo = lerp(albedo, rock, (half)ridgeWeight);
        }
    }
#endif

    // Value climate only — keep splat hue (厚涂不改色调).
    half climate = (half)OilValueNoise(positionWS.xz * 0.002);
    albedo *= lerp(0.94h, 1.07h, climate);
    albedo *= occlusion;
    nTS.z += 1e-4h;
    s.occlusion = occlusion;
    s.smoothness = smoothness;
    s.normalWS = (half3)TerrainDetailToWorld(normalize(nTS), geomN);
    s.albedo = albedo * (half3)_BaseColor.rgb;
    return s;
}

half4 TerrainOilNPRPassFragment(Varyings input) : SV_TARGET
{
    UNITY_SETUP_INSTANCE_ID(input);
    OilBakeClipAboveWater(input.positionWS);

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
    surface.dither = InterleavedGradientNoise(input.positionCS_SS.xy, 0);
    surface.renderingLayerMask = asuint(unity_RenderingLayer.x);

#if defined(OIL_MOBILE_PERF)
    half brush = 0.5h;
#else
    // Shadow-edge wobble only — never drive albedo/height (tiling reads as fake brush stamps).
    half brush = SampleOilShadowBrushCheap(input.positionWS, input.controlUV, 0.25);
#endif
    // Bump-map style relief: splat luma + fine world noise. No OilBrush height plate.
    half stroke = PaintStroke(PaintLuma(albedo), 1.25h);
#if !defined(OIL_MOBILE_PERF)
    half micro = (half)OilValueNoise(input.positionWS.xz * 0.14 + float2(7.3, 2.1));
    half fine = (half)OilValueNoise(input.positionWS.xz * 0.37 + float2(1.9, 4.6));
    stroke = saturate(stroke * 0.82h + micro * 0.12h + fine * 0.06h);
#endif
    // Mild value climate only — keep splat paint readable, not stacked slabs.
    half relief = saturate((half)_OilDetail) * 0.38h;
    albedo = ImpastoDeviation(albedo, stroke, relief);
    surface.color = (float3)albedo;
#if !defined(OIL_MOBILE_PERF)
    {
        // Screen-space height → normal tilt, like a detail normal map (small amplitude).
        half strokeDx = (half)ddx((float)stroke);
        half strokeDz = (half)ddy((float)stroke);
        normalWS = ImpastoTiltNormal(normalWS, strokeDx, strokeDz, relief * 0.55h);
        surface.normal = (float3)normalWS;
    }
#endif
    Fragment fragment = GetFragmentScreen(input.positionCS_SS);
    half3 color = OilNPRLighting(
        surface,
        albedo,
        half3(0.36h, 0.46h, 0.62h),
        half3(0.58h, 0.48h, 0.3h),
        (half3)_SpecularColor.rgb,
        0.0h,
        (half)_ShadeSteps,
        0.0h,
        (half)_ShadowLift,
        (half)_ShadeLift,
        brush,
        (half)_ShadowWobble,
        (half)_OilDetail,
        // Kill glossy response — terrain oil should be matte pigment.
        terrain.smoothness * 0.15h,
        fragment.screenUV);
    color = ApplyOilStrokeFlank(color, normalWS, (half3)input.normalWS);

#if defined(OIL_MOBILE_PERF)
    color = (half3)ApplyOilFog((float3)color, surface.depth, brush);
#else
    // No ImpastoPaintStack / RidgeSpec on terrain — those were the repeating fake stamps
    // and the blown specular. Splat normals + mild tilt already carry the 凹凸.
    if (terrain.smoothness > 0.08h && GetDirectionalLightCount() > 0)
    {
        half3 L = (half3)GetDirectionalLightData(0).directionAndMask.xyz;
        half3 H = normalize(L + viewDir);
        half spec = pow(saturate(dot(normalWS, H)), lerp(32.0h, 96.0h, terrain.smoothness));
        color += spec * terrain.smoothness * (half3)_SpecularColor.rgb * 0.06h;
    }
    // Mild canvas tooth only — full OilWeaveTint was too stripey on large ground.
    {
        float2 p = input.positionWS.xz * 0.15;
        half w = (half)sin(p.x * 1.7 + p.y * 0.8) * 0.5h + 0.5h;
        color *= lerp(0.97h, 1.03h, w);
    }
    color = (half3)ApplyOilFog((float3)color, surface.depth, brush, input.positionWS);
#endif
    return half4(saturate(color), 1.0h);
}

#endif
