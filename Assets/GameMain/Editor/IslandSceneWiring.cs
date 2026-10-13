using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
using UnityGameFramework.Runtime;

namespace Game.Editor
{
    /// <summary>
    /// Play entry = Launch only. Runtime loads IslandTerrain + IslandOcean + InlandWater via YooAsset
    /// (AssetRaw/Island + AssetRaw/Ocean; no Resources, no LoadScene).
    /// </summary>
    public static class IslandSceneWiring
    {
        public const string LaunchScene = SceneAssets.Launch;
        public const string TerrainPrefabPath = SceneAssets.IslandTerrainPrefabPath;
        public const string OceanPrefabPath = SceneAssets.IslandOceanPrefabPath;
        public const string InlandWaterPrefabPath = SceneAssets.IslandInlandWaterPrefabPath;

        const string PhonePath = "Assets/GameMain/UI/Settings/PhonePanelSettings.asset";
        const string PadPath = "Assets/GameMain/UI/Settings/PadPanelSettings.asset";

        [MenuItem("GameMain/Boluo/Wire Launch As Play Entry")]
        public static void WireLaunchAsPlayEntry()
        {
            PanelSettings phone = AssetDatabase.LoadAssetAtPath<PanelSettings>(PhonePath);
            PanelSettings pad = AssetDatabase.LoadAssetAtPath<PanelSettings>(PadPath);
            if (phone == null || pad == null)
            {
                LaunchSceneSetup.Setup();
                phone = AssetDatabase.LoadAssetAtPath<PanelSettings>(PhonePath);
                pad = AssetDatabase.LoadAssetAtPath<PanelSettings>(PadPath);
            }

            EnsureIslandWorldPrefabs();
            EnsureLaunchScene(phone, pad);
            SetLaunchOnlyBuildSettings();
            EditorSceneManager.OpenScene(LaunchScene, OpenSceneMode.Single);
            Log.Info(
                "[IslandScene] Play entry = Launch only. Runtime prefabs: " +
                TerrainPrefabPath + " + " + OceanPrefabPath);
        }

        [MenuItem("GameMain/Boluo/Validate Island Terrain + Ocean Prefabs")]
        public static void ValidatePrefabsMenu()
        {
            EnsureIslandWorldPrefabs();
            EditorSceneManager.OpenScene(LaunchScene, OpenSceneMode.Single);
        }

        [MenuItem("GameMain/Boluo/Validate Environment Import And Build Gameplay Assets")]
        public static void FullPostMigrate()
        {
            // BoluoEnvAssetMigrator.ValidateEnvironmentImport() removed with the migrator.
            IslandInlandWaterBuilder.Build();
            MapItemEditorMenu.ValidateMapItemData();
            MapItemEditorMenu.BuildMapItemHierarchyPrefab();
            IslandPresetBuilder.Build();
            YooAssetCollectorSetup.SetupAll();
            WireLaunchAsPlayEntry();
        }

        public static void SetLaunchOnlyBuildSettings()
        {
            EditorBuildSettings.scenes = new[]
            {
                new EditorBuildSettingsScene(LaunchScene, true),
            };
        }

        /// <summary>Alias used by LaunchSceneSetup.</summary>
        public static void SetLaunchFirstBuildSettings() => SetLaunchOnlyBuildSettings();

        static void EnsureLaunchScene(PanelSettings phone, PanelSettings pad)
        {
            if (!File.Exists(LaunchScene))
                LaunchSceneSetup.Setup();

            Scene scene = EditorSceneManager.OpenScene(LaunchScene, OpenSceneMode.Single);
            EnsureGameFrameworkInActiveScene(phone, pad);
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, LaunchScene);
        }

        /// <summary>
        /// Prefabs live under AssetRaw for YooAsset. Rebuild via CustomSRP/Create Island Terrain Prefabs.
        /// </summary>
        static void EnsureIslandWorldPrefabs()
        {
            Directory.CreateDirectory("Assets/AssetRaw/Island");
            Directory.CreateDirectory("Assets/AssetRaw/Ocean");
            var terrain = AssetDatabase.LoadAssetAtPath<GameObject>(TerrainPrefabPath);
            var ocean = AssetDatabase.LoadAssetAtPath<GameObject>(OceanPrefabPath);
            if (terrain == null)
            {
                Log.Error(
                    "[IslandScene] Missing " + TerrainPrefabPath +
                    " — run CustomSRP/Create Island Terrain Prefabs.");
                return;
            }

            if (ocean == null)
            {
                Log.Warning(
                    "[IslandScene] Missing " + OceanPrefabPath +
                    " — run CustomSRP/Create Island Terrain Prefabs.");
            }

            if (AssetDatabase.LoadAssetAtPath<GameObject>(InlandWaterPrefabPath) == null)
                IslandInlandWaterBuilder.Build();

            Log.Info(
                "[IslandScene] OK YooAsset locations " +
                SceneAssets.IslandTerrainLocation + " / " + SceneAssets.IslandOceanLocation +
                " / " + SceneAssets.IslandInlandWaterLocation +
                " ← " + TerrainPrefabPath + " + " + OceanPrefabPath + " + " + InlandWaterPrefabPath);
        }

        static void EnsureGameFrameworkInActiveScene(PanelSettings phone, PanelSettings pad)
        {
            GameObject framework = GameObject.Find("GameFramework");
            if (framework == null)
            {
                string prefabPath = FindGameFrameworkPrefab();
                if (prefabPath == null)
                    throw new System.InvalidOperationException(
                        "GameFramework.prefab was not found. Import com.jiangyin.gameframework first.");

                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                framework = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                framework.name = "GameFramework";
            }

            BaseComponent baseComponent = framework.GetComponentInChildren<BaseComponent>(true);
            if (baseComponent != null)
                baseComponent.EditorResourceMode = true;

            ProcedureComponent procedure = framework.GetComponentInChildren<ProcedureComponent>(true);
            if (procedure != null)
            {
                var serialized = new SerializedObject(procedure);
                SerializedProperty names = serialized.FindProperty("m_AvailableProcedureTypeNames");
                names.arraySize = 4;
                names.GetArrayElementAtIndex(0).stringValue = "Game.ProcedureLaunch";
                names.GetArrayElementAtIndex(1).stringValue = "Game.ProcedureInitYooAsset";
                names.GetArrayElementAtIndex(2).stringValue = "Game.ProcedureGuestLogin";
                names.GetArrayElementAtIndex(3).stringValue = "Game.ProcedureIsland";
                serialized.FindProperty("m_EntranceProcedureTypeName").stringValue = "Game.ProcedureLaunch";
                serialized.ApplyModifiedPropertiesWithoutUndo();
            }

            UIComponent ui = framework.GetComponentInChildren<UIComponent>(true);
            if (ui != null)
            {
                var serialized = new SerializedObject(ui);
                SerializedProperty groups = serialized.FindProperty("m_UIGroups");
                groups.arraySize = 4;
                WriteGroup(groups, 0, "Loading", 0);
                WriteGroup(groups, 1, "Main", 1);
                WriteGroup(groups, 2, "Panel", 2);
                WriteGroup(groups, 3, "Dialog", 3);
                serialized.ApplyModifiedPropertiesWithoutUndo();
            }

            GameObject host = GameObject.Find("GameEntry");
            if (host == null)
                host = new GameObject("GameEntry");
            if (host.GetComponent<GameEntry>() == null)
                host.AddComponent<GameEntry>();
            ScreenLayout layout = host.GetComponent<ScreenLayout>();
            if (layout == null)
                layout = host.AddComponent<ScreenLayout>();
            layout.PhonePanelSettings = phone;
            layout.PadPanelSettings = pad;

            Camera camera = Camera.main;
            if (camera != null)
            {
                camera.allowHDR = false;
                camera.allowMSAA = false;
                IslandCameraSrpSetup.EnsureCustomRenderPipelineCamera(camera.gameObject);
            }
        }

        static string FindGameFrameworkPrefab()
        {
            string[] prefabs = AssetDatabase.FindAssets("GameFramework t:Prefab");
            for (int i = 0; i < prefabs.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(prefabs[i]);
                if (path.EndsWith("GameFramework.prefab"))
                    return path;
            }
            return null;
        }

        static void WriteGroup(SerializedProperty groups, int index, string name, int depth)
        {
            SerializedProperty group = groups.GetArrayElementAtIndex(index);
            group.FindPropertyRelative("m_Name").stringValue = name;
            group.FindPropertyRelative("m_Depth").intValue = depth;
        }
    }
}
