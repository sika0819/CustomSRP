using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using CustomSRP.Examples;

namespace CustomSRP.Editor
{
    /// <summary>
    /// Metallic / Smoothness 网格、多平行光、BRDF、透明 / Clip、Lit MeshBall。
    /// </summary>
    public static class CreateDirectionalLightsScene
    {
        const string ScenePath = TestSceneUtility.ScenesPath + "/DirectionalLights.unity";

        static readonly float[] MetallicSteps = { 0f, 0.25f, 0.5f, 0.75f, 1f };
        static readonly float[] SmoothnessSteps = { 0f, 0.25f, 0.5f, 0.75f, 0.95f };

        [MenuItem("CustomSRP/Create Directional Lights Scene")]
        public static void Create()
        {
            TestSceneUtility.EnsureStandardFolders();
            Texture2D uvAlpha = TestSceneUtility.EnsureUvAlphaTexture();
            Texture2D albedo = TestSceneUtility.EnsureLitAlbedoTexture();
            Texture2D fabric = TestSceneUtility.EnsureLitFabricTexture();
            DirectionalLightsMaterials mats = EnsureMaterials(uvAlpha, albedo, fabric);

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            BuildSceneContents(mats);
            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.Refresh();

            TestSceneUtility.AddToBuildSettings(ScenePath, makeFirst: false);
            Debug.Log($"[CustomSRP] Created Directional Lights scene: {ScenePath}");
            EditorSceneManager.OpenScene(ScenePath);
        }

        struct DirectionalLightsMaterials
        {
            public Material DefaultLit;
            public Material Textured;
            public Material BlueMetal;
            public Material Fade;
            public Material Transparent;
            public Material Clip;
            public Material Instanced;
        }

        static DirectionalLightsMaterials EnsureMaterials(
            Texture2D uvAlpha,
            Texture2D albedo,
            Texture2D fabric)
        {
            string path = TestSceneUtility.MaterialsPath;
            var mats = new DirectionalLightsMaterials
            {
                DefaultLit = TestSceneUtility.CreateOrUpdateLit(
                    path + "/LitDefault.mat",
                    new Color(0.85f, 0.85f, 0.85f, 1f),
                    TestSceneUtility.SurfaceType.Opaque,
                    metallic: 0f,
                    smoothness: 0.5f),
                Textured = TestSceneUtility.CreateOrUpdateLit(
                    path + "/LitTextured.mat",
                    Color.white,
                    TestSceneUtility.SurfaceType.Opaque,
                    albedo ?? fabric,
                    metallic: 0f,
                    smoothness: 0.55f),
                BlueMetal = TestSceneUtility.CreateOrUpdateLit(
                    path + "/LitBlueMetal.mat",
                    new Color(0.15f, 0.35f, 0.95f, 1f),
                    TestSceneUtility.SurfaceType.Opaque,
                    fabric,
                    metallic: 1f,
                    smoothness: 0.85f),
                Fade = TestSceneUtility.CreateOrUpdateLit(
                    path + "/LitFade.mat",
                    new Color(0.9f, 0.9f, 0.95f, 0.4f),
                    TestSceneUtility.SurfaceType.Transparent,
                    metallic: 0f,
                    smoothness: 0.8f),
                Transparent = TestSceneUtility.CreateOrUpdateLit(
                    path + "/LitTransparent.mat",
                    new Color(0.75f, 0.9f, 1f, 0.35f),
                    TestSceneUtility.SurfaceType.TransparentPremultiply,
                    metallic: 0f,
                    smoothness: 0.9f),
                Clip = TestSceneUtility.CreateOrUpdateLit(
                    path + "/LitClip.mat",
                    Color.white,
                    TestSceneUtility.SurfaceType.Clip,
                    uvAlpha,
                    metallic: 0.1f,
                    smoothness: 0.6f,
                    enableGpuInstancing: true,
                    cutoff: 0.5f),
                Instanced = TestSceneUtility.CreateOrUpdateLit(
                    path + "/LitInstanced.mat",
                    Color.white,
                    TestSceneUtility.SurfaceType.Opaque,
                    albedo,
                    metallic: 0f,
                    smoothness: 0.5f,
                    enableGpuInstancing: true)
            };

            AssetDatabase.SaveAssets();
            return mats;
        }

        static void BuildSceneContents(DirectionalLightsMaterials mats)
        {
            TestSceneUtility.EnsureMainCamera(new Vector3(0f, 3.5f, -14f), new Vector3(12f, 0f, 0f));
            BuildLights();

            var gridRoot = new GameObject("MetallicSmoothnessGrid");
            const float spacing = 1.35f;
            float originX = -((MetallicSteps.Length - 1) * spacing) * 0.5f;
            float originY = 0.6f;
            float originZ = -1.5f;

            for (int y = 0; y < SmoothnessSteps.Length; y++)
            {
                for (int x = 0; x < MetallicSteps.Length; x++)
                {
                    float metallic = MetallicSteps[x];
                    float smoothness = SmoothnessSteps[SmoothnessSteps.Length - 1 - y];
                    var pos = new Vector3(originX + x * spacing, originY + y * spacing, originZ);
                    var go = TestSceneUtility.CreateSphere(
                        $"Sphere_M{metallic:0.##}_S{smoothness:0.##}",
                        pos,
                        0.5f,
                        mats.DefaultLit);
                    go.transform.SetParent(gridRoot.transform, true);

                    var props = go.AddComponent<PerObjectMaterialProperties>();
                    var so = new SerializedObject(props);
                    so.FindProperty("baseColor").colorValue = Color.white;
                    so.FindProperty("metallic").floatValue = metallic;
                    so.FindProperty("smoothness").floatValue = smoothness;
                    so.FindProperty("alphaCutoff").floatValue = 0.5f;
                    so.ApplyModifiedPropertiesWithoutUndo();
                }
            }

            var texturedRoot = new GameObject("TexturedSpheres");
            for (int i = 0; i < 5; i++)
            {
                var go = TestSceneUtility.CreateSphere(
                    $"Sphere_Textured_{i}",
                    new Vector3(-6.5f, 0.8f + i * 1.2f, 2.5f),
                    0.45f,
                    mats.Textured);
                go.transform.SetParent(texturedRoot.transform, true);

                var props = go.AddComponent<PerObjectMaterialProperties>();
                var so = new SerializedObject(props);
                so.FindProperty("baseColor").colorValue = Color.white;
                so.FindProperty("metallic").floatValue = i / 4f;
                so.FindProperty("smoothness").floatValue = 0.2f + i * 0.18f;
                so.ApplyModifiedPropertiesWithoutUndo();
            }

            var blueRoot = new GameObject("BlueMetalSpheres");
            for (int i = 0; i < 5; i++)
            {
                var go = TestSceneUtility.CreateSphere(
                    $"Sphere_Blue_{i}",
                    new Vector3(6.5f, 0.8f + i * 1.2f, 2.5f),
                    0.45f,
                    mats.BlueMetal);
                go.transform.SetParent(blueRoot.transform, true);

                var props = go.AddComponent<PerObjectMaterialProperties>();
                var so = new SerializedObject(props);
                so.FindProperty("baseColor").colorValue = new Color(0.15f, 0.35f, 0.95f, 1f);
                so.FindProperty("metallic").floatValue = 1f;
                so.FindProperty("smoothness").floatValue = SmoothnessSteps[i];
                so.ApplyModifiedPropertiesWithoutUndo();
            }

            var fadeRoot = new GameObject("FadeSpheres");
            for (int i = 0; i < 4; i++)
            {
                var go = TestSceneUtility.CreateSphere(
                    $"Sphere_Fade_{i}",
                    new Vector3(-3.5f + i * 1.2f, 0.7f, 5.5f),
                    0.45f,
                    mats.Fade);
                go.transform.SetParent(fadeRoot.transform, true);
            }

            var glassRoot = new GameObject("TransparentSpheres");
            for (int i = 0; i < 4; i++)
            {
                var go = TestSceneUtility.CreateSphere(
                    $"Sphere_Glass_{i}",
                    new Vector3(-3.5f + i * 1.2f, 2.2f, 5.5f),
                    0.45f,
                    mats.Transparent);
                go.transform.SetParent(glassRoot.transform, true);
            }

            var clipRoot = new GameObject("ClipSpheres");
            for (int i = 0; i < 4; i++)
            {
                var go = TestSceneUtility.CreateSphere(
                    $"Sphere_Clip_{i}",
                    new Vector3(-3.5f + i * 1.2f, 3.7f, 5.5f),
                    0.45f,
                    mats.Clip);
                go.transform.SetParent(clipRoot.transform, true);

                var props = go.AddComponent<PerObjectMaterialProperties>();
                var so = new SerializedObject(props);
                so.FindProperty("baseColor").colorValue = new Color(0.95f, 0.9f, 0.8f, 1f);
                so.FindProperty("alphaCutoff").floatValue = 0.35f + i * 0.12f;
                so.FindProperty("metallic").floatValue = 0.05f;
                so.FindProperty("smoothness").floatValue = 0.55f;
                so.ApplyModifiedPropertiesWithoutUndo();
            }

            var meshBallGo = new GameObject("MeshBall");
            meshBallGo.transform.position = new Vector3(0f, 1.5f, 14f);
            var meshBall = meshBallGo.AddComponent<MeshBall>();
            var temp = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Mesh sphereMesh = temp.GetComponent<MeshFilter>().sharedMesh;
            Object.DestroyImmediate(temp);

            var meshBallSo = new SerializedObject(meshBall);
            meshBallSo.FindProperty("mesh").objectReferenceValue = sphereMesh;
            meshBallSo.FindProperty("material").objectReferenceValue = mats.Instanced;
            meshBallSo.ApplyModifiedPropertiesWithoutUndo();
        }

        static void BuildLights()
        {
            // Main sun (tutorial default-ish: ~FFF4D6, 50° / -30°)
            TestSceneUtility.CreateDirectionalLight(
                "Directional Light",
                new Vector3(50f, -30f, 0f),
                new Color(1f, 0.956f, 0.839f),
                1f);

            TestSceneUtility.CreateDirectionalLight(
                "Directional Light Red",
                new Vector3(30f, 40f, 0f),
                new Color(1f, 0.25f, 0.2f),
                0.55f);

            TestSceneUtility.CreateDirectionalLight(
                "Directional Light Green",
                new Vector3(40f, -120f, 0f),
                new Color(0.25f, 1f, 0.35f),
                0.45f);

            TestSceneUtility.CreateDirectionalLight(
                "Directional Light Blue",
                new Vector3(20f, 160f, 0f),
                new Color(0.25f, 0.45f, 1f),
                0.5f);
        }
    }
}
