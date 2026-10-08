using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace CustomSRP.Editor
{
    /// <summary>
    /// 地面 + 投射体、级联阴影、透明投射模式、多平行光阴影。
    /// 材质尽量复用 DirectionalLights / DrawCalls 已有资源。
    /// </summary>
    public static class CreateDirectionalShadowsScene
    {
        const string ScenePath = TestSceneUtility.ScenesPath + "/DirectionalShadows.unity";

        [MenuItem("CustomSRP/Create Directional Shadows Scene")]
        public static void Create()
        {
            TestSceneUtility.EnsureStandardFolders();
            Texture2D uvAlpha = TestSceneUtility.EnsureUvAlphaTexture();
            Texture2D albedo = TestSceneUtility.EnsureLitAlbedoTexture();
            Texture2D fabric = TestSceneUtility.EnsureLitFabricTexture();
            ShadowSceneMaterials mats = EnsureMaterials(uvAlpha, albedo, fabric);

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            BuildSceneContents(mats);
            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.Refresh();

            TestSceneUtility.AddToBuildSettings(ScenePath, makeFirst: false);
            Debug.Log($"[CustomSRP] Created Directional Shadows scene: {ScenePath}");
            EditorSceneManager.OpenScene(ScenePath);
        }

        struct ShadowSceneMaterials
        {
            public Material DefaultLit;
            public Material Ground;
            public Material RedCube;
            public Material BlueSphere;
            public Material Clip;
            public Material Fade;
            public Material Transparent;
            public Material UnlitCaster;
            public Material NoReceive;
        }

        static ShadowSceneMaterials EnsureMaterials(
            Texture2D uvAlpha,
            Texture2D albedo,
            Texture2D fabric)
        {
            string path = TestSceneUtility.MaterialsPath;

            // Reuse DirectionalLights materials where possible.
            Material defaultLit = AssetDatabase.LoadAssetAtPath<Material>(path + "/LitDefault.mat");
            if (defaultLit == null)
            {
                defaultLit = TestSceneUtility.CreateOrUpdateLit(
                    path + "/LitDefault.mat",
                    new Color(0.85f, 0.85f, 0.85f, 1f),
                    TestSceneUtility.SurfaceType.Opaque,
                    metallic: 0f,
                    smoothness: 0.5f);
            }
            else
            {
                TestSceneUtility.ApplySurface(defaultLit, TestSceneUtility.SurfaceType.Opaque);
                EditorUtility.SetDirty(defaultLit);
            }

            Material clip = AssetDatabase.LoadAssetAtPath<Material>(path + "/LitClip.mat");
            if (clip == null)
            {
                clip = TestSceneUtility.CreateOrUpdateLit(
                    path + "/LitClip.mat",
                    Color.white,
                    TestSceneUtility.SurfaceType.Clip,
                    uvAlpha,
                    metallic: 0.1f,
                    smoothness: 0.6f,
                    enableGpuInstancing: true,
                    cutoff: 0.5f);
            }
            else
            {
                TestSceneUtility.ApplySurface(clip, TestSceneUtility.SurfaceType.Clip);
                EditorUtility.SetDirty(clip);
            }

            Material fade = AssetDatabase.LoadAssetAtPath<Material>(path + "/LitFade.mat");
            if (fade == null)
            {
                fade = TestSceneUtility.CreateOrUpdateLit(
                    path + "/LitFade.mat",
                    new Color(0.9f, 0.9f, 0.95f, 0.4f),
                    TestSceneUtility.SurfaceType.Transparent,
                    metallic: 0f,
                    smoothness: 0.8f);
            }
            else
            {
                TestSceneUtility.ApplySurface(fade, TestSceneUtility.SurfaceType.Transparent);
                EditorUtility.SetDirty(fade);
            }

            Material transparent = AssetDatabase.LoadAssetAtPath<Material>(path + "/LitTransparent.mat");
            if (transparent == null)
            {
                transparent = TestSceneUtility.CreateOrUpdateLit(
                    path + "/LitTransparent.mat",
                    new Color(0.75f, 0.9f, 1f, 0.35f),
                    TestSceneUtility.SurfaceType.TransparentPremultiply,
                    metallic: 0f,
                    smoothness: 0.9f);
            }
            else
            {
                TestSceneUtility.ApplySurface(transparent, TestSceneUtility.SurfaceType.TransparentPremultiply);
                EditorUtility.SetDirty(transparent);
            }

            var mats = new ShadowSceneMaterials
            {
                DefaultLit = defaultLit,
                Ground = TestSceneUtility.CreateOrUpdateLit(
                    path + "/LitShadowGround.mat",
                    new Color(0.55f, 0.55f, 0.52f, 1f),
                    TestSceneUtility.SurfaceType.Opaque,
                    metallic: 0f,
                    smoothness: 0.15f),
                RedCube = TestSceneUtility.CreateOrUpdateLit(
                    path + "/LitShadowRed.mat",
                    new Color(0.85f, 0.2f, 0.15f, 1f),
                    TestSceneUtility.SurfaceType.Opaque,
                    fabric,
                    metallic: 0f,
                    smoothness: 0.45f),
                BlueSphere = TestSceneUtility.CreateOrUpdateLit(
                    path + "/LitShadowBlue.mat",
                    new Color(0.2f, 0.35f, 0.9f, 1f),
                    TestSceneUtility.SurfaceType.Opaque,
                    albedo,
                    metallic: 0.1f,
                    smoothness: 0.7f),
                Clip = clip,
                Fade = fade,
                Transparent = transparent,
                UnlitCaster = TestSceneUtility.CreateOrUpdateUnlit(
                    path + "/UnlitShadowCaster.mat",
                    new Color(0.95f, 0.85f, 0.2f, 1f),
                    TestSceneUtility.SurfaceType.Opaque),
                NoReceive = TestSceneUtility.CreateOrUpdateLit(
                    path + "/LitNoReceiveShadows.mat",
                    new Color(0.3f, 0.85f, 0.45f, 1f),
                    TestSceneUtility.SurfaceType.Opaque,
                    metallic: 0f,
                    smoothness: 0.55f)
            };

            mats.NoReceive.SetFloat("_ReceiveShadows", 0f);
            mats.NoReceive.DisableKeyword("_RECEIVE_SHADOWS");
            EditorUtility.SetDirty(mats.NoReceive);

            AssetDatabase.SaveAssets();
            return mats;
        }

        static void BuildSceneContents(ShadowSceneMaterials mats)
        {
            TestSceneUtility.EnsureMainCamera(new Vector3(0f, 6f, -14f), new Vector3(22f, 0f, 0f));
            BuildLights();

            TestSceneUtility.CreatePlane(
                "Ground",
                Vector3.zero,
                new Vector3(3f, 1f, 3f),
                mats.Ground);

            var casters = new GameObject("ShadowCasters");

            // Tutorial-style opaque casters on a plane.
            Parent(casters, TestSceneUtility.CreateCube(
                "Cube_Center", new Vector3(0f, 0.75f, 0f), mats.RedCube));
            Parent(casters, Scale(TestSceneUtility.CreateCube(
                "Cube_Tall", new Vector3(-2.5f, 1.5f, 1.5f), mats.DefaultLit),
                new Vector3(1f, 3f, 1f)));
            Parent(casters, TestSceneUtility.CreateSphere(
                "Sphere_Blue", new Vector3(2.2f, 0.75f, 0.5f), 0.75f, mats.BlueSphere));
            Parent(casters, TestSceneUtility.CreateCube(
                "Cube_Small", new Vector3(1f, 0.4f, -2f), mats.DefaultLit));

            // Long cube for shadow pancaking / near-plane deformation demo.
            var longCube = TestSceneUtility.CreateCube(
                "Cube_Long", new Vector3(-1.5f, 0.5f, -4f), mats.RedCube);
            longCube.transform.localScale = new Vector3(8f, 1f, 0.4f);
            longCube.transform.rotation = Quaternion.Euler(0f, 25f, 0f);
            Parent(casters, longCube);

            // Wall to show wall→floor shadow bleed / bias.
            var wall = TestSceneUtility.CreateCube(
                "Wall", new Vector3(4.5f, 2f, 2f), mats.DefaultLit);
            wall.transform.localScale = new Vector3(0.3f, 4f, 4f);
            Parent(casters, wall);

            // Unlit that still casts shadows.
            Parent(casters, TestSceneUtility.CreateCube(
                "Cube_UnlitCaster", new Vector3(-4f, 0.75f, -1f), mats.UnlitCaster));

            // Casts but does not receive.
            Parent(casters, TestSceneUtility.CreateSphere(
                "Sphere_NoReceive", new Vector3(0f, 0.75f, 3.5f), 0.7f, mats.NoReceive));

            var transparency = new GameObject("TransparencyShadows");
            Parent(transparency, TestSceneUtility.CreateSphere(
                "Sphere_Clip", new Vector3(-3.5f, 1.2f, 4.5f), 0.7f, mats.Clip));
            Parent(transparency, TestSceneUtility.CreateSphere(
                "Sphere_Fade", new Vector3(-1.5f, 1.2f, 4.5f), 0.7f, mats.Fade));
            Parent(transparency, TestSceneUtility.CreateSphere(
                "Sphere_Glass", new Vector3(0.5f, 1.2f, 4.5f), 0.7f, mats.Transparent));

            // Extra opaque receivers farther out for cascade / fade checks.
            var receivers = new GameObject("FarReceivers");
            for (int i = 0; i < 4; i++)
            {
                float z = 8f + i * 4f;
                Parent(receivers, TestSceneUtility.CreateCube(
                    $"Cube_Far_{i}", new Vector3(-1.5f + i * 1.2f, 0.5f, z), mats.DefaultLit));
            }
        }

        static void BuildLights()
        {
            // Main sun — Hard shadows; Bias=0 / Normal Bias=1 (tutorial defaults).
            TestSceneUtility.CreateDirectionalLight(
                "Directional Light",
                new Vector3(50f, -30f, 0f),
                new Color(1f, 0.956f, 0.839f),
                1f,
                LightShadows.Hard,
                shadowStrength: 1f,
                shadowBias: 0f,
                shadowNormalBias: 1f,
                shadowNearPlane: 0.2f);

            // Secondary colored light with shadows at half strength.
            TestSceneUtility.CreateDirectionalLight(
                "Directional Light Red",
                new Vector3(35f, 50f, 0f),
                new Color(1f, 0.3f, 0.25f),
                0.45f,
                LightShadows.Hard,
                shadowStrength: 0.5f,
                shadowBias: 0f,
                shadowNormalBias: 1f);

            // Fill light without shadows (still contributes lighting).
            TestSceneUtility.CreateDirectionalLight(
                "Directional Light Fill",
                new Vector3(20f, 160f, 0f),
                new Color(0.35f, 0.45f, 1f),
                0.25f,
                LightShadows.None);
        }

        static void Parent(GameObject parent, GameObject child)
        {
            child.transform.SetParent(parent.transform, true);
        }

        static GameObject Scale(GameObject go, Vector3 scale)
        {
            go.transform.localScale = scale;
            // Keep bottom on ground for tall cubes created at center-height positions.
            return go;
        }
    }
}
