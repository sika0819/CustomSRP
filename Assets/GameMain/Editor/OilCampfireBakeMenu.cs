#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using UnityGameFramework.Runtime;

namespace Game.Editor
{
    /// <summary>
    /// 在选中的 Huodui / 带 FireController 的对象上预烘焙 UnityFire 噪声火焰平面。
    /// </summary>
    public static class OilCampfireBakeMenu
    {
        [MenuItem("GameMain/Boluo/Bake Oil Campfire Particles")]
        public static void BakeSelected()
        {
            GameObject[] sel = Selection.gameObjects;
            if (sel == null || sel.Length == 0)
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                    "Assets/GameMain/Environment/Prefabs/DaoYu1/Other/Huodui.prefab");
                if (prefab == null)
                {
                    Log.Error("[OilCampfire] Select a Huodui or open the prefab.");
                    return;
                }

                BakePrefabAsset(prefab);
                return;
            }

            int n = 0;
            for (int i = 0; i < sel.Length; i++)
            {
                if (BakeInstance(sel[i]))
                    n++;
            }

            Log.Info("[OilCampfire] Baked UnityFire NoiseFire on " + n + " object(s).");
        }

        [MenuItem("GameMain/Boluo/Bake Oil Campfire Particles (Huodui Prefab)")]
        public static void BakeHuoduiPrefab()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/GameMain/Environment/Prefabs/DaoYu1/Other/Huodui.prefab");
            if (prefab == null)
            {
                Log.Error("[OilCampfire] Huodui.prefab not found.");
                return;
            }

            BakePrefabAsset(prefab);
        }

        static void BakePrefabAsset(GameObject prefabAsset)
        {
            string path = AssetDatabase.GetAssetPath(prefabAsset);
            GameObject root = PrefabUtility.LoadPrefabContents(path);
            try
            {
                if (!BakeInstance(root))
                {
                    Log.Warning("[OilCampfire] Bake skipped — no FireController added?");
                    return;
                }

                PrefabUtility.SaveAsPrefabAsset(root, path);
                Log.Info("[OilCampfire] Saved UnityFire NoiseFire bake → " + path);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        static bool BakeInstance(GameObject go)
        {
            if (go == null)
                return false;

            FireController fire = go.GetComponent<FireController>()
                                  ?? go.GetComponentInChildren<FireController>(true);
            if (fire == null)
                fire = go.AddComponent<FireController>();

            var mat = AssetDatabase.LoadAssetAtPath<Material>(Game.SceneAssets.CampfireNoiseFireMatPath);
            if (mat != null)
                fire.BindFlameMaterial(mat);
            else
                Log.Warning("[OilCampfire] Missing material: " + Game.SceneAssets.CampfireNoiseFireMatPath);

            fire.EnsureRuntimeBindings();
            EditorUtility.SetDirty(go);
            return true;
        }
    }
}
#endif
