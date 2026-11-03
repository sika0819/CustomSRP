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
    // Mask B — oil stroke height used for 明暗交界 (ridge lit / groove dug).
    half strokeHeight;
    // Layer0 = sand (CreateIslandTerrainScene fold order).
    half sandWeight;
};

void AccumulateTerrainLayer(
    inout half3 albedo,
    inout half3 normalTS,
    inout half occlusion,
    inout half smoothness,
    inout half strokeHeight,
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
    half h = 0.5h;
#else
    half ao = hasMask > 0.5 ? mask.g : (half)(remapScale.g + remapOffset.g);
    half sm = hasMask > 0.5 ? mask.a : (half)smoothnessConstant;
    half h = hasMask > 0.5 ? mask.b : 0.5h;
#endif
    occlusion += ao * w;
    smoothness += sm * w;
    strokeHeight += h * w;
    // Stroke-scale smear — enough for painted bevel, not grit pits.
    float oilScale = normalScale * lerp(1.05, 1.45, saturate(_OilDetail));
    normalTS += (half3)DecodeNormal(
        SAMPLE_TEXTURE2D_BIAS(normalMap, normalSamp, uv, TERRAIN_NORMAL_BIAS), oilScale) * w;
}

// Oil stroke 明暗交界: lit ridge lifts, far shoulder steps down — clear edge, soft floor.
half3 ApplyOilStrokeFlank(half3 color, half3 normalWS, half3 geomNormalWS)
{
    half bump = saturate((half)_OilDetail);
    if (bump < 0.001h || GetDirectionalLightCount() <= 0)
    {
        return color;
    }

    half3 geomN = normalize(geomNormalWS);
    half3 L = (half3)GetDirectionalLightData(0).directionAndMask.xyz;
    half flank = clamp(dot(normalWS - geomN, L), -0.55h, 0.55h);
    // Sharpen mid so stroke faces read as color blocks, not soft mud.
    half edge = flank * (1.05h + abs(flank));
    half lift = max(edge, 0.0h) * bump * 1.35h;
    half dig = max(-edge, 0.0h) * bump * 0.38h;
    return color * max(0.0h, 1.0h + lift - dig);
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

    // Mild UV break only — strong wobble reads as dirty grit on clean oil pigment.
    float n0 = OilValueNoise(positionWS.xz * 0.015);
    float n1 = OilValueNoise(positionWS.xz * 0.015 + float2(19.1, 7.3));
    float2 wobble = (float2(n0, n1) - 0.5) * 0.08;

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
    half strokeHeight = 0.0h;
    AccumulateTerrainLayer(
        albedo, nTS, occlusion, smoothness, strokeHeight, control.r, uv0, m0,
        _LayerHasMask0, _MaskMapRemapScale0, _MaskMapRemapOffset0, _Smoothness0, _NormalScale0,
        TEXTURE2D_ARGS(_Splat0, sampler_Splat0), TEXTURE2D_ARGS(_Normal0, sampler_Splat0));
    AccumulateTerrainLayer(
        albedo, nTS, occlusion, smoothness, strokeHeight, control.g, uv1, m1,
        _LayerHasMask1, _MaskMapRemapScale1, _MaskMapRemapOffset1, _Smoothness1, _NormalScale1,
        TEXTURE2D_ARGS(_Splat1, sampler_Splat0), TEXTURE2D_ARGS(_Normal1, sampler_Splat0));
    AccumulateTerrainLayer(
        albedo, nTS, occlusion, smoothness, strokeHeight, control.b, uv2, m2,
        _LayerHasMask2, _MaskMapRemapScale2, _MaskMapRemapOffset2, _Smoothness2, _NormalScale2,
        TEXTURE2D_ARGS(_Splat2, sampler_Splat0), TEXTURE2D_ARGS(_Normal2, sampler_Splat0));
    AccumulateTerrainLayer(
        albedo, nTS, occlusion, smoothness, strokeHeight, control.a, uv3, m3,
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

    // Clean oil: AO only a whisper — stronger AO paints mud into stroke grooves.
    half softAo = lerp(1.0h, occlusion, 0.08h);
    albedo *= softAo;
    nTS.z += 1e-4h;
    s.occlusion = softAo;
    s.smoothness = smoothness;
    s.strokeHeight = strokeHeight;
    s.sandWeight = (half)control.r;
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
    // Authored albedo already carries stroke color blocks; height sharpens 颜色交界.
    half sandW = saturate(terrain.sandWeight);
    half stroke = PaintStroke(terrain.strokeHeight, 1.55h);
    stroke = saturate(stroke * 0.55h + PaintStroke(PaintLuma(albedo), 1.45h) * 0.45h);
    half relief = saturate((half)_OilDetail) * 0.48h;
    // Keep authored sand lit/dark (warm beige / deeper tan) — no palette override.
    // Shallow dig — deep shadeL reads as dirty pits, not oil stroke flanks.
    half digAmt = lerp(0.14h, 0.08h, sandW);
    half liftAmt = lerp(0.48h, 0.34h, sandW);
    half band = smoothstep(0.38h, 0.62h, stroke);
    {
        half luma = PaintLuma(albedo);
        half3 chr = albedo / max(luma, 1e-3h);
        half litL = luma * (1.0h + relief * liftAmt);
        half shadeL = luma * (1.0h - relief * digAmt);
        half lumaOut = lerp(shadeL, litL, band);
        albedo = saturate(chr * lumaOut);
        half g = saturate(abs((half)ddx((float)stroke)) + abs((half)ddy((float)stroke))) * 6.0h;
        half shadeMul = lerp(0.92h, 0.96h, sandW);
        albedo = lerp(albedo, albedo * lerp(shadeMul, 1.08h, band), g * relief);
    }
    surface.color = (float3)albedo;
#if !defined(OIL_MOBILE_PERF)
    {
        half strokeDx = (half)ddx((float)stroke);
        half strokeDz = (half)ddy((float)stroke);
        normalWS = ImpastoTiltNormal(normalWS, strokeDx, strokeDz, relief * lerp(0.75h, 0.5h, sandW));
        surface.normal = (float3)normalWS;
    }
#endif
    Fragment fragment = GetFragmentScreen(input.positionCS_SS);
    half3 color = OilNPRLighting(
        surface,
        albedo,
        half3(0.48h, 0.42h, 0.34h),
        half3(0.62h, 0.5h, 0.32h),
        (half3)_SpecularColor.rgb,
        0.0h,
        (half)_ShadeSteps,
        0.0h,
        (half)_ShadowLift,
        (half)_ShadeLift,
        brush,
        (half)_ShadowWobble,
        (half)_OilDetail,
        terrain.smoothness * 0.15h,
        fragment.screenUV);
    color = ApplyOilStrokeFlank(color, normalWS, (half3)input.normalWS);
    // Sand: mild warm lock only — preserve authored beige/tan, kill cool cast.
    if (sandW > 0.02h)
    {
        half luma = PaintLuma(color);
        half3 warmChr = half3(1.14h, 1.0h, 0.72h); // matches old Sand lit ~223,200,154
        half3 locked = saturate(warmChr * (luma / max(PaintLuma(warmChr), 1e-3h)));
        color = lerp(color, locked, sandW * 0.45h);
    }

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
    // Barely-there canvas tooth — stronger weave dirtied clean pigment.
    color = (half3)ApplyOilFog((float3)color, surface.depth, brush, input.positionWS);
#endif
    return half4(saturate(color), 1.0h);
}

#endif
