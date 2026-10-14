using UnityEngine;

namespace CustomSRP
{
    /// <summary>
    /// GrassAtlas 4×4：0=Tall 1=Field 2=Thick 3=Thin 4=Wheat。
    /// 花用独立 FlowerAtlas；mesh UV 已写入 FlowerSrcRects，shader 用 FullRects。
    /// </summary>
    public static class OilTerrainGrassAtlas
    {
        public const int TileCount = 16;
        public const int Grid = 4;
        public const string EditorPath = "Assets/CustomSRP/Grass/GrassAtlas.png";
        public const string FlowerEditorPath = "Assets/CustomSRP/Grass/FlowerAtlas.png";
        public const string WheatEditorPath = "Assets/CustomSRP/Grass/Wheat.png";

        public static readonly Vector4[] FlowerSrcRects =
        {
            new Vector4(0.430f, 0.288f, 0.310f, 0.310f),
            new Vector4(0.426f, 0.634f, 0.350f, 0.350f),
            new Vector4(0.010f, 0.622f, 0.375f, 0.375f),
            new Vector4(0.818f, 0.008f, 0.160f, 0.960f),
        };

        public static readonly Vector4[] DefaultRects = BuildGridRects();
        public static readonly Vector4[] FullRects = BuildFullRects();

        public static Vector4 Rect(int tileId)
        {
            int i = (uint)tileId < (uint)DefaultRects.Length ? tileId : 1;
            return DefaultRects[i];
        }

        static Vector4[] BuildGridRects()
        {
            var rects = new Vector4[TileCount];
            float s = 1f / Grid;
            for (int i = 0; i < TileCount; i++)
            {
                int x = i % Grid;
                int y = i / Grid;
                rects[i] = new Vector4(x * s, y * s, s, s);
            }

            return rects;
        }

        static Vector4[] BuildFullRects()
        {
            var rects = new Vector4[TileCount];
            for (int i = 0; i < TileCount; i++)
                rects[i] = new Vector4(0f, 0f, 1f, 1f);
            return rects;
        }
    }
}
