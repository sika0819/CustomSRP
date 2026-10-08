#ifndef CUSTOM_COMMON_INCLUDED
#define CUSTOM_COMMON_INCLUDED

#include "../Shaders/UnityInput.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/CommonMaterial.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Packing.hlsl"

SAMPLER(sampler_linear_clamp);
SAMPLER(sampler_point_clamp);

bool IsOrthographicCamera()
{
    return unity_OrthoParams.w;
}

float OrthographicDepthBufferToLinear(float rawDepth)
{
#if UNITY_REVERSED_Z
    rawDepth = 1.0 - rawDepth;
#endif
    return (_ProjectionParams.z - _ProjectionParams.y) * rawDepth + _ProjectionParams.y;
}

#include "Fragment.hlsl"
#include "ForwardPlus.hlsl"

float Square(float v)
{
    return v * v;
}

float DistanceSquared(float3 pA, float3 pB)
{
    return dot(pA - pB, pA - pB);
}

float3 DecodeNormal(float4 sample, float scale)
{
#if defined(UNITY_NO_DXT5nm)
    return normalize(UnpackNormalRGB(sample, scale));
#else
    return normalize(UnpackNormalMapRGorAG(sample, scale));
#endif
}

float3 NormalTangentToWorld(float3 normalTS, float3 normalWS, float4 tangentWS)
{
    float3x3 tangentToWorld =
        CreateTangentToWorld(normalWS, tangentWS.xyz, tangentWS.w);
    return TransformTangentToWorld(normalTS, tangentToWorld);
}

#endif
