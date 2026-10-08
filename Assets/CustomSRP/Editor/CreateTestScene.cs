using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace CustomSRP.Editor
{
    public static class CreateTestScene
    {
        const string ScenePath = TestSceneUtility.ScenesPath + "/CustomRPTest.unity";

        [InitializeOnLoadMethod]
        static void AutoCreateIfMissing()
        {
            EditorApplication.delayCall += () =>
            {
                if (EditorApplication.isPlayingOrWillChangePlaymode)
                {
                    return;
                }

                if (AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath) != null)
                {
                    return;
                }

                Create();
            };
        }

        [MenuItem("CustomSRP/Create Test Scene")]
        public static void Create()
        {
            TestSceneUtility.EnsureStandardFolders();
            Texture2D uvAlpha = TestSceneUtility.EnsureUvAlphaTexture();
            Materials mats = EnsureMaterials(uvAlpha);

            var scene = UnityEditor.SceneManagement.EditorSceneManager.NewScene(
                UnityEditor.SceneManagement.NewSceneSetup.DefaultGameObjects,
                UnityEditor.SceneManagement.NewSceneMode.Single);
            BuildSceneContents(mats);
            UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.Refresh();

            TestSceneUtility.AddToBuildSettings(ScenePath, makeFirst: true);
            Debug.Log($"[CustomSRP] Created test scene: {ScenePath}");
            UnityEditor.SceneManagement.EditorSceneManager.OpenScene(ScenePath);
        }

        struct Materials
        {
            public Material UnlitGreen;
            public Material UnlitYellow;
            public Material UnlitWhiteTransparent;
            public Material UnsupportedOpaque;
            public Material UnsupportedTransparent;
        }

        static Materials EnsureMaterials(Texture2D uvAlpha)
        {
            string path = TestSceneUtility.MaterialsPath;
            var mats = new Materials
            {
                UnlitGreen = TestSceneUtility.CreateOrUpdateUnlit(
                    path + "/UnlitGreen.mat",
                    new Color(0.2f, 0.8f, 0.25f, 1f),
                    TestSceneUtility.SurfaceType.Opaque),
                UnlitYellow = TestSceneUtility.CreateOrUpdateUnlit(
                    path + "/UnlitYellow.mat",
                    new Color(0.95f, 0.85f, 0.15f, 1f),
                    TestSceneUtility.SurfaceType.Opaque),
                UnlitWhiteTransparent = TestSceneUtility.CreateOrUpdateUnlit(
                    path + "/UnlitWhiteTransparent.mat",
                    Color.white,
                    TestSceneUtility.SurfaceType.Transparent,
                    uvAlpha),
                UnsupportedOpaque = TestSceneUtility.CreateOrUpdateUnsupported(
                    path + "/UnsupportedOpaque.mat",
                    new Color(0.85f, 0.15f, 0.15f, 1f),
                    false),
                UnsupportedTransparent = TestSceneUtility.CreateOrUpdateUnsupported(
                    path + "/UnsupportedTransparent.mat",
                    new Color(0.2f, 0.35f, 0.95f, 0.45f),
                    true)
            };

            AssetDatabase.SaveAssets();
            return mats;
        }

        static void BuildSceneContents(Materials mats)
        {
            TestSceneUtility.EnsureMainCamera(new Vector3(0f, 2.2f, -8f), new Vector3(12f, 0f, 0f));
            TestSceneUtility.EnsureDirectionalLight();

            TestSceneUtility.CreateCube("Cube_Red_A", new Vector3(-2.2f, 0.5f, 0f), mats.UnsupportedOpaque);
            TestSceneUtility.CreateCube("Cube_Red_B", new Vector3(-0.7f, 0.5f, 1.2f), mats.UnsupportedOpaque);
            TestSceneUtility.CreateCube("Cube_Red_C", new Vector3(-1.5f, 0.5f, -1.4f), mats.UnsupportedOpaque);

            TestSceneUtility.CreateCube("Cube_Green_A", new Vector3(1.0f, 0.5f, 0.2f), mats.UnlitGreen);
            TestSceneUtility.CreateCube("Cube_Green_B", new Vector3(2.4f, 0.5f, -0.8f), mats.UnlitGreen);
            TestSceneUtility.CreateCube("Cube_Yellow_A", new Vector3(0.4f, 0.5f, -2.0f), mats.UnlitYellow);
            TestSceneUtility.CreateCube("Cube_Yellow_B", new Vector3(2.0f, 0.5f, 1.5f), mats.UnlitYellow);

            TestSceneUtility.CreateSphere("Sphere_Blue_A", new Vector3(-2.5f, 1.6f, 2.0f), 0.7f, mats.UnsupportedTransparent);
            TestSceneUtility.CreateSphere("Sphere_Blue_B", new Vector3(-0.2f, 1.8f, 2.4f), 0.55f, mats.UnsupportedTransparent);

            TestSceneUtility.CreateSphere("Sphere_White_A", new Vector3(1.8f, 1.7f, 2.2f), 0.65f, mats.UnlitWhiteTransparent);
            TestSceneUtility.CreateSphere("Sphere_White_B", new Vector3(0.6f, 2.0f, 0.8f), 0.5f, mats.UnlitWhiteTransparent);

            var secondaryGo = new GameObject("Secondary Camera", typeof(Camera));
            var secondary = secondaryGo.GetComponent<Camera>();
            secondary.tag = "Untagged";
            secondary.transform.SetPositionAndRotation(new Vector3(3.5f, 1.5f, -4.5f), Quaternion.identity);
            secondary.clearFlags = CameraClearFlags.Depth;
            secondary.depth = 0f;
            secondary.fieldOfView = 40f;
            secondary.rect = new Rect(0.65f, 0.65f, 0.33f, 0.33f);

            TestSceneUtility.CreateCube("Cube_SecondaryMarker", new Vector3(4.2f, 0.35f, -3.2f), mats.UnlitYellow);

            CreateUiButton();
        }

        static void CreateUiButton()
        {
            var canvasGo = new GameObject("Canvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            var canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;

            var scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);

            var eventSystem = Object.FindFirstObjectByType<UnityEngine.EventSystems.EventSystem>();
            if (eventSystem == null)
            {
                new GameObject(
                    "EventSystem",
                    typeof(UnityEngine.EventSystems.EventSystem),
                    typeof(UnityEngine.EventSystems.StandaloneInputModule));
            }

            var buttonGo = new GameObject("Button", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
            buttonGo.transform.SetParent(canvasGo.transform, false);
            var rect = buttonGo.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(24f, -24f);
            rect.sizeDelta = new Vector2(160f, 40f);
            buttonGo.GetComponent<Image>().color = new Color(0.2f, 0.55f, 0.9f, 0.9f);

            var textGo = new GameObject("Text", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            textGo.transform.SetParent(buttonGo.transform, false);
            var textRect = textGo.GetComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = Vector2.zero;
            textRect.offsetMax = Vector2.zero;
            var text = textGo.GetComponent<Text>();
            text.text = "CustomSRP";
            text.alignment = TextAnchor.MiddleCenter;
            text.color = Color.white;
            text.fontSize = 18;
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (text.font == null)
            {
                text.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            }
        }
    }
}
