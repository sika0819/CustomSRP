#ifndef CUSTOM_FORWARD_PLUS_INCLUDED
#define CUSTOM_FORWARD_PLUS_INCLUDED

// xy: Screen UV to tile coordinates.
// z: Tiles per row, as integer.
// w: Tile data size, as integer.
float4 _ForwardPlusTileSettings;

#if defined(SHADER_API_GLES3) || defined(SHADER_API_GLES)
// GLES: skip tiles SSBO (Adreno BufferBlock limit). OilNPROtherLights iterates
// the packed other-light CBUFFER instead of per-tile indices.
#define CUSTOM_SRP_FORWARD_PLUS_STUB 1
#else
StructuredBuffer<int> _ForwardPlusTiles;
#endif

struct ForwardPlusTile
{
    int2 coordinates;
    int index;

    int GetTileDataSize()
    {
        return asint(_ForwardPlusTileSettings.w);
    }

    int GetHeaderIndex()
    {
        return index * GetTileDataSize();
    }

    int GetLightCount()
    {
#if defined(CUSTOM_SRP_FORWARD_PLUS_STUB)
        return 0;
#else
        return _ForwardPlusTiles[GetHeaderIndex()];
#endif
    }

    int GetFirstLightIndexInTile()
    {
        return GetHeaderIndex() + 1;
    }

    int GetLastLightIndexInTile()
    {
        return GetHeaderIndex() + GetLightCount();
    }

    int GetLightIndex(int lightIndexInTile)
    {
#if defined(CUSTOM_SRP_FORWARD_PLUS_STUB)
        return 0;
#else
        return _ForwardPlusTiles[lightIndexInTile];
#endif
    }

    bool IsMinimumEdgePixel(float2 screenUV)
    {
        float2 startUV = coordinates / _ForwardPlusTileSettings.xy;
        return any(screenUV - startUV < _CameraBufferSize.xy);
    }

    int GetMaxLightsPerTile()
    {
        return GetTileDataSize() - 1;
    }

    int2 GetScreenSize()
    {
        return int2(round(_CameraBufferSize.zw / _ForwardPlusTileSettings.xy));
    }
};

ForwardPlusTile GetForwardPlusTile(float2 screenUV)
{
    ForwardPlusTile tile;
    tile.coordinates = int2(screenUV * _ForwardPlusTileSettings.xy);
    tile.index = tile.coordinates.y * asint(_ForwardPlusTileSettings.z) +
        tile.coordinates.x;
    return tile;
}

#endif
