using System;
using UnityEngine;

namespace CustomSRP
{
    /// <summary>实例 float4.w：tileId | dry | tint | flags，各 8bit。</summary>
    public static class OilTerrainGrassPacked
    {
        public const byte FlagFlower = 1;
        public const byte FlagWheat = 2;

        public static float PackW(byte tileId, byte dry, byte tint, byte flags)
        {
            uint bits = tileId
                | ((uint)dry << 8)
                | ((uint)tint << 16)
                | ((uint)flags << 24);
            return BitConverter.Int32BitsToSingle(unchecked((int)bits));
        }

        public static Vector4 Pack(Vector3 pos, int variant, byte dry)
        {
            byte tile = TileOf(variant);
            byte tint = TintOf(pos);
            byte flags = FlagsOf(variant);
            return new Vector4(pos.x, pos.y, pos.z, PackW(tile, dry, tint, flags));
        }

        public static byte TintOf(Vector3 pos)
        {
            float h = Frac(Mathf.Sin(pos.x * 12.9898f + pos.z * 78.233f) * 43758.5453f);
            return (byte)Mathf.Clamp(Mathf.RoundToInt(h * 255f), 0, 255);
        }

        public static byte FlagsOf(int variant)
        {
            byte f = 0;
            if (OilTerrainGrassBladeMesh.UsesAtlasColor(variant))
                f |= FlagFlower;
            if (variant == (int)OilTerrainGrassBladeMesh.Variant.Wheat)
                f |= FlagWheat;
            return f;
        }

        public static byte TileOf(int variant)
        {
            switch (variant)
            {
                case (int)OilTerrainGrassBladeMesh.Variant.Billboard: return 0;
                case (int)OilTerrainGrassBladeMesh.Variant.Patch: return 1;
                case (int)OilTerrainGrassBladeMesh.Variant.Single: return 1;
                case (int)OilTerrainGrassBladeMesh.Variant.Thick: return 2;
                case (int)OilTerrainGrassBladeMesh.Variant.Thin: return 3;
                case (int)OilTerrainGrassBladeMesh.Variant.Shrub: return 2;
                case (int)OilTerrainGrassBladeMesh.Variant.Wheat: return 4;
                case (int)OilTerrainGrassBladeMesh.Variant.Aster: return 5;
                case (int)OilTerrainGrassBladeMesh.Variant.Chamomile: return 6;
                case (int)OilTerrainGrassBladeMesh.Variant.Dandelions: return 7;
                case (int)OilTerrainGrassBladeMesh.Variant.Speedwell: return 8;
                case (int)OilTerrainGrassBladeMesh.Variant.Clover: return 9;
                default: return 1;
            }
        }

        public static int TopologyOf(int variant)
        {
            if ((uint)variant >= (uint)OilTerrainGrassBladeMesh.VariantCount)
                return (int)OilTerrainGrassBladeMesh.Variant.Patch;
            return variant;
        }

        static float Frac(float v) => v - Mathf.Floor(v);
    }
}
