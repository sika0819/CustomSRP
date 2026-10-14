#ifndef CUSTOM_OIL_RIVER_NPR_PASS_INCLUDED
#define CUSTOM_OIL_RIVER_NPR_PASS_INCLUDED

#define OIL_NPR_NO_OTHER_LIGHTS
#include "OilNPRLighting.hlsl"
#include "OilPaintNoise.hlsl"
#include "OilRiverShore.hlsl"

TEXTURE2D(_IntersectionNoise);
SAMPLER(sampler_IntersectionNoise);
TEXTURE2D(_GlintMap);
SAMPLER(sampler_GlintMap);

struct Attributes
{
    float3 positionOS : POSITION;
    float3 normalOS : NORMAL;
    float2 uv : TEXCOORD0;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct Varyings
{
    float4 positionCS_SS : SV_POSITION;
    float3 positionWS : VAR_POSITION;
    float3 normalWS : VAR_NORMAL;
    float2 flowUv : TEXCOORD0;
    float2 meshUv : TEXCOORD1;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

Varyings OilRiverNPRPassVertex(Attributes input)
{
    Varyings output;
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_TRANSFER_INSTANCE_ID(input, output);

    float3 positionWS = TransformObjectToWorld(input.positionOS);
    float4 positionCS = TransformWorldToHClip(positionWS);
    output.positionWS = positionWS;
    output.positionCS_SS = positionCS;
    output.normalWS = TransformObjectToWorldNormal(input.normalOS);
    output.meshUv = input.uv;
    output.flowUv = RiverMeshFlowUv(input.uv);
    return output;
}

// 相机→太阳只一道：世界侧向映射到贴图中心竖带，禁止 Monet 横向平铺出多列。
half OilRiverSunGlint(float3 positionWS, half3 normalWS, float t, half stroke)
{
    if (GetDirectionalLightCount() <= 0)
    {
        return 0.0h;
    }

    half3 L = (half3)_DirectionalLightData[0].directionAndMask.xyz;
    float elev = saturate(L.y);
    float2 sunXZ = float2(L.x, L.z);
    float sunLen = length(sunXZ);
    half lit = saturate(((half)L.y + 0.06h) * 30.0h);
    half stable = saturate(((half)sunLen - 0.05h) * 20.0h);
    if (lit * stable < 0.001h)
    {
        return 0.0h;
    }

    float2 sunH = sunXZ / max(sunLen, 1e-4);
    float2 side = float2(-sunH.y, sunH.x);
    float2 fromCam = positionWS.xz - _WorldSpaceCameraPos.xz;
    float along = dot(fromCam, sunH);
    float lat = dot(fromCam, side);
    lat += dot(float2(normalWS.x, normalWS.z), side) * 1.8;
    float halfW = lerp(1.6, 5.5, saturate(along / 100.0));
    half lane = (half)saturate(1.0 - abs(lat) / max(halfW, 1e-3));
    lane = lane * lane * (3.0h - 2.0h * lane);
    half near = (half)saturate((along - 1.2) / 10.0);
    lane *= near * lit * stable * (half)saturate(elev * 2.8 + 0.12);
    if (lane < 1e-4h)
    {
        return 0.0h;
    }

    // U：侧向落在单列贴图中心；V：沿光迹滚动（不平铺出第二道）。
    float u = saturate(lat / max(halfW * 2.4, 1e-3) * 0.5 + 0.5);
    float v = frac(along / max((float)_GlintTile, 12.0) - t * 0.12);
    float2 st = float2(u, v);
#if defined(OIL_MOBILE_PERF)
    half3 g = (half3)SAMPLE_TEXTURE2D(_GlintMap, sampler_GlintMap, st).rgb;
#else
    half3 g0 = (half3)SAMPLE_TEXTURE2D(_GlintMap, sampler_GlintMap, st).rgb;
    half3 g1 = (half3)SAMPLE_TEXTURE2D(
        _GlintMap, sampler_GlintMap, st + float2(0.0, 0.37)).rgb;
    half3 g = max(g0, g1 * 0.7h);
#endif
    half dab = saturate((PaintLuma(g) - 0.12h) * 2.6h);
    dab *= lerp(0.55h, 1.15h, stroke);
    return dab * lane;
}

half4 OilRiverNPRPassFragment(Varyings input) : SV_TARGET
{
    UNITY_SETUP_INSTANCE_ID(input);

    float t = _Time.y;
    half crest;
    half3 flowN = RiverFlowNormal(input.flowUv, t, crest);
    half3 geoN = normalize((half3)input.normalWS);
    half3 normalWS = normalize(lerp(geoN, flowN, 0.55h));
    // 光照用法线压平，避免 NdotL / 高光在河面打出圆形光斑。
    half3 lightN = half3(0.0h, 1.0h, 0.0h);
    half3 viewDir = normalize((half3)(_WorldSpaceCameraPos - input.positionWS));

    half3 albedo = lerp(_ShallowColor.rgb, _BaseColor.rgb, 0.45h);
    albedo *= (half)_AlbedoBoost;

    // Monet 水彩：横向切片采样 PaintMap。
    float2 paintSt = RiverMonetPaintUv(
        input.flowUv, t, _PaintTile, _StrokeBands);
    half3 paint = SampleRiverPaint(paintSt);
    half stroke = PaintStroke(PaintLuma(paint), (half)_PaintContrast);
    half relief = saturate((half)_PaintRelief);
    albedo = lerp(albedo, paint, relief);
    albedo = ImpastoDeviation(albedo, stroke, relief * 0.55h);

    // 始终采 Depth（Copy Depth 已开）。岸缘用 Intersection，不用 FoamMap。
    Fragment fragment = GetFragment(input.positionCS_SS);
    half2 shore = OilRiverShoreFoamMask(
        fragment,
        float3(normalWS.x, normalWS.z, crest),
        input.flowUv,
        input.meshUv,
        crest,
        t,
        _IntersectionNoise,
        sampler_IntersectionNoise);
    half intersection = shore.x;
    half shoreWash = shore.y;

    half3 whiteEdge = half3(0.94h, 0.97h, 0.99h);
    half3 shorePigment = lerp(albedo, whiteEdge, 0.7h);
    albedo = lerp(albedo, _ShallowColor.rgb, shoreWash * 0.28h);
    albedo = lerp(albedo, shorePigment, intersection * 0.72h);

    Surface surface;
    surface.position = input.positionWS;
    surface.normal = (float3)lightN;
    surface.interpolatedNormal = input.normalWS;
    surface.viewDirection = (float3)viewDir;
    surface.depth = -TransformWorldToView(input.positionWS).z;
    surface.color = (float3)albedo;
    surface.alpha = (float)_BaseColor.a;
    surface.metallic = 0.0;
    surface.occlusion = 1.0;
    surface.smoothness = 0.0;
    surface.fresnelStrength = 0.0;
    surface.dither = InterleavedGradientNoise(input.positionCS_SS.xy, 0);
    surface.renderingLayerMask = asuint(unity_RenderingLayer.x);

    half3 color = OilNPRLightingEx(
        surface,
        albedo,
        OilPeriodAmbient((half3)_AmbientColor.rgb),
        (half3)_ShadowTint.rgb,
        (half3)_ShadowWarm.rgb,
        (half3)_SpecularColor.rgb,
        (half)_SpecularThreshold,
        max((half)_ShadeSteps, 3.0h),
        0.0h,
        (half)_ShadowLift,
        (half)_ShadeLift,
        stroke,
        (half)_ShadowWobble,
        1.0h,
        0.0h,
        fragment.screenUV);

    color = ImpastoDeviation(color, stroke, relief * 0.45h);
#if !defined(OIL_MOBILE_PERF)
    color = OilWeaveTint(color, paintSt * 6.0);
#endif

    // 太阳光痕一道：世界车道遮罩 + 单列 GlintMap，不要 Blinn 圆斑、不要多列平铺。
    half glint = OilRiverSunGlint(input.positionWS, normalWS, t, stroke);
    glint *= (1.0h - intersection * 0.85h);
    color += (half3)_GlintColor.rgb * glint * (half)_GlintStrength;

    color = (half3)ApplyOilFog((float3)color, surface.depth, stroke, input.positionWS);

    half3 shoreEdge = lerp(color, whiteEdge, 0.72h);
    color = lerp(color, _ShallowColor.rgb, shoreWash * 0.22h);
    color = lerp(color, shoreEdge, intersection * 0.68h);

    float viewDepth = max(fragment.bufferDepth - fragment.depth, 0.0);
    float clearReach = max((float)_IntersectionLength * 1.6, 1.8);
    half glass = (half)(1.0 - smoothstep(0.05, clearReach, viewDepth));
    half deepA = (half)_BaseColor.a;
    half shallowA = (half)_ShallowColor.a;
    half alpha = lerp(deepA, shallowA, glass * 0.65h);
    alpha = lerp(alpha, min(shallowA, 0.55h), glass * glass * 0.5h);
    alpha = saturate(alpha + intersection * 0.18h + relief * 0.08h);

#if defined(_PREMULTIPLY_ALPHA)
    color *= alpha;
#endif

    return half4(saturate(color), alpha);
}

#endif
