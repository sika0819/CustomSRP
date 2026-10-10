using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace CustomSRP.Editor
{
    /// <summary>
    /// 测试场景共用：文件夹、UVAlpha、Unlit 材质配置、图元、Build Settings。
    /// </summary>
    public static class TestSceneUtility
    {
        public const string Root = "Assets/CustomSRP";
        public const string ScenesPath = Root + "/Scenes";
        public const string MaterialsPath = Root + "/Materials";
        public const string TexturesPath = Root + "/Textures";
        public const string UvAlphaPath = TexturesPath + "/UVAlpha.png";

        public enum SurfaceType
        {
            Opaque,
            Clip,
            Transparent,
            TransparentPremultiply
        }

        public enum ShadowMode
        {
            On,
            Clip,
            Dither,
            Off
        }

        public static void EnsureStandardFolders()
        {
            EnsureFolder("Assets", "CustomSRP");
            EnsureFolder(Root, "Scenes");
            EnsureFolder(Root, "Materials");
            EnsureFolder(Root, "Textures");
            EnsureFolder(Root, "Examples");
            EnsureFolder(Root, "ShaderLibrary");
        }

        public static Texture2D EnsureLitAlbedoTexture()
        {
            const string path = TexturesPath + "/LitAlbedo.png";
            ConfigureSrgbTextureImporter(path);
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        public static Texture2D EnsureLitFabricTexture()
        {
            const string path = TexturesPath + "/LitFabric.png";
            ConfigureSrgbTextureImporter(path);
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        static void ConfigureSrgbTextureImporter(string path)
        {
            if (!File.Exists(path))
            {
                Debug.LogWarning($"[CustomSRP] Missing texture: {path}");
                return;
            }

            AssetDatabase.ImportAsset(path);
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            if (importer == null)
            {
                return;
            }

            importer.sRGBTexture = true;
            importer.alphaSource = TextureImporterAlphaSource.FromInput;
            importer.alphaIsTransparency = false;
            importer.mipmapEnabled = true;
            importer.wrapMode = TextureWrapMode.Repeat;
            importer.SaveAndReimport();
        }

        public static void EnsureFolder(string parent, string name)
        {
            string path = $"{parent}/{name}";
            if (!AssetDatabase.IsValidFolder(path))
            {
                AssetDatabase.CreateFolder(parent, name);
            }
        }

        public static Texture2D EnsureUvAlphaTexture()
        {
            if (File.Exists(UvAlphaPath))
            {
                ConfigureUvAlphaImporter();
                return AssetDatabase.LoadAssetAtPath<Texture2D>(UvAlphaPath);
            }

            const int size = 512;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float u = x / (float)size;
                    float v = y / (float)size;
                    float n = 0f;
                    float amp = 0.55f;
                    float freq = 2.5f;
                    for (int o = 0; o < 4; o++)
                    {
                        n += SoftNoise(u * freq, v * freq) * amp;
                        freq *= 2.1f;
                        amp *= 0.5f;
                    }

                    n = Mathf.Clamp01((n - 0.28f) / 0.55f);
                    n = n * n * (3f - 2f * n);
                    tex.SetPixel(x, y, new Color(1f, 1f, 1f, n));
                }
            }

            tex.Apply();
            File.WriteAllBytes(UvAlphaPath, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            ConfigureUvAlphaImporter();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(UvAlphaPath);
        }

        static float SoftNoise(float x, float y)
        {
            int x0 = Mathf.FloorToInt(x);
            int y0 = Mathf.FloorToInt(y);
            float fx = x - x0;
            float fy = y - y0;
            fx = fx * fx * (3f - 2f * fx);
            fy = fy * fy * (3f - 2f * fy);
            float v00 = Hash(x0, y0);
            float v10 = Hash(x0 + 1, y0);
            float v01 = Hash(x0, y0 + 1);
            float v11 = Hash(x0 + 1, y0 + 1);
            return Mathf.Lerp(Mathf.Lerp(v00, v10, fx), Mathf.Lerp(v01, v11, fx), fy);
        }

        static float Hash(int x, int y)
        {
            int n = x * 374761393 + y * 668265263;
            n = (n ^ (n >> 13)) * 1274126177;
            n ^= n >> 16;
            return (n & 0x7fffffff) / (float)0x7fffffff;
        }

        static void ConfigureUvAlphaImporter()
        {
            AssetDatabase.ImportAsset(UvAlphaPath);
            var importer = (TextureImporter)AssetImporter.GetAtPath(UvAlphaPath);
            if (importer == null)
            {
                return;
            }

            importer.sRGBTexture = true;
            importer.alphaSource = TextureImporterAlphaSource.FromInput;
            importer.alphaIsTransparency = true;
            importer.mipmapEnabled = false;
            importer.SaveAndReimport();
        }

        public static Material CreateOrUpdateUnlit(
            string path,
            Color color,
            SurfaceType surface,
            Texture2D baseMap = null,
            bool enableGpuInstancing = false,
            float cutoff = 0.5f)
        {
            Shader unlit = Shader.Find("CustomSRP/Unlit");
            if (unlit == null)
            {
                throw new System.InvalidOperationException("CustomSRP/Unlit shader not found.");
            }

            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                material = new Material(unlit);
                AssetDatabase.CreateAsset(material, path);
            }
            else
            {
                material.shader = unlit;
            }

            material.SetColor("_BaseColor", color);
            material.SetFloat("_Cutoff", cutoff);
            if (baseMap != null)
            {
                material.SetTexture("_BaseMap", baseMap);
            }

            ApplySurface(material, surface);
            material.enableInstancing = enableGpuInstancing;
            EditorUtility.SetDirty(material);
            return material;
        }

        public static void ApplySurface(Material material, SurfaceType surface)
        {
            switch (surface)
            {
                case SurfaceType.Transparent:
                    material.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
                    material.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
                    material.SetFloat("_ZWrite", 0f);
                    material.SetFloat("_Clipping", 0f);
                    material.DisableKeyword("_CLIPPING");
                    if (material.HasProperty("_PremulAlpha"))
                    {
                        material.SetFloat("_PremulAlpha", 0f);
                        material.DisableKeyword("_PREMULTIPLY_ALPHA");
                    }

                    ApplyShadowMode(material, ShadowMode.Dither);
                    material.renderQueue = (int)RenderQueue.Transparent;
                    material.SetOverrideTag("RenderType", "Transparent");
                    break;

                case SurfaceType.TransparentPremultiply:
                    material.SetFloat("_SrcBlend", (float)BlendMode.One);
                    material.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
                    material.SetFloat("_ZWrite", 0f);
                    material.SetFloat("_Clipping", 0f);
                    material.DisableKeyword("_CLIPPING");
                    if (material.HasProperty("_PremulAlpha"))
                    {
                        material.SetFloat("_PremulAlpha", 1f);
                        material.EnableKeyword("_PREMULTIPLY_ALPHA");
                    }

                    ApplyShadowMode(material, ShadowMode.Dither);
                    material.renderQueue = (int)RenderQueue.Transparent;
                    material.SetOverrideTag("RenderType", "Transparent");
                    break;

                case SurfaceType.Clip:
                    material.SetFloat("_SrcBlend", (float)BlendMode.One);
                    material.SetFloat("_DstBlend", (float)BlendMode.Zero);
                    material.SetFloat("_ZWrite", 1f);
                    material.SetFloat("_Clipping", 1f);
                    material.EnableKeyword("_CLIPPING");
                    if (material.HasProperty("_PremulAlpha"))
                    {
                        material.SetFloat("_PremulAlpha", 0f);
                        material.DisableKeyword("_PREMULTIPLY_ALPHA");
                    }

                    ApplyShadowMode(material, ShadowMode.Clip);
                    material.renderQueue = (int)RenderQueue.AlphaTest;
                    material.SetOverrideTag("RenderType", "TransparentCutout");
                    break;

                default:
                    material.SetFloat("_SrcBlend", (float)BlendMode.One);
                    material.SetFloat("_DstBlend", (float)BlendMode.Zero);
                    material.SetFloat("_ZWrite", 1f);
                    material.SetFloat("_Clipping", 0f);
                    material.DisableKeyword("_CLIPPING");
                    if (material.HasProperty("_PremulAlpha"))
                    {
                        material.SetFloat("_PremulAlpha", 0f);
                        material.DisableKeyword("_PREMULTIPLY_ALPHA");
                    }

                    ApplyShadowMode(material, ShadowMode.On);
                    material.renderQueue = (int)RenderQueue.Geometry;
                    material.SetOverrideTag("RenderType", "Opaque");
                    break;
            }

            if (material.HasProperty("_ReceiveShadows"))
            {
                material.SetFloat("_ReceiveShadows", 1f);
                material.EnableKeyword("_RECEIVE_SHADOWS");
            }
        }

        public static void ApplyShadowMode(Material material, ShadowMode mode)
        {
            if (!material.HasProperty("_Shadows"))
            {
                return;
            }

            material.SetFloat("_Shadows", (float)mode);
            material.DisableKeyword("_SHADOWS_CLIP");
            material.DisableKeyword("_SHADOWS_DITHER");
            switch (mode)
            {
                case ShadowMode.Clip:
                    material.EnableKeyword("_SHADOWS_CLIP");
                    break;
                case ShadowMode.Dither:
                    material.EnableKeyword("_SHADOWS_DITHER");
                    break;
            }

            material.SetShaderPassEnabled("ShadowCaster", mode != ShadowMode.Off);
        }

        public static Material CreateOrUpdateLit(
            string path,
            Color color,
            SurfaceType surface,
            Texture2D baseMap = null,
            float metallic = 0f,
            float smoothness = 0.5f,
            bool enableGpuInstancing = false,
            float cutoff = 0.5f)
        {
            Shader lit = Shader.Find("CustomSRP/Lit");
            if (lit == null)
            {
                throw new System.InvalidOperationException("CustomSRP/Lit shader not found.");
            }

            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                material = new Material(lit);
                AssetDatabase.CreateAsset(material, path);
            }
            else
            {
                material.shader = lit;
            }

            material.SetColor("_BaseColor", color);
            material.SetFloat("_Cutoff", cutoff);
            material.SetFloat("_Metallic", metallic);
            material.SetFloat("_Smoothness", smoothness);
            if (material.HasProperty("_Fresnel"))
            {
                material.SetFloat("_Fresnel", 1f);
            }
            if (baseMap != null)
            {
                material.SetTexture("_BaseMap", baseMap);
            }

            ApplySurface(material, surface);
            material.enableInstancing = enableGpuInstancing;
            EditorUtility.SetDirty(material);
            return material;
        }

        public const string OilCanvasPath = TexturesPath + "/OilCanvas.png";
        public const string OilCanvasWeavePath = TexturesPath + "/OilCanvas_Weave.png";
        public const string OilAlbedoPath = TexturesPath + "/OilAlbedo.png";
        public const string OilGroundPath = TexturesPath + "/OilGround.png";
        public const string OilBrushPath = TexturesPath + "/OilBrush.png";
        public const string OilSkyStrokePath = TexturesPath + "/OilSkyStroke.png";
        public const string SkyboxTexturesPath = TexturesPath + "/Skybox";
        public const string SkyOilCanvasPath = SkyboxTexturesPath + "/OilCanvas.png";
        public const string SkyBrushStampPath = SkyboxTexturesPath + "/SkyBrush.png";
        public const string SkySunOilPath = SkyboxTexturesPath + "/Sun_Oil.png";
        public const string SkyMoonOilPath = SkyboxTexturesPath + "/Moon_Oil.png";
        public const string SkyStarsOilPath = SkyboxTexturesPath + "/Stars_Oil.png";
        public const string OilOceanPath = TexturesPath + "/OilOcean.png";
        public const string OilOceanFoamPath = TexturesPath + "/OilOceanFoam.png";
        public const string OilOceanGlintPath = TexturesPath + "/OilOceanGlint.png";
        public const string OilSunStrokePath = TexturesPath + "/OilSunStroke.png";

        /// <summary>
        /// Soft blotchy albedo for Kuwahara dabs (sRGB). Prefer authored OilAlbedo.png.
        /// </summary>
        public static Texture2D EnsureOilAlbedoTexture()
        {
            if (!File.Exists(OilAlbedoPath))
            {
                Debug.LogError(
                    "[CustomSRP] Missing OilAlbedo.png. Expected at " + OilAlbedoPath);
                return EnsureLitAlbedoTexture();
            }

            ConfigureSrgbTextureImporter(OilAlbedoPath);
            return AssetDatabase.LoadAssetAtPath<Texture2D>(OilAlbedoPath);
        }

        public static Texture2D EnsureOilGroundTexture()
        {
            if (!File.Exists(OilGroundPath))
            {
                Debug.LogError(
                    "[CustomSRP] Missing OilGround.png. Expected at " + OilGroundPath);
                return EnsureOilAlbedoTexture();
            }

            ConfigureSrgbTextureImporter(OilGroundPath);
            return AssetDatabase.LoadAssetAtPath<Texture2D>(OilGroundPath);
        }

        public static Texture2D EnsureOilBrushTexture()
        {
            if (!File.Exists(OilBrushPath))
            {
                Debug.LogError(
                    "[CustomSRP] Missing OilBrush.png. Expected at " + OilBrushPath);
                return null;
            }

            AssetDatabase.ImportAsset(OilBrushPath);
            var importer = (TextureImporter)AssetImporter.GetAtPath(OilBrushPath);
            if (importer != null)
            {
                importer.sRGBTexture = false;
                importer.mipmapEnabled = true;
                importer.wrapMode = TextureWrapMode.Repeat;
                importer.filterMode = FilterMode.Bilinear;
                importer.SaveAndReimport();
            }

            return AssetDatabase.LoadAssetAtPath<Texture2D>(OilBrushPath);
        }

        /// <summary>
        /// Large flow strokes for sky (OilSkyStroke.png). Regenerate via generate_oil_sky_stroke.py.
        /// </summary>
        public static Texture2D EnsureOilSkyStrokeTexture()
        {
            if (!File.Exists(OilSkyStrokePath))
            {
                Debug.LogError(
                    "[CustomSRP] Missing OilSkyStroke.png. Run:\n" +
                    $"  python3 {TexturesPath}/generate_oil_sky_stroke.py");
                return null;
            }

            ConfigureSrgbTextureImporter(OilSkyStrokePath);
            var importer = (TextureImporter)AssetImporter.GetAtPath(OilSkyStrokePath);
            if (importer != null)
            {
                importer.wrapMode = TextureWrapMode.Repeat;
                importer.filterMode = FilterMode.Bilinear;
                importer.SaveAndReimport();
            }

            return AssetDatabase.LoadAssetAtPath<Texture2D>(OilSkyStrokePath);
        }

        /// <summary>
        /// Linear packed canvas: RG = tangent normal, B = paint thickness.
        /// Authored seamless weave lives at OilCanvas.png; do not regenerate with noise.
        /// </summary>
        public static Texture2D EnsureOilCanvasTexture()
        {
            if (!File.Exists(OilCanvasPath))
            {
                Debug.LogError(
                    "[CustomSRP] Missing OilCanvas.png (packed RG normal + B thickness). " +
                    "Expected at " + OilCanvasPath);
                return null;
            }

            AssetDatabase.ImportAsset(OilCanvasPath);
            var importer = (TextureImporter)AssetImporter.GetAtPath(OilCanvasPath);
            if (importer != null)
            {
                importer.textureType = TextureImporterType.Default;
                importer.sRGBTexture = false;
                importer.mipmapEnabled = true;
                importer.wrapMode = TextureWrapMode.Repeat;
                importer.filterMode = FilterMode.Bilinear;
                importer.anisoLevel = 4;
                importer.textureCompression = TextureImporterCompression.CompressedHQ;
                importer.SaveAndReimport();
            }

            if (File.Exists(OilCanvasWeavePath))
            {
                ConfigureSrgbTextureImporter(OilCanvasWeavePath);
            }

            return AssetDatabase.LoadAssetAtPath<Texture2D>(OilCanvasPath);
        }

        public static Material CreateOrUpdateOilNPR(
            string path,
            Color color,
            Texture2D baseMap = null,
            Texture2D canvasMap = null,
            bool outline = true)
        {
            Shader oil = Shader.Find("CustomSRP/OilNPR");
            if (oil == null)
            {
                throw new System.InvalidOperationException("CustomSRP/OilNPR shader not found.");
            }

            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                material = new Material(oil);
                AssetDatabase.CreateAsset(material, path);
            }
            else
            {
                material.shader = oil;
            }

            material.SetColor("_BaseColor", color);
            material.SetFloat("_Cutoff", 0.5f);
            material.SetFloat("_ShadeSteps", 4f);
            material.SetFloat("_ShadeLift", 0.42f);
            material.SetFloat("_ShadowLift", 0.95f);
            material.SetColor("_ShadowTint", new Color(0.42f, 0.48f, 0.72f, 1f));
            material.SetColor("_ShadowWarm", new Color(0.62f, 0.45f, 0.32f, 1f));
            material.SetFloat("_ShadowWobble", 0.45f);
            material.SetFloat("_ShadowBrushScale", 1.8f);
            material.SetFloat("_KuwaharaRadius", 0.05f);
            material.SetColor("_AmbientColor", new Color(0.55f, 0.5f, 0.44f, 1f));
            material.SetColor("_SpecularColor", new Color(0.55f, 0.48f, 0.4f, 1f));
            material.SetFloat("_SpecularThreshold", 0.86f);
            material.SetFloat("_CanvasStrength", 0.1f);
            material.SetFloat("_PaintThickness", 0.18f);
            material.SetFloat("_EdgeStrength", 0.55f);
            material.SetColor("_EdgeColor", new Color(0.28f, 0.2f, 0.16f, 1f));
            material.SetFloat("_OutlineWidth", 0.03f);
            material.SetColor("_OutlineColor", new Color(0.16f, 0.11f, 0.08f, 1f));
            material.SetFloat("_OutlineNoise", 0.55f);
            material.SetFloat("_OutlineNoiseScale", 3.5f);
            material.SetFloat("_OutlineWobble", 0.85f);
            material.SetFloat("_OutlineBreak", 0.1f);
            material.SetFloat("_OutlineBrushScale", 2.5f);
            Texture2D brush = EnsureOilBrushTexture();
            if (brush != null)
            {
                material.SetTexture("_OutlineBrushMap", brush);
                material.SetTextureScale("_OutlineBrushMap", new Vector2(2.2f, 2.2f));
            }

            if (baseMap != null)
            {
                material.SetTexture("_BaseMap", baseMap);
                material.SetTextureScale("_BaseMap", new Vector2(1.1f, 1.1f));
            }

            if (canvasMap != null)
            {
                material.SetTexture("_CanvasMap", canvasMap);
                material.SetTextureScale("_CanvasMap", new Vector2(0.45f, 0.45f));
            }

            ApplySurface(material, SurfaceType.Opaque);
            ApplyShadowMode(material, ShadowMode.On);
            material.SetFloat("_ReceiveShadows", 1f);
            material.EnableKeyword("_RECEIVE_SHADOWS");
            material.SetFloat("_Kuwahara", 1f);
            material.EnableKeyword("_KUWAHARA_ON");
            material.SetFloat("_Canvas", 1f);
            material.EnableKeyword("_CANVAS_ON");
            material.SetFloat("_InternalEdge", 1f);
            material.EnableKeyword("_INTERNAL_EDGE_ON");
            material.SetFloat("_Outline", outline ? 1f : 0f);
            if (outline)
            {
                material.EnableKeyword("_OUTLINE_ON");
            }
            else
            {
                material.DisableKeyword("_OUTLINE_ON");
            }

            material.SetShaderPassEnabled("Outline", outline);
            material.enableInstancing = false;
            EditorUtility.SetDirty(material);
            return material;
        }

        public static Texture2D EnsureOilOceanTexture()
        {
            if (!File.Exists(OilOceanPath))
            {
                Debug.LogError(
                    "[CustomSRP] Missing OilOcean.png. Expected at " + OilOceanPath);
                return EnsureOilAlbedoTexture();
            }

            ConfigureSrgbTextureImporter(OilOceanPath);
            return AssetDatabase.LoadAssetAtPath<Texture2D>(OilOceanPath);
        }

        public static Texture2D EnsureOilOceanFoamTexture()
        {
            if (!File.Exists(OilOceanFoamPath))
            {
                Debug.LogError(
                    "[CustomSRP] Missing OilOceanFoam.png. Expected at " + OilOceanFoamPath);
                return EnsureOilBrushTexture();
            }

            ConfigureSrgbTextureImporter(OilOceanFoamPath);
            return AssetDatabase.LoadAssetAtPath<Texture2D>(OilOceanFoamPath);
        }

        public static Texture2D EnsureOilOceanGlintTexture()
        {
            if (!File.Exists(OilOceanGlintPath))
            {
                Debug.LogError(
                    "[CustomSRP] Missing OilOceanGlint.png. Expected at " + OilOceanGlintPath);
                return EnsureOilOceanFoamTexture();
            }

            ConfigureSrgbTextureImporter(OilOceanGlintPath);
            return AssetDatabase.LoadAssetAtPath<Texture2D>(OilOceanGlintPath);
        }

        /// <summary>
        /// Van Gogh–style oil ocean: stepped chroma, flow strokes, chunky foam, yellow glints.
        /// </summary>
        public static Material CreateOrUpdateOilOcean(
            string path,
            Texture2D canvasMap = null,
            Texture2D brushMap = null,
            Texture2D shoreHeightMap = null,
            Vector2 shoreOriginXZ = default,
            Vector2 shoreSizeXZ = default,
            float shoreHeightScaleM = 1184.7f,
            float shoreWaterLevelM = 0.35f)
        {
            Shader ocean = Shader.Find("CustomSRP/OilOceanNPR");
            if (ocean == null)
            {
                throw new System.InvalidOperationException(
                    "CustomSRP/OilOceanNPR shader not found.");
            }

            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                material = new Material(ocean);
                AssetDatabase.CreateAsset(material, path);
            }
            else
            {
                material.shader = ocean;
            }

            material.SetColor("_BaseColor", new Color(1f, 1f, 1f, 0.94f));
            material.SetColor("_DeepColor", new Color(0.02f, 0.16f, 0.55f, 1f));
            material.SetColor("_MidColor", new Color(0.06f, 0.42f, 0.88f, 1f));
            material.SetColor("_ShallowColor", new Color(0.18f, 0.58f, 0.92f, 1f));
            material.SetColor("_OchreTint", new Color(0.55f, 0.42f, 0.18f, 1f));
            material.SetColor("_FoamColor", new Color(0.92f, 0.93f, 0.9f, 1f));
            material.SetColor("_GlintColor", new Color(0.85f, 0.7f, 0.28f, 1f));
            material.SetFloat("_FoamStrength", 1.7f);
            material.SetFloat("_ShoreFoamWidth", 18f);
            material.SetFloat("_SunPathStrength", 1.4f);
            material.SetFloat("_SunPathWidth", 4.5f);
            Texture2D paint = EnsureOilOceanTexture();
            if (paint != null)
            {
                material.SetTexture("_PaintMap", paint);
            }

            Texture2D sunStroke = AssetDatabase.LoadAssetAtPath<Texture2D>(OilSunStrokePath);
            if (sunStroke != null)
            {
                material.SetTexture("_SparkleBrush", sunStroke);
            }

            material.SetFloat("_PaintTile", 4500f);
            material.SetFloat("_PaintContrast", 1.2f);
            material.SetFloat("_PaintRelief", 0.7f);
            material.SetFloat("_ShadeSteps", 4f);
            material.SetFloat("_ShadeLift", 0.38f);
            material.SetFloat("_ShadowLift", 1.05f);
            material.SetColor("_ShadowTint", new Color(0.10f, 0.24f, 0.62f, 1f));
            material.SetColor("_ShadowWarm", new Color(0.14f, 0.36f, 0.72f, 1f));
            material.SetFloat("_ShadowWobble", 0.45f);
            material.SetColor("_AmbientColor", new Color(0.34f, 0.46f, 0.68f, 1f));
            material.SetColor("_SpecularColor", new Color(0.9f, 0.88f, 0.8f, 1f));
            material.SetFloat("_SpecularThreshold", 0.8f);
            material.SetFloat("_PaintThickness", 0.35f);

            if (shoreSizeXZ.x > 1f && shoreSizeXZ.y > 1f)
            {
                material.SetVector(
                    "_ShoreOriginSize",
                    new Vector4(shoreOriginXZ.x, shoreOriginXZ.y, shoreSizeXZ.x, shoreSizeXZ.y));
                material.SetFloat("_ShoreHeightScale", shoreHeightScaleM);
                material.SetFloat("_ShoreWaterLevel", shoreWaterLevelM);
                material.SetFloat("_ShoreFeatherM", 18f);
                material.SetFloat("_ShoreMapStrength", 1.6f);
            }
            else
            {
                material.SetFloat("_ShoreMapStrength", 0f);
            }

            if (shoreHeightMap != null)
            {
                material.SetTexture("_ShoreHeightMap", shoreHeightMap);
            }

            ApplySurface(material, SurfaceType.TransparentPremultiply);
            material.SetFloat("_ReceiveShadows", 1f);
            material.EnableKeyword("_RECEIVE_SHADOWS");
            material.DisableKeyword("_KUWAHARA_ON");
            material.DisableKeyword("_CANVAS_ON");
            material.DisableKeyword("_SHADOWS_DITHER");
            material.enableInstancing = false;
            EditorUtility.SetDirty(material);
            return material;
        }

        public static Texture2D EnsureSkyOilCanvasTexture()
        {
            return LoadSkyboxTexture(SkyOilCanvasPath);
        }

        static Texture2D LoadSkyboxTexture(string assetPath)
        {
            if (!File.Exists(assetPath))
            {
                Debug.LogError("[CustomSRP] Missing skybox texture: " + assetPath);
                return null;
            }

            ConfigureSrgbTextureImporter(assetPath);
            return AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
        }

        /// <summary>
        /// Boluo-style oil sky (canvas strokes + sun and moon). timeOfDay is clock hours 0–24.
        /// </summary>
        public static Material CreateOrUpdateOilSkybox(
            string path,
            Texture2D canvasMap = null,
            float timeOfDay = 12f)
        {
            Shader sky = Shader.Find(OilSkyboxTime.ShaderName);
            if (sky == null)
            {
                throw new System.InvalidOperationException(
                    "CustomSRP/OilSkyboxNPR shader not found.");
            }

            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                material = new Material(sky);
                AssetDatabase.CreateAsset(material, path);
            }
            else
            {
                material.shader = sky;
            }

            // canvasMap is the terrain canvas. The sky samples Textures/Skybox/OilCanvas.png.
            _ = canvasMap;

            material.DisableKeyword("_CANVAS_ON");
            if (material.HasProperty("_Canvas"))
            {
                material.SetFloat("_Canvas", 0f);
            }

            if (material.HasProperty("_CanvasStrength"))
            {
                material.SetFloat("_CanvasStrength", 0f);
            }

            if (material.HasProperty("_CanvasMap"))
            {
                material.SetTexture("_CanvasMap", null);
            }

            Texture2D oilCanvas = LoadSkyboxTexture(SkyOilCanvasPath);
            if (oilCanvas != null)
            {
                material.SetTexture("_OilCanvas", oilCanvas);
                material.SetTextureScale("_OilCanvas", new Vector2(1.15f, 1f));
            }

            Texture2D sunTex = LoadSkyboxTexture(SkySunOilPath);
            if (sunTex != null)
            {
                material.SetTexture("_SunTex", sunTex);
            }

            Texture2D moonTex = LoadSkyboxTexture(SkyMoonOilPath);
            if (moonTex != null)
            {
                material.SetTexture("_MoonTex", moonTex);
            }

            Texture2D starsTex = LoadSkyboxTexture(SkyStarsOilPath);
            if (starsTex != null)
            {
                material.SetTexture("_StarsTex", starsTex);
            }

            OilSkyboxTime.SyncSkyMaterialStatic(
                material,
                OilSkyboxTime.HoursToPeriod(timeOfDay),
                brushScale: 8f,
                brushStrength: 1f,
                brushContrast: 1.8f,
                brushRelief: 1f);

            material.enableInstancing = false;
            EditorUtility.SetDirty(material);
            return material;
        }

        public static Material CreateOrUpdateOilCloud(string path)
        {
            Shader shader = Shader.Find(OilCloudLayer.ShaderName);
            if (shader == null)
            {
                throw new System.InvalidOperationException(
                    "CustomSRP/OilCloudNPR shader not found.");
            }

            Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                material = new Material(shader);
                AssetDatabase.CreateAsset(material, path);
            }
            else
            {
                material.shader = shader;
            }

            Texture2D cloudMap = AssetDatabase.LoadAssetAtPath<Texture2D>(OilCloudLayer.CloudMapPath);
            Texture2D noiseMap = AssetDatabase.LoadAssetAtPath<Texture2D>(OilCloudLayer.NoiseMapPath);
            Texture2D brush = AssetDatabase.LoadAssetAtPath<Texture2D>(OilCloudLayer.BrushPath);
            if (cloudMap != null)
            {
                material.SetTexture("_CloudMap", cloudMap);
            }

            if (noiseMap != null)
            {
                material.SetTexture("_NoiseMap", noiseMap);
                material.SetTextureScale("_NoiseMap", new Vector2(0.32f, 0.32f));
            }

            if (brush != null)
            {
                material.SetTexture("_CloudBrush", brush);
            }

            material.SetFloat("_UVDisturbance", 0.04f);
            material.SetFloat("_SdfSoftness", 0.14f);
            material.SetFloat("_SdfMin", 0.48f);
            material.SetFloat("_SdfMax", 0.90f);
            material.SetFloat("_TopShadow", 1f);
            material.SetFloat("_TopHighlight", 0.92f);
            material.SetFloat("_EdgeIntensity", 1.55f);
            material.enableInstancing = false;
            EditorUtility.SetDirty(material);
            return material;
        }

        /// <summary>
        /// Floor preset: bigger paint dabs, warmer umbra, weak weave, no outline.
        /// </summary>
        public static void ApplyOilGroundPreset(Material material, Texture2D groundMap)
        {
            if (material == null)
            {
                return;
            }

            if (groundMap != null)
            {
                material.SetTexture("_BaseMap", groundMap);
                material.SetTextureScale("_BaseMap", new Vector2(2.2f, 2.2f));
            }

            material.SetColor("_BaseColor", new Color(0.92f, 0.88f, 0.78f, 1f));
            material.SetFloat("_KuwaharaRadius", 0.085f);
            material.SetFloat("_CanvasStrength", 0.06f);
            material.SetTextureScale("_CanvasMap", new Vector2(0.3f, 0.3f));
            material.SetFloat("_PaintThickness", 0.12f);
            material.SetFloat("_ShadeSteps", 3f);
            material.SetFloat("_ShadeLift", 0.48f);
            material.SetFloat("_ShadowLift", 1.05f);
            material.SetColor("_ShadowTint", new Color(0.36f, 0.46f, 0.62f, 1f));
            material.SetColor("_ShadowWarm", new Color(0.58f, 0.48f, 0.3f, 1f));
            material.SetFloat("_ShadowWobble", 0.55f);
            material.SetFloat("_ShadowBrushScale", 1.35f);
            material.SetColor("_AmbientColor", new Color(0.5f, 0.52f, 0.44f, 1f));
            material.SetFloat("_EdgeStrength", 0.35f);
            material.SetColor("_EdgeColor", new Color(0.32f, 0.28f, 0.18f, 1f));
            material.SetFloat("_Outline", 0f);
            material.DisableKeyword("_OUTLINE_ON");
            material.SetShaderPassEnabled("Outline", false);
            EditorUtility.SetDirty(material);
        }

        /// <summary>
        /// Android Empty package: keep oil look, cut fill-rate / sample cost.
        /// Objects keep Kuwahara+canvas+outline; ground uses baked paint (no live Kuwahara).
        /// </summary>
        public static void ApplyOilMobilePerfPreset(params Material[] materials)
        {
            foreach (var material in materials)
            {
                if (material == null)
                {
                    continue;
                }

                bool isGround = material.name.Contains("Ground");
                if (isGround)
                {
                    material.SetFloat("_Kuwahara", 0f);
                    material.DisableKeyword("_KUWAHARA_ON");
                    material.SetFloat("_InternalEdge", 0f);
                    material.DisableKeyword("_INTERNAL_EDGE_ON");
                    material.SetFloat("_CanvasStrength", 0.05f);
                    material.SetFloat("_PaintThickness", 0.1f);
                }
                else
                {
                    material.SetFloat("_KuwaharaRadius", 0.04f);
                    material.SetFloat("_InternalEdge", 0f);
                    material.DisableKeyword("_INTERNAL_EDGE_ON");
                    material.SetFloat("_CanvasStrength", 0.08f);
                    material.SetFloat("_PaintThickness", 0.14f);
                    material.SetFloat("_OutlineWidth", 0.026f);
                }

                EditorUtility.SetDirty(material);
            }
        }

        public static GameObject CreateCapsule(
            string name,
            Vector3 position,
            float height,
            Material material)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            go.name = name;
            go.transform.position = position;
            go.transform.localScale = new Vector3(1f, height * 0.5f, 1f);
            go.GetComponent<MeshRenderer>().sharedMaterial = material;
            return go;
        }

        public static Light CreateDirectionalLight(
            string name,
            Vector3 eulerAngles,
            Color color,
            float intensity = 1f,
            LightShadows shadows = LightShadows.None,
            float shadowStrength = 1f,
            float shadowBias = 0f,
            float shadowNormalBias = 1f,
            float shadowNearPlane = 0.2f,
            LightmapBakeType lightmapBakeType = LightmapBakeType.Realtime)
        {
            var lightGo = new GameObject(name, typeof(Light));
            var light = lightGo.GetComponent<Light>();
            light.type = LightType.Directional;
            light.color = color;
            light.intensity = intensity;
            light.shadows = shadows;
            light.shadowStrength = shadowStrength;
            light.shadowBias = shadowBias;
            light.shadowNormalBias = shadowNormalBias;
            light.shadowNearPlane = shadowNearPlane;
            light.lightmapBakeType = lightmapBakeType;
            light.transform.rotation = Quaternion.Euler(eulerAngles);
            return light;
        }

        /// <summary>
        /// Contribute GI → 进 lightmap；否则动态物体走 Light Probes。
        /// </summary>
        public static void SetContributeGI(GameObject go, bool contribute, float lightmapScale = 1f)
        {
            StaticEditorFlags flags = GameObjectUtility.GetStaticEditorFlags(go);
            if (contribute)
            {
                flags |= StaticEditorFlags.ContributeGI;
            }
            else
            {
                flags &= ~StaticEditorFlags.ContributeGI;
            }

            GameObjectUtility.SetStaticEditorFlags(go, flags);

            var renderer = go.GetComponent<MeshRenderer>();
            if (renderer == null)
            {
                return;
            }

            renderer.receiveGI = contribute ? ReceiveGI.Lightmaps : ReceiveGI.LightProbes;
            renderer.scaleInLightmap = lightmapScale;
        }

        public static LightingSettings EnsureBakedIndirectLightingSettings(string assetPath)
        {
            return EnsureGiLightingSettings(assetPath, MixedLightingMode.IndirectOnly);
        }

        public static LightingSettings EnsureShadowmaskLightingSettings(string assetPath)
        {
            return EnsureGiLightingSettings(assetPath, MixedLightingMode.Shadowmask);
        }

        /// <summary>
        /// Baked Light → IndirectOnly；Shadow Masks → Shadowmask。
        /// Quality 的 Distance/Always Shadowmask 由 Project Settings 决定（Ultra 默认 Distance）。
        /// </summary>
        public static LightingSettings EnsureGiLightingSettings(
            string assetPath,
            MixedLightingMode mixedMode)
        {
            EnsureFolder(Root, "Settings");
            var settings = AssetDatabase.LoadAssetAtPath<LightingSettings>(assetPath);
            if (settings == null)
            {
                settings = new LightingSettings { name = Path.GetFileNameWithoutExtension(assetPath) };
                AssetDatabase.CreateAsset(settings, assetPath);
            }

            settings.bakedGI = true;
            settings.realtimeGI = false;
            settings.mixedBakeMode = mixedMode;
            settings.lightmapResolution = 20f;
            settings.lightmapCompression = LightmapCompression.None;
            settings.directionalityMode = LightmapsMode.NonDirectional;
            settings.lightmapper = LightingSettings.Lightmapper.ProgressiveGPU;
            settings.directSampleCount = 32;
            settings.indirectSampleCount = 512;
            settings.environmentSampleCount = 512;
            EditorUtility.SetDirty(settings);
            Lightmapping.lightingSettings = settings;
            return settings;
        }

        public static GameObject CreatePlane(
            string name,
            Vector3 position,
            Vector3 scale,
            Material material)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Plane);
            go.name = name;
            go.transform.SetPositionAndRotation(position, Quaternion.identity);
            go.transform.localScale = scale;
            go.GetComponent<MeshRenderer>().sharedMaterial = material;
            return go;
        }

        public static Material CreateOrUpdateUnsupported(string path, Color color, bool transparent)
        {
            Shader standard = Shader.Find("Standard");
            if (standard == null)
            {
                standard = Shader.Find("Legacy Shaders/Diffuse");
            }

            if (standard == null)
            {
                throw new System.InvalidOperationException("No legacy Standard/Diffuse shader found for unsupported materials.");
            }

            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                material = new Material(standard);
                AssetDatabase.CreateAsset(material, path);
            }
            else
            {
                material.shader = standard;
            }

            if (material.HasProperty("_Color"))
            {
                material.SetColor("_Color", color);
            }

            if (transparent && material.HasProperty("_Mode"))
            {
                material.SetFloat("_Mode", 3f);
                material.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
                material.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
                material.SetInt("_ZWrite", 0);
                material.DisableKeyword("_ALPHATEST_ON");
                material.EnableKeyword("_ALPHABLEND_ON");
                material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                material.renderQueue = (int)RenderQueue.Transparent;
            }

            EditorUtility.SetDirty(material);
            return material;
        }

        public static GameObject CreateCube(string name, Vector3 position, Material material)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.position = position;
            go.GetComponent<MeshRenderer>().sharedMaterial = material;
            return go;
        }

        public static GameObject CreateSphere(string name, Vector3 position, float radius, Material material)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = name;
            go.transform.position = position;
            go.transform.localScale = Vector3.one * radius * 2f;
            go.GetComponent<MeshRenderer>().sharedMaterial = material;
            return go;
        }

        public static void EnsureMainCamera(Vector3 position, Vector3 eulerAngles)
        {
            var mainCamera = Object.FindFirstObjectByType<Camera>();
            if (mainCamera == null)
            {
                var camGo = new GameObject("Main Camera", typeof(Camera), typeof(AudioListener));
                mainCamera = camGo.GetComponent<Camera>();
            }

            mainCamera.name = "Main Camera";
            mainCamera.tag = "MainCamera";
            mainCamera.transform.SetPositionAndRotation(position, Quaternion.Euler(eulerAngles));
            mainCamera.clearFlags = CameraClearFlags.Skybox;
            mainCamera.depth = -1f;
            mainCamera.backgroundColor = new Color(0.15f, 0.15f, 0.18f);
        }

        public static void EnsureDirectionalLight()
        {
            if (Object.FindFirstObjectByType<Light>() != null)
            {
                return;
            }

            var lightGo = new GameObject("Directional Light", typeof(Light));
            var light = lightGo.GetComponent<Light>();
            light.type = LightType.Directional;
            light.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
        }

        public static void AddToBuildSettings(string scenePath, bool makeFirst = false)
        {
            var scenes = EditorBuildSettings.scenes;
            for (int i = 0; i < scenes.Length; i++)
            {
                if (scenes[i].path == scenePath)
                {
                    if (makeFirst && i != 0)
                    {
                        var first = scenes[i];
                        for (int j = i; j > 0; j--)
                        {
                            scenes[j] = scenes[j - 1];
                        }

                        scenes[0] = first;
                        EditorBuildSettings.scenes = scenes;
                    }

                    return;
                }
            }

            var list = new EditorBuildSettingsScene[scenes.Length + 1];
            if (makeFirst)
            {
                list[0] = new EditorBuildSettingsScene(scenePath, true);
                for (int i = 0; i < scenes.Length; i++)
                {
                    list[i + 1] = scenes[i];
                }
            }
            else
            {
                for (int i = 0; i < scenes.Length; i++)
                {
                    list[i] = scenes[i];
                }

                list[scenes.Length] = new EditorBuildSettingsScene(scenePath, true);
            }

            EditorBuildSettings.scenes = list;
        }
    }
}
