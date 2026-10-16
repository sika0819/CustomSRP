using UnityEngine;

namespace CustomSRP
{
    /// <summary>
    /// 普通草 + 小麦共用 GrassAtlas（4×2）；花模型共用 FlowerAtlas（mesh UV 已按图集排布）。
    /// </summary>
    public static class OilTerrainGrassAtlas
    {
        public const int TileCount = 16;
        public const int GrassTileCount = 7;
        public const int GrassCols = 4;
        public const int GrassRows = 2;

        /// <summary>普通草 + 小麦图集。</summary>
        public const string EditorPath = "Assets/CustomSRP/Grass/GrassAtlas.png";
        public const string ResourcesPath = "OilTerrainGrass/GrassAtlas";

        /// <summary>花图集：Aster / Chamomile / Dandelions / Clover / Speedwell 共用。</summary>
        public const string FlowerEditorPath = "Assets/CustomSRP/Grass/FlowerAtlas.png";
        public const string FlowerResourcesPath = "OilTerrainGrass/FlowerAtlas";

        public const string WheatEditorPath = "Assets/CustomSRP/Grass/Wheat.png";
        public const string LavenderEditorPath = FlowerEditorPath;

        /// <summary>
        /// 单张源贴图（打图集用 / 编辑器预览）。运行时草走 <see cref="EditorPath"/>。
        /// </summary>
        public static readonly string[] VariantSourceEditorPaths =
        {
            "Assets/CustomSRP/Grass/Grass_Billboard.png",
            "Assets/CustomSRP/Grass/Grass_Patch.png",
            "Assets/CustomSRP/Grass/Grass_Single.png",
            "Assets/CustomSRP/Grass/Grass_Thick.png",
            "Assets/CustomSRP/Grass/Grass_Thin.png",
            "Assets/CustomSRP/Grass/Grass_Shrub.png",
            WheatEditorPath,
            FlowerEditorPath, // Aster
            FlowerEditorPath, // Chamomile
            FlowerEditorPath, // Dandelions
            FlowerEditorPath, // Speedwell
            FlowerEditorPath, // Clover
            LavenderEditorPath,
        };

        /// <summary>与 <see cref="OilTerrainGrassBladeMesh.Variant"/> 一一对应的运行时加载路径（编辑器）。</summary>
        public static readonly string[] VariantEditorPaths =
        {
            EditorPath, // Billboard
            EditorPath, // Patch
            EditorPath, // Single
            EditorPath, // Thick
            EditorPath, // Thin
            EditorPath, // Shrub
            EditorPath, // Wheat
            FlowerEditorPath, // Aster
            FlowerEditorPath, // Chamomile
            FlowerEditorPath, // Dandelions
            FlowerEditorPath, // Speedwell
            FlowerEditorPath, // Clover
            FlowerEditorPath, // Lavender
        };

        /// <summary>Resources.Load 相对路径（无扩展名）。</summary>
        public static readonly string[] VariantResourcesPaths =
        {
            ResourcesPath,
            ResourcesPath,
            ResourcesPath,
            ResourcesPath,
            ResourcesPath,
            ResourcesPath,
            ResourcesPath,
            FlowerResourcesPath,
            FlowerResourcesPath,
            FlowerResourcesPath,
            FlowerResourcesPath,
            FlowerResourcesPath,
            FlowerResourcesPath,
        };

        public static readonly Vector4 FullUv = new Vector4(0f, 0f, 1f, 1f);

        /// <summary>
        /// FlowerAtlas 内花冠 UV 矩形（Unity V=0 底部）。
        /// 0 Aster / 1 Chamomile / 2 Dandelions / 3 Speedwell。
        /// OBJ 花模型已自带图集 UV，不依赖此表；程序网格裁格用。
        /// </summary>
        public static readonly Vector4[] FlowerSrcRects =
        {
            new Vector4(0.430f, 0.288f, 0.310f, 0.310f),
            new Vector4(0.426f, 0.634f, 0.350f, 0.350f),
            new Vector4(0.010f, 0.622f, 0.375f, 0.375f),
            new Vector4(0.818f, 0.008f, 0.160f, 0.960f),
        };

        /// <summary>GrassAtlas 4×2 格（V=0 底部 = Billboard 行）。</summary>
        public static readonly Vector4[] GrassRects = BuildGrassRects();

        public static readonly Vector4[] DefaultRects = BuildGridRects();
        public static readonly Vector4[] FullRects = BuildFullRects();

        public static bool UsesGrassAtlas(int variant) =>
            (uint)variant < (uint)GrassTileCount;

        public static Vector4 Rect(int tileId)
        {
            if ((uint)tileId < (uint)GrassRects.Length)
                return GrassRects[tileId];
            int i = (uint)tileId < (uint)DefaultRects.Length ? tileId : 1;
            return DefaultRects[i];
        }

        /// <summary>
        /// 某株型材质用的 rect 表。旧 bake 的 tileId 恒为 0，故草图集把 16 槽都填成该株型格子。
        /// </summary>
        public static Vector4[] RectsForTopology(int topology)
        {
            if (!UsesGrassAtlas(topology))
                return FullRects;

            Vector4 r = GrassRects[topology];
            var rects = new Vector4[TileCount];
            for (int i = 0; i < TileCount; i++)
                rects[i] = r;
            return rects;
        }

        static Vector4[] BuildGrassRects()
        {
            var rects = new Vector4[GrassTileCount];
            float sw = 1f / GrassCols;
            float sh = 1f / GrassRows;
            for (int i = 0; i < GrassTileCount; i++)
            {
                int x = i % GrassCols;
                int y = i / GrassCols;
                rects[i] = new Vector4(x * sw, y * sh, sw, sh);
            }

            return rects;
        }

        static Vector4[] BuildGridRects()
        {
            const int grid = 4;
            var rects = new Vector4[TileCount];
            float s = 1f / grid;
            for (int i = 0; i < TileCount; i++)
            {
                int x = i % grid;
                int y = i / grid;
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
