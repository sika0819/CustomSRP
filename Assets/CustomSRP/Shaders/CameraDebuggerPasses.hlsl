#ifndef CAMERA_DEBUGGER_PASSES_INCLUDED
#define CAMERA_DEBUGGER_PASSES_INCLUDED

#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Debug.hlsl"

float _DebugOpacity;
float _ColorLUTResolution;

TEXTURE3D(_ColorGradingLUT);

struct Varyings
{
    float4 positionCS_SS : SV_POSITION;
    float2 screenUV : VAR_SCREEN_UV;
};

Varyings DefaultPassVertex(uint vertexID : SV_VertexID)
{
    Varyings output;
    output.positionCS_SS = float4(
        vertexID <= 1 ? -1.0 : 3.0,
        vertexID == 1 ? 3.0 : -1.0,
        0.0, 1.0
    );
    output.screenUV = float2(
        vertexID <= 1 ? 0.0 : 2.0,
        vertexID == 1 ? 2.0 : 0.0
    );
    if (_ProjectionParams.x < 0.0)
    {
        output.screenUV.y = 1.0 - output.screenUV.y;
    }
    return output;
}

float4 ForwardPlusTilesPassFragment(Varyings input) : SV_TARGET
{
    ForwardPlusTile tile = GetForwardPlusTile(input.screenUV);
    float3 color;
    if (tile.IsMinimumEdgePixel(input.screenUV))
    {
        color = 1.0;
    }
    else
    {
        color = OverlayHeatMap(
            input.screenUV * _CameraBufferSize.zw, tile.GetScreenSize(),
            tile.GetLightCount(), tile.GetMaxLightsPerTile(), 1.0).rgb;
    }
    return float4(color, _DebugOpacity);
}

Varyings ColorLUTPassVertex(uint vertexID : SV_VertexID)
{
    Varyings output;

    float height = 2.0 * _CameraBufferSize.y * _CameraBufferSize.z;
    height /= _ColorLUTResolution;

    float bottom, top;
    if (_ProjectionParams.x < 0.0)
    {
        bottom = 1.0;
        top = 1.0 - height;
    }
    else
    {
        bottom = -1.0;
        top = height - 1.0;
    }

    if (vertexID == 0)
    {
        output.positionCS_SS = float4(-1.0, bottom, 0.0, 1.0);
        output.screenUV = float2(0.0, 0.0);
    }
    else if (vertexID == 1 || vertexID == 4)
    {
        output.positionCS_SS = float4(-1.0, top, 0.0, 1.0);
        output.screenUV = float2(0.0, 1.0);
    }
    else if (vertexID == 2 || vertexID == 3)
    {
        output.positionCS_SS = float4(1.0, bottom, 0.0, 1.0);
        output.screenUV = float2(_ColorLUTResolution, 0.0);
    }
    else
    {
        output.positionCS_SS = float4(1.0, top, 0.0, 1.0);
        output.screenUV = float2(_ColorLUTResolution, 1.0);
    }

    return output;
}

float4 ColorLUTPassFragment(Varyings input) : SV_TARGET
{
    float3 uvw;
    uvw.x = frac(input.screenUV.x);
    uvw.y = input.screenUV.y;
    uvw.z = (input.screenUV.x - uvw.x) / _ColorLUTResolution;
    return SAMPLE_TEXTURE3D(
        _ColorGradingLUT, sampler_linear_clamp, uvw);
}

#endif
