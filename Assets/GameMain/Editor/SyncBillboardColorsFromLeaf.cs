using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Game
{
    /// <summary>
    /// Copy non-main color fields from each tree's LOD0 leaf mat onto its LOD_Billboard mat(s).
    /// Skips _BaseColor / _Color / _MainColor.
    /// </summary>
    public static class SyncBillboardColorsFromLeaf
    {
        const string PrefabsRoot = "Assets/GameMain/Environment/Prefabs/DaoYu1";
        static readonly string[] Folders = { "Songshu", "Lvshu" };

        static readonly string[] SyncProps =
        {
            "_TopColor", "_BottomColor", "_AmbientColor", "_ShadowTint", "_ShadowWarm",
            "_SpecularColor", "_OutlineColor"
        };

        [MenuItem("Window/Game/Sync Billboard Colors From Leaf")]
        public static void RunFromMenu()
        {
            int n = SyncAll();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            EditorUtility.DisplayDialog(
                "Sync Billboard Colors",
                $"已同步 {n} 个 Billboard 材质（非主色字段对齐 LOD0 树叶材质）。",
                "OK");
        }

        public static int SyncAll()
        {
            int updated = 0;
            var seen = new HashSet<string>();

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
                        if (!TryGetLeafAndBillboardMats(root, out Material leaf, out List<Material> billboards))
                            continue;

                        for (int i = 0; i < billboards.Count; i++)
                        {
                            Material bb = billboards[i];
                            if (bb == null)
                                continue;
                            string assetPath = AssetDatabase.GetAssetPath(bb);
                            if (string.IsNullOrEmpty(assetPath) || !seen.Add(assetPath))
                                continue;

                            if (CopyColors(leaf, bb))
                            {
                                EditorUtility.SetDirty(bb);
                                updated++;
                                Debug.Log($"[SyncBillboardColors] {assetPath} <- {leaf.name}");
                            }
                        }

                        // Also Materials/Billboard twin by name
                        string twin = $"Assets/GameMain/Environment/Materials/Billboard/{root.name}_Billboard.mat";
                        var twinMat = AssetDatabase.LoadAssetAtPath<Material>(twin);
                        if (twinMat != null && seen.Add(twin))
                        {
                            if (CopyColors(leaf, twinMat))
                            {
                                EditorUtility.SetDirty(twinMat);
                                updated++;
                            }
                        }
                    }
                    finally
                    {
                        PrefabUtility.UnloadPrefabContents(root);
                    }
                }
            }

            return updated;
        }

        static bool TryGetLeafAndBillboardMats(
            GameObject root, out Material leaf, out List<Material> billboards)
        {
            leaf = null;
            billboards = new List<Material>();

            var lod = root.GetComponentInChildren<LODGroup>(true);
            if (lod == null)
                return false;

            LOD[] lods = lod.GetLODs();
            if (lods == null || lods.Length == 0 || lods[0].renderers == null)
                return false;

            for (int i = 0; i < lods[0].renderers.Length; i++)
            {
                var mr = lods[0].renderers[i] as MeshRenderer;
                if (mr == null || mr.sharedMaterials == null)
                    continue;
                for (int m = 0; m < mr.sharedMaterials.Length; m++)
                {
                    var mat = mr.sharedMaterials[m];
                    if (mat == null)
                        continue;
                    string n = mat.name;
                    if (n.Contains("Leaf") || n.Contains("Vegetation"))
                    {
                        leaf = mat;
                        break;
                    }
                }

                if (leaf != null)
                    break;
            }

            if (leaf == null)
                return false;

            var transforms = root.GetComponentsInChildren<Transform>(true);
            for (int t = 0; t < transforms.Length; t++)
            {
                if (transforms[t].name != "LOD_Billboard")
                    continue;
                var mr = transforms[t].GetComponent<MeshRenderer>();
                if (mr == null || mr.sharedMaterials == null || mr.sharedMaterials.Length == 0)
                    continue;
                if (mr.sharedMaterials[0] != null)
                    billboards.Add(mr.sharedMaterials[0]);
            }

            return billboards.Count > 0;
        }

        static bool CopyColors(Material leaf, Material bb)
        {
            bool dirty = false;
            for (int i = 0; i < SyncProps.Length; i++)
            {
                string p = SyncProps[i];
                if (!leaf.HasProperty(p) || !bb.HasProperty(p))
                    continue;
                Color src = leaf.GetColor(p);
                Color dst = bb.GetColor(p);
                if (ColorsNearlyEqual(src, dst))
                    continue;
                bb.SetColor(p, src);
                dirty = true;
            }

            return dirty;
        }

        static bool ColorsNearlyEqual(Color a, Color b)
        {
            const float eps = 1e-4f;
            return Mathf.Abs(a.r - b.r) <= eps
                && Mathf.Abs(a.g - b.g) <= eps
                && Mathf.Abs(a.b - b.b) <= eps
                && Mathf.Abs(a.a - b.a) <= eps;
        }
    }
}
