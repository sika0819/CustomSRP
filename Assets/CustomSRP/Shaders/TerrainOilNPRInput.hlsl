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

TEXTURE2D(_RidgeRockMap);
SAMPLER(sampler_RidgeRockMap);

#include "OilImpasto.hlsl"
#include "OilPaintNoise.hlsl"

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
    float4 _RidgeRockMap_ST;
    float _RidgeAmount;
    float4 _PaintMap_ST;
    float _PaintTile;
    float _PaintContrast;
    float _PaintRelief;
    float _TerrainWorldSize;
    float _TerrainHeight;
    float _OilDetail;
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
    // Layer 4 (ridge rock) is the weight missing from the first control map.
    // Amount 0, or a 4-layer terrain, leaves the blend unchanged.
    float sum4 = dot(control, 1.0);
    float rockW = saturate((1.0 - sum4) * _RidgeAmount);
    float veg = max(sum4, 1e-5);
    control /= veg;

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
    if (rockW > 0.001)
    {
        float2 uvR = controlUV * _RidgeRockMap_ST.xy + _RidgeRockMap_ST.zw;
        float3 rock = SAMPLE_TEXTURE2D(_RidgeRockMap, sampler_RidgeRockMap, uvR).rgb;
        albedo = lerp(albedo, rock, rockW);
    }

    return (half3)(albedo * _BaseColor.rgb);
}

float3 TerrainPlaneWeights(float3 normalWS)
{
    float3 w = abs(normalize(normalWS));
    w = pow(max(w, 1e-4), 4.0);
    w /= max(w.x + w.y + w.z, 1e-4);
    return w;
}

float2 TerrainWorldUv(float2 axis, float4 st)
{
    float size = max(_TerrainWorldSize, 1.0);
    return axis * (st.xy / size) + st.zw;
}

void TerrainMacro(float3 positionWS, out float macro, out float2 jitter)
{
    macro = OilFbm(positionWS.xz * 0.00022);
    jitter = float2(
        OilFbm(positionWS.xz * 0.00041 + 1.7),
        OilFbm(positionWS.zx * 0.00041 + 4.2)) - 0.5;
    jitter *= 0.22;
}

half3 SampleTexMacro(TEXTURE2D_PARAM(tex, samp), float2 uv, float macro, float2 jitter)
{
    half3 broad = (half3)SAMPLE_TEXTURE2D(tex, samp, uv + jitter).rgb;
    half3 fine = (half3)SAMPLE_TEXTURE2D(tex, samp, uv * 2.7 + jitter.yx + 0.13).rgb;
    half3 col = lerp(broad, fine, (half)saturate(macro) * 0.42h);
    half3 warm = col * half3(1.08h, 1.03h, 0.86h);
    half3 cool = col * half3(0.84h, 0.93h, 1.06h);
    return lerp(cool, warm, (half)saturate(macro));
}

// Top plane keeps control UV (matches the alphamap). Side planes use world
// meters so a cliff does not stretch the splat along XZ.
half3 SampleLayerTriplanar(
    TEXTURE2D_PARAM(tex, samp),
    float4 st,
    float2 controlUV,
    float3 positionWS,
    float3 weights,
    float macro,
    float2 jitter)
{
    float2 uvY = controlUV * st.xy + st.zw;
    half3 colY = SampleTexMacro(TEXTURE2D_ARGS(tex, samp), uvY, macro, jitter);
    float2 uvX = TerrainWorldUv(positionWS.zy, st);
    half3 colX = (half3)SAMPLE_TEXTURE2D(tex, samp, uvX + jitter).rgb;
    float2 uvZ = TerrainWorldUv(positionWS.xy, st);
    half3 colZ = (half3)SAMPLE_TEXTURE2D(tex, samp, uvZ + jitter).rgb;
    return colX * (half)weights.x + colY * (half)weights.y + colZ * (half)weights.z;
}

half3 SampleTerrainPainted(float2 controlUV, float3 positionWS, float3 normalWS)
{
    float4 control = SAMPLE_TEXTURE2D(_Control, sampler_Control, controlUV);
    float sum4 = dot(control, 1.0);
    float rockW = saturate((1.0 - sum4) * _RidgeAmount);
    float veg = max(sum4, 1e-5);
    control /= veg;

    float3 weights = TerrainPlaneWeights(normalWS);
    float macro;
    float2 jitter;
    TerrainMacro(positionWS, macro, jitter);

    half3 s0 = SampleLayerTriplanar(
        TEXTURE2D_ARGS(_Splat0, sampler_Splat0), _Splat0_ST,
        controlUV, positionWS, weights, macro, jitter);
    half3 s1 = SampleLayerTriplanar(
        TEXTURE2D_ARGS(_Splat1, sampler_Splat0), _Splat1_ST,
        controlUV, positionWS, weights, macro, jitter);
    half3 s2 = SampleLayerTriplanar(
        TEXTURE2D_ARGS(_Splat2, sampler_Splat0), _Splat2_ST,
        controlUV, positionWS, weights, macro, jitter);
    half3 s3 = SampleLayerTriplanar(
        TEXTURE2D_ARGS(_Splat3, sampler_Splat0), _Splat3_ST,
        controlUV, positionWS, weights, macro, jitter);

    half3 albedo =
        s0 * (half)control.r +
        s1 * (half)control.g +
        s2 * (half)control.b +
        s3 * (half)control.a;
    half3 rock = SampleLayerTriplanar(
        TEXTURE2D_ARGS(_RidgeRockMap, sampler_RidgeRockMap), _RidgeRockMap_ST,
        controlUV, positionWS, weights, macro, jitter);
    albedo = lerp(albedo, rock, (half)rockW);
    return albedo * (half3)_BaseColor.rgb;
}

// Horizontal projection only. One sample per layer, not another triplanar set.
half3 SampleFlatSplatNormal(float2 controlUV)
{
    float4 control = SAMPLE_TEXTURE2D(_Control, sampler_Control, controlUV);
    float wSum = max(dot(control, 1.0), 1e-5);
    control /= wSum;

    float2 uv0 = TransformTerrainUV(controlUV, _Splat0_ST);
    float2 uv1 = TransformTerrainUV(controlUV, _Splat1_ST);
    float2 uv2 = TransformTerrainUV(controlUV, _Splat2_ST);
    float2 uv3 = TransformTerrainUV(controlUV, _Splat3_ST);

    half3 n =
        (half3)DecodeNormal(SAMPLE_TEXTURE2D(_Normal0, sampler_Splat0, uv0), 1.0) * (half)control.r +
        (half3)DecodeNormal(SAMPLE_TEXTURE2D(_Normal1, sampler_Splat0, uv1), 1.0) * (half)control.g +
        (half3)DecodeNormal(SAMPLE_TEXTURE2D(_Normal2, sampler_Splat0, uv2), 1.0) * (half)control.b +
        (half3)DecodeNormal(SAMPLE_TEXTURE2D(_Normal3, sampler_Splat0, uv3), 1.0) * (half)control.a;
    return normalize(n);
}

half CreviceShade(float3 normalWS, half stroke, half relief)
{
    // Stroke grooves, not screen derivatives. ddx/ddy of the normal
    // draws a dark grid on every terrain patch border.
    half groove = saturate((0.5h - stroke) * relief);
    half slope = saturate((1.0h - (half)normalize(normalWS).y) * 0.35h);
    return saturate(groove * 0.65h + slope * 0.22h);
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
    // Match OilGround Plane: world-space strokes, no extra shadow-position warp.
    return SampleOilShadowBrush(positionWS, controlUV, 0.25);
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
