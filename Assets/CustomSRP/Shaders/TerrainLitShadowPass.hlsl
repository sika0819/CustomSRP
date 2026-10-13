#ifndef CUSTOM_TERRAIN_LIT_SHADOW_PASS_INCLUDED
#define CUSTOM_TERRAIN_LIT_SHADOW_PASS_INCLUDED

#include "TerrainInstancing.hlsl"

struct Attributes
{
    float3 positionOS : POSITION;
    float3 normalOS : NORMAL;
    float2 texcoord : TEXCOORD0;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct Varyings
{
    float4 positionCS_SS : SV_POSITION;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

Varyings TerrainLitShadowPassVertex(Attributes input)
{
    Varyings output;
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_TRANSFER_INSTANCE_ID(input, output);
    float3 positionOS = input.positionOS;
    float3 normalOS = input.normalOS;
    float2 texcoord = input.texcoord;
    ApplyTerrainInstancing(positionOS, normalOS, texcoord);
    float3 positionWS = TransformObjectToWorld(positionOS);
    output.positionCS_SS = TransformWorldToHClip(positionWS);
    // Skip shadow pancaking. A raised camera coarsens terrain patches to
    // hundreds of meters; clamping those vertices onto the shadow near plane
    // stretches them across the cascade and draws a disk on the ground.
    // The clipper keeps only the part of each triangle inside the frustum.
    return output;
}

void TerrainLitShadowPassFragment(Varyings input)
{
    UNITY_SETUP_INSTANCE_ID(input);
}

#endif
