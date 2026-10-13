using System.IO;
using UnityEditor;
using UnityEngine;

using UnityGameFramework.Runtime;
namespace Game.Editor
{
    /// <summary>
    /// Bakes Boluo DaoYu river meshes + waterfall FX into AssetRaw/Island/IslandInlandWater
    /// for ProcedureIsland YooAsset spawn (not in AllMapItem).
    /// Flag：Temp/build_inland_water.flag（存在则 delayCall 执行后删除）。
    /// </summary>
    public static class IslandInlandWaterBuilder
    {
        public const string PrefabPath = SceneAssets.IslandInlandWaterPrefabPath;
        public const string FlagRelPath = "Temp/build_inland_water.flag";

        [InitializeOnLoadMethod]
        static void WatchFlag()
        {
            EditorApplication.delayCall += TryConsumeFlag;
        }

        static void TryConsumeFlag()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                EditorApplication.delayCall += TryConsumeFlag;
                return;
            }

            string flag = Path.GetFullPath(Path.Combine(Application.dataPath, "..", FlagRelPath));
            if (!File.Exists(flag))
                return;
            try { File.Delete(flag); } catch { /* ignore */ }
            Log.Info("[InlandWater] Flag detected — building IslandInlandWater prefab");
            Build();
        }

        struct Placement
        {
            public string Path;
            public Vector3 Position;
            public Quaternion Rotation;
            public Vector3? Scale;
        }

        // Poses from Boluo DaoYu.unity / DaoYu_HM.unity (parent at origin).
        static readonly Placement[] Placements =
        {
            new Placement
            {
                Path = "Assets/GameMain/Environment/Prefabs/DaoYu1/Common/River_01.prefab",
                Position = new Vector3(238.85437f, 101.538124f, 637.07263f),
                Rotation = Quaternion.identity,
            },
            new Placement
            {
                Path = "Assets/GameMain/Environment/Prefabs/DaoYu1/Common/River_02.prefab",
                Position = new Vector3(435.38004f, 22.099998f, 424.85004f),
                Rotation = Quaternion.identity,
            },
            new Placement
            {
                Path = "Assets/GameMain/Environment/Prefabs/DaoYu1/Common/River_03.prefab",
                Position = new Vector3(352.2f, 64.2f, 435.9f),
                Rotation = Quaternion.identity,
            },
            new Placement
            {
                Path = "Assets/GameMain/Environment/Prefabs/DaoYu1/Common/River_04.prefab",
                Position = new Vector3(352.2f, 64.2f, 435.9f),
                Rotation = Quaternion.identity,
            },
            new Placement
            {
                Path = "Assets/GameMain/Environment/Prefabs/DaoYu1/texiao/WaterfallSplash.prefab",
                Position = new Vector3(423.81f, 66.34f, 457.4f),
                Rotation = Quaternion.Euler(0f, 80.507f, 0f),
                Scale = new Vector3(1.5176096f, 1.5176096f, 1.5176096f),
            },
            new Placement
            {
                Path = "Assets/GameMain/Environment/Prefabs/DaoYu1/texiao/WaterfallMist.prefab",
                Position = new Vector3(441.52f, 32.32f, 456.19f),
                Rotation = Quaternion.Euler(0f, 84.276f, 0f),
                Scale = new Vector3(3.8280227f, 8.553667f, 8.553667f),
            },
        };

        [MenuItem("GameMain/Boluo/Build Island Inland Water Prefab")]
        public static void Build()
        {
            Directory.CreateDirectory("Assets/AssetRaw/Island");

            var root = new GameObject("IslandInlandWater");
            int placed = 0;
            for (int i = 0; i < Placements.Length; i++)
            {
                Placement p = Placements[i];
                var source = AssetDatabase.LoadAssetAtPath<GameObject>(p.Path);
                if (source == null)
                {
                    Log.Error("[InlandWater] Missing " + p.Path);
                    continue;
                }

                GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(source);
                instance.transform.SetParent(root.transform, false);
                instance.transform.localPosition = p.Position;
                instance.transform.localRotation = p.Rotation;
                if (p.Scale.HasValue)
                    instance.transform.localScale = p.Scale.Value;
                placed++;
            }

            if (placed == 0)
            {
                Object.DestroyImmediate(root);
                Log.Error("[InlandWater] No placements — abort.");
                return;
            }

            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            Object.DestroyImmediate(root);
            AssetDatabase.SaveAssets();
            Log.Info("[InlandWater] Built " + PrefabPath + " children=" + placed);
        }
    }
}
