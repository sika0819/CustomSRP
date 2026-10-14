using UnityEngine;
using UnityGameFramework.Runtime;

namespace CustomSRP
{
    /// <summary>
    /// CustomSRP 侧草地接线：材质 / compute / atlas + 可选 bake bytes。
    /// 进岛由 Game.IslandGrassService 调用（反射），勿在编辑器菜单里误用 DontDestroyOnLoad。
    /// </summary>
    public static class OilTerrainGrassBootstrap
    {
        const string ComputeEditorPath = "Assets/CustomSRP/Grass/OilTerrainGrassCull.compute";
        const string MaterialEditorPath = "Assets/CustomSRP/Grass/OilTerrainGrass.mat";
        const string AtlasEditorPath = OilTerrainGrassAtlas.EditorPath;

        public static OilTerrainGrassBinder EnsureConfigured() =>
            EnsureConfigured(null);

        public static OilTerrainGrassBinder EnsureConfigured(byte[] bakeBytes)
        {
            OilTerrainGrassBinder binder = OilTerrainGrassBinder.Ensure();
            if (binder == null)
                return null;

            AssignAssets(binder);
            ApplyDeviceBudget(binder);

            if (bakeBytes != null && bakeBytes.Length > 0)
            {
                if (binder.TryLoadBake(bakeBytes))
                    Log.Info($"[OilTerrainGrass] bake loaded bytes={bakeBytes.Length}");
                else
                    Log.Error("[OilTerrainGrass] TryLoadBake failed (corrupt SGB1?)");
            }
            else if (!binder.HasBake)
            {
                Log.Warning(
                    "[OilTerrainGrass] no bake bytes passed — " +
                    "IslandGrassService should load AssetRaw/Configs/Island_GrassBake.bytes");
            }

            return binder;
        }

        static void AssignAssets(OilTerrainGrassBinder binder)
        {
            ComputeShader cs = LoadCompute();
            if (cs != null)
                binder.SetComputeShader(cs);

            Material mat = LoadMaterial();
            if (mat != null)
                binder.SetMaterialOverride(mat);

            Texture atlas = LoadTexture(AtlasEditorPath, "OilTerrainGrass/GrassAtlas");
            if (atlas != null)
                binder.SetAtlasTexture(atlas);

            Texture flower = LoadTexture(
                OilTerrainGrassAtlas.FlowerEditorPath, "OilTerrainGrass/FlowerAtlas");
            if (flower != null)
                binder.SetFlowerAtlas(flower);

            Texture wheat = LoadTexture(
                OilTerrainGrassAtlas.WheatEditorPath, "OilTerrainGrass/Wheat");
            if (wheat != null)
                binder.SetWheatTexture(wheat);
        }

        public static void ApplyDeviceBudget(OilTerrainGrassBinder binder)
        {
            if (binder == null)
                return;

#if UNITY_ANDROID || UNITY_IOS
            binder.ApplyBudget(24000, 90f, bladeSegments: 3);
#else
            binder.ApplyBudget(48000, 180f, bladeSegments: 4);
#endif
        }

        static ComputeShader LoadCompute()
        {
#if UNITY_EDITOR
            var cs = UnityEditor.AssetDatabase.LoadAssetAtPath<ComputeShader>(ComputeEditorPath);
            if (cs != null)
                return cs;
#endif
            return Resources.Load<ComputeShader>("OilTerrainGrass/OilTerrainGrassCull");
        }

        static Material LoadMaterial()
        {
#if UNITY_EDITOR
            var mat = UnityEditor.AssetDatabase.LoadAssetAtPath<Material>(MaterialEditorPath);
            if (mat != null)
                return mat;
#endif
            return Resources.Load<Material>("OilTerrainGrass/OilTerrainGrass");
        }

        static Texture LoadTexture(string editorPath, string resourcesPath)
        {
#if UNITY_EDITOR
            var tex = UnityEditor.AssetDatabase.LoadAssetAtPath<Texture>(editorPath);
            if (tex != null)
                return tex;
#endif
            return Resources.Load<Texture>(resourcesPath);
        }
    }
}
