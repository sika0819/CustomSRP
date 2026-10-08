using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace CustomSRP.Editor
{
    /// <summary>
    /// Circuitry 球（Mask MODS / Detail / Normal / Emission），复用 CustomSRP/Lit。
    /// Occlusion 乘 IndirectBRDF；间接 = Light Probe SH + 环境反射。
    /// </summary>
    public static class CreateComplexMapsScene
    {
        const string ScenePath = TestSceneUtility.ScenesPath + "/ComplexMaps.unity";
        const string CircuitryFolder = TestSceneUtility.TexturesPath + "/Circuitry";
        const string CircuitryMatPath = TestSceneUtility.MaterialsPath + "/LitCircuitry.mat";

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

        [MenuItem("CustomSRP/Create Complex Maps Scene")]
        public static void Create()
        {
            TestSceneUtility.EnsureStandardFolders();
            TestSceneUtility.EnsureFolder(TestSceneUtility.TexturesPath, "Circuitry");

            CircuitryTextures textures = EnsureCircuitryTextures();
            Material circuitry = EnsureCircuitryMaterial(textures);
            Material ground = TestSceneUtility.CreateOrUpdateLit(
                TestSceneUtility.MaterialsPath + "/LitShadowGround.mat",
                new Color(0.55f, 0.55f, 0.58f, 1f),
                TestSceneUtility.SurfaceType.Opaque,
                metallic: 0f,
                smoothness: 0.35f);
            Material plain = TestSceneUtility.CreateOrUpdateLit(
                TestSceneUtility.MaterialsPath + "/LitDefault.mat",
                new Color(0.85f, 0.85f, 0.85f, 1f),
                TestSceneUtility.SurfaceType.Opaque,
                metallic: 0f,
                smoothness: 0.5f);

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            BuildSceneContents(circuitry, ground, plain);
            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            TestSceneUtility.AddToBuildSettings(ScenePath, makeFirst: false);
            Debug.Log($"[CustomSRP] Created Complex Maps scene: {ScenePath}");
            EditorSceneManager.OpenScene(ScenePath);
        }

        struct CircuitryTextures
        {
            public Texture2D Albedo;
            public Texture2D Mask;
            public Texture2D Emission;
            public Texture2D Normals;
            public Texture2D Detail;
            public Texture2D DetailNormal;
        }

        static CircuitryTextures EnsureCircuitryTextures()
        {
            var textures = new CircuitryTextures
            {
                Albedo = LoadAndConfigure(
                    CircuitryFolder + "/Circuitry Albedo.png",
                    sRGB: true,
                    normalMap: false,
                    fadeMipMaps: false),
                Mask = LoadAndConfigure(
                    CircuitryFolder + "/Circuitry Mask MODS.png",
                    sRGB: false,
                    normalMap: false,
                    fadeMipMaps: false),
                Emission = LoadAndConfigure(
                    CircuitryFolder + "/Circuitry Emission.png",
                    sRGB: true,
                    normalMap: false,
                    fadeMipMaps: false),
                Normals = LoadAndConfigure(
                    CircuitryFolder + "/Circuitry Normals.png",
                    sRGB: false,
                    normalMap: true,
                    fadeMipMaps: false),
                Detail = LoadAndConfigure(
                    CircuitryFolder + "/Circuitry Detail.png",
                    sRGB: false,
                    normalMap: false,
                    fadeMipMaps: true),
                DetailNormal = LoadAndConfigure(
                    CircuitryFolder + "/Circuitry Detail Normal.png",
                    sRGB: false,
                    normalMap: true,
                    fadeMipMaps: true)
            };

            if (textures.Albedo == null)
            {
                Debug.LogWarning(
                    $"[CustomSRP] Missing Circuitry textures under {CircuitryFolder}. " +
                    "Scene will still create; assign maps manually.");
            }

            return textures;
        }

        static Texture2D LoadAndConfigure(
            string path,
            bool sRGB,
            bool normalMap,
            bool fadeMipMaps)
        {
            if (!File.Exists(path))
            {
                return null;
            }

            AssetDatabase.ImportAsset(path);
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer != null)
            {
                importer.textureType = normalMap
                    ? TextureImporterType.NormalMap
                    : TextureImporterType.Default;
                importer.sRGBTexture = sRGB && !normalMap;
                importer.mipmapEnabled = true;
                importer.wrapMode = TextureWrapMode.Repeat;
                importer.alphaSource = TextureImporterAlphaSource.FromInput;
                importer.alphaIsTransparency = false;
                if (fadeMipMaps)
                {
                    importer.filterMode = FilterMode.Trilinear;
                    var so = new SerializedObject(importer);
                    SetBoolProp(so, "m_MipMapMode", false);
                    // TextureImporter serialized: m_EnableMipMapFade / fade distances
                    foreach (string name in new[] { "m_FadeOut", "m_Fadeout", "fadeOut" })
                    {
                        SerializedProperty p = so.FindProperty(name);
                        if (p != null && p.propertyType == SerializedPropertyType.Boolean)
                        {
                            p.boolValue = true;
                            break;
                        }
                    }

                    SerializedProperty start = so.FindProperty("m_MipMapFadeDistanceStart");
                    SerializedProperty end = so.FindProperty("m_MipMapFadeDistanceEnd");
                    if (start != null)
                    {
                        start.floatValue = 1f;
                    }

                    if (end != null)
                    {
                        end.floatValue = 3f;
                    }

                    so.ApplyModifiedPropertiesWithoutUndo();
                }

                importer.SaveAndReimport();
            }

            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        static void SetBoolProp(SerializedObject so, string name, bool value)
        {
            SerializedProperty p = so.FindProperty(name);
            if (p != null && p.propertyType == SerializedPropertyType.Boolean)
            {
                p.boolValue = value;
            }
        }

        static Material EnsureCircuitryMaterial(CircuitryTextures textures)
        {
            Shader lit = Shader.Find("CustomSRP/Lit");
            if (lit == null)
            {
                throw new System.InvalidOperationException("CustomSRP/Lit shader not found.");
            }

            var material = AssetDatabase.LoadAssetAtPath<Material>(CircuitryMatPath);
            if (material == null)
            {
                material = new Material(lit);
                AssetDatabase.CreateAsset(material, CircuitryMatPath);
            }
            else
            {
                material.shader = lit;
            }

            material.SetColor("_BaseColor", Color.white);
            material.SetFloat("_Metallic", 1f);
            material.SetFloat("_Smoothness", 1f);
            material.SetFloat("_Occlusion", 0.5f);
            material.SetFloat("_NormalScale", 1f);
            material.SetFloat("_DetailAlbedo", 0.2f);
            material.SetFloat("_DetailSmoothness", 0.2f);
            material.SetFloat("_DetailNormalScale", 0.5f);
            material.SetColor("_EmissionColor", Color.white);

            if (textures.Albedo != null)
            {
                material.SetTexture("_BaseMap", textures.Albedo);
                material.SetTextureScale("_BaseMap", new Vector2(2f, 1f));
            }

            if (textures.Mask != null)
            {
                material.SetTexture("_MaskMap", textures.Mask);
                material.EnableKeyword("_MASK_MAP");
                material.SetFloat("_MaskMapToggle", 1f);
            }

            if (textures.Normals != null)
            {
                material.SetTexture("_NormalMap", textures.Normals);
                material.EnableKeyword("_NORMAL_MAP");
                material.SetFloat("_NormalMapToggle", 1f);
            }

            if (textures.Emission != null)
            {
                material.SetTexture("_EmissionMap", textures.Emission);
            }

            if (textures.Detail != null)
            {
                material.SetTexture("_DetailMap", textures.Detail);
                material.SetTextureScale("_DetailMap", new Vector2(8f, 4f));
                material.EnableKeyword("_DETAIL_MAP");
                material.SetFloat("_DetailMapToggle", 1f);
            }

            if (textures.DetailNormal != null)
            {
                material.SetTexture("_DetailNormalMap", textures.DetailNormal);
            }

            material.EnableKeyword("_RECEIVE_SHADOWS");
            material.SetFloat("_ReceiveShadows", 1f);
            TestSceneUtility.ApplySurface(material, TestSceneUtility.SurfaceType.Opaque);
            material.enableInstancing = true;
            EditorUtility.SetDirty(material);
            return material;
        }

        static void BuildSceneContents(Material circuitry, Material ground, Material plain)
        {
            TestSceneUtility.EnsureMainCamera(new Vector3(0f, 1.2f, -3.2f), new Vector3(8f, 0f, 0f));

            TestSceneUtility.CreateDirectionalLight(
                "Directional Light",
                new Vector3(50f, -30f, 0f),
                Color.white,
                intensity: 1.1f,
                shadows: LightShadows.Soft,
                shadowBias: 0f,
                shadowNormalBias: 1f);

            TestSceneUtility.CreateDirectionalLight(
                "Fill Light",
                new Vector3(35f, 140f, 0f),
                new Color(0.55f, 0.7f, 1f),
                intensity: 0.35f,
                shadows: LightShadows.None);

            TestSceneUtility.CreatePlane(
                "Ground",
                Vector3.zero,
                new Vector3(1.2f, 1f, 1.2f),
                ground);

            TestSceneUtility.CreateSphere(
                "Circuitry",
                new Vector3(0f, 1f, 0f),
                0.75f,
                circuitry);

            // 对照：无复杂贴图的默认 Lit
            TestSceneUtility.CreateSphere(
                "PlainLit",
                new Vector3(2.2f, 0.75f, 0f),
                0.5f,
                plain);
        }
    }
}
