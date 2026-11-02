#ifndef CUSTOM_OIL_WATERFALL_NPR_PASS_INCLUDED
#define CUSTOM_OIL_WATERFALL_NPR_PASS_INCLUDED

#define OIL_NPR_NO_OTHER_LIGHTS
#include "OilNPRLighting.hlsl"
#include "OilPaintNoise.hlsl"
#include "OilWaterReflect.hlsl"

TEXTURE2D(_MistMap);
SAMPLER(sampler_MistMap);

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

Varyings OilWaterfallNPRPassVertex(Attributes input)
{
    Varyings output;
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_TRANSFER_INSTANCE_ID(input, output);

    float3 positionWS = TransformObjectToWorld(input.positionOS);
    output.positionWS = positionWS;
    output.positionCS_SS = TransformWorldToHClip(positionWS);
    output.normalWS = TransformObjectToWorldNormal(input.normalOS);
    output.meshUv = input.uv;
    output.flowUv = WaterfallMeshFlowUv(input.uv);
    return output;
}

half SampleWaterfallMist(float2 meshUv, float t)
{
    float2 st = meshUv * max(_MistTile, 0.5);
    st += float2(t * _MistSpeed * 0.35, t * _MistSpeed * 0.9);
#if defined(OIL_MOBILE_PERF)
    half3 m = (half3)SAMPLE_TEXTURE2D(_MistMap, sampler_MistMap, st).rgb;
#else
    half3 m0 = (half3)SAMPLE_TEXTURE2D(_MistMap, sampler_MistMap, st).rgb;
    half3 m1 = (half3)SAMPLE_TEXTURE2D(_MistMap, sampler_MistMap, st * 1.67 + 0.27).rgb;
    half3 m = max(m0, m1 * 0.8h);
#endif
    return saturate((PaintLuma(m) - 0.12h) * 2.2h);
}

half4 OilWaterfallNPRPassFragment(Varyings input) : SV_TARGET
{
    UNITY_SETUP_INSTANCE_ID(input);
    OilBakeClipAboveWater(input.positionWS);

    float t = _Time.y;
    half3 viewDir = normalize((half3)(_WorldSpaceCameraPos - input.positionWS));
    half v = (half)saturate(input.meshUv.y);

    half3 sheet = OilInlandWaterPigment();

    float2 paintSt = WaterfallMonetPaintUv(
        input.flowUv, t, _PaintTile, _StrokeBands);
    half3 paint = SampleWaterfallPaint(paintSt);
    half stroke = PaintStroke(PaintLuma(paint), 3.0h);
    sheet = ImpastoStrokeAlbedo(sheet, paint, stroke, 0.55h);

    half teeth = saturate((PaintLuma(paint) - 0.55h) * 2.4h);
    teeth *= lerp(0.65h, 1.2h, stroke) * (half)_TeethStrength;

    half topBand = 1.0h - smoothstep(0.0h, 0.18h, v);
    half botBand = smoothstep(0.72h, 1.0h, v);

    half mistTex = SampleWaterfallMist(input.meshUv, t);
    half feather = (half)max(_MistFeather, 0.05);
    half mistFade = smoothstep(0.0h, feather, v)
        * smoothstep(0.0h, feather, 1.0h - v);
    half mistZone = max(botBand * 1.15h, topBand * 0.35h);
    half mist = saturate(mistTex * mistZone * mistFade * (half)_MistStrength);

    Surface s;
    s.position = input.positionWS;
    // 和河面一样朝上受光。竖帘法线会把侧光收成另一套时段色。
    s.normal = float3(0.0, 1.0, 0.0);
    s.interpolatedNormal = s.normal;
    s.viewDirection = (float3)viewDir;
    s.depth = -TransformWorldToView(input.positionWS).z;
    s.color = (float3)sheet;
    s.alpha = (float)_BaseColor.a;
    s.metallic = 0.0;
    s.occlusion = 1.0;
    s.smoothness = 0.0;
    s.fresnelStrength = 0.0;
    s.dither = InterleavedGradientNoise(input.positionCS_SS.xy, 0);
    s.renderingLayerMask = asuint(unity_RenderingLayer.x);

#if defined(OIL_MOBILE_PERF)
    Fragment fragment = GetFragmentScreen(input.positionCS_SS);
#else
    Fragment fragment = GetFragment(input.positionCS_SS);
#endif

    half3 color = OilNPRLightingEx(
        s,
        sheet,
        OilNPRAmbient(s),
        half3(0.78h, 0.74h, 0.52h),
        1.0h,
        3.0h,
        0.0h,
        stroke,
        0.0h,
        1.0h,
        0.0h,
        s.smoothness,
        fragment.screenUV);

    color = ImpastoDeviation(color, stroke, 0.45h);
#if !defined(OIL_MOBILE_PERF)
    color = OilWeaveTint(color, paintSt * 6.0);
#endif

    float depth = s.depth;
#if defined(OIL_MOBILE_PERF)
    color = (half3)ApplyOilFog((float3)color, depth, stroke);
#else
    color = (half3)ApplyOilFog((float3)color, depth, stroke, input.positionWS);
#endif

    half night = OilWaterNightWeight(_OilSkyPeriod);
    half3 pigment = OilInlandWaterPigment();
    color = ApplyOilWaterReflectionEx(
        color,
        input.positionWS,
        half3(0.0h, 1.0h, 0.0h),
        viewDir,
        stroke,
        1.0h,
        fragment.screenUV,
        0.35h,
        3.0h,
        lerp(0.78h, 0.40h, night),
        pigment,
        pigment,
        true);
    color = OilInlandWaterHoldHue(color, pigment);

    half alpha = lerp((half)_BaseColor.a, (half)_ShallowColor.a, v * 0.5h);
    alpha = saturate(alpha + teeth * 0.18h);
    alpha = lerp(alpha, max(alpha * 0.8h, (half)_MistColor.a * 0.55h), mist * 0.45h);

#if defined(_PREMULTIPLY_ALPHA)
    color *= alpha;
#endif

    return half4(saturate(color), alpha);
}

#endif
