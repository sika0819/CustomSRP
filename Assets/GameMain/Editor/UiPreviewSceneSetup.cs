using System.IO;
using Game;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace Game.Editor
{
    public static class UiPreviewSceneSetup
    {
        const string ScenePath = "Assets/GameMain/Scenes/UiPreview.unity";
        const string PanelPath = "Assets/GameMain/UI/Settings/UiPreviewPanelSettings.asset";

        static readonly string[] FormNames =
        {
            "Loading",
            "Island Main",
            "Weather",
            "Music",
            "Volume",
            "Preset",
            "Timer Pause",
            "Mobile Data",
            "Download Ask",
            "Network Required",
            "Download Progress",
            "Storage Full"
        };

        static readonly string[] FormFiles =
        {
            "LoadingForm",
            "IslandMainForm",
            "WeatherForm",
            "MusicForm",
            "VolumeForm",
            "PresetForm",
            "TimerPauseForm",
            "MobileDataForm",
            "DownloadAskForm",
            "NetworkRequiredForm",
            "DownloadProgressForm",
            "StorageFullForm"
        };

        [MenuItem("Game/Setup UI Preview Scene")]
        public static void Setup()
        {
            PanelSettings panel = CreatePanel();
            Scene previous = SceneManager.GetActiveScene();
            if (previous.isDirty && !string.IsNullOrEmpty(previous.path))
                EditorSceneManager.SaveScene(previous);

            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            GameObject lightObject = new GameObject("Directional Light");
            Light light = lightObject.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1f;
            lightObject.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

            GameObject cameraObject = new GameObject("Main Camera");
            cameraObject.tag = "MainCamera";
            Camera camera = cameraObject.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.11f, 0.12f, 0.14f, 1f);
            camera.orthographic = true;
            camera.orthographicSize = 5f;
            cameraObject.AddComponent<AudioListener>();

            GameObject host = new GameObject("UI Preview");
            UIDocument document = host.AddComponent<UIDocument>();
            document.panelSettings = panel;
            UiPreview preview = host.AddComponent<UiPreview>();

            SerializedObject serialized = new SerializedObject(preview);
            SerializedProperty slots = serialized.FindProperty("forms");
            slots.arraySize = FormFiles.Length;
            for (int i = 0; i < FormFiles.Length; i++)
            {
                SerializedProperty slot = slots.GetArrayElementAtIndex(i);
                slot.FindPropertyRelative("Title").stringValue = FormNames[i];
                slot.FindPropertyRelative("Tree").objectReferenceValue =
                    AssetDatabase.LoadAssetAtPath<VisualTreeAsset>("Assets/GameMain/UI/Forms/" + FormFiles[i] + ".uxml");
            }

            serialized.FindProperty("musicTrack").objectReferenceValue =
                AssetDatabase.LoadAssetAtPath<VisualTreeAsset>("Assets/GameMain/UI/Forms/MusicTrackItem.uxml");
            serialized.FindProperty("presetItem").objectReferenceValue =
                AssetDatabase.LoadAssetAtPath<VisualTreeAsset>("Assets/GameMain/UI/Forms/PresetItem.uxml");
            serialized.FindProperty("loadingStill").objectReferenceValue =
                AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/GameMain/UI/Video/loading.png");
            serialized.ApplyModifiedPropertiesWithoutUndo();

            Directory.CreateDirectory("Assets/GameMain/Scenes");
            EditorSceneManager.SaveScene(scene, ScenePath);
            Debug.Log("UI preview scene is ready at " + ScenePath);
        }

        static PanelSettings CreatePanel()
        {
            Directory.CreateDirectory("Assets/GameMain/UI/Settings");
            PanelSettings settings = AssetDatabase.LoadAssetAtPath<PanelSettings>(PanelPath);
            if (settings == null)
            {
                settings = ScriptableObject.CreateInstance<PanelSettings>();
                AssetDatabase.CreateAsset(settings, PanelPath);
            }

            PanelSettings phone = AssetDatabase.LoadAssetAtPath<PanelSettings>("Assets/GameMain/UI/Settings/PhonePanelSettings.asset");
            if (phone != null)
                settings.themeStyleSheet = phone.themeStyleSheet;

            settings.scaleMode = PanelScaleMode.ScaleWithScreenSize;
            settings.referenceResolution = new Vector2Int(1800, 1000);
            settings.screenMatchMode = PanelScreenMatchMode.MatchWidthOrHeight;
            settings.match = 0.5f;
            settings.sortingOrder = 0;
            EditorUtility.SetDirty(settings);
            return settings;
        }
    }
}
