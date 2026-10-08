#ifndef CUSTOM_TERRAIN_LIT_SHADOW_PASS_INCLUDED
#define CUSTOM_TERRAIN_LIT_SHADOW_PASS_INCLUDED

bool _ShadowPancaking;

struct Attributes
{
    float3 positionOS : POSITION;
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
    float3 positionWS = TransformObjectToWorld(input.positionOS);
    output.positionCS_SS = TransformWorldToHClip(positionWS);

    if (_ShadowPancaking)
    {
#if UNITY_REVERSED_Z
        output.positionCS_SS.z =
            min(output.positionCS_SS.z, output.positionCS_SS.w * UNITY_NEAR_CLIP_VALUE);
#else
        output.positionCS_SS.z =
            max(output.positionCS_SS.z, output.positionCS_SS.w * UNITY_NEAR_CLIP_VALUE);
#endif
    }

    return output;
}

void TerrainLitShadowPassFragment(Varyings input)
{
    UNITY_SETUP_INSTANCE_ID(input);
}

#endif
