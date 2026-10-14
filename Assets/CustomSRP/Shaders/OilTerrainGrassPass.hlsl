#ifndef CUSTOM_OIL_TERRAIN_GRASS_PASS_INCLUDED
#define CUSTOM_OIL_TERRAIN_GRASS_PASS_INCLUDED

#include "../ShaderLibrary/Surface.hlsl"
#include "../ShaderLibrary/Shadows.hlsl"
#include "../ShaderLibrary/Light.hlsl"
#include "OilTerrainGrassWind.hlsl"

float4 _OilPeriodAmbient;

StructuredBuffer<float4> _AllInstancesPosWSBuffer;
StructuredBuffer<uint> _VisibleInstanceOnlyTransformIDBuffer;

TEXTURE2D(_GrassTexture);
SAMPLER(sampler_GrassTexture);

CBUFFER_START(UnityPerMaterial)
    float4 _GrassTexture_ST;
    half4 _BaseColor;
    half4 _GroundColor;
    half4 _AmbientColor;
    float _GrassWidth;
    float _GrassHeight;
    float _WindStrength;
    float _RandomNormal;
    float _AlphaClip;
    float _Billboard;
    float _UseTexColor;
    float _ShadeSteps;
    float _ShadeLift;
    float4 _GrassAtlasRects[16];
CBUFFER_END

half QuantizeGrassNdotL(half ndotL, half steps)
{
    steps = max(steps, 2.0h);
    return floor(ndotL * steps) / (steps - 1.0h);
}

half3 OilTerrainGrassVertexLit(half3 albedo, half3 normalWS, float3 positionWS)
{
    half3 ambient = _AmbientColor.rgb;
    if (_OilPeriodAmbient.a > 0.5h)
        ambient = (half3)_OilPeriodAmbient.rgb;

    if (GetDirectionalLightCount() <= 0)
        return ambient * albedo;

    DirectionalLightData lightData = _DirectionalLightData[0];
    half3 lightColor = (half3)lightData.color.rgb;
    half3 L = (half3)lightData.directionAndMask.xyz;

#if defined(OIL_MOBILE_PERF)
    half attenuation = 1.0h;
#else
    Surface surface;
    surface.position = positionWS;
    surface.normal = (float3)normalWS;
    surface.interpolatedNormal = (float3)normalWS;
    surface.viewDirection = normalize(_WorldSpaceCameraPos - positionWS);
    surface.depth = -TransformWorldToView(positionWS).z;
    surface.color = (float3)albedo;
    surface.alpha = 1.0;
    surface.metallic = 0.0;
    surface.occlusion = 1.0;
    surface.smoothness = 0.0;
    surface.fresnelStrength = 0.0;
    surface.dither = 0.5;
    surface.renderingLayerMask = asuint(unity_RenderingLayer.x);
    ShadowData shadowData = GetShadowData(surface);
    Light light = GetDirectionalLight(0, surface, shadowData);
    lightColor = (half3)light.color;
    L = (half3)light.direction;
    half attenuation = (half)light.attenuation;
#endif

    half halfLambert = saturate(dot(normalWS, L) * 0.5h + 0.5h);
    half lit = halfLambert * lerp(max((half)_ShadeLift, 0.2h), 1.0h, attenuation);
    lit = QuantizeGrassNdotL(saturate(lit), (half)_ShadeSteps);

    half3 litColor = albedo * (ambient * 0.45h + lightColor);
    half3 umbraColor = albedo * half3(0.2h, 0.14h, 0.36h) + half3(0.02h, 0.012h, 0.035h);
    umbraColor = lerp(umbraColor, albedo * half3(0.42h, 0.48h, 0.72h), 0.35h);
    return lerp(umbraColor, litColor, lit);
}

half3 OilWheatAlbedo(half tip, float3 positionWS)
{
    half3 top = half3(0.96h, 0.78h, 0.18h);
    half3 bottom = half3(0.58h, 0.46h, 0.14h);
    half wobble = (half)(sin(positionWS.x * 0.7 + positionWS.z * 1.1) * 0.5 + 0.5);
    return lerp(bottom, top, saturate(tip * 1.15h + wobble * 0.08h));
}

struct Attributes
{
    float4 positionOS : POSITION;
    float2 uv : TEXCOORD0;
    uint instanceID : SV_InstanceID;
};

struct Varyings
{
    float4 positionCS_SS : SV_POSITION;
    float2 uv : TEXCOORD0;
    float3 positionWS : TEXCOORD1;
    half3 color : COLOR;
    half fade : TEXCOORD2;
    half useTex : TEXCOORD3;
    half3 normalWS : TEXCOORD4;
};

Varyings OilTerrainGrassPassVertex(Attributes input)
{
    Varyings output;
    uint id = _VisibleInstanceOnlyTransformIDBuffer[input.instanceID];
    float4 inst = _AllInstancesPosWSBuffer[id];
    float3 pivotWS = inst.xyz;
    uint bits = asuint(inst.w);
    uint tileId = bits & 0xFFu;
    float dry = ((bits >> 8) & 0xFFu) * (1.0 / 255.0);
    float tint01 = ((bits >> 16) & 0xFFu) * (1.0 / 255.0);
    uint flags = bits >> 24;
    bool flower = (flags & 1u) != 0u;
    bool wheat = (flags & 2u) != 0u;

    float hash = sin(pivotWS.x * 23.4643 + pivotWS.z);
    float hashW = sin(pivotWS.x * 95.4643 + pivotWS.z);
    float hashY = sin(pivotWS.x * 12.9898 + pivotWS.z * 78.233);
    float perHeight = lerp(0.88, 1.14, hash * 0.45 + 0.55) * _GrassHeight * lerp(1.0, 0.50, dry);
    float fade = OilGrassDistanceFade(pivotWS);
    perHeight *= fade;
    float widthJitter = lerp(0.90, 1.12, hashW * 0.45 + 0.55) * _GrassWidth * lerp(1.0, 0.72, dry);
    float yaw = hashY * 6.2831853;

    float3 local = input.positionOS.xyz;
    local.x *= widthJitter;
    local.z *= widthJitter;
    local.y *= perHeight;

    float3 rotated;
    if (_Billboard > 0.5)
    {
        float3 camRight = UNITY_MATRIX_V[0].xyz;
        rotated = local.x * camRight + float3(0, local.y, 0);
    }
    else
    {
        float s, c;
        sincos(yaw, s, c);
        rotated = float3(local.x * c + local.z * s, local.y, -local.x * s + local.z * c);
    }

    float tip = flower ? saturate(input.positionOS.y) : saturate(input.uv.y);
    float3 windDir = _OilGrassWindDir;
    if (dot(windDir, windDir) < 1e-6)
        windDir = float3(cos(yaw), 0, -sin(yaw));
    windDir = normalize(windDir);
    if (_Billboard > 0.5)
        windDir = UNITY_MATRIX_V[0].xyz;

    float wind = OilGrassWindDisplace(pivotWS, _WindStrength, tip, dry);
    // Flowers sway less.
    if (flower)
        wind *= 0.55;
    float3 positionWS = pivotWS + rotated + windDir * wind;

    float4 rect = _GrassAtlasRects[tileId];
    float2 uv = input.uv;
    // Flower mesh UVs already baked into FlowerAtlas space — do not re-crop.
    if (!(flower || _UseTexColor > 0.5))
        uv = rect.xy + uv * rect.zw;

    output.positionCS_SS = TransformWorldToHClip(positionWS);
    output.positionWS = positionWS;
    output.uv = TRANSFORM_TEX(uv, _GrassTexture);
    output.fade = fade;
    output.useTex = flower ? 1.0h : (half)_UseTexColor;

    half3 n = normalize(half3(0, 1, 0) + (_RandomNormal * hash) * (half3)windDir);
    output.normalWS = n;

    if (flower)
    {
        output.color = 1;
        return output;
    }

    half3 albedo;
    if (wheat)
    {
        albedo = OilWheatAlbedo((half)saturate(input.uv.y), positionWS);
    }
    else
    {
        half tintShift = (half)(tint01 * 2.0 - 1.0);
        half3 bladeSrc = _BaseColor.rgb;
        half3 young = bladeSrc * half3(1.18h, 1.22h, 1.05h);
        half3 old = bladeSrc * half3(0.72h, 0.78h, 0.62h);
        half3 blade = lerp(old, young, saturate((half)tint01));
        blade *= lerp(half3(1.0h, 1.0h, 1.0h), half3(1.08h, 1.02h, 0.86h), saturate(tintShift));
        half dryh = (half)dry;
        half3 straw = half3(0.62h, 0.54h, 0.32h);
        half3 strawRoot = half3(0.42h, 0.34h, 0.20h);
        half3 ground = lerp(_GroundColor.rgb, strawRoot, dryh);
        blade = lerp(blade, straw, dryh);
        half tipAmt = (half)saturate(input.uv.y);
        albedo = lerp(ground, blade, tipAmt);
    }

    output.color = OilTerrainGrassVertexLit(albedo, n, positionWS);
    return output;
}

half4 OilTerrainGrassPassFragment(Varyings input) : SV_TARGET
{
    half4 tex = SAMPLE_TEXTURE2D(_GrassTexture, sampler_GrassTexture, input.uv);
    half alpha = tex.a * (half)input.fade;
    half clipT = input.useTex > 0.5h ? min((half)_AlphaClip, 0.34h) : (half)_AlphaClip;
    clip(alpha - clipT);

    half3 rgb;
    if (input.useTex > 0.5h)
    {
        half3 lit = OilTerrainGrassVertexLit(tex.rgb, input.normalWS, input.positionWS);
        rgb = lit;
    }
    else
    {
        rgb = input.color * lerp(half3(0.92h, 0.95h, 0.88h), tex.rgb, 0.35h);
    }

    float viewZ = -TransformWorldToView(input.positionWS).z;
#if defined(OIL_MOBILE_PERF)
    rgb = (half3)ApplyOilFog((float3)rgb, viewZ, 0.5h);
#else
    rgb = (half3)ApplyOilFog((float3)rgb, viewZ, 0.5h, input.positionWS);
#endif
    return half4(rgb, 1.0h);
}

#endif
