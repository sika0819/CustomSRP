using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace CustomSRP
{
    // Burst [BurstCompile] on this IJobFor trips a Unity 6.3 / Burst 1.8 hashing bug:
    // "Failed to resolve method ... IJobFor.Execute" while compiling ForJobStruct.
    // Runs as a managed parallel job until Burst fixes the constrained interface resolve.
    public struct ForwardPlusTilesJob : IJobFor
    {
        [ReadOnly]
        public NativeArray<float4> lightBounds;

        [WriteOnly, NativeDisableParallelForRestriction]
        public NativeArray<int> tileData;

        public int otherLightCount;

        public float2 tileScreenUVSize;

        public int maxLightsPerTile;

        public int tilesPerRow;

        public int tileDataSize;

        public void Execute(int tileIndex)
        {
            int y = tileIndex / tilesPerRow;
            int x = tileIndex - y * tilesPerRow;
            float4 bounds = new float4(x, y, x + 1, y + 1) * tileScreenUVSize.xyxy;

            int headerIndex = tileIndex * tileDataSize;
            int dataIndex = headerIndex;
            int lightsInTileCount = 0;

            for (int i = 0; i < otherLightCount; i++)
            {
                float4 b = lightBounds[i];
                if (math.all(new float4(b.xy, bounds.xy) <= new float4(bounds.zw, b.zw)))
                {
                    tileData[++dataIndex] = i;
                    if (++lightsInTileCount >= maxLightsPerTile)
                    {
                        break;
                    }
                }
            }

            tileData[headerIndex] = lightsInTileCount;
        }
    }
}
