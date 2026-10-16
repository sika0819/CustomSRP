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
            // 草 + 小麦：GrassAtlas 格号 = variant；花仍用 mesh / FlowerAtlas 整图 UV。
            if (OilTerrainGrassAtlas.UsesGrassAtlas(variant))
                return (byte)variant;
            return 0;
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
