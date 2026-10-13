#ifndef CUSTOM_OIL_RIVER_NPR_PASS_INCLUDED
#define CUSTOM_OIL_RIVER_NPR_PASS_INCLUDED

#include "OilNPRLighting.hlsl"
#include "OilPaintNoise.hlsl"
#include "OilRiverShore.hlsl"

TEXTURE2D(_FoamMap);
SAMPLER(sampler_FoamMap);
TEXTURE2D(_IntersectionNoise);
SAMPLER(sampler_IntersectionNoise);

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

half4 OilRiverNPRPassFragment(Varyings input) : SV_TARGET
{
    UNITY_SETUP_INSTANCE_ID(input);

    float t = _Time.y;
    half crest;
    half3 flowN = RiverFlowNormal(input.flowUv, t, crest);
    half3 geoN = normalize((half3)input.normalWS);
    half3 normalWS = normalize(lerp(geoN, flowN, 0.55h));
    half3 viewDir = normalize((half3)(_WorldSpaceCameraPos - input.positionWS));

    half3 albedo = lerp(_ShallowColor.rgb, _BaseColor.rgb, 0.45h);
    albedo *= (half)_AlbedoBoost;

    float2 paintUv = input.flowUv * 0.11 + float2(t * 0.08, -t * 0.35);
    float waterNoise = OilFbm(paintUv);
    half stroke = PaintStroke((half)waterNoise, (half)_PaintContrast);
    albedo = lerp(albedo, albedo * lerp(0.94h, 1.06h, stroke), saturate((half)_PaintRelief));

    float2 foamDir = float2(0.12, -1.0);
    float2 foamUv = input.flowUv * max(_FoamTiling, 0.02)
        + t * _FoamSpeed * 5.0 * foamDir;
    half foamTex = (half)SAMPLE_TEXTURE2D(_FoamMap, sampler_FoamMap, foamUv).r;

    half joinFade = RiverJoinFade(input.meshUv);
    half crestStr = max((half)_CrestFoamStrength, 0.05h);
    // 河心只留高亮碎沫；拍岸交给 Depth。
    half midFoam = smoothstep(0.82h, 0.98h, foamTex) * crestStr * 0.22h;
    midFoam *= lerp(0.35h, 1.0h, joinFade);

    // 始终采 Depth（Copy Depth 已开）。OIL_MOBILE_PERF 不再清零岸沫。
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

    half3 whiteFoam = half3(0.94h, 0.97h, 0.99h);
    half3 midPigment = lerp(whiteFoam, _FoamColor.rgb, 0.25h);
    // 半透岸沫：水色透出，但边要看得见。
    half3 shorePigment = lerp(albedo, whiteFoam, 0.7h);
    albedo = lerp(albedo, _ShallowColor.rgb, shoreWash * 0.28h);
    albedo = lerp(albedo, shorePigment, intersection * 0.72h);
    albedo = lerp(albedo, midPigment, saturate(midFoam * 2.0h) * (1.0h - intersection));

    Surface surface;
    surface.position = input.positionWS;
    surface.normal = (float3)normalWS;
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
        0.6h,
        (half)_ShadowWobble,
        1.0h,
        0.0h,
        fragment.screenUV);

    color = ImpastoDeviation(color, stroke, saturate((half)_PaintRelief) * 0.4h);

    if (GetDirectionalLightCount() > 0)
    {
        half3 L = (half3)_DirectionalLightData[0].directionAndMask.xyz;
        half sunY = saturate((half)L.y + 0.05h);
        half3 H = normalize(L + viewDir);
        half spec = pow(saturate(dot(normalWS, H)), 64.0h);
        half specMask = smoothstep(
            (half)_SpecularThreshold - 0.06h,
            (half)_SpecularThreshold,
            spec) * sunY * (1.0h - midFoam) * (1.0h - intersection);
        color += (half3)_SpecularColor.rgb * specMask * 0.65h;
    }

    color = (half3)ApplyOilFog((float3)color, surface.depth, stroke, input.positionWS);

    // 雾后叠岸沫：半透，不盖死河色。
    half3 shoreEdge = lerp(color, whiteFoam, 0.72h);
    color = lerp(color, _ShallowColor.rgb, shoreWash * 0.22h);
    color = lerp(color, shoreEdge, intersection * 0.68h);
    color = lerp(color, midPigment, saturate(midFoam * 2.0h) * (1.0h - intersection));

    // 按水深降 alpha，让河床 / 岸底透出来（与 OilOcean glass 同思路）。
    float viewDepth = max(fragment.bufferDepth - fragment.depth, 0.0);
    float clearReach = max((float)_IntersectionLength * 2.4, 2.8);
    half glass = (half)(1.0 - smoothstep(0.05, clearReach, viewDepth));
    half deepA = (half)_BaseColor.a;
    half shallowA = (half)_ShallowColor.a;
    half alpha = lerp(deepA, shallowA, glass);
    // 贴岸再清一档，岸底更明显。
    alpha = lerp(alpha, min(shallowA, 0.3h), glass * glass);
    // 沫略提不透明，但不把水体拉回实心。
    alpha = saturate(alpha + midFoam * 0.3h + intersection * 0.22h);

#if defined(_PREMULTIPLY_ALPHA)
    color *= alpha;
#endif

    return half4(saturate(color), alpha);
}

#endif
