#ifndef CUSTOM_TERRAIN_INSTANCING_INCLUDED
#define CUSTOM_TERRAIN_INSTANCING_INCLUDED

// Unity Terrain sets these globals when Draw Instanced is enabled.
float4 _TerrainHeightmapScale; // xz: meters/sample, y: height scale
float4 _TerrainHeightmapRecipSize; // xy: 1/size, zw: 1/(size-1)

TEXTURE2D(_TerrainHeightmapTexture);

UNITY_INSTANCING_BUFFER_START(Terrain)
    UNITY_DEFINE_INSTANCED_PROP(float4, _TerrainPatchInstanceData) // xy base, z skipScale
UNITY_INSTANCING_BUFFER_END(Terrain)

// Height texel. Clamp so the terrain border does not sample off the map and grow a skirt normal.
float TerrainSampleHeight(int2 texel)
{
    int2 last = int2(round(rcp(_TerrainHeightmapRecipSize.xy))) - 1;
    texel = clamp(texel, int2(0, 0), max(last, int2(0, 0)));
    return UnpackHeightmap(LOAD_TEXTURE2D(_TerrainHeightmapTexture, texel));
}

// Patch-edge texels in _TerrainNormalmapTexture point along the patch border.
// Quantized NdotL turns that into a light/dark line. The heightfield is continuous,
// so the shading normal comes from neighboring heights instead.
float3 TerrainNormalFromHeight(int2 texel)
{
    float hL = TerrainSampleHeight(texel + int2(-1, 0));
    float hR = TerrainSampleHeight(texel + int2(1, 0));
    float hD = TerrainSampleHeight(texel + int2(0, -1));
    float hU = TerrainSampleHeight(texel + int2(0, 1));
    float2 stepXZ = _TerrainHeightmapScale.xz * 2.0;
    float3 tx = float3(stepXZ.x, (hR - hL) * _TerrainHeightmapScale.y, 0.0);
    float3 tz = float3(0.0, (hU - hD) * _TerrainHeightmapScale.y, stepXZ.y);
    return normalize(cross(tz, tx));
}

// Mutates patch local position/normal/uv into terrain object space.
void ApplyTerrainInstancing(inout float3 positionOS, inout float3 normalOS, inout float2 uv)
{
#ifdef UNITY_INSTANCING_ENABLED
    float2 patchVertex = positionOS.xy;
    float4 instanceData = UNITY_ACCESS_INSTANCED_PROP(Terrain, _TerrainPatchInstanceData);
    float2 sampleCoords = (patchVertex.xy + instanceData.xy) * instanceData.z;
    // Same texel the height uses. Do not load _TerrainNormalmapTexture:
    // its patch-edge texels point along the border and quantize into a light seam.
    int2 texel = int2(sampleCoords);
    float height = TerrainSampleHeight(texel);
    positionOS.xz = sampleCoords * _TerrainHeightmapScale.xz;
    positionOS.y = height * _TerrainHeightmapScale.y;
    normalOS = TerrainNormalFromHeight(texel);
    uv = sampleCoords * _TerrainHeightmapRecipSize.zw;
#endif
}

void ApplyTerrainInstancing(inout float3 positionOS)
{
    float3 normalOS = float3(0.0, 1.0, 0.0);
    float2 uv = float2(0.0, 0.0);
    ApplyTerrainInstancing(positionOS, normalOS, uv);
}

#endif
