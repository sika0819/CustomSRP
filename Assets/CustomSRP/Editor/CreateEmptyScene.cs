using System.IO;
using CustomSRP.Examples;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace CustomSRP.Editor
{
    /// <summary>
    /// Oil NPR 验证场景：无后处理 / LDR / 关 copy；主光 Hard Shadow；若干 OilNPR 网格。
    /// </summary>
    public static class CreateEmptyScene
    {
        const string ScenePath = TestSceneUtility.ScenesPath + "/Empty.unity";

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
                    TestSceneUtility.Root + "/Editor/.force-rebuild-Empty";
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

        [MenuItem("CustomSRP/Create Empty Scene")]
        public static void Create()
        {
            Create(mobilePerf: false);
        }

        public static void Create(bool mobilePerf)
        {
            TestSceneUtility.EnsureStandardFolders();

            Texture2D albedo = TestSceneUtility.EnsureOilAlbedoTexture();
            Texture2D groundMap = TestSceneUtility.EnsureOilGroundTexture();
            Texture2D canvas = TestSceneUtility.EnsureOilCanvasTexture();
            OilMaterials mats = EnsureMaterials(albedo, groundMap, canvas, mobilePerf);

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            BuildSceneContents(mats, mobilePerf);
            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            TestSceneUtility.AddToBuildSettings(ScenePath, makeFirst: false);
            Debug.Log(
                $"[CustomSRP] Created Empty scene " +
                $"(Oil NPR / no Post FX / no Color·Depth copy / LDR / hard shadows" +
                $"{(mobilePerf ? " / mobile perf" : "")}): {ScenePath}");
            EditorSceneManager.OpenScene(ScenePath);
        }

        struct OilMaterials
        {
            public Material Ochre;
            public Material Teal;
            public Material Clay;
            public Material Ground;
        }

        static OilMaterials EnsureMaterials(
            Texture2D albedo, Texture2D groundMap, Texture2D canvas, bool mobilePerf)
        {
            string path = TestSceneUtility.MaterialsPath;
            var mats = new OilMaterials
            {
                Ochre = TestSceneUtility.CreateOrUpdateOilNPR(
                    path + "/OilOchre.mat",
                    new Color(0.9f, 0.72f, 0.48f, 1f),
                    albedo,
                    canvas),
                Teal = TestSceneUtility.CreateOrUpdateOilNPR(
                    path + "/OilTeal.mat",
                    new Color(0.42f, 0.68f, 0.7f, 1f),
                    albedo,
                    canvas),
                Clay = TestSceneUtility.CreateOrUpdateOilNPR(
                    path + "/OilClay.mat",
                    new Color(0.78f, 0.5f, 0.46f, 1f),
                    albedo,
                    canvas),
                Ground = TestSceneUtility.CreateOrUpdateOilNPR(
                    path + "/OilGround.mat",
                    new Color(0.92f, 0.88f, 0.78f, 1f),
                    groundMap,
                    canvas,
                    outline: false)
            };

            TestSceneUtility.ApplyOilGroundPreset(mats.Ground, groundMap);
            if (mobilePerf)
            {
                TestSceneUtility.ApplyOilMobilePerfPreset(
                    mats.Ochre, mats.Teal, mats.Clay, mats.Ground);
            }

            AssetDatabase.SaveAssets();
            return mats;
        }

        static void BuildSceneContents(OilMaterials mats, bool mobilePerf)
        {
            TestSceneUtility.EnsureMainCamera(
                new Vector3(0f, 2.2f, -7f), new Vector3(12f, 0f, 0f));
            ConfigurePerfCamera(mobilePerf);
            BuildDirectionalLightHardShadows();

            TestSceneUtility.CreatePlane(
                "OilGround", new Vector3(0f, 0f, 0f), new Vector3(1.2f, 1f, 1.2f), mats.Ground);
            TestSceneUtility.CreateSphere(
                "OilSphere", new Vector3(-1.6f, 1f, 0f), 0.7f, mats.Ochre);
            var cube = TestSceneUtility.CreateCube(
                "OilCube", new Vector3(0.2f, 0.75f, 0.4f), mats.Teal);
            cube.transform.rotation = Quaternion.Euler(0f, 28f, 0f);
            cube.transform.localScale = new Vector3(1.2f, 1.5f, 1.2f);

            TestSceneUtility.CreateCapsule(
                "OilCapsule", new Vector3(1.8f, 1.1f, -0.2f), 2.2f, mats.Clay);

            var main = Object.FindFirstObjectByType<Camera>();
            if (main != null && main.GetComponent<MobilePerfHud>() == null)
            {
                main.gameObject.AddComponent<MobilePerfHud>();
            }
        }

        static void ConfigurePerfCamera(bool mobilePerf)
        {
            var main = Object.FindFirstObjectByType<Camera>();
            if (main == null)
            {
                return;
            }

            main.allowHDR = false;
            main.allowMSAA = false;
            main.clearFlags = CameraClearFlags.Skybox;

            var crp = main.GetComponent<CustomRenderPipelineCamera>();
            if (crp == null)
            {
                crp = main.gameObject.AddComponent<CustomRenderPipelineCamera>();
            }

            var so = new SerializedObject(crp);
            SerializedProperty settings = so.FindProperty("settings");
            settings.FindPropertyRelative("copyColor").boolValue = false;
            settings.FindPropertyRelative("copyDepth").boolValue = false;
            settings.FindPropertyRelative("overridePostFX").boolValue = true;
            settings.FindPropertyRelative("postFXSettings").objectReferenceValue = null;
            // CameraSettings.RenderScaleMode: 0=Inherit, 1=Multiply, 2=Override — use Override.
            settings.FindPropertyRelative("renderScaleMode").enumValueIndex = mobilePerf ? 2 : 0;
            settings.FindPropertyRelative("renderScale").floatValue = mobilePerf ? 0.75f : 1f;
            settings.FindPropertyRelative("allowFXAA").boolValue = false;
            settings.FindPropertyRelative("keepAlpha").boolValue = false;
            settings.FindPropertyRelative("maskLights").boolValue = false;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        static void BuildDirectionalLightHardShadows()
        {
            TestSceneUtility.CreateDirectionalLight(
                "Directional Light",
                new Vector3(50f, -30f, 0f),
                Color.white,
                intensity: 1.15f,
                shadows: LightShadows.Hard,
                shadowStrength: 1f,
                shadowBias: 0.05f,
                shadowNormalBias: 0.8f);
        }
    }
}
