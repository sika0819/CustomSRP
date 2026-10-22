#ifndef CUSTOM_OIL_TERRAIN_GRASS_PASS_INCLUDED
#define CUSTOM_OIL_TERRAIN_GRASS_PASS_INCLUDED

#include "../ShaderLibrary/Surface.hlsl"
#include "../ShaderLibrary/Shadows.hlsl"
#include "../ShaderLibrary/Light.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/EntityLighting.hlsl"
#include "OilTerrainGrassWind.hlsl"
#include "OilVegetationLighting.hlsl"

StructuredBuffer<float4> _AllInstancesPosWSBuffer;
StructuredBuffer<uint> _VisibleInstanceOnlyTransformIDBuffer;

TEXTURE2D(_GrassTexture);
SAMPLER(sampler_GrassTexture);

CBUFFER_START(UnityPerMaterial)
    float4 _GrassTexture_ST;
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

half3 OilTerrainGrassVertexLit(half3 albedo, half3 normalWS, float3 positionWS)
{
    half3 N = normalize((half3)normalWS);
    half3 lightColor = 0.0h;
    half3 L = half3(0.0h, 1.0h, 0.0h);
    half attenuation = 0.0h;
    half lightLum = 0.0h;

    if (GetDirectionalLightCount() > 0)
    {
        DirectionalLightData lightData = _DirectionalLightData[0];
        lightColor = (half3)lightData.color.rgb;
        L = (half3)lightData.directionAndMask.xyz;
        lightLum = OilVegMainLightLuma(lightColor);

#if defined(OIL_MOBILE_PERF)
        attenuation = 1.0h;
#else
        Surface surface;
        surface.position = positionWS;
        surface.normal = (float3)N;
        surface.interpolatedNormal = (float3)N;
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
        attenuation = (half)light.attenuation;
        lightLum = OilVegMainLightLuma(lightColor);
#endif
    }

    // Same OilVegetationDirect + NightKeep as near leaves / far tree cross-cards.
    half3 lit = OilVegetationDirect(albedo, N, lightColor, L, attenuation, lightLum);
    return OilVegetationNightKeep(lit, albedo, N, lightLum);
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
    uint flags = bits >> 24;
    bool flower = (flags & 1u) != 0u;

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
    // 花 mesh 自带 FlowerAtlas UV；草 / 小麦按图集格裁 UV。
    if (!flower)
        uv = rect.xy + uv * rect.zw;

    output.positionCS_SS = TransformWorldToHClip(positionWS);
    output.positionWS = positionWS;
    output.uv = TRANSFORM_TEX(uv, _GrassTexture);
    output.fade = fade;
    output.useTex = 1.0h;

    half3 n = normalize(half3(0, 1, 0) + (_RandomNormal * hash) * (half3)windDir);
    output.normalWS = n;
    output.color = 1;
    return output;
}

half4 OilTerrainGrassPassFragment(Varyings input) : SV_TARGET
{
    OilBakeClipAboveWater(input.positionWS);
    half4 tex = SAMPLE_TEXTURE2D(_GrassTexture, sampler_GrassTexture, input.uv);
    half alpha = tex.a * (half)input.fade;
    half clipT = input.useTex > 0.5h ? min((half)_AlphaClip, 0.34h) : (half)_AlphaClip;
    clip(alpha - clipT);

    half3 rgb = OilTerrainGrassVertexLit(tex.rgb, input.normalWS, input.positionWS);

    float viewZ = -TransformWorldToView(input.positionWS).z;
#if defined(OIL_MOBILE_PERF)
    rgb = (half3)ApplyOilFog((float3)rgb, viewZ, 0.5h);
#else
    rgb = (half3)ApplyOilFog((float3)rgb, viewZ, 0.5h, input.positionWS);
#endif
    return half4(rgb, 1.0h);
}

#endif
