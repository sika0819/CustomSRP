#ifndef CUSTOM_TERRAIN_OIL_NPR_INPUT_INCLUDED
#define CUSTOM_TERRAIN_OIL_NPR_INPUT_INCLUDED

// Unity Terrain injects control + splat maps onto materialTemplate.
TEXTURE2D(_Control);
TEXTURE2D(_Splat0);
TEXTURE2D(_Splat1);
TEXTURE2D(_Splat2);
TEXTURE2D(_Splat3);
TEXTURE2D(_Normal0);
TEXTURE2D(_Normal1);
TEXTURE2D(_Normal2);
TEXTURE2D(_Normal3);
TEXTURE2D(_Mask0);
TEXTURE2D(_Mask1);
TEXTURE2D(_Mask2);
TEXTURE2D(_Mask3);
SAMPLER(sampler_Control);
SAMPLER(sampler_Splat0);

float4 _Control_ST;
float4 _Splat0_ST;
float4 _Splat1_ST;
float4 _Splat2_ST;
float4 _Splat3_ST;

float _Metallic0, _Metallic1, _Metallic2, _Metallic3;
float _Smoothness0, _Smoothness1, _Smoothness2, _Smoothness3;
float _NormalScale0, _NormalScale1, _NormalScale2, _NormalScale3;
float4 _MaskMapRemapScale0, _MaskMapRemapScale1, _MaskMapRemapScale2, _MaskMapRemapScale3;
float4 _MaskMapRemapOffset0, _MaskMapRemapOffset1, _MaskMapRemapOffset2, _MaskMapRemapOffset3;
float _LayerHasMask0, _LayerHasMask1, _LayerHasMask2, _LayerHasMask3;

TEXTURE2D(_RidgeRockMap);
SAMPLER(sampler_RidgeRockMap);

TEXTURE2D(_OutlineBrushMap);
SAMPLER(sampler_OutlineBrushMap);
float4 _OutlineBrushMap_ST;
float _ShadowBrushScale;

CBUFFER_START(UnityPerMaterial)
    float4 _BaseColor;
    float4 _AmbientColor;
    float4 _SpecularColor;
    float _ShadeSteps;
    float _ShadowLift;
    float _ShadeLift;
    float _ShadowWobble;
    float4 _RidgeRockMap_ST;
    float _RidgeAmount;
    float _TerrainHeight;
    float _OilDetail;
    float _HeightTransition;
    float _ShoreWetY;
    float _ShoreWetBand;
    float _ShoreWetStrength;
CBUFFER_END

#include "OilShadowBrush.hlsl"

float2 TransformTerrainUV(float2 uv, float4 st)
{
    return uv * st.xy + st.zw;
}

// R metallic, G AO, B height, A smoothness. Layers without a mask keep AO at the remap
// sum (identity → 1) instead of the 0.5 placeholder.
half4 SampleLayerMask(TEXTURE2D_PARAM(tex, samp), float2 uv, float hasMask, float4 scale, float4 offset)
{
    half4 mask = 0.5h;
    if (hasMask > 0.5)
    {
        mask = (half4)SAMPLE_TEXTURE2D(tex, samp, uv);
    }

    return (half4)(mask * (half4)scale + (half4)offset);
}

// Zero-weight layers do not move the height blend. Skip the tap.
half4 SampleLayerMaskWeighted(
    float weight,
    float2 uv,
    float hasMask,
    float4 scale,
    float4 offset,
    TEXTURE2D_PARAM(tex, samp))
{
    if (weight < 0.001)
    {
        return (half4)(0.5h * (half4)scale + (half4)offset);
    }

    return SampleLayerMask(TEXTURE2D_ARGS(tex, samp), uv, hasMask, scale, offset);
}

// Height only shifts weights that already exist. Equal heights leave the control map alone.
float4 TerrainHeightBlend(float4 control, float4 height)
{
    float4 h = height * step(0.001, control);
    float maxH = max(max(h.x, h.y), max(h.z, h.w));
    float transition = max(_HeightTransition, 1e-3);
    float4 weight = saturate((h - maxH + transition) / transition) * control;
    return weight / max(dot(weight, 1.0), 1e-5);
}

// Splat UV +U is terrain +X. Tangent follows that, so the layer normal sits on the
// height-derived geometric normal and does not revive patch-border shade lines.
float3 TerrainDetailToWorld(float3 normalTS, float3 geomNormalWS)
{
    float3 n = normalize(geomNormalWS);
    float3 tRef = abs(n.x) > 0.95 ? float3(0.0, 0.0, 1.0) : float3(1.0, 0.0, 0.0);
    float3 t = normalize(tRef - n * dot(n, tRef));
    return NormalTangentToWorld(normalTS, n, float4(t, -1.0));
}

#endif
