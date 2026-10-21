#ifndef CUSTOM_OIL_WATERFALL_NPR_PASS_INCLUDED
#define CUSTOM_OIL_WATERFALL_NPR_PASS_INCLUDED

#include "OilNPRLighting.hlsl"
#include "OilPaintNoise.hlsl"

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
    half crest;
    half3 sheetN = WaterfallSheetNormal(input.flowUv, t, crest);
    half3 geoN = normalize((half3)input.normalWS);
    // 双面帘：背面翻转几何法线再与波纹混合。
    if (dot(geoN, (half3)(_WorldSpaceCameraPos - input.positionWS)) < 0.0h)
    {
        geoN = -geoN;
    }
    half3 normalWS = normalize(lerp(geoN, sheetN, 0.55h));
    half3 viewDir = normalize((half3)(_WorldSpaceCameraPos - input.positionWS));

    // 顶缘偏深蓝，帘身偏浅白蓝（对齐参考油画）。
    half v = (half)saturate(input.meshUv.y);
    half3 sheet = lerp(_BaseColor.rgb, _ShallowColor.rgb, v * 0.55h + 0.2h);
    sheet *= (half)_AlbedoBoost;

    float2 paintSt = WaterfallMonetPaintUv(
        input.flowUv, t, _PaintTile, _StrokeBands);
    half3 paint = SampleWaterfallPaint(paintSt);
    half stroke = PaintStroke(PaintLuma(paint), (half)_PaintContrast);
    half relief = saturate((half)_PaintRelief);
    sheet = lerp(sheet, paint, relief);
    sheet = ImpastoDeviation(sheet, stroke, relief * 0.5h);

    // 白齿：亮笔拉成竖向钛白条（来自 PaintMap，不是 FoamMap）。
    half teeth = saturate((PaintLuma(paint) - 0.55h) * 2.4h);
    teeth *= lerp(0.65h, 1.2h, stroke) * (half)_TeethStrength;
    half3 whiteTeeth = half3(0.94h, 0.97h, 0.99h);
    sheet = lerp(sheet, whiteTeeth, teeth * 0.55h);

    half topBand = 1.0h - smoothstep(0.0h, 0.18h, v);
    half botBand = smoothstep(0.72h, 1.0h, v);

    half mistTex = SampleWaterfallMist(input.meshUv, t);
    half feather = (half)max(_MistFeather, 0.05);
    half mistFade = smoothstep(0.0h, feather, v)
        * smoothstep(0.0h, feather, 1.0h - v);
    // 底雾更重，顶缘轻薄喷溅。
    half mistZone = max(botBand * 1.15h, topBand * 0.35h);
    half mist = saturate(mistTex * mistZone * mistFade * (half)_MistStrength);

    Surface s;
    s.position = input.positionWS;
    s.normal = (float3)normalWS;
    s.interpolatedNormal = input.normalWS;
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
        (half3)_SpecularColor.rgb,
        (half)_SpecularThreshold,
        max((half)_ShadeSteps, 3.0h),
        0.0h,
        stroke,
        0.0h,
        1.0h,
        0.0h,
        s.smoothness,
        fragment.screenUV);

    color = ImpastoDeviation(color, stroke, relief * 0.4h);
#if !defined(OIL_MOBILE_PERF)
    color = OilWeaveTint(color, paintSt * 5.0);
#endif
    color = lerp(color, whiteTeeth, teeth * 0.28h);
    color = lerp(color, _MistColor.rgb, mist * _MistColor.a);

    if (GetDirectionalLightCount() > 0)
    {
        half3 L = (half3)_DirectionalLightData[0].directionAndMask.xyz;
        half sunY = saturate((half)L.y + 0.05h);
        half3 H = normalize(L + viewDir);
        half spec = pow(saturate(dot(normalWS, H)), 72.0h);
        half specMask = smoothstep(
            (half)_SpecularThreshold - 0.06h,
            (half)_SpecularThreshold,
            spec) * sunY * (topBand + teeth * 0.35h);
        color += (half3)_SpecularColor.rgb * specMask * 0.55h;
    }

    color = (half3)ApplyOilFog((float3)color, s.depth, stroke, input.positionWS);

    half alpha = lerp((half)_BaseColor.a, (half)_ShallowColor.a, v * 0.5h);
    alpha = saturate(alpha + teeth * 0.18h);
    alpha = lerp(alpha, max(alpha * 0.8h, (half)_MistColor.a * 0.55h), mist * 0.45h);

#if defined(_PREMULTIPLY_ALPHA)
    color *= alpha;
#endif

    return half4(saturate(color), alpha);
}

#endif
