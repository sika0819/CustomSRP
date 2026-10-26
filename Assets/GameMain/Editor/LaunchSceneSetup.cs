using System.IO;
using Game;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.U2D;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.U2D;
using UnityEngine.UIElements;
using UnityEngine.Video;
using UnityGameFramework.Runtime;

namespace Game.Editor
{
    public static class LaunchSceneSetup
    {
        const string ScenePath = "Assets/GameMain/Scenes/Launch.unity";
        const string PhonePath = "Assets/GameMain/UI/Settings/PhonePanelSettings.asset";
        const string PadPath = "Assets/GameMain/UI/Settings/PadPanelSettings.asset";

        static readonly string[] FormNames =
        {
            "LoadingForm",
            "IslandMainForm",
            "WeatherForm",
            "MusicForm",
            "VolumeForm",
            "PresetForm",
            "TimerPauseForm"
        };

        [MenuItem("Game/Setup Launch Scene")]
        public static void Setup()
        {
            PanelSettings phone = CreatePanelSettings(PhonePath, PanelScreenMatchMode.MatchWidthOrHeight, 1f);
            PanelSettings pad = CreatePanelSettings(PadPath, PanelScreenMatchMode.Expand, 0.5f);
            BuildAtlases();
            BuildFormPrefabs(phone);
            BuildScene(phone, pad);
            AssetDatabase.SaveAssets();
            Log.Info("Launch scene is ready at " + ScenePath);
        }

        static PanelSettings CreatePanelSettings(string path, PanelScreenMatchMode matchMode, float match)
        {
            Directory.CreateDirectory("Assets/GameMain/UI/Settings");
            PanelSettings settings = AssetDatabase.LoadAssetAtPath<PanelSettings>(path);
            if (settings == null)
            {
                settings = ScriptableObject.CreateInstance<PanelSettings>();
                AssetDatabase.CreateAsset(settings, path);
            }

            settings.scaleMode = PanelScaleMode.ScaleWithScreenSize;
            settings.referenceResolution = new Vector2Int(1125, 2436);
            settings.screenMatchMode = matchMode;
            settings.match = match;
            DynamicAtlasSettings atlas = settings.dynamicAtlasSettings;
            atlas.maxSubTextureSize = 512;
            atlas.maxAtlasSize = 4096;
            settings.dynamicAtlasSettings = atlas;
            EditorUtility.SetDirty(settings);
            return settings;
        }

        static void BuildAtlases()
        {
            Directory.CreateDirectory("Assets/GameMain/UI/Atlases");
            for (int i = 0; i < FormNames.Length; i++)
            {
                string folder = "Assets/GameMain/UI/Textures/" + FormNames[i];
                if (!AssetDatabase.IsValidFolder(folder))
                    continue;

                string[] guids = AssetDatabase.FindAssets("t:Sprite", new[] { folder });
                if (guids.Length == 0)
                    continue;

                string atlasPath = "Assets/GameMain/UI/Atlases/" + FormNames[i] + ".spriteatlas";
                SpriteAtlas atlas = AssetDatabase.LoadAssetAtPath<SpriteAtlas>(atlasPath);
                if (atlas == null)
                {
                    atlas = new SpriteAtlas();
                    AssetDatabase.CreateAsset(atlas, atlasPath);
                }

                Sprite[] sprites = new Sprite[guids.Length];
                for (int s = 0; s < guids.Length; s++)
                    sprites[s] = AssetDatabase.LoadAssetAtPath<Sprite>(AssetDatabase.GUIDToAssetPath(guids[s]));
                atlas.Add(sprites);
                EditorUtility.SetDirty(atlas);
            }
        }

        static void BuildFormPrefabs(PanelSettings phone)
        {
            Directory.CreateDirectory("Assets/GameMain/UI/Forms");
            VisualTreeAsset presetItem = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>("Assets/GameMain/UI/Forms/PresetItem.uxml");
            for (int i = 0; i < FormNames.Length; i++)
            {
                string name = FormNames[i];
                string prefabPath = "Assets/GameMain/UI/Forms/" + name + ".prefab";
                GameObject go = new GameObject(name);
                UIDocument document = go.AddComponent<UIDocument>();
                document.panelSettings = phone;
                document.visualTreeAsset = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>("Assets/GameMain/UI/Forms/" + name + ".uxml");
                System.Type formType = System.Type.GetType("Game." + name + ", Game");
                if (formType != null)
                    go.AddComponent(formType);

                if (name == "PresetForm")
                {
                    PresetForm form = go.GetComponent<PresetForm>();
                    form.PresetTemplate = presetItem;
                }
                else if (name == "LoadingForm")
                {
                    LoadingForm form = go.GetComponent<LoadingForm>();
                    form.PortraitClip = AssetDatabase.LoadAssetAtPath<VideoClip>("Assets/GameMain/UI/Video/loading.mp4");
                    form.LandscapeClip = AssetDatabase.LoadAssetAtPath<VideoClip>("Assets/GameMain/UI/Video/loading_landscape.mp4");
                    form.PortraitStill = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/GameMain/UI/Video/loading.png");
                    form.LandscapeStill = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/GameMain/UI/Video/loading_landscape.png");
                }

                PrefabUtility.SaveAsPrefabAsset(go, prefabPath);
                Object.DestroyImmediate(go);
            }
        }

        static void BuildScene(PanelSettings phone, PanelSettings pad)
        {
            Directory.CreateDirectory("Assets/GameMain/Scenes");
            Scene scene = File.Exists(ScenePath)
                ? EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single)
                : EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

            GameObject framework = GameObject.Find("GameFramework");
            if (framework == null)
            {
                string[] prefabs = AssetDatabase.FindAssets("GameFramework t:Prefab");
                string prefabPath = null;
                for (int i = 0; i < prefabs.Length; i++)
                {
                    string path = AssetDatabase.GUIDToAssetPath(prefabs[i]);
                    if (path.EndsWith("GameFramework.prefab"))
                    {
                        prefabPath = path;
                        break;
                    }
                }

                if (prefabPath == null)
                    throw new System.InvalidOperationException("GameFramework.prefab was not found. Import com.jiangyin.gameframework first.");

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
                SerializedObject serialized = new SerializedObject(procedure);
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
                SerializedObject serialized = new SerializedObject(ui);
                SerializedProperty groups = serialized.FindProperty("m_UIGroups");
                groups.arraySize = 4;
                WriteGroup(groups, 0, "Main", 1);
                WriteGroup(groups, 1, "Panel", 2);
                WriteGroup(groups, 2, "Dialog", 3);
                WriteGroup(groups, 3, "Loading", 10);
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

            EnsureBootEarlyCover(phone);

            Camera camera = Camera.main;
            if (camera != null)
            {
                // Match early-cover root (near-black) so pre-UITK frames never flash white.
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(0.047f, 0.047f, 0.055f, 1f);
                camera.allowHDR = false;
                camera.allowMSAA = false;
                IslandCameraSrpSetup.EnsureCustomRenderPipelineCamera(camera.gameObject);
            }

            EditorSceneManager.SaveScene(scene, ScenePath);
            AddSceneToBuildSettings();
        }

        static void EnsureBootEarlyCover(PanelSettings phone)
        {
            const string coverName = "BootEarlyCover";
            GameObject go = GameObject.Find(coverName);
            if (go == null)
                go = new GameObject(coverName);

            UIDocument document = go.GetComponent<UIDocument>();
            if (document == null)
                document = go.AddComponent<UIDocument>();
            document.panelSettings = phone;
            document.visualTreeAsset = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(
                "Assets/GameMain/Boot/BootEarlyCover.uxml");
            document.sortingOrder = BootEarlyCover.SortingOrder;

            BootEarlyCover cover = go.GetComponent<BootEarlyCover>();
            if (cover == null)
                cover = go.AddComponent<BootEarlyCover>();

            SerializedObject so = new SerializedObject(cover);
            so.FindProperty("_portraitStill").objectReferenceValue =
                AssetDatabase.LoadAssetAtPath<Texture2D>(
                    "Assets/GameMain/Boot/BootEarlyCover_Portrait.png");
            so.FindProperty("_landscapeStill").objectReferenceValue =
                AssetDatabase.LoadAssetAtPath<Texture2D>(
                    "Assets/GameMain/Boot/BootEarlyCover_Landscape.png");
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        static void WriteGroup(SerializedProperty groups, int index, string name, int depth)
        {
            SerializedProperty group = groups.GetArrayElementAtIndex(index);
            group.FindPropertyRelative("m_Name").stringValue = name;
            group.FindPropertyRelative("m_Depth").intValue = depth;
        }

        static void AddSceneToBuildSettings()
        {
            IslandSceneWiring.SetLaunchOnlyBuildSettings();
        }
    }
}
