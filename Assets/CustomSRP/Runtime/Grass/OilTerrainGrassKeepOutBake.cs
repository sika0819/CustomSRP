using System;
using System.IO;
using UnityEngine;

namespace CustomSRP
{
    /// <summary>岛级草地禁种遮罩（GKO1）。编辑器按 MapItem LOD0 轮廓烤一次。</summary>
    public static class OilTerrainGrassKeepOutBake
    {
        public const int Magic = 0x314F4B47; // GKO1
        public const int Version = 1;
        public const int DefaultRes = 1024;
        public const string EditorPath = "Assets/CustomSRP/Grass/Island_GrassKeepOut.bytes";
        public const string ResourcesPath = "Assets/Resources/OilTerrainGrass/Island_GrassKeepOut.bytes";

        public sealed class Data
        {
            public float OriginX;
            public float OriginZ;
            public float WorldSize;
            public int Res;
            public byte[] Mask;
        }

        public static byte[] Write(Data data)
        {
            if (data?.Mask == null || data.Res <= 0)
                return null;
            int len = data.Res * data.Res;
            if (data.Mask.Length != len || data.WorldSize < 1f)
                return null;

            using (var stream = new MemoryStream(16 + len))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(Magic);
                writer.Write(Version);
                writer.Write(data.OriginX);
                writer.Write(data.OriginZ);
                writer.Write(data.WorldSize);
                writer.Write(data.Res);
                writer.Write(data.Mask);
                return stream.ToArray();
            }
        }

        public static bool TryRead(byte[] bytes, out Data data)
        {
            data = null;
            if (bytes == null || bytes.Length < 24)
                return false;

            using (var stream = new MemoryStream(bytes, writable: false))
            using (var reader = new BinaryReader(stream))
            {
                if (reader.ReadInt32() != Magic)
                    return false;
                if (reader.ReadInt32() != Version)
                    return false;

                var parsed = new Data
                {
                    OriginX = reader.ReadSingle(),
                    OriginZ = reader.ReadSingle(),
                    WorldSize = reader.ReadSingle(),
                    Res = reader.ReadInt32(),
                };
                if (parsed.Res <= 0 || parsed.Res > 4096 || parsed.WorldSize < 1f)
                    return false;

                int len = parsed.Res * parsed.Res;
                if (stream.Length - stream.Position < len)
                    return false;

                parsed.Mask = reader.ReadBytes(len);
                if (parsed.Mask == null || parsed.Mask.Length != len)
                    return false;

                data = parsed;
                return true;
            }
        }

        public static bool Contains(Data data, float worldX, float worldZ)
        {
            if (data?.Mask == null || data.Res <= 0)
                return false;
            float cell = data.WorldSize / data.Res;
            if (cell < 1e-4f)
                return false;
            int gx = (int)Mathf.Floor((worldX - data.OriginX) / cell);
            int gz = (int)Mathf.Floor((worldZ - data.OriginZ) / cell);
            if ((uint)gx >= (uint)data.Res || (uint)gz >= (uint)data.Res)
                return false;
            return data.Mask[gz * data.Res + gx] != 0;
        }

        /// <summary>禁种格膨胀一圈，建筑脚下多留空隙。</summary>
        public static void Dilate(Data data, int radius = 1)
        {
            if (data?.Mask == null || data.Res <= 0 || radius <= 0)
                return;
            int res = data.Res;
            var src = data.Mask;
            var dst = new byte[src.Length];
            System.Array.Copy(src, dst, src.Length);
            for (int z = 0; z < res; z++)
            {
                for (int x = 0; x < res; x++)
                {
                    if (src[z * res + x] == 0)
                        continue;
                    for (int dz = -radius; dz <= radius; dz++)
                    {
                        int zz = z + dz;
                        if ((uint)zz >= (uint)res)
                            continue;
                        for (int dx = -radius; dx <= radius; dx++)
                        {
                            int xx = x + dx;
                            if ((uint)xx >= (uint)res)
                                continue;
                            dst[zz * res + xx] = 1;
                        }
                    }
                }
            }

            data.Mask = dst;
        }

        /// <summary>把值为 1 的格子按切比雪夫距离膨胀，写入调用方提供的缓冲。</summary>
        public static void DilateBuffer(byte[] mask, int res, int radius)
        {
            if (mask == null || res <= 0 || radius <= 0 || mask.Length != res * res)
                return;
            var dst = new byte[mask.Length];
            Array.Copy(mask, dst, mask.Length);
            for (int z = 0; z < res; z++)
            {
                for (int x = 0; x < res; x++)
                {
                    if (mask[z * res + x] == 0)
                        continue;
                    for (int dz = -radius; dz <= radius; dz++)
                    {
                        int zz = z + dz;
                        if ((uint)zz >= (uint)res)
                            continue;
                        for (int dx = -radius; dx <= radius; dx++)
                        {
                            int xx = x + dx;
                            if ((uint)xx >= (uint)res)
                                continue;
                            dst[zz * res + xx] = 1;
                        }
                    }
                }
            }

            Array.Copy(dst, mask, mask.Length);
        }
    }
}
