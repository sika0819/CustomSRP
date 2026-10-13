#ifndef CUSTOM_OIL_WATERFALL_NPR_PASS_INCLUDED
#define CUSTOM_OIL_WATERFALL_NPR_PASS_INCLUDED

#include "OilNPRLighting.hlsl"
#include "OilPaintNoise.hlsl"

TEXTURE2D(_FoamMap);
SAMPLER(sampler_FoamMap);

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

half4 OilWaterfallNPRPassFragment(Varyings input) : SV_TARGET
{
    UNITY_SETUP_INSTANCE_ID(input);

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

    // 与 OilRiver 同色板：浅→深 0.45，避免白 Base 把帘刷成白蓝条。
    half3 sheet = lerp(_ShallowColor.rgb, _BaseColor.rgb, 0.45h);
    sheet *= (half)_AlbedoBoost;

    float2 paintUv = input.flowUv;
    paintUv.y *= max(_PaintStretch, 0.2);
    paintUv += float2(0.0, -t * 1.8);
    float waterNoise = OilFbm(paintUv * 0.22);
    half stroke = PaintStroke((half)waterNoise, (half)_PaintContrast);
    sheet = lerp(sheet, sheet * lerp(0.94h, 1.06h, stroke), saturate((half)_PaintRelief));

    float2 foamDir = float2(0.12, -1.0);
    float foamSpeedScale = 5.0;
    float2 foamUv = input.flowUv * max(_FoamTiling, 0.02)
        + t * _FoamSpeed * foamSpeedScale * foamDir;
    half foamTex = (half)SAMPLE_TEXTURE2D(_FoamMap, sampler_FoamMap, foamUv).r;
    half joinFade = WaterfallJoinFade(input.meshUv);
    // 竖帘碎沫：保留动感，但不洗成灰白。
    half sheetFoam = smoothstep(0.55h, 0.92h, foamTex)
        * (half)_CrestFoamStrength * 1.1h * joinFade;

    half topBand = 1.0h - smoothstep(0.0h, 0.16h, (half)input.meshUv.y);
    half botBand = smoothstep(0.78h, 1.0h, (half)input.meshUv.y);
    half splash = saturate((topBand + botBand) * smoothstep(0.4h, 0.85h, foamTex));

    float2 mistUv = input.meshUv * float2(4.0, 6.0)
        + float2(t * _MistSpeed * 0.4, t * _MistSpeed);
    half mistN = (half)OilFbm(mistUv);
    half feather = (half)max(_MistFeather, 0.05);
    half mistFade = smoothstep(0.0h, feather, (half)input.meshUv.y)
        * smoothstep(0.0h, feather, 1.0h - (half)input.meshUv.y);
    half mist = saturate((mistN - 0.35h) * 1.8h) * mistFade * (half)_MistStrength;

    half3 whiteFoam = half3(0.94h, 0.97h, 0.99h);
    half3 foamPigment = lerp(whiteFoam, _FoamColor.rgb, 0.35h);
    sheet = lerp(sheet, foamPigment, saturate(sheetFoam * 1.4h));
    sheet = lerp(sheet, foamPigment, splash * 0.55h);

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
        OilPeriodAmbient((half3)_AmbientColor.rgb),
        (half3)_ShadowTint.rgb,
        (half3)_ShadowWarm.rgb,
        (half3)_SpecularColor.rgb,
        (half)_SpecularThreshold,
        max((half)_ShadeSteps, 3.0h),
        0.0h,
        (half)_ShadowLift,
        (half)_ShadeLift,
        0.6h,
        (half)_ShadowWobble,
        1.0h,
        0.0h,
        fragment.screenUV);

    color = ImpastoDeviation(color, stroke, saturate((half)_PaintRelief) * 0.4h);
    color = lerp(color, foamPigment, saturate(sheetFoam * 1.2h + splash * 0.5h));
    color = lerp(color, _MistColor.rgb, mist * _MistColor.a * 0.45h);

    if (GetDirectionalLightCount() > 0)
    {
        half3 L = (half3)_DirectionalLightData[0].directionAndMask.xyz;
        half sunY = saturate((half)L.y + 0.05h);
        half3 H = normalize(L + viewDir);
        half spec = pow(saturate(dot(normalWS, H)), 96.0h);
        half specMask = smoothstep(
            (half)_SpecularThreshold - 0.06h,
            (half)_SpecularThreshold,
            spec) * sunY * topBand;
        color += (half3)_SpecularColor.rgb * specMask * 0.65h;
    }

    color = (half3)ApplyOilFog((float3)color, s.depth, stroke, input.positionWS);

    half alpha = lerp((half)_ShallowColor.a, (half)_BaseColor.a, 0.45h);
    alpha = saturate(alpha + sheetFoam * 0.35h + splash * 0.25h);
    alpha = lerp(alpha, max(alpha * 0.85h, (half)_MistColor.a * 0.5h), mist * 0.3h);

#if defined(_PREMULTIPLY_ALPHA)
    color *= alpha;
#endif

    return half4(saturate(color), alpha);
}

#endif
