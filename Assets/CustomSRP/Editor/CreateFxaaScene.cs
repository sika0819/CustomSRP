using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace CustomSRP.Editor
{
    /// <summary>
    /// 针垫长边、Circuitry 细纹、斜切硬边，左右分屏对比 allowFXAA。
    /// Post FX 只开 Neutral Tone Mapping（Bloom 关闭），因为 FXAA 走 Post FX。
    /// </summary>
    public static class CreateFxaaScene
    {
        const string ScenePath = TestSceneUtility.ScenesPath + "/FXAA.unity";
        const string GroundMatPath = TestSceneUtility.MaterialsPath + "/LitFxaaGround.mat";
        const string SolidMatPath = TestSceneUtility.MaterialsPath + "/LitFxaaSolid.mat";
        const string EdgeMatPath = TestSceneUtility.MaterialsPath + "/LitFxaaEdge.mat";
        const string CircuitryMatPath = TestSceneUtility.MaterialsPath + "/LitCircuitry.mat";
        const string PostFxPath = TestSceneUtility.Root + "/Settings/PostFXSettingsFxaa.asset";

        const float GoldenAngle = 2.39996323f;

        [InitializeOnLoadMethod]
        static void AutoCreateIfMissing()
        {
            EditorApplication.delayCall += () =>
            {
                if (EditorApplication.isPlayingOrWillChangePlaymode)
                {
                    return;
                }

                const string forceFlag =
                    TestSceneUtility.Root + "/Editor/FxaaSceneData/.force-rebuild";
                bool force = File.Exists(forceFlag);
                if (!force && AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath) != null)
                {
                    return;
                }

                if (force)
                {
                    File.Delete(forceFlag);
                }

                Create();
            };
        }

        [MenuItem("CustomSRP/Create FXAA Scene")]
        public static void Create()
        {
            TestSceneUtility.EnsureStandardFolders();
            TestSceneUtility.EnsureFolder(TestSceneUtility.Root, "Settings");
            CustomSrpBootstrap.EnsureCameraRendererShaderAssigned(
                AssetDatabase.LoadAssetAtPath<CustomRenderPipelineAsset>(
                    "Assets/CustomSRP/Settings/CustomRenderPipelineAsset.asset"));

            Material ground = TestSceneUtility.CreateOrUpdateLit(
                GroundMatPath,
                new Color(0.28f, 0.29f, 0.31f, 1f),
                TestSceneUtility.SurfaceType.Opaque,
                smoothness: 0.15f);
            Material solid = TestSceneUtility.CreateOrUpdateLit(
                SolidMatPath,
                new Color(0.82f, 0.83f, 0.84f, 1f),
                TestSceneUtility.SurfaceType.Opaque,
                smoothness: 0.35f);
            Material edge = TestSceneUtility.CreateOrUpdateLit(
                EdgeMatPath,
                new Color(0.95f, 0.95f, 0.93f, 1f),
                TestSceneUtility.SurfaceType.Opaque,
                smoothness: 0.2f);
            Material circuitry = AssetDatabase.LoadAssetAtPath<Material>(CircuitryMatPath);
            if (circuitry == null)
            {
                Debug.LogWarning(
                    "[CustomSRP] Missing LitCircuitry.mat. " +
                    "Run CustomSRP/Create Complex Maps Scene, then rebuild FXAA.");
                circuitry = solid;
            }

            PostFXSettings postFx = EnsurePostFx();

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.18f, 0.19f, 0.21f);
            RenderSettings.ambientIntensity = 1f;

            BuildContents(ground, solid, edge, circuitry, postFx);
            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            TestSceneUtility.AddToBuildSettings(ScenePath, makeFirst: false);
            Debug.Log("[CustomSRP] Created FXAA scene (split allowFXAA off/on): " + ScenePath);
            EditorSceneManager.OpenScene(ScenePath);
        }

        static PostFXSettings EnsurePostFx()
        {
            var settings = AssetDatabase.LoadAssetAtPath<PostFXSettings>(PostFxPath);
            if (settings == null)
            {
                settings = ScriptableObject.CreateInstance<PostFXSettings>();
                AssetDatabase.CreateAsset(settings, PostFxPath);
            }

            var so = new SerializedObject(settings);
            so.FindProperty("shader").objectReferenceValue =
                Shader.Find("Hidden/CustomSRP/Post FX Stack");

            SerializedProperty bloom = so.FindProperty("bloom");
            bloom.FindPropertyRelative("ignoreRenderScale").boolValue = false;
            bloom.FindPropertyRelative("maxIterations").intValue = 0;
            bloom.FindPropertyRelative("downscaleLimit").intValue = 2;
            bloom.FindPropertyRelative("intensity").floatValue = 0f;
            bloom.FindPropertyRelative("threshold").floatValue = 1f;
            bloom.FindPropertyRelative("mode").enumValueIndex = 0;

            SerializedProperty adjustments = so.FindProperty("colorAdjustments");
            adjustments.FindPropertyRelative("postExposure").floatValue = 0f;
            adjustments.FindPropertyRelative("contrast").floatValue = 0f;
            adjustments.FindPropertyRelative("colorFilter").colorValue = Color.white;
            adjustments.FindPropertyRelative("hueShift").floatValue = 0f;
            adjustments.FindPropertyRelative("saturation").floatValue = 0f;

            so.FindProperty("toneMapping").FindPropertyRelative("mode").enumValueIndex = 2; // Neutral
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(settings);
            return settings;
        }

        static void BuildContents(
            Material ground,
            Material solid,
            Material edge,
            Material circuitry,
            PostFXSettings postFx)
        {
            Vector3 eye = new Vector3(0.4f, 1.35f, -4.5f);
            Vector3 look = new Vector3(0f, 0.45f, 0.05f);
            Quaternion rot = Quaternion.LookRotation(look - eye, Vector3.up);

            Camera left = CreateCamera(
                "Main Camera", eye, rot, depth: 0f,
                rect: new Rect(0f, 0f, 0.5f, 1f), main: true);
            ConfigureCrp(left, postFx, allowFxaa: false);

            Camera right = CreateCamera(
                "Camera FXAA", eye, rot, depth: 1f,
                rect: new Rect(0.5f, 0f, 0.5f, 1f), main: false);
            ConfigureCrp(right, postFx, allowFxaa: true);

            TestSceneUtility.CreateDirectionalLight(
                "Directional Light",
                new Vector3(42f, -28f, 0f),
                Color.white,
                intensity: 1.15f,
                shadows: LightShadows.Soft,
                shadowStrength: 1f,
                shadowBias: 0.02f,
                shadowNormalBias: 0.4f);

            var groundGo = GameObject.CreatePrimitive(PrimitiveType.Plane);
            groundGo.name = "Ground";
            groundGo.GetComponent<MeshRenderer>().sharedMaterial = ground;

            BuildPincushion(solid);
            PlacePrimitive(
                PrimitiveType.Sphere, "Circuitry", circuitry,
                new Vector3(0.95f, 0.32f, 0.28f),
                Quaternion.identity,
                Vector3.one * 0.58f);
            PlacePrimitive(
                PrimitiveType.Cube, "Edge Cube", edge,
                new Vector3(-1.05f, 0.28f, 0.22f),
                Quaternion.Euler(16f, 34f, 11f),
                new Vector3(0.46f, 0.46f, 0.46f));
            PlacePrimitive(
                PrimitiveType.Cube, "Edge Slab", edge,
                new Vector3(-0.72f, 0.14f, -0.48f),
                Quaternion.Euler(6f, 52f, 0f),
                new Vector3(0.78f, 0.1f, 0.3f));
            PlacePrimitive(
                PrimitiveType.Cube, "Edge Blade", edge,
                new Vector3(0.12f, 0.72f, -0.62f),
                Quaternion.Euler(76f, 22f, 14f),
                new Vector3(0.028f, 0.92f, 0.26f));
        }

        static void BuildPincushion(Material material)
        {
            var root = new GameObject("Pincushion");
            root.transform.position = new Vector3(0f, 0.48f, 0f);

            const float coreScale = 0.82f;
            const float coreRadius = coreScale * 0.5f;
            const float pinLength = 0.46f;
            const float pinThickness = 0.032f;
            const int count = 48;

            PlacePrimitive(
                PrimitiveType.Sphere, "Pincushion Core", material,
                Vector3.zero, Quaternion.identity, Vector3.one * coreScale,
                root.transform);

            for (int i = 0; i < count; i++)
            {
                float t = i / (float)(count - 1);
                float y = 1f - t * 2f;
                float ring = Mathf.Sqrt(Mathf.Max(0f, 1f - y * y));
                float theta = GoldenAngle * i;
                var dir = new Vector3(Mathf.Cos(theta) * ring, y, Mathf.Sin(theta) * ring);
                if (dir.y < -0.15f)
                {
                    continue;
                }

                PlacePrimitive(
                    PrimitiveType.Cube,
                    "Pin " + i,
                    material,
                    dir * (coreRadius + pinLength * 0.5f),
                    Quaternion.FromToRotation(Vector3.up, dir),
                    new Vector3(pinThickness, pinLength, pinThickness),
                    root.transform);
            }
        }

        static void PlacePrimitive(
            PrimitiveType type,
            string name,
            Material material,
            Vector3 position,
            Quaternion rotation,
            Vector3 scale,
            Transform parent = null)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = name;
            if (parent != null)
            {
                go.transform.SetParent(parent, false);
                go.transform.localPosition = position;
                go.transform.localRotation = rotation;
                go.transform.localScale = scale;
            }
            else
            {
                go.transform.SetPositionAndRotation(position, rotation);
                go.transform.localScale = scale;
            }

            go.GetComponent<MeshRenderer>().sharedMaterial = material;
        }

        static Camera CreateCamera(
            string name,
            Vector3 pos,
            Quaternion rot,
            float depth,
            Rect rect,
            bool main)
        {
            var go = new GameObject(name, typeof(Camera));
            if (main)
            {
                go.tag = "MainCamera";
            }

            var camera = go.GetComponent<Camera>();
            camera.transform.SetPositionAndRotation(pos, rot);
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.1f, 0.11f, 0.13f, 0f);
            camera.depth = depth;
            camera.rect = rect;
            camera.allowHDR = true;
            camera.fieldOfView = 34f;
            camera.nearClipPlane = 0.3f;
            camera.farClipPlane = 100f;
            return camera;
        }

        static void ConfigureCrp(Camera camera, PostFXSettings postFx, bool allowFxaa)
        {
            var crp = camera.gameObject.AddComponent<CustomRenderPipelineCamera>();
            var so = new SerializedObject(crp);
            SerializedProperty settings = so.FindProperty("settings");
            settings.FindPropertyRelative("copyColor").boolValue = false;
            settings.FindPropertyRelative("copyDepth").boolValue = false;
            settings.FindPropertyRelative("overridePostFX").boolValue = true;
            settings.FindPropertyRelative("postFXSettings").objectReferenceValue = postFx;
            settings.FindPropertyRelative("renderScaleMode").enumValueIndex = 0; // Inherit
            settings.FindPropertyRelative("renderScale").floatValue = 1f;
            settings.FindPropertyRelative("allowFXAA").boolValue = allowFxaa;
            settings.FindPropertyRelative("keepAlpha").boolValue = false;
            so.ApplyModifiedPropertiesWithoutUndo();
        }
    }
}
