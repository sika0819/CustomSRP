#ifndef CUSTOM_TERRAIN_OIL_NPR_INPUT_INCLUDED
#define CUSTOM_TERRAIN_OIL_NPR_INPUT_INCLUDED

// Unity Terrain injects control + splat maps onto materialTemplate.
TEXTURE2D(_Control);
TEXTURE2D(_Splat0);
TEXTURE2D(_Splat1);
TEXTURE2D(_Splat2);
TEXTURE2D(_Splat3);
SAMPLER(sampler_Control);
SAMPLER(sampler_Splat0);

float4 _Control_ST;
float4 _Splat0_ST;
float4 _Splat1_ST;
float4 _Splat2_ST;
float4 _Splat3_ST;

float _Metallic0, _Metallic1, _Metallic2, _Metallic3;
float _Smoothness0, _Smoothness1, _Smoothness2, _Smoothness3;

TEXTURE2D(_CanvasMap);
SAMPLER(sampler_CanvasMap);

TEXTURE2D(_OutlineBrushMap);
SAMPLER(sampler_OutlineBrushMap);

CBUFFER_START(UnityPerMaterial)
    float4 _BaseColor;
    float4 _CanvasMap_ST;
    float4 _OutlineBrushMap_ST;
    float4 _AmbientColor;
    float4 _ShadowTint;
    float4 _ShadowWarm;
    float4 _SpecularColor;
    float4 _EdgeColor;
    float _ShadeSteps;
    float _SpecularThreshold;
    float _CanvasStrength;
    float _PaintThickness;
    float _EdgeStrength;
    float _KuwaharaRadius;
    float _ShadowLift;
    float _ShadeLift;
    float _ShadowWobble;
    float _ShadowBrushScale;
CBUFFER_END

float2 TransformTerrainUV(float2 uv, float4 st)
{
    return uv * st.xy + st.zw;
}

float2 TransformCanvasUV(float2 uv)
{
    return uv * _CanvasMap_ST.xy + _CanvasMap_ST.zw;
}

half3 SampleTerrainAlbedo(float2 controlUV)
{
    float4 control = SAMPLE_TEXTURE2D(_Control, sampler_Control, controlUV);
    float wSum = max(dot(control, 1.0), 1e-5);
    control /= wSum;

    float2 uv0 = TransformTerrainUV(controlUV, _Splat0_ST);
    float2 uv1 = TransformTerrainUV(controlUV, _Splat1_ST);
    float2 uv2 = TransformTerrainUV(controlUV, _Splat2_ST);
    float2 uv3 = TransformTerrainUV(controlUV, _Splat3_ST);

    float3 s0 = SAMPLE_TEXTURE2D(_Splat0, sampler_Splat0, uv0).rgb;
    float3 s1 = SAMPLE_TEXTURE2D(_Splat1, sampler_Splat0, uv1).rgb;
    float3 s2 = SAMPLE_TEXTURE2D(_Splat2, sampler_Splat0, uv2).rgb;
    float3 s3 = SAMPLE_TEXTURE2D(_Splat3, sampler_Splat0, uv3).rgb;

    float3 albedo =
        s0 * control.r +
        s1 * control.g +
        s2 * control.b +
        s3 * control.a;
    return (half3)(albedo * _BaseColor.rgb);
}

// Control-UV Kuwahara over blended splat (mobile: ~5 blends).
half3 SampleKuwaharaTerrain(float2 uv, half radiusUV)
{
    radiusUV = max(radiusUV, 0.0005h);
    half2 stepUV = half2(radiusUV, radiusUV) * 0.5h;

    half3 mean[4];
    half var[4];

    UNITY_UNROLL
    for (int q = 0; q < 4; q++)
    {
        half2 qSign = half2(
            (q == 0 || q == 3) ? 1.0h : -1.0h,
            (q == 0 || q == 1) ? 1.0h : -1.0h);

        half3 sum = 0.0h;
        half3 sumSq = 0.0h;
        half count = 0.0h;

#if defined(SHADER_API_MOBILE)
        half3 c = SampleTerrainAlbedo(uv + (float2)(stepUV * qSign));
        sum += c;
        sumSq += c * c;
        count = 1.0h;
#else
        UNITY_UNROLL
        for (int y = 0; y <= 1; y++)
        {
            UNITY_UNROLL
            for (int x = 0; x <= 1; x++)
            {
                half2 offset = half2((half)x, (half)y) * stepUV * qSign;
                half3 c = SampleTerrainAlbedo(uv + (float2)offset);
                sum += c;
                sumSq += c * c;
                count += 1.0h;
            }
        }
#endif

        half3 center = SampleTerrainAlbedo(uv);
        sum += center;
        sumSq += center * center;
        count += 1.0h;

        half inv = rcp(count);
        mean[q] = sum * inv;
        half3 m2 = sumSq * inv;
        var[q] = dot(m2 - mean[q] * mean[q], half3(1.0h, 1.0h, 1.0h));
    }

    half bestVar = var[0];
    half3 bestMean = mean[0];
    UNITY_UNROLL
    for (int i = 1; i < 4; i++)
    {
        if (var[i] < bestVar)
        {
            bestVar = var[i];
            bestMean = mean[i];
        }
    }

    return bestMean;
}

half4 SampleCanvas(float2 uv)
{
    return (half4)SAMPLE_TEXTURE2D(_CanvasMap, sampler_CanvasMap, uv);
}

half3 GetCanvasNormalTS(half4 canvas, half strength)
{
    half2 nxy = canvas.rg * 2.0h - 1.0h;
    strength = min(strength, 0.5h);
    return normalize(half3(nxy * strength, 1.0h));
}

half GetCanvasThicknessMask(half4 canvas)
{
    return canvas.b;
}

#include "OilShadowBrush.hlsl"

half SampleShadowBrush(float3 positionWS, float2 controlUV)
{
    // ~1.3 km island: 0.008 UV/m keeps strokes readable from the scenic camera.
    return SampleOilShadowBrush(positionWS, controlUV, 0.008);
}

// Terrain often has no tangents — build a stable TBN from world normal.
void TerrainCanvasToWorld(
    half3 normalTS,
    float3 normalWS,
    out float3 outNormalWS)
{
    float3 n = normalize(normalWS);
    float3 up = abs(n.y) < 0.999 ? float3(0.0, 1.0, 0.0) : float3(1.0, 0.0, 0.0);
    float3 t = normalize(cross(up, n));
    float3 b = cross(n, t);
    outNormalWS = normalize(
        t * normalTS.x + b * normalTS.y + n * normalTS.z);
}

#endif
