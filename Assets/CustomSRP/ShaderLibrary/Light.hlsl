#ifndef CUSTOM_LIGHT_INCLUDED
#define CUSTOM_LIGHT_INCLUDED

// Adreno GLES3 only guarantees a handful of SSBO/BufferBlock slots. Oil + terrain
// shaders previously declared 5–6 StructuredBuffers and failed to link
// ("BufferBlock location or component exceeds max allowed") → blank island.
// GLES path packs dir/other lights into CBUFFER mirrors (C# always uploads both).
#if defined(SHADER_API_GLES3) || defined(SHADER_API_GLES)
#define CUSTOM_SRP_LIGHT_CBUFFER 1
#define CUSTOM_SRP_MAX_DIR_LIGHTS 4
#define CUSTOM_SRP_MAX_OTHER_LIGHTS_GLES 8
#endif

CBUFFER_START(_CustomLight)
    int _DirectionalLightCount;
    int _OtherLightCount;
CBUFFER_END

struct DirectionalLightData
{
    float4 color, directionAndMask, shadowData;
};

struct OtherLightData
{
    float4 color, position, directionAndMask, spotAngle, shadowData;
};

#if defined(CUSTOM_SRP_LIGHT_CBUFFER)
CBUFFER_START(_CustomLightDataMobile)
    float4 _DirectionalLightColors[CUSTOM_SRP_MAX_DIR_LIGHTS];
    float4 _DirectionalLightDirectionsAndMasks[CUSTOM_SRP_MAX_DIR_LIGHTS];
    float4 _DirectionalLightShadowData[CUSTOM_SRP_MAX_DIR_LIGHTS];
    float4 _OtherLightColors[CUSTOM_SRP_MAX_OTHER_LIGHTS_GLES];
    float4 _OtherLightPositions[CUSTOM_SRP_MAX_OTHER_LIGHTS_GLES];
    float4 _OtherLightDirectionsAndMasks[CUSTOM_SRP_MAX_OTHER_LIGHTS_GLES];
    float4 _OtherLightSpotAngles[CUSTOM_SRP_MAX_OTHER_LIGHTS_GLES];
CBUFFER_END

DirectionalLightData GetDirectionalLightData(int index)
{
    DirectionalLightData data;
    data.color = _DirectionalLightColors[index];
    data.directionAndMask = _DirectionalLightDirectionsAndMasks[index];
    data.shadowData = _DirectionalLightShadowData[index];
    return data;
}

OtherLightData GetOtherLightData(int index)
{
    OtherLightData data;
    data.color = _OtherLightColors[index];
    data.position = _OtherLightPositions[index];
    data.directionAndMask = _OtherLightDirectionsAndMasks[index];
    data.spotAngle = _OtherLightSpotAngles[index];
    data.shadowData = 0.0;
    return data;
}
#else
StructuredBuffer<DirectionalLightData> _DirectionalLightData;
StructuredBuffer<OtherLightData> _OtherLightData;

DirectionalLightData GetDirectionalLightData(int index)
{
    return _DirectionalLightData[index];
}

OtherLightData GetOtherLightData(int index)
{
    return _OtherLightData[index];
}
#endif

struct Light
{
    float3 color;
    float3 direction;
    float attenuation;
    uint renderingLayerMask;
};

int GetDirectionalLightCount()
{
    return _DirectionalLightCount;
}

int GetOtherLightCount()
{
#if defined(CUSTOM_SRP_LIGHT_CBUFFER)
    return min(_OtherLightCount, CUSTOM_SRP_MAX_OTHER_LIGHTS_GLES);
#else
    return _OtherLightCount;
#endif
}

DirectionalShadowData GetDirectionalShadowData(
    float4 lightShadowData, ShadowData shadowData)
{
    DirectionalShadowData data;
    data.strength = lightShadowData.x * shadowData.strength;
    data.tileIndex = lightShadowData.y + shadowData.cascadeIndex;
    data.normalBias = lightShadowData.z;
    return data;
}

Light GetDirectionalLight(int index, Surface surfaceWS, ShadowData shadowData)
{
    DirectionalLightData data = GetDirectionalLightData(index);
    Light light;
    light.color = data.color.rgb;
    light.direction = data.directionAndMask.xyz;
    light.renderingLayerMask = asuint(data.directionAndMask.w);
    DirectionalShadowData directionalShadowData =
        GetDirectionalShadowData(data.shadowData, shadowData);
    light.attenuation =
        GetDirectionalShadowAttenuation(directionalShadowData, shadowData, surfaceWS);
    return light;
}

OtherShadowData GetOtherShadowData(float4 lightShadowData)
{
    OtherShadowData data;
    data.strength = lightShadowData.x;
    data.tileIndex = lightShadowData.y;
    data.isPoint = lightShadowData.z == 1.0;
    data.lightPositionWS = 0.0;
    data.lightDirectionWS = 0.0;
    data.spotDirectionWS = 0.0;
    return data;
}

Light GetOtherLight(int index, Surface surfaceWS)
{
    OtherLightData data = GetOtherLightData(index);
    Light light;
    light.color = data.color.rgb;
    float3 position = data.position.xyz;
    float3 ray = position - surfaceWS.position;
    light.direction = normalize(ray);
    float distanceSqr = max(dot(ray, ray), 0.00001);
    float rangeAttenuation = Square(
        saturate(1.0 - Square(distanceSqr * data.position.w)));
    float3 spotDirection = data.directionAndMask.xyz;
    light.renderingLayerMask = asuint(data.directionAndMask.w);
    float spotAttenuation = Square(
        saturate(dot(spotDirection, light.direction) *
            data.spotAngle.x + data.spotAngle.y));
    // Point/Spot never cast. OtherShadows.ReserveShadows uploads strength 0,
    // and this path must not sample _OtherShadowAtlas either.
    light.attenuation = spotAttenuation * rangeAttenuation / distanceSqr;
    return light;
}

#endif
