using System.IO;
using UnityEditor;
using UnityEngine;

namespace Game
{
    /// <summary>
    /// LOD_Billboard mesh is 1-submesh cross; keep only materials[0] (*_Billboard), drop Trunk slot.
    /// </summary>
    public static class TrimLodBillboardMaterials
    {
        const string PrefabsRoot = "Assets/GameMain/Environment/Prefabs/DaoYu1";
        static readonly string[] Folders = { "Songshu", "Lvshu" };

        [MenuItem("Window/Game/Trim LOD_Billboard Materials (keep slot0)")]
        public static void RunFromMenu()
        {
            int n = TrimAll();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            EditorUtility.DisplayDialog(
                "Trim LOD_Billboard Materials",
                $"已处理 {n} 个 Prefab（只保留 materials[0]）。",
                "OK");
        }

        public static int TrimAll()
        {
            int count = 0;
            foreach (string folder in Folders)
            {
                string dir = Path.Combine(PrefabsRoot, folder).Replace('\\', '/');
                if (!Directory.Exists(dir))
                    continue;

                string[] files = Directory.GetFiles(dir, "*.prefab", SearchOption.AllDirectories);
                for (int i = 0; i < files.Length; i++)
                {
                    string path = files[i].Replace('\\', '/');
                    var root = PrefabUtility.LoadPrefabContents(path);
                    if (root == null)
                        continue;

                    try
                    {
                        bool dirty = false;
                        var transforms = root.GetComponentsInChildren<Transform>(true);
                        for (int t = 0; t < transforms.Length; t++)
                        {
                            if (transforms[t].name != "LOD_Billboard")
                                continue;

                            var mr = transforms[t].GetComponent<MeshRenderer>();
                            if (mr == null || mr.sharedMaterials == null || mr.sharedMaterials.Length <= 1)
                                continue;

                            var keep = mr.sharedMaterials[0];
                            mr.sharedMaterials = keep != null
                                ? new[] { keep }
                                : new Material[0];
                            dirty = true;
                        }

                        if (!dirty)
                            continue;

                        PrefabUtility.SaveAsPrefabAsset(root, path);
                        count++;
                        Debug.Log($"[TrimLodBillboard] {path}");
                    }
                    finally
                    {
                        PrefabUtility.UnloadPrefabContents(root);
                    }
                }
            }

            return count;
        }
    }
}
