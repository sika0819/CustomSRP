using System.IO;
using UnityEditor;
using UnityEngine;

namespace CustomSRP.Editor
{
    /// <summary>
    /// Adds <see cref="OilFoliageVolumeFit"/> on DaoYu1 vegetation prefab roots
    /// so every mesh LOD (LOD0/1/2/…) gets mesh-local leaf volume bounds.
    /// </summary>
    public static class StampOilFoliageVolumeFit
    {
        const string PrefabsRoot = "Assets/GameMain/Environment/Prefabs/DaoYu1";
        static readonly string[] Folders = { "Songshu", "Lvshu", "Yeshu", "Guanmu" };

        [MenuItem("Window/Game/Stamp Oil Foliage Volume Fit")]
        public static void RunFromMenu()
        {
            int n = StampAll();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            EditorUtility.DisplayDialog(
                "Oil Foliage Volume Fit",
                $"已在 {n} 个预制体根上挂 OilFoliageVolumeFit（树/灌木各 mesh LOD 本地 Y 归一化）。",
                "OK");
        }

        public static int StampAll()
        {
            int updated = 0;
            foreach (string folder in Folders)
            {
                string dir = Path.Combine(PrefabsRoot, folder).Replace('\\', '/');
                if (!Directory.Exists(dir))
                    continue;

                foreach (string file in Directory.GetFiles(dir, "*.prefab", SearchOption.AllDirectories))
                {
                    string path = file.Replace('\\', '/');
                    var root = PrefabUtility.LoadPrefabContents(path);
                    if (root == null)
                        continue;

                    try
                    {
                        if (!HasOilFoliageRenderer(root))
                            continue;

                        var fit = root.GetComponent<OilFoliageVolumeFit>();
                        if (fit == null)
                        {
                            fit = root.AddComponent<OilFoliageVolumeFit>();
                            updated++;
                        }

                        fit.Apply();
                        PrefabUtility.SaveAsPrefabAsset(root, path);
                    }
                    finally
                    {
                        PrefabUtility.UnloadPrefabContents(root);
                    }
                }
            }

            return updated;
        }

        static bool HasOilFoliageRenderer(GameObject root)
        {
            var renderers = root.GetComponentsInChildren<MeshRenderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                Material[] mats = renderers[i].sharedMaterials;
                if (mats == null)
                    continue;
                for (int m = 0; m < mats.Length; m++)
                {
                    Material mat = mats[m];
                    if (mat != null && mat.HasProperty("_OilFoliage") && mat.GetFloat("_OilFoliage") > 0.5f)
                        return true;
                }
            }

            return false;
        }
    }
}
