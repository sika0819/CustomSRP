using System.Runtime.InteropServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace CustomSRP
{
    [StructLayout(LayoutKind.Sequential)]
    public struct OilOceanVertex
    {
        public float3 position;
        public float3 normal;
        public float4 tangent;
        public float2 uv;
        public float2 shorePigment;
    }

    /// <summary>
    /// Multi-octave Perlin (noise.cnoise) with ridged peaks — viscous paint, not sine swell.
    /// </summary>
    public static class OilOceanNoise
    {
        public static float Sample(float2 p, float time, float seed, float heightScale)
        {
            // Long swells. A 10 m grid cannot hold shorter crests without flat triangles.
            float2 q = p * 0.0024f;
            float t = time;

            float swell = noise.cnoise(new float2(q.x * 1.1f + t * 0.07f, q.y * 0.95f - t * 0.05f));
            float roll = noise.cnoise(new float2(q.x * 2.2f - t * 0.11f, q.y * 1.8f + t * 0.08f + seed));
            float ridge = 1f - math.abs(noise.cnoise(new float2(q.x * 1.7f + t * 0.05f, q.y * 1.5f + seed)));
            ridge = ridge * ridge;

            float h = swell * 0.62f + roll * 0.28f + ridge * 0.16f;
            h += (seed - 0.5f) * 0.18f;
            return h * heightScale;
        }

        public static float3 NormalFromHeights(float hC, float hX, float hZ, float epsilon)
        {
            return math.normalize(new float3(hC - hX, epsilon, hC - hZ));
        }
    }

    /// <summary>
    /// Displace a continuous water-grid vertex (shared topology, no stroke gaps).
    /// </summary>
    [BurstCompile]
    public struct OilOceanVertexDisplaceJob : IJobParallelFor
    {
        public NativeArray<OilOceanVertex> vertices;
        public float2 origin;
        public float time;
        public float waveHeight;
        public float normalEps;

        public void Execute(int index)
        {
            OilOceanVertex v = vertices[index];
            // Local rest XZ stays put; noise is in world space so the grid can follow the camera.
            float2 xz = new float2(v.position.x, v.position.z) + origin;
            v.uv = xz;
            float seed = v.shorePigment.y;
            float y = OilOceanNoise.Sample(xz, time + seed * 2f, seed, waveHeight);

            // One extra octave for the slope. The fragment shader owns the short-wave normal.
            float e = normalEps;
            float2 q = xz * 0.0024f;
            float t = time + seed * 2f;
            float swell = noise.cnoise(new float2(q.x * 1.1f + t * 0.07f, q.y * 0.95f - t * 0.05f));
            float2 qx = (xz + new float2(e, 0f)) * 0.0024f;
            float2 qz = (xz + new float2(0f, e)) * 0.0024f;
            float swellX = noise.cnoise(new float2(qx.x * 1.1f + t * 0.07f, qx.y * 0.95f - t * 0.05f));
            float swellZ = noise.cnoise(new float2(qz.x * 1.1f + t * 0.07f, qz.y * 0.95f - t * 0.05f));
            float yx = y + (swellX - swell) * waveHeight * 0.62f;
            float yz = y + (swellZ - swell) * waveHeight * 0.62f;
            float3 n = OilOceanNoise.NormalFromHeights(y, yx, yz, e);

            v.position.y = y;
            v.normal = n;
            v.tangent = new float4(-1f, 0f, 0f, -1f);
            vertices[index] = v;
        }
    }
}
