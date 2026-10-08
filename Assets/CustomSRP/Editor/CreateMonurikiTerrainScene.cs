using System.IO;
using CustomSRP.Examples;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace CustomSRP.Editor
{
    /// <summary>
    /// 莫努里基岛 1:1 米制地形：Unity Terrain + CustomSRP/TerrainOilNPR（splat + 油画光照）。
    /// </summary>
    public static class CreateMonurikiTerrainScene
    {
        const string ScenePath = TestSceneUtility.ScenesPath + "/MonurikiTerrain.unity";
        const string HeightmapDir = "Assets/Terrain/Monuriki";
        const string RawPath = HeightmapDir + "/monuriki_heightmap.raw";
        const string JsonPath = HeightmapDir + "/monuriki_heightmap.json";
        const string TerrainDataPath = HeightmapDir + "/Monuriki.asset";
        const string LegacyMeshPath = HeightmapDir + "/MonurikiLitMesh.asset";
        const string TerrainMatPath = TestSceneUtility.MaterialsPath + "/TerrainOilNPRMonuriki.mat";
        const string LegacyTerrainLitMatPath = TestSceneUtility.MaterialsPath + "/TerrainLitMonuriki.mat";
        const string OceanMatPath = TestSceneUtility.MaterialsPath + "/LitMonurikiOcean.mat";
        const string LegacyLitMatPath = TestSceneUtility.MaterialsPath + "/LitMonurikiTerrain.mat";

        const int HeightRes = 2049;
        const int AlphamapRes = 1024;
        const int SplatTexSize = 256;

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
                    TestSceneUtility.Root + "/Editor/.force-rebuild-MonurikiTerrain";
                bool force = File.Exists(forceFlag);
                if (!force && AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath) != null)
                {
                    return;
                }

                if (force)
                {
                    File.Delete(forceFlag);
                }

                if (!File.Exists(RawPath) || !File.Exists(JsonPath))
                {
                    return;
                }

                Create();
            };
        }

        [MenuItem("CustomSRP/Create Monuriki Terrain Scene (1:1)")]
        public static void Create()
        {
            if (!File.Exists(RawPath) || !File.Exists(JsonPath))
            {
                Debug.LogError($"[CustomSRP] Missing heightmap at {HeightmapDir}");
                return;
            }

            TestSceneUtility.EnsureStandardFolders();
            TestSceneUtility.EnsureFolder("Assets", "Terrain");
            TestSceneUtility.EnsureFolder("Assets/Terrain", "Monuriki");

            TerrainScale scale = ReadScale(JsonPath);
            float[,] heights01 = LoadHeights01(RawPath, HeightRes);

            TerrainData terrainData = EnsureTerrainData(heights01, scale);
            TerrainLayer[] layers = EnsureTerrainLayers();
            terrainData.terrainLayers = layers;
            PaintAlphamaps(terrainData, heights01, scale);

            Texture2D canvas = TestSceneUtility.EnsureOilCanvasTexture();
            Texture2D brush = TestSceneUtility.EnsureOilBrushTexture();
            Material terrainMat = CreateOrUpdateTerrainOilNPR(TerrainMatPath, canvas, brush);
            Material oceanMat = TestSceneUtility.CreateOrUpdateLit(
                OceanMatPath,
                new Color(0.12f, 0.28f, 0.42f, 0.85f),
                TestSceneUtility.SurfaceType.TransparentPremultiply,
                metallic: 0f,
                smoothness: 0.85f);

            DeleteLegacyMeshAssets();

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            BuildScene(terrainData, terrainMat, oceanMat, scale);
            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            TestSceneUtility.AddToBuildSettings(ScenePath, makeFirst: false);
            Debug.Log(
                $"[CustomSRP] Created Monuriki 1:1 terrain scene: {ScenePath}\n" +
                $"  size = {scale.SizeX:F1} × {scale.HeightY:F1} × {scale.SizeZ:F1} m " +
                $"(island long axis ≈ {scale.IslandLengthM:F0} m)\n" +
                "  Terrain + CustomSRP/TerrainOilNPR (Kuwahara + canvas + quantized light)");
            EditorSceneManager.OpenScene(ScenePath);
        }

        struct TerrainScale
        {
            public float SizeX;
            public float SizeZ;
            public float HeightY;
            public float IslandLengthM;
        }

        static TerrainScale ReadScale(string jsonPath)
        {
            string json = File.ReadAllText(jsonPath);
            var size = new TerrainScale
            {
                SizeX = 1308.4f,
                SizeZ = 1308.4f,
                HeightY = 178f,
                IslandLengthM = 1150f
            };

            int block = json.IndexOf("recommended_terrain_size_m");
            if (block >= 0)
            {
                string slice = json.Substring(block, Mathf.Min(280, json.Length - block));
                size.SizeX = ReadJsonFloat(slice, "\"x\":", size.SizeX);
                size.SizeZ = ReadJsonFloat(slice, "\"z\":", size.SizeZ);
                size.HeightY = ReadJsonFloat(slice, "\"y\":", size.HeightY);
            }

            int island = json.IndexOf("\"island_size_m\"");
            if (island >= 0)
            {
                string slice = json.Substring(island, Mathf.Min(200, json.Length - island));
                float ew = ReadJsonFloat(slice, "\"east_west\":", size.IslandLengthM);
                float ns = ReadJsonFloat(slice, "\"north_south\":", size.IslandLengthM);
                size.IslandLengthM = Mathf.Max(ew, ns);
            }

            return size;
        }

        static float ReadJsonFloat(string text, string key, float fallback)
        {
            int i = text.IndexOf(key);
            if (i < 0)
            {
                return fallback;
            }

            i += key.Length;
            while (i < text.Length && (text[i] == ' ' || text[i] == '\t' || text[i] == ':'))
            {
                i++;
            }

            int j = i;
            while (j < text.Length && (char.IsDigit(text[j]) || text[j] == '.' || text[j] == '-'))
            {
                j++;
            }

            if (j > i && float.TryParse(
                    text.Substring(i, j - i),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out float v))
            {
                return v;
            }

            return fallback;
        }

        static float[,] LoadHeights01(string rawPath, int res)
        {
            byte[] bytes = File.ReadAllBytes(rawPath);
            int expected = res * res * 2;
            if (bytes.Length != expected)
            {
                throw new IOException(
                    $"Heightmap size mismatch: {bytes.Length} bytes, expected {expected} for {res}².");
            }

            // RAW: row0 = north. Unity Terrain: index0 at z=0 (south) after flip.
            var heights = new float[res, res];
            for (int row = 0; row < res; row++)
            {
                int srcRow = res - 1 - row;
                for (int col = 0; col < res; col++)
                {
                    int o = (srcRow * res + col) * 2;
                    ushort h = (ushort)(bytes[o] | (bytes[o + 1] << 8));
                    heights[row, col] = h / 65535f;
                }
            }

            return heights;
        }

        static TerrainData EnsureTerrainData(float[,] heights01, TerrainScale scale)
        {
            var data = AssetDatabase.LoadAssetAtPath<TerrainData>(TerrainDataPath);
            if (data == null)
            {
                data = new TerrainData();
                AssetDatabase.CreateAsset(data, TerrainDataPath);
            }

            data.heightmapResolution = HeightRes;
            data.size = new Vector3(scale.SizeX, scale.HeightY, scale.SizeZ);
            data.SetHeights(0, 0, heights01);
            // Wider soft blends need denser control maps.
            data.alphamapResolution = AlphamapRes;

            EditorUtility.SetDirty(data);
            return data;
        }

        static TerrainLayer[] EnsureTerrainLayers()
        {
            // Authored oil-paint seamless splats (do not overwrite if present).
            Texture2D sand = EnsureOilSplatTexture(HeightmapDir + "/SplatSand.png", SplatKind.Sand);
            Texture2D grass = EnsureOilSplatTexture(HeightmapDir + "/SplatGrass.png", SplatKind.Grass);
            Texture2D rock = EnsureOilSplatTexture(HeightmapDir + "/SplatRock.png", SplatKind.Rock);

            return new[]
            {
                EnsureLayer(
                    HeightmapDir + "/LayerSand.terrainlayer",
                    "MonurikiSand",
                    sand,
                    tileSize: 48f,
                    metallic: 0f,
                    smoothness: 0.34f),
                EnsureLayer(
                    HeightmapDir + "/LayerGrass.terrainlayer",
                    "MonurikiGrass",
                    grass,
                    tileSize: 36f,
                    metallic: 0f,
                    smoothness: 0.24f),
                EnsureLayer(
                    HeightmapDir + "/LayerRock.terrainlayer",
                    "MonurikiRock",
                    rock,
                    tileSize: 56f,
                    metallic: 0.04f,
                    smoothness: 0.16f)
            };
        }

        enum SplatKind
        {
            Sand,
            Grass,
            Rock
        }

        /// <summary>
        /// Prefer authored oil seamless PNG. Only bake procedural noise if the file is missing.
        /// </summary>
        static Texture2D EnsureOilSplatTexture(string path, SplatKind kind)
        {
            if (!File.Exists(path))
            {
                WriteProceduralSplatFallback(path, kind);
            }

            ConfigureSplatImporter(path);
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        static void ConfigureSplatImporter(string path)
        {
            AssetDatabase.ImportAsset(path);
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null)
            {
                return;
            }

            importer.textureType = TextureImporterType.Default;
            importer.sRGBTexture = true;
            importer.mipmapEnabled = true;
            importer.wrapMode = TextureWrapMode.Repeat;
            importer.filterMode = FilterMode.Bilinear;
            importer.anisoLevel = 4;
            importer.maxTextureSize = 1024;
            importer.SaveAndReimport();
        }

        /// <summary>
        /// Procedural fallback only — authored oil textures live at Splat*.png.
        /// </summary>
        static void WriteProceduralSplatFallback(string path, SplatKind kind)
        {
            int size = SplatTexSize;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var pixels = new Color[size * size];

            Color baseA;
            Color baseB;
            float grain;
            float clump;
            float smoothA;
            float smoothB;
            switch (kind)
            {
                case SplatKind.Sand:
                    baseA = new Color(0.78f, 0.70f, 0.50f);
                    baseB = new Color(0.68f, 0.58f, 0.40f);
                    grain = 0.10f;
                    clump = 0.06f;
                    smoothA = 0.42f;
                    smoothB = 0.28f;
                    break;
                case SplatKind.Rock:
                    baseA = new Color(0.52f, 0.49f, 0.46f);
                    baseB = new Color(0.36f, 0.34f, 0.32f);
                    grain = 0.16f;
                    clump = 0.12f;
                    smoothA = 0.22f;
                    smoothB = 0.10f;
                    break;
                default: // Grass
                    baseA = new Color(0.40f, 0.54f, 0.28f);
                    baseB = new Color(0.28f, 0.40f, 0.20f);
                    grain = 0.12f;
                    clump = 0.18f;
                    smoothA = 0.30f;
                    smoothB = 0.18f;
                    break;
            }

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float u = x / (float)size;
                    float v = y / (float)size;
                    float n1 = TileNoise(u * 8f, v * 8f, 8);
                    float n2 = TileNoise(u * 19f, v * 23f, 19);
                    float n3 = TileNoise(u * 41f + 0.3f, v * 37f + 0.7f, 41);
                    float macro = n1 * 0.55f + n2 * 0.30f + n3 * 0.15f;
                    float fine = n2 * 0.45f + n3 * 0.55f;

                    float mix = Mathf.Clamp01(macro * (0.55f + clump) + fine * grain);
                    Color rgb = Color.Lerp(baseA, baseB, mix);
                    if (kind == SplatKind.Grass)
                    {
                        float fleck = Mathf.SmoothStep(0.62f, 0.92f, n3);
                        rgb = Color.Lerp(rgb, new Color(0.55f, 0.62f, 0.22f), fleck * 0.35f);
                    }
                    else if (kind == SplatKind.Rock)
                    {
                        float crack = Mathf.SmoothStep(0.72f, 0.95f, n2);
                        rgb = Color.Lerp(rgb, new Color(0.22f, 0.21f, 0.20f), crack * 0.45f);
                    }
                    else
                    {
                        float wet = Mathf.SmoothStep(0.78f, 0.98f, n1);
                        rgb = Color.Lerp(rgb, new Color(0.58f, 0.52f, 0.40f), wet * 0.25f);
                    }

                    float smooth = Mathf.Lerp(smoothA, smoothB, mix);
                    pixels[y * size + x] = new Color(rgb.r, rgb.g, rgb.b, smooth);
                }
            }

            tex.SetPixels(pixels);
            tex.Apply(false, false);
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
        }

        static float TileNoise(float x, float y, int period)
        {
            float fx = Mathf.Floor(x);
            float fy = Mathf.Floor(y);
            float ux = x - fx;
            float uy = y - fy;
            ux = ux * ux * (3f - 2f * ux);
            uy = uy * uy * (3f - 2f * uy);

            int x0 = Mod((int)fx, period);
            int y0 = Mod((int)fy, period);
            int x1 = Mod(x0 + 1, period);
            int y1 = Mod(y0 + 1, period);

            float v00 = Hash01(x0, y0);
            float v10 = Hash01(x1, y0);
            float v01 = Hash01(x0, y1);
            float v11 = Hash01(x1, y1);
            float a = Mathf.Lerp(v00, v10, ux);
            float b = Mathf.Lerp(v01, v11, ux);
            return Mathf.Lerp(a, b, uy);
        }

        static int Mod(int a, int m)
        {
            int r = a % m;
            return r < 0 ? r + m : r;
        }

        static float Hash01(int x, int y)
        {
            unchecked
            {
                uint h = (uint)(x * 374761393 + y * 668265263);
                h = (h ^ (h >> 13)) * 1274126177u;
                h ^= h >> 16;
                return (h & 0x00FFFFFFu) / 16777215f;
            }
        }

        static TerrainLayer EnsureLayer(
            string path,
            string name,
            Texture2D diffuse,
            float tileSize,
            float metallic,
            float smoothness)
        {
            var layer = AssetDatabase.LoadAssetAtPath<TerrainLayer>(path);
            if (layer == null)
            {
                layer = new TerrainLayer();
                AssetDatabase.CreateAsset(layer, path);
            }

            layer.name = name;
            layer.diffuseTexture = diffuse;
            layer.tileSize = new Vector2(tileSize, tileSize);
            layer.metallic = metallic;
            layer.smoothness = smoothness;
            EditorUtility.SetDirty(layer);
            return layer;
        }

        /// <summary>
        /// Sand / grass / rock weights from height + slope, with soft edges and noise breakup.
        /// </summary>
        static void PaintAlphamaps(TerrainData data, float[,] heights01, TerrainScale scale)
        {
            int w = data.alphamapWidth;
            int h = data.alphamapHeight;
            int layers = data.alphamapLayers;
            if (layers < 3)
            {
                return;
            }

            int srcRes = heights01.GetLength(0);
            float meterPerSampleX = scale.SizeX / Mathf.Max(1, srcRes - 1);
            float meterPerSampleZ = scale.SizeZ / Mathf.Max(1, srcRes - 1);
            float heightM = scale.HeightY;

            var maps = new float[h, w, layers];
            for (int y = 0; y < h; y++)
            {
                float ty = y / (float)(h - 1);
                int srcY = Mathf.RoundToInt(ty * (srcRes - 1));
                for (int x = 0; x < w; x++)
                {
                    float tx = x / (float)(w - 1);
                    int srcX = Mathf.RoundToInt(tx * (srcRes - 1));
                    float elev = heights01[srcY, srcX];
                    float slope = SampleSlope01(
                        heights01, srcY, srcX, srcRes, heightM, meterPerSampleX, meterPerSampleZ);

                    // Shoreline: soft sand band (~0–18 m) with noise-frayed waterline.
                    float shoreNoise = TileNoise(tx * 48f, ty * 48f, 48) * 0.035f;
                    float sand = 1f - SmoothStep(0.02f + shoreNoise, 0.12f + shoreNoise * 0.5f, elev);

                    // High rock by elevation (peaks / ridges).
                    float ridgeNoise = (TileNoise(tx * 22f + 1.7f, ty * 22f, 22) - 0.5f) * 0.08f;
                    float rockHigh = SmoothStep(0.38f + ridgeNoise, 0.62f + ridgeNoise, elev);

                    // Cliff rock by slope (breaks height-only banding on steep faces).
                    float rockSlope = SmoothStep(0.28f, 0.62f, slope);

                    // Mid slopes: mix grass with a little rock so mountains aren't one blob.
                    float midNoise = TileNoise(tx * 14f, ty * 18f + 3f, 14);
                    float rockMid = SmoothStep(0.22f, 0.48f, elev) *
                        SmoothStep(0.18f, 0.45f, slope) *
                        Mathf.Lerp(0.15f, 0.55f, midNoise);

                    float rock = Mathf.Clamp01(
                        Mathf.Max(rockHigh, rockSlope * 0.95f) + rockMid * 0.65f);
                    // Keep beaches mostly sand even if slightly steep.
                    rock *= 1f - sand * 0.85f;

                    float grass = Mathf.Clamp01(1f - sand - rock);
                    // Patchy grass→sand near shore (dunes / scrub).
                    float scrub = TileNoise(tx * 31f + 0.5f, ty * 27f, 31);
                    if (elev < 0.18f && sand > 0.05f)
                    {
                        float blend = SmoothStep(0.05f, 0.18f, elev) * scrub;
                        float transfer = Mathf.Min(grass, blend * 0.35f);
                        grass -= transfer;
                        sand += transfer;
                    }

                    // Peak grass pockets in gentler high ground.
                    if (elev > 0.35f && slope < 0.35f)
                    {
                        float pocket = (1f - SmoothStep(0.2f, 0.4f, slope)) *
                            TileNoise(tx * 9f, ty * 11f, 9);
                        float transfer = Mathf.Min(rock, pocket * 0.25f);
                        rock -= transfer;
                        grass += transfer;
                    }

                    float sum = sand + grass + rock;
                    if (sum < 1e-5f)
                    {
                        grass = 1f;
                        sum = 1f;
                    }

                    maps[y, x, 0] = sand / sum;
                    maps[y, x, 1] = grass / sum;
                    maps[y, x, 2] = rock / sum;
                }
            }

            data.SetAlphamaps(0, 0, maps);
            EditorUtility.SetDirty(data);
        }

        static float SampleSlope01(
            float[,] heights,
            int y,
            int x,
            int res,
            float heightM,
            float meterPerX,
            float meterPerZ)
        {
            int x0 = Mathf.Max(x - 1, 0);
            int x1 = Mathf.Min(x + 1, res - 1);
            int y0 = Mathf.Max(y - 1, 0);
            int y1 = Mathf.Min(y + 1, res - 1);
            float dx = (heights[y, x1] - heights[y, x0]) * heightM /
                Mathf.Max(1e-4f, (x1 - x0) * meterPerX);
            float dz = (heights[y1, x] - heights[y0, x]) * heightM /
                Mathf.Max(1e-4f, (y1 - y0) * meterPerZ);
            float grade = Mathf.Sqrt(dx * dx + dz * dz);
            // ~0 flat, ~1 at ~45°+ cliffs for Monuriki scale.
            return Mathf.Clamp01(grade / 1.2f);
        }

        static float SmoothStep(float edge0, float edge1, float x)
        {
            float t = Mathf.Clamp01((x - edge0) / Mathf.Max(1e-5f, edge1 - edge0));
            return t * t * (3f - 2f * t);
        }

        static Material CreateOrUpdateTerrainOilNPR(
            string path, Texture2D canvas, Texture2D brush)
        {
            Shader shader = Shader.Find("CustomSRP/TerrainOilNPR");
            if (shader == null)
            {
                throw new System.InvalidOperationException(
                    "CustomSRP/TerrainOilNPR shader not found.");
            }

            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                material = new Material(shader);
                AssetDatabase.CreateAsset(material, path);
            }
            else
            {
                material.shader = shader;
            }

            material.SetColor("_BaseColor", Color.white);
            material.SetFloat("_Kuwahara", 1f);
            material.EnableKeyword("_KUWAHARA_ON");
            // Control UV over ~1.3 km — small radius ≈ multi-meter paint dabs.
            material.SetFloat("_KuwaharaRadius", 0.0045f);
            material.SetFloat("_Canvas", 1f);
            material.EnableKeyword("_CANVAS_ON");
            if (canvas != null)
            {
                material.SetTexture("_CanvasMap", canvas);
                material.SetTextureScale("_CanvasMap", new Vector2(18f, 18f));
            }

            material.SetFloat("_CanvasStrength", 0.08f);
            material.SetFloat("_PaintThickness", 0.12f);
            material.SetFloat("_ShadeSteps", 4f);
            material.SetFloat("_ShadeLift", 0.42f);
            material.SetFloat("_ShadowLift", 0.95f);
            material.SetColor("_ShadowTint", new Color(0.42f, 0.48f, 0.72f, 1f));
            material.SetColor("_ShadowWarm", new Color(0.62f, 0.45f, 0.32f, 1f));
            material.SetFloat("_ShadowWobble", 0.4f);
            material.SetFloat("_ShadowBrushScale", 1.2f);
            material.SetColor("_SpecularColor", new Color(0.5f, 0.45f, 0.38f, 1f));
            material.SetFloat("_SpecularThreshold", 0.88f);
            material.SetColor("_AmbientColor", new Color(0.52f, 0.5f, 0.44f, 1f));
            material.SetFloat("_InternalEdge", 1f);
            material.EnableKeyword("_INTERNAL_EDGE_ON");
            material.SetFloat("_EdgeStrength", 0.4f);
            material.SetColor("_EdgeColor", new Color(0.28f, 0.22f, 0.16f, 1f));
            if (brush != null)
            {
                material.SetTexture("_OutlineBrushMap", brush);
                material.SetTextureScale("_OutlineBrushMap", Vector2.one);
            }

            material.EnableKeyword("_RECEIVE_SHADOWS");
            material.SetFloat("_ReceiveShadows", 1f);
            material.enableInstancing = false;
            material.renderQueue = 1900; // Geometry-100
            EditorUtility.SetDirty(material);
            return material;
        }

        static void DeleteLegacyMeshAssets()
        {
            if (AssetDatabase.LoadAssetAtPath<Object>(LegacyMeshPath) != null)
            {
                AssetDatabase.DeleteAsset(LegacyMeshPath);
            }

            if (AssetDatabase.LoadAssetAtPath<Object>(LegacyLitMatPath) != null)
            {
                AssetDatabase.DeleteAsset(LegacyLitMatPath);
            }

            // Replaced by TerrainOilNPRMonuriki.mat
            if (AssetDatabase.LoadAssetAtPath<Object>(LegacyTerrainLitMatPath) != null)
            {
                AssetDatabase.DeleteAsset(LegacyTerrainLitMatPath);
            }
        }

        static void BuildScene(
            TerrainData terrainData,
            Material terrainMat,
            Material oceanMat,
            TerrainScale scale)
        {
            GameObject terrainGo = Terrain.CreateTerrainGameObject(terrainData);
            terrainGo.name = "MonurikiTerrain";
            terrainGo.transform.position = Vector3.zero;

            var terrain = terrainGo.GetComponent<Terrain>();
            terrain.materialTemplate = terrainMat;
            // Scenic camera sits ~1 km off the island. Below this, Unity swaps in the
            // basemap shader and received shadows lose the oil umbra.
            terrain.basemapDistance = 4500f;
            terrain.drawHeightmap = true;
            terrain.allowAutoConnect = false;
            terrain.drawTreesAndFoliage = false;

            float oceanScale = Mathf.Max(scale.SizeX, scale.SizeZ) / 10f * 1.35f;
            // Slightly below mean shoreline so soft sand band reads as beach, not cliff.
            var ocean = TestSceneUtility.CreatePlane(
                "Ocean",
                new Vector3(scale.SizeX * 0.5f, 0.25f, scale.SizeZ * 0.5f),
                new Vector3(oceanScale, 1f, oceanScale),
                oceanMat);

            TestSceneUtility.CreateDirectionalLight(
                "Directional Light",
                new Vector3(35f, -40f, 10f),
                new Color(1f, 0.97f, 0.92f),
                intensity: 1.15f,
                shadows: LightShadows.Hard,
                shadowStrength: 1f,
                shadowBias: 0.05f,
                shadowNormalBias: 0.8f);

            float cx = scale.SizeX * 0.5f;
            float cz = scale.SizeZ * 0.52f;
            TestSceneUtility.EnsureMainCamera(
                new Vector3(cx - scale.SizeX * 0.55f, scale.HeightY * 0.85f, cz - scale.SizeZ * 0.7f),
                new Vector3(28f, 35f, 0f));
            ConfigureOilCamera(scale);

            ocean.transform.SetAsFirstSibling();
        }

        static void ConfigureOilCamera(TerrainScale scale)
        {
            var cam = Object.FindFirstObjectByType<Camera>();
            if (cam == null)
            {
                return;
            }

            cam.farClipPlane = Mathf.Max(4000f, scale.SizeX * 3f);
            cam.nearClipPlane = 0.3f;
            cam.allowHDR = false;
            cam.allowMSAA = false;
            cam.clearFlags = CameraClearFlags.Skybox;

            var crp = cam.GetComponent<CustomRenderPipelineCamera>();
            if (crp == null)
            {
                crp = cam.gameObject.AddComponent<CustomRenderPipelineCamera>();
            }

            var so = new SerializedObject(crp);
            SerializedProperty settings = so.FindProperty("settings");
            settings.FindPropertyRelative("copyColor").boolValue = false;
            settings.FindPropertyRelative("copyDepth").boolValue = false;
            settings.FindPropertyRelative("overridePostFX").boolValue = true;
            settings.FindPropertyRelative("postFXSettings").objectReferenceValue = null;
            settings.FindPropertyRelative("renderScaleMode").enumValueIndex = 0;
            settings.FindPropertyRelative("renderScale").floatValue = 1f;
            settings.FindPropertyRelative("allowFXAA").boolValue = false;
            settings.FindPropertyRelative("keepAlpha").boolValue = false;
            settings.FindPropertyRelative("maskLights").boolValue = false;
            so.ApplyModifiedPropertiesWithoutUndo();
        }
    }
}
