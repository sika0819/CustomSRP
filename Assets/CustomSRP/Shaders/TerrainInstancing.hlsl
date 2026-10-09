#ifndef CUSTOM_TERRAIN_INSTANCING_INCLUDED
#define CUSTOM_TERRAIN_INSTANCING_INCLUDED

// Unity Terrain sets these globals when Draw Instanced is enabled.
float4 _TerrainHeightmapScale; // xz: meters/sample, y: height scale
float4 _TerrainHeightmapRecipSize; // xy: 1/size, zw: 1/(size-1)

#ifdef UNITY_INSTANCING_ENABLED
TEXTURE2D(_TerrainHeightmapTexture);
TEXTURE2D(_TerrainNormalmapTexture);
#endif

UNITY_INSTANCING_BUFFER_START(Terrain)
    UNITY_DEFINE_INSTANCED_PROP(float4, _TerrainPatchInstanceData) // xy base, z skipScale
UNITY_INSTANCING_BUFFER_END(Terrain)

// Mutates patch local position/normal/uv into terrain object space.
void ApplyTerrainInstancing(inout float3 positionOS, inout float3 normalOS, inout float2 uv)
{
#ifdef UNITY_INSTANCING_ENABLED
    float2 patchVertex = positionOS.xy;
    float4 instanceData = UNITY_ACCESS_INSTANCED_PROP(Terrain, _TerrainPatchInstanceData);
    float2 sampleCoords = (patchVertex.xy + instanceData.xy) * instanceData.z;
    float height = UnpackHeightmap(
        LOAD_TEXTURE2D(_TerrainHeightmapTexture, int2(sampleCoords)));
    positionOS.xz = sampleCoords * _TerrainHeightmapScale.xz;
    positionOS.y = height * _TerrainHeightmapScale.y;
    normalOS = LOAD_TEXTURE2D(_TerrainNormalmapTexture, int2(sampleCoords)).rgb * 2.0 - 1.0;
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
