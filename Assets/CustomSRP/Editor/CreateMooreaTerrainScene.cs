using System.IO;
using CustomSRP.Examples;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace CustomSRP.Editor
{
    /// <summary>
    /// 莫雷阿岛 1:1 米制地形：Unity Terrain + CustomSRP/TerrainOilNPR（splat + 油画光照）。
    /// </summary>
    public static class CreateMooreaTerrainScene
    {
        const string ScenePath = TestSceneUtility.ScenesPath + "/MooreaTerrain.unity";
        const string HeightmapDir = "Assets/Terrain/Moorea";
        const string RawPath = HeightmapDir + "/moorea_heightmap.raw";
        const string JsonPath = HeightmapDir + "/moorea_heightmap.json";
        const string HeightPngPath = HeightmapDir + "/moorea_heightmap.png";
        // Binary TerrainData (Force Binary serialization). Not a scene — open MooreaTerrain.unity.
        const string TerrainDataPath = HeightmapDir + "/MooreaTerrainData.asset";
        const string LegacyTerrainDataPath = HeightmapDir + "/Moorea.asset";
        const string LegacyMeshPath = HeightmapDir + "/MooreaLitMesh.asset";
        const string TerrainMatPath = TestSceneUtility.MaterialsPath + "/TerrainOilNPRMoorea.mat";
        const string LegacyTerrainLitMatPath = TestSceneUtility.MaterialsPath + "/TerrainLitMoorea.mat";
        const string OceanMatPath = TestSceneUtility.MaterialsPath + "/LitMooreaOcean.mat";
        const string SkyMatPath = TestSceneUtility.MaterialsPath + "/OilSkybox.mat";
        const string CloudMatPath = TestSceneUtility.MaterialsPath + "/OilCloud.mat";
        const string LegacyLitMatPath = TestSceneUtility.MaterialsPath + "/LitMooreaTerrain.mat";

        const int HeightRes = 2049;
        const int AlphamapRes = 1024;
        const int SplatTexSize = 256;

        // Only rebuild when menu is used, or when this flag file exists (CI / agent).
        // Auto-create during AssetDatabase import races and can leave half-written TerrainData.
        // Shoreline cleaned heightmap: flat seabed below ocean plane.
        [InitializeOnLoadMethod]
        static void AutoCreateIfForced()
        {
            EditorApplication.delayCall += () =>
            {
                if (EditorApplication.isPlayingOrWillChangePlaymode)
                {
                    return;
                }

                const string forceFlag =
                    TestSceneUtility.Root + "/Editor/.force-rebuild-MooreaTerrain";
                const string repaintFlag =
                    TestSceneUtility.Root + "/Editor/.force-repaint-MooreaLanduse";
                const string applyHeightsFlag =
                    TestSceneUtility.Root + "/Editor/.force-apply-MooreaHeightmap";
                if (File.Exists(applyHeightsFlag))
                {
                    File.Delete(applyHeightsFlag);
                    ApplyHeightmapInPlace();
                    return;
                }

                if (File.Exists(repaintFlag))
                {
                    File.Delete(repaintFlag);
                    RepaintLandCover();
                    return;
                }

                if (!File.Exists(forceFlag))
                {
                    return;
                }

                File.Delete(forceFlag);
                if (!File.Exists(RawPath) || !File.Exists(JsonPath))
                {
                    Debug.LogError($"[CustomSRP] Moorea force-rebuild skipped — missing {RawPath}");
                    return;
                }

                Create();
            };
        }

        /// <summary>
        /// Push cleaned RAW heights into existing TerrainData (flat seabed / soft beach).
        /// Does not recreate the scene.
        /// </summary>
        [MenuItem("CustomSRP/Rebuild Moorea Ocean Mesh")]
        public static void RebuildOceanMesh()
        {
            var sea = Object.FindAnyObjectByType<OilOceanSurface>();
            if (sea == null)
            {
                Debug.LogError(
                    "[CustomSRP] No OilOceanSurface in the open scene. " +
                    "Open MooreaTerrain and select the Sea object, or recreate the scene.");
                return;
            }

            sea.vertexDistance = Mathf.Max(
                sea.vertexDistance, sea.gridScale / OilOceanSurface.MaxSubdivisions);
            sea.followCamera = true;
            sea.waveHeight = 6.5f;
            sea.boundsPadding = 4f;
            sea.Rebuild();
            EditorUtility.SetDirty(sea);
            Debug.Log("[CustomSRP] Rebuilt Moorea ocean mesh on " + sea.gameObject.name);
        }

        /// <summary>
        /// Write edited Unity Terrain heights back to moorea_heightmap.{raw,png}
        /// (north-up little-endian uint16) so OilOceanSurface / shore foam stay in sync.
        /// </summary>
        [MenuItem("CustomSRP/Export Moorea Heightmap From Terrain")]
        public static void ExportHeightmapFromTerrain()
        {
            var data = AssetDatabase.LoadAssetAtPath<TerrainData>(TerrainDataPath);
            if (data == null)
            {
                Debug.LogError($"[CustomSRP] Missing TerrainData at {TerrainDataPath}");
                return;
            }

            try
            {
                int res = data.heightmapResolution;
                if (res != HeightRes)
                {
                    Debug.LogWarning(
                        $"[CustomSRP] Terrain heightmapResolution={res}, " +
                        $"exporting at that size (pipeline default is {HeightRes}).");
                }

                float[,] heights01 = data.GetHeights(0, 0, res, res);
                float peakM = data.size.y;
                WriteHeights01(RawPath, HeightPngPath, heights01, peakM);
                PatchHeightmapJsonPeak(peakM, res);

                // Skip postprocessor Apply — we just exported FROM terrain.
                const string skipApply =
                    TestSceneUtility.Root + "/Editor/.skip-apply-MooreaHeightmap";
                File.WriteAllText(skipApply, "export");

                AssetDatabase.ImportAsset(RawPath);
                AssetDatabase.ImportAsset(HeightPngPath);
                EnsureShoreHeightTexture();

                if (File.Exists(skipApply))
                {
                    File.Delete(skipApply);
                }

                float land01 = LandCoverage01(heights01);
                var sea = Object.FindAnyObjectByType<OilOceanSurface>();
                if (sea != null)
                {
                    sea.Rebuild();
                }

                Debug.Log(
                    $"[CustomSRP] Exported Moorea heightmap from TerrainData →\n" +
                    $"  {RawPath}\n  {HeightPngPath}\n" +
                    $"  res={res} peak={peakM:F1} m land≈{land01:P0}");
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[CustomSRP] Export Moorea heightmap failed:\n{ex}");
            }
        }

        [MenuItem("CustomSRP/Apply Moorea Heightmap")]
        public static void ApplyHeightmapInPlace()
        {
            if (!File.Exists(RawPath) || !File.Exists(JsonPath))
            {
                Debug.LogError($"[CustomSRP] Missing heightmap under {HeightmapDir}");
                return;
            }

            var data = AssetDatabase.LoadAssetAtPath<TerrainData>(TerrainDataPath);
            if (data == null)
            {
                Create();
                return;
            }

            try
            {
                TerrainScale scale = ReadScale(JsonPath);
                float[,] heights01 = LoadHeights01(RawPath, HeightRes);
                float land01 = LandCoverage01(heights01);
                if (land01 < 0.05f)
                {
                    throw new IOException(
                        $"Heightmap looks empty (land={land01:P0}). Re-run fetch_moorea_heightmap.py.");
                }

                data.heightmapResolution = HeightRes;
                data.size = new Vector3(scale.SizeX, scale.HeightY, scale.SizeZ);
                data.SetHeights(0, 0, heights01);
                TerrainLayer[] layers = EnsureTerrainLayers();
                data.terrainLayers = layers;
                PaintAlphamaps(data, heights01, scale);
                EditorUtility.SetDirty(data);

                var terrain = Object.FindAnyObjectByType<Terrain>();
                if (terrain != null && terrain.terrainData == data)
                {
                    terrain.terrainData = data;
                    terrain.Flush();
                }

                var terrainMat = AssetDatabase.LoadAssetAtPath<Material>(TerrainMatPath);
                ApplyRidgeRock(terrainMat);
                // Shore height texture for ocean foam must match new RAW.
                EnsureShoreHeightTexture();
                AssetDatabase.SaveAssets();
                Debug.Log(
                    $"[CustomSRP] Applied Moorea heightmap (land ≈ {land01:P0}). " +
                    "Shoreline seabed is flat below ocean plane.");
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[CustomSRP] Apply Moorea heightmap failed:\n{ex}");
            }
        }

        [MenuItem("CustomSRP/Repaint Moorea Land Cover")]
        public static void RepaintLandCover()
        {
            if (!File.Exists(RawPath) || !File.Exists(JsonPath))
            {
                Debug.LogError($"[CustomSRP] Missing heightmap under {HeightmapDir}");
                return;
            }

            var data = AssetDatabase.LoadAssetAtPath<TerrainData>(TerrainDataPath);
            if (data == null)
            {
                Create();
                return;
            }

            try
            {
                TerrainScale scale = ReadScale(JsonPath);
                float[,] heights01 = LoadHeights01(RawPath, HeightRes);
                TerrainLayer[] layers = EnsureTerrainLayers();
                data.terrainLayers = layers;
                PaintAlphamaps(data, heights01, scale);
                EditorUtility.SetDirty(data);

                var terrain = Object.FindAnyObjectByType<Terrain>();
                if (terrain != null && terrain.terrainData == data)
                {
                    terrain.Flush();
                }

                var terrainMat = AssetDatabase.LoadAssetAtPath<Material>(TerrainMatPath);
                ApplyRidgeRock(terrainMat);
                AssetDatabase.SaveAssets();
                Debug.Log(
                    "[CustomSRP] Repainted Moorea alphamap from moorea_landuse_index.png " +
                    "(Sand / Grass / Forest / Built + ridge rock).");
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[CustomSRP] Repaint Moorea land cover failed:\n{ex}");
            }
        }

        [MenuItem("CustomSRP/Create Moorea Terrain Scene (1:1)")]
        public static void Create()
        {
            if (!File.Exists(RawPath) || !File.Exists(JsonPath))
            {
                Debug.LogError(
                    $"[CustomSRP] Missing heightmap. Run:\n" +
                    $"  python3 {HeightmapDir}/fetch_moorea_heightmap.py");
                return;
            }

            try
            {
                TestSceneUtility.EnsureStandardFolders();
                TestSceneUtility.EnsureFolder("Assets", "Terrain");
                TestSceneUtility.EnsureFolder("Assets/Terrain", "Moorea");

                TerrainScale scale = ReadScale(JsonPath);
                float[,] heights01 = LoadHeights01(RawPath, HeightRes);
                float land01 = LandCoverage01(heights01);
                if (land01 < 0.05f)
                {
                    throw new IOException(
                        $"Heightmap looks empty (land={land01:P0}). Re-run fetch_moorea_heightmap.py.");
                }

                TerrainData terrainData = RebuildTerrainData(heights01, scale);
                TerrainLayer[] layers = EnsureTerrainLayers();
                terrainData.terrainLayers = layers;
                PaintAlphamaps(terrainData, heights01, scale);
                EditorUtility.SetDirty(terrainData);

                Texture2D canvas = TestSceneUtility.EnsureOilCanvasTexture();
                Texture2D brush = TestSceneUtility.EnsureOilBrushTexture();
                Material terrainMat = CreateOrUpdateTerrainOilNPR(TerrainMatPath, canvas, brush);
                Material skyMat = TestSceneUtility.CreateOrUpdateOilSkybox(
                    SkyMatPath, canvas, timeOfDay: 12f);
                Material oceanMat = TestSceneUtility.CreateOrUpdateOilOcean(
                    OceanMatPath,
                    canvas,
                    brush,
                    shoreHeightMap: null,
                    shoreOriginXZ: Vector2.zero,
                    shoreSizeXZ: new Vector2(scale.SizeX, scale.SizeZ),
                    shoreHeightScaleM: scale.HeightY,
                    shoreWaterLevelM: 0.35f);

                DeleteLegacyMeshAssets();

                var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                BuildScene(terrainData, terrainMat, oceanMat, skyMat, scale);
                EditorSceneManager.SaveScene(scene, ScenePath);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();

                TestSceneUtility.AddToBuildSettings(ScenePath, makeFirst: false);
                Debug.Log(
                    $"[CustomSRP] Created Moorea 1:1 terrain scene: {ScenePath}\n" +
                    $"  TerrainData = {TerrainDataPath}\n" +
                    $"  size = {scale.SizeX:F1} × {scale.HeightY:F1} × {scale.SizeZ:F1} m " +
                    $"(land ≈ {land01:P0})\n" +
                    "  Open the .unity scene (not the .asset). TerrainData is binary by design.");
                EditorSceneManager.OpenScene(ScenePath);
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[CustomSRP] Create Moorea Terrain failed:\n{ex}");
            }
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
                SizeX = 16500f,
                SizeZ = 16500f,
                HeightY = 1207f,
                IslandLengthM = 15000f
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

        /// <summary>
        /// Unity GetHeights: [z=0 south, x]. RAW/PNG: row0 = north, little-endian uint16.
        /// </summary>
        static void WriteHeights01(
            string rawPath,
            string pngPath,
            float[,] heights01,
            float peakMeters)
        {
            int res = heights01.GetLength(0);
            if (heights01.GetLength(1) != res)
            {
                throw new IOException("Heightmap must be square.");
            }

            var bytes = new byte[res * res * 2];
            // Texture2D row0 = bottom → put south there so PNG top = north (same as RAW).
            var texPixels = new Color32[res * res];
            for (int fileRow = 0; fileRow < res; fileRow++)
            {
                int unityZ = res - 1 - fileRow;
                for (int x = 0; x < res; x++)
                {
                    float h01 = Mathf.Clamp01(heights01[unityZ, x]);
                    ushort u = (ushort)Mathf.RoundToInt(h01 * 65535f);
                    int o = (fileRow * res + x) * 2;
                    bytes[o] = (byte)(u & 0xff);
                    bytes[o + 1] = (byte)(u >> 8);
                    byte g = (byte)Mathf.RoundToInt(h01 * 255f);
                    int texY = res - 1 - fileRow;
                    texPixels[texY * res + x] = new Color32(g, g, g, 255);
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(rawPath) ?? HeightmapDir);
            File.WriteAllBytes(rawPath, bytes);

            var tex = new Texture2D(res, res, TextureFormat.RGBA32, false, true);
            tex.SetPixels32(texPixels);
            File.WriteAllBytes(pngPath, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);

            // Keep a human note of peak for encoding.
            if (peakMeters > 0f)
            {
                Debug.Log($"[CustomSRP] Height encoding peak = {peakMeters:F1} m → 65535.");
            }
        }

        static void PatchHeightmapJsonPeak(float peakMeters, int res)
        {
            if (!File.Exists(JsonPath))
            {
                return;
            }

            string json = File.ReadAllText(JsonPath);
            json = ReplaceJsonNumber(json, "\"elevation_max_m\":", peakMeters);
            json = ReplaceJsonNumber(json, "\"terrain_height_m\":", peakMeters);
            json = ReplaceJsonNumber(json, "\"y\":", peakMeters, afterKey: "recommended_terrain_size_m");
            // Keep width/height in sync if terrain resolution differs.
            if (json.Contains("\"width\":"))
            {
                json = ReplaceJsonInt(json, "\"width\":", res);
                json = ReplaceJsonInt(json, "\"height\":", res);
            }

            string encoding =
                $"uint16, 0 = 0 m, 65535 = {peakMeters:0.###} m, linear";
            int enc = json.IndexOf("\"encoding\":");
            if (enc >= 0)
            {
                int q0 = json.IndexOf('"', enc + 11);
                int q1 = json.IndexOf('"', q0 + 1);
                if (q0 >= 0 && q1 > q0)
                {
                    json = json.Substring(0, q0 + 1) + encoding + json.Substring(q1);
                }
            }

            File.WriteAllText(JsonPath, json);
        }

        static string ReplaceJsonNumber(
            string json,
            string key,
            float value,
            string afterKey = null)
        {
            int start = 0;
            if (!string.IsNullOrEmpty(afterKey))
            {
                start = json.IndexOf(afterKey);
                if (start < 0)
                {
                    return json;
                }
            }

            int i = json.IndexOf(key, start);
            if (i < 0)
            {
                return json;
            }

            i += key.Length;
            while (i < json.Length && (json[i] == ' ' || json[i] == '\t'))
            {
                i++;
            }

            int j = i;
            while (j < json.Length &&
                   (char.IsDigit(json[j]) || json[j] == '.' || json[j] == '-'))
            {
                j++;
            }

            string num = value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            return json.Substring(0, i) + num + json.Substring(j);
        }

        static string ReplaceJsonInt(string json, string key, int value)
        {
            int i = json.IndexOf(key);
            if (i < 0)
            {
                return json;
            }

            i += key.Length;
            while (i < json.Length && (json[i] == ' ' || json[i] == '\t'))
            {
                i++;
            }

            int j = i;
            while (j < json.Length && char.IsDigit(json[j]))
            {
                j++;
            }

            return json.Substring(0, i) + value + json.Substring(j);
        }

        static float LandCoverage01(float[,] heights01)
        {
            int h = heights01.GetLength(0);
            int w = heights01.GetLength(1);
            int land = 0;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    if (heights01[y, x] > 1e-4f)
                    {
                        land++;
                    }
                }
            }

            return land / (float)(h * w);
        }

        /// <summary>
        /// Always recreate TerrainData so a half-written binary asset cannot stick around.
        /// </summary>
        static TerrainData RebuildTerrainData(float[,] heights01, TerrainScale scale)
        {
            if (AssetDatabase.LoadAssetAtPath<Object>(LegacyTerrainDataPath) != null)
            {
                AssetDatabase.DeleteAsset(LegacyTerrainDataPath);
            }

            if (AssetDatabase.LoadAssetAtPath<Object>(TerrainDataPath) != null)
            {
                AssetDatabase.DeleteAsset(TerrainDataPath);
            }

            var data = new TerrainData();
            AssetDatabase.CreateAsset(data, TerrainDataPath);
            // Order matters: resolution before size before heights.
            data.heightmapResolution = HeightRes;
            data.alphamapResolution = AlphamapRes;
            data.size = new Vector3(scale.SizeX, scale.HeightY, scale.SizeZ);
            data.SetHeights(0, 0, heights01);
            EditorUtility.SetDirty(data);
            AssetDatabase.SaveAssets();

            data = AssetDatabase.LoadAssetAtPath<TerrainData>(TerrainDataPath);
            if (data == null)
            {
                throw new IOException($"Failed to create TerrainData at {TerrainDataPath}");
            }

            float maxH = 0f;
            float sumH = 0f;
            int samples = 0;
            int step = 64;
            for (int y = 0; y < data.heightmapResolution; y += step)
            {
                for (int x = 0; x < data.heightmapResolution; x += step)
                {
                    float h = data.GetHeight(x, y);
                    maxH = Mathf.Max(maxH, h);
                    sumH += h;
                    samples++;
                }
            }

            if (maxH < 10f)
            {
                throw new IOException(
                    $"TerrainData heights look empty after SetHeights (max GetHeight={maxH:F2}).");
            }

            Debug.Log(
                $"[CustomSRP] TerrainData OK: {TerrainDataPath}\n" +
                $"  resolution={data.heightmapResolution} size={data.size}\n" +
                $"  GetHeight corner={data.GetHeight(0, 0):F1} " +
                $"center={data.GetHeight(HeightRes / 2, HeightRes / 2):F1} " +
                $"maxSample={maxH:F1} meanSample={sumH / Mathf.Max(1, samples):F1}");
            return data;
        }

        /// <summary>
        /// Ridge rock is alphamap layer 4. _RidgeAmount scales the control residual
        /// (1 - weight of sand/grass/forest/built). Tile matches LayerRock (56 m).
        /// </summary>
        static void ApplyRidgeRock(Material material)
        {
            if (material == null)
            {
                return;
            }

            var rock = AssetDatabase.LoadAssetAtPath<Texture2D>(HeightmapDir + "/SplatRock.png");
            if (rock == null)
            {
                return;
            }

            material.SetTexture("_RidgeRockMap", rock);
            material.SetTextureScale("_RidgeRockMap", new Vector2(16500f / 56f, 16500f / 56f));
            material.SetFloat("_RidgeAmount", 1f);
            EditorUtility.SetDirty(material);
        }

        static TerrainLayer[] EnsureTerrainLayers()
        {
            // Authored oil-paint seamless splats (do not overwrite if present).
            // Control 0: R sand, G grass, B forest, A built-up.
            // Layer 4 is ridge rock (not in that control map; the oil pass reads the residual).
            Texture2D sand = EnsureOilSplatTexture(HeightmapDir + "/SplatSand.png", SplatKind.Sand);
            Texture2D grass = EnsureOilSplatTexture(HeightmapDir + "/SplatGrass.png", SplatKind.Grass);
            Texture2D forest = EnsureOilSplatTexture(HeightmapDir + "/SplatForest.png", SplatKind.Forest);
            Texture2D built = EnsureOilSplatTexture(HeightmapDir + "/SplatBuilt.png", SplatKind.Built);
            Texture2D rock = EnsureOilSplatTexture(HeightmapDir + "/SplatRock.png", SplatKind.Rock);

            return new[]
            {
                EnsureLayer(
                    HeightmapDir + "/LayerSand.terrainlayer",
                    "LayerSand",
                    sand,
                    tileSize: 48f,
                    metallic: 0f,
                    smoothness: 0.34f),
                EnsureLayer(
                    HeightmapDir + "/LayerGrass.terrainlayer",
                    "LayerGrass",
                    grass,
                    tileSize: 36f,
                    metallic: 0f,
                    smoothness: 0.24f),
                EnsureLayer(
                    HeightmapDir + "/LayerForest.terrainlayer",
                    "LayerForest",
                    forest,
                    tileSize: 52f,
                    metallic: 0f,
                    smoothness: 0.18f),
                EnsureLayer(
                    HeightmapDir + "/LayerBuilt.terrainlayer",
                    "LayerBuilt",
                    built,
                    tileSize: 32f,
                    metallic: 0f,
                    smoothness: 0.22f),
                EnsureLayer(
                    HeightmapDir + "/LayerRock.terrainlayer",
                    "LayerRock",
                    rock,
                    tileSize: 56f,
                    metallic: 0f,
                    smoothness: 0.16f)
            };
        }

        enum SplatKind
        {
            Sand,
            Grass,
            Rock,
            Forest,
            Built
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
        /// Linear height preview used by OilOceanNPR for shoreline foam (拍岸).
        /// </summary>
        static Texture2D EnsureShoreHeightTexture()
        {
            if (!File.Exists(HeightPngPath))
            {
                Debug.LogWarning(
                    "[CustomSRP] Missing moorea_heightmap.png — shore foam falls back to depth only.");
                return null;
            }

            AssetDatabase.ImportAsset(HeightPngPath);
            var importer = AssetImporter.GetAtPath(HeightPngPath) as TextureImporter;
            if (importer != null)
            {
                importer.textureType = TextureImporterType.Default;
                importer.sRGBTexture = false;
                importer.mipmapEnabled = true;
                importer.wrapMode = TextureWrapMode.Clamp;
                importer.filterMode = FilterMode.Bilinear;
                importer.maxTextureSize = 4096;
                importer.anisoLevel = 2;
                importer.SaveAndReimport();
            }

            return AssetDatabase.LoadAssetAtPath<Texture2D>(HeightPngPath);
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
                // 基础配色尝试.xlsx → 季节·夏
                case SplatKind.Sand:
                    baseA = new Color(0.86f, 0.78f, 0.62f);
                    baseB = new Color(0.74f, 0.66f, 0.50f); // #BDA880
                    grain = 0.10f;
                    clump = 0.06f;
                    smoothA = 0.42f;
                    smoothB = 0.28f;
                    break;
                case SplatKind.Rock:
                    baseA = new Color(0.62f, 0.58f, 0.52f);
                    baseB = new Color(0.48f, 0.44f, 0.38f); // #7A7061
                    grain = 0.16f;
                    clump = 0.12f;
                    smoothA = 0.22f;
                    smoothB = 0.10f;
                    break;
                case SplatKind.Forest:
                    baseA = new Color(0.32f, 0.62f, 0.16f); // #529E29
                    baseB = new Color(0.18f, 0.40f, 0.10f); // #2E661A
                    grain = 0.14f;
                    clump = 0.16f;
                    smoothA = 0.22f;
                    smoothB = 0.12f;
                    break;
                case SplatKind.Built:
                    baseA = new Color(0.76f, 0.64f, 0.48f);
                    baseB = new Color(0.64f, 0.52f, 0.36f); // #A3855C 泥
                    grain = 0.12f;
                    clump = 0.14f;
                    smoothA = 0.28f;
                    smoothB = 0.16f;
                    break;
                default: // Grass
                    baseA = new Color(0.50f, 0.74f, 0.18f); // #80BD2E 草顶
                    baseB = new Color(0.24f, 0.44f, 0.10f); // #3D701A 草底
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
                        rgb = Color.Lerp(rgb, new Color(0x83 / 255f, 0x9E / 255f, 0x5E / 255f), fleck * 0.35f);
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

            MakePixelsSeamless(pixels, size, size, band: size / 8);
            tex.SetPixels(pixels);
            tex.Apply(false, false);
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
        }

        /// <summary>
        /// Force opposite edges to match so Repeat wrap does not print a tile grid.
        /// </summary>
        static void MakePixelsSeamless(Color[] pixels, int width, int height, int band)
        {
            band = Mathf.Clamp(band, 8, Mathf.Min(width, height) / 2);
            for (int y = 0; y < height; y++)
            {
                for (int c = 0; c < 4; c++)
                {
                    float avg = 0.5f * (GetCh(pixels, width, 0, y, c) + GetCh(pixels, width, width - 1, y, c));
                    SetCh(pixels, width, 0, y, c, avg);
                    SetCh(pixels, width, width - 1, y, c, avg);
                }

                for (int i = 1; i < band; i++)
                {
                    float t = i / (float)band;
                    float alpha = 0.55f * (1f - t) * (1f - t);
                    for (int c = 0; c < 4; c++)
                    {
                        float a = GetCh(pixels, width, i, y, c);
                        float b = GetCh(pixels, width, width - 1 - i, y, c);
                        float avg = 0.5f * (a + b);
                        SetCh(pixels, width, i, y, c, Mathf.Lerp(a, avg, alpha));
                        SetCh(pixels, width, width - 1 - i, y, c, Mathf.Lerp(b, avg, alpha));
                    }
                }
            }

            for (int x = 0; x < width; x++)
            {
                for (int c = 0; c < 4; c++)
                {
                    float avg = 0.5f * (GetCh(pixels, width, x, 0, c) + GetCh(pixels, width, x, height - 1, c));
                    SetCh(pixels, width, x, 0, c, avg);
                    SetCh(pixels, width, x, height - 1, c, avg);
                }

                for (int i = 1; i < band; i++)
                {
                    float t = i / (float)band;
                    float alpha = 0.55f * (1f - t) * (1f - t);
                    for (int c = 0; c < 4; c++)
                    {
                        float a = GetCh(pixels, width, x, i, c);
                        float b = GetCh(pixels, width, x, height - 1 - i, c);
                        float avg = 0.5f * (a + b);
                        SetCh(pixels, width, x, i, c, Mathf.Lerp(a, avg, alpha));
                        SetCh(pixels, width, x, height - 1 - i, c, Mathf.Lerp(b, avg, alpha));
                    }
                }
            }
        }

        static float GetCh(Color[] pixels, int width, int x, int y, int c)
        {
            Color col = pixels[y * width + x];
            return c switch
            {
                0 => col.r,
                1 => col.g,
                2 => col.b,
                _ => col.a
            };
        }

        static void SetCh(Color[] pixels, int width, int x, int y, int c, float v)
        {
            int i = y * width + x;
            Color col = pixels[i];
            switch (c)
            {
                case 0: col.r = v; break;
                case 1: col.g = v; break;
                case 2: col.b = v; break;
                default: col.a = v; break;
            }

            pixels[i] = col;
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

        const string LanduseIndexPath = HeightmapDir + "/moorea_landuse_index.png";

        /// <summary>
        /// WorldCover index → 4 splat weights. Alphamap y=0 is south, matching the heightmap flip.
        /// 10/95 forest, 20/30/40/90 grass, 50 built-up, 60/80/other sand (reef and lagoon).
        /// </summary>
        static void PaintAlphamaps(TerrainData data, float[,] heights01, TerrainScale scale)
        {
            if (!File.Exists(LanduseIndexPath))
            {
                Debug.LogWarning(
                    $"[CustomSRP] {LanduseIndexPath} missing — slope alphamap fallback.");
                PaintAlphamapsFromSlope(data, heights01, scale);
                return;
            }

            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            try
            {
                if (!tex.LoadImage(File.ReadAllBytes(LanduseIndexPath)))
                {
                    throw new IOException($"Failed to decode {LanduseIndexPath}");
                }

                int tw = tex.width;
                int th = tex.height;
                Color32[] pix = tex.GetPixels32();
                int w = data.alphamapWidth;
                int h = data.alphamapHeight;
                int layers = data.alphamapLayers;
                if (layers < 4 || tw < 2 || th < 2)
                {
                    throw new IOException(
                        $"Alphamap layers={layers} (want 4), landuse {tw}x{th}.");
                }

                int srcRes = heights01.GetLength(0);
                var maps = new float[h, w, layers];
                for (int y = 0; y < h; y++)
                {
                    float ty = y / (float)(h - 1);
                    float v = ty * (th - 1);
                    int y0 = Mathf.FloorToInt(v);
                    int y1 = Mathf.Min(y0 + 1, th - 1);
                    float fy = v - y0;
                    int srcY = Mathf.RoundToInt(ty * (srcRes - 1));
                    for (int x = 0; x < w; x++)
                    {
                        float tx = x / (float)(w - 1);
                        float u = tx * (tw - 1);
                        int x0 = Mathf.FloorToInt(u);
                        int x1 = Mathf.Min(x0 + 1, tw - 1);
                        float fx = u - x0;
                        AccumulateLanduse(maps, pix, tw, y, x, x0, y0, (1f - fx) * (1f - fy));
                        AccumulateLanduse(maps, pix, tw, y, x, x1, y0, fx * (1f - fy));
                        AccumulateLanduse(maps, pix, tw, y, x, x0, y1, (1f - fx) * fy);
                        AccumulateLanduse(maps, pix, tw, y, x, x1, y1, fx * fy);

                        float sum = 0f;
                        for (int layer = 0; layer < 4; layer++)
                        {
                            sum += maps[y, x, layer];
                        }

                        if (sum < 1e-5f)
                        {
                            maps[y, x, 0] = 1f;
                        }
                        else
                        {
                            float inv = 1f / sum;
                            for (int layer = 0; layer < 4; layer++)
                            {
                                maps[y, x, layer] *= inv;
                            }
                        }

                        // Wave-wash streaks: pull grass/forest toward sand on low wet shore.
                        int srcX = Mathf.RoundToInt(tx * (srcRes - 1));
                        float elev = heights01[srcY, srcX];
                        ApplyShoreWash(maps, y, x, elev, tx, ty);
                    }
                }

                ApplyRidgeAlphamap(maps, heights01, scale);
                data.SetAlphamaps(0, 0, maps);
                EditorUtility.SetDirty(data);
            }
            finally
            {
                Object.DestroyImmediate(tex);
            }
        }

        /// <summary>
        /// Beach wash marks: anisotropic noise strips sand inland along the wet band.
        /// Layers: 0 sand, 1 grass, 2 forest, 3 built — built and dry high ground stay.
        /// </summary>
        static void ApplyShoreWash(float[,,] maps, int y, int x, float elev, float tx, float ty)
        {
            // Wet sand band (~0–95 m on Moorea scale).
            float beach = 1f - SmoothStep(0.002f, 0.085f, elev);
            if (beach < 0.02f)
            {
                return;
            }

            // Elongated shore-parallel streaks (anisotropic UVs).
            float streak = TileNoise(tx * 14f + ty * 52f, ty * 14f - tx * 52f, 52);
            float foamLip = TileNoise(tx * 38f - ty * 9f, ty * 41f + tx * 7f, 41);
            float wash = beach * beach * SmoothStep(0.28f, 0.78f, streak);
            wash *= Mathf.Lerp(0.55f, 1f, foamLip);
            // Stronger right at the waterline, fades inland.
            wash *= 1f - SmoothStep(0.02f, 0.085f, elev);

            float takeGrass = Mathf.Min(maps[y, x, 1], wash * 0.72f);
            float takeForest = Mathf.Min(maps[y, x, 2], wash * 0.55f);
            maps[y, x, 1] -= takeGrass;
            maps[y, x, 2] -= takeForest;
            maps[y, x, 0] += takeGrass + takeForest;

            int layers = maps.GetLength(2);
            float sum = 0f;
            for (int layer = 0; layer < layers && layer < 4; layer++)
            {
                sum += maps[y, x, layer];
            }
            if (sum > 1e-5f)
            {
                float inv = 1f / sum;
                maps[y, x, 0] *= inv;
                maps[y, x, 1] *= inv;
                maps[y, x, 2] *= inv;
                maps[y, x, 3] *= inv;
            }
        }

        /// <summary>
        /// Crest rock from the heightfield, written into alphamap layer 4.
        /// A ridge is ground that sits above its neighborhood (positive TPI) and is steep.
        /// Gullies and pixels whose window touches the flat seabed stay vegetated.
        /// Weight comes only from grass and forest. Sand and built-up stay.
        /// Radii are alphamap texels (~16 m): 3 ≈ 50 m arêtes, 10 ≈ 160 m spines.
        /// </summary>
        static void ApplyRidgeAlphamap(float[,,] maps, float[,] heights01, TerrainScale scale)
        {
            int h = maps.GetLength(0);
            int w = maps.GetLength(1);
            int layers = maps.GetLength(2);
            const int rockLayer = 4;
            if (layers <= rockLayer || h < 8 || w != h)
            {
                Debug.LogWarning(
                    $"[CustomSRP] Ridge alphamap skipped (layers={layers}, map={w}x{h}).");
                return;
            }

            int n = w;
            float[] elev = ResampleHeightsNearest(heights01, n);
            BoxBlur(elev, n, 1);
            float[] sat = BuildSat(elev, n);
            float[] floorH = BoxMin(elev, n, 10);

            float heightM = scale.HeightY;
            float meters = scale.SizeX / Mathf.Max(1, n - 1);
            var crest = new float[n * n];
            for (int y = 0; y < n; y++)
            {
                float ty = y / (float)(n - 1);
                for (int x = 0; x < n; x++)
                {
                    int i = y * n + x;
                    float e = elev[i];
                    float tpiS = e - BoxMean(sat, n, x, y, 3);
                    float tpiL = e - BoxMean(sat, n, x, y, 10);
                    float slope = Grade01(elev, n, x, y, heightM, meters, 2);
                    float sea = SmoothStep(0.004f, 0.02f, floorH[i]);
                    float sharp = SmoothStep(0.0035f, 0.012f, tpiS);
                    float broad = SmoothStep(0.010f, 0.030f, tpiL);
                    float slopeGate = SmoothStep(0.28f, 0.62f, slope);
                    float cliff = SmoothStep(0.75f, 1.1f, slope);
                    float gully = SmoothStep(0.005f, 0.018f, -Mathf.Min(tpiS, tpiL));
                    float c = Mathf.Max(sharp * 0.85f, broad) * slopeGate;
                    c = Mathf.Max(c, cliff * Mathf.Max(sharp, 0.4f) * sea);
                    c *= (1f - gully * 0.9f) * sea;
                    c *= SmoothStep(0.02f, 0.07f, e);
                    float tx = x / (float)(n - 1);
                    float nse = TileNoise(tx * 19f, ty * 17f, 19);
                    crest[i] = Mathf.Clamp01(c * Mathf.Lerp(0.82f, 1f, nse));
                }
            }

            BoxBlur(crest, n, 1);

            int strong = 0;
            int vegPx = 0;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    float c = crest[y * n + x];
                    float grass = maps[y, x, 1];
                    float forest = maps[y, x, 2];
                    float veg = grass + forest;
                    if (veg > 0.02f)
                    {
                        vegPx++;
                    }

                    if (c < 0.02f || veg < 0.02f)
                    {
                        continue;
                    }

                    float take = c * 0.90f * veg;
                    float invVeg = 1f / veg;
                    maps[y, x, 1] = grass - take * grass * invVeg;
                    maps[y, x, 2] = forest - take * forest * invVeg;
                    maps[y, x, rockLayer] += take;
                    if (maps[y, x, rockLayer] > 0.35f)
                    {
                        strong++;
                    }

                    float sum = 0f;
                    for (int layer = 0; layer < layers; layer++)
                    {
                        sum += maps[y, x, layer];
                    }

                    if (sum > 1e-5f)
                    {
                        float inv = 1f / sum;
                        for (int layer = 0; layer < layers; layer++)
                        {
                            maps[y, x, layer] *= inv;
                        }
                    }
                }
            }

            Debug.Log(
                $"[CustomSRP] Ridge alphamap: strong rock on {strong} / {Mathf.Max(1, vegPx)} " +
                "vegetated texels (crests only, gullies stay grass/forest).");
        }

        static float[] ResampleHeightsNearest(float[,] src, int n)
        {
            int srcRes = src.GetLength(0);
            var dst = new float[n * n];
            float denom = Mathf.Max(1, n - 1);
            float srcDenom = srcRes - 1;
            for (int y = 0; y < n; y++)
            {
                int sy = Mathf.RoundToInt(y / denom * srcDenom);
                int row = y * n;
                for (int x = 0; x < n; x++)
                {
                    int sx = Mathf.RoundToInt(x / denom * srcDenom);
                    dst[row + x] = src[sy, sx];
                }
            }

            return dst;
        }

        static float[] BuildSat(float[] src, int n)
        {
            int w = n + 1;
            var sat = new float[w * w];
            for (int y = 0; y < n; y++)
            {
                int srcRow = y * n;
                int satRow = (y + 1) * w;
                int satPrev = y * w;
                for (int x = 0; x < n; x++)
                {
                    sat[satRow + x + 1] = src[srcRow + x]
                        + sat[satRow + x]
                        + sat[satPrev + x + 1]
                        - sat[satPrev + x];
                }
            }

            return sat;
        }

        static float BoxMean(float[] sat, int n, int x, int y, int radius)
        {
            int w = n + 1;
            int x0 = Mathf.Max(0, x - radius);
            int y0 = Mathf.Max(0, y - radius);
            int x1 = Mathf.Min(n - 1, x + radius) + 1;
            int y1 = Mathf.Min(n - 1, y + radius) + 1;
            float sum = sat[y1 * w + x1] - sat[y0 * w + x1] - sat[y1 * w + x0] + sat[y0 * w + x0];
            int count = (x1 - x0) * (y1 - y0);
            return sum / Mathf.Max(1, count);
        }

        static void BoxBlur(float[] src, int n, int radius)
        {
            var tmp = new float[n * n];
            for (int y = 0; y < n; y++)
            {
                int row = y * n;
                for (int x = 0; x < n; x++)
                {
                    int x0 = Mathf.Max(0, x - radius);
                    int x1 = Mathf.Min(n - 1, x + radius);
                    float sum = 0f;
                    for (int i = x0; i <= x1; i++)
                    {
                        sum += src[row + i];
                    }

                    tmp[row + x] = sum / (x1 - x0 + 1);
                }
            }

            for (int x = 0; x < n; x++)
            {
                for (int y = 0; y < n; y++)
                {
                    int y0 = Mathf.Max(0, y - radius);
                    int y1 = Mathf.Min(n - 1, y + radius);
                    float sum = 0f;
                    for (int i = y0; i <= y1; i++)
                    {
                        sum += tmp[i * n + x];
                    }

                    src[y * n + x] = sum / (y1 - y0 + 1);
                }
            }
        }

        static float[] BoxMin(float[] src, int n, int radius)
        {
            var tmp = new float[n * n];
            var dst = new float[n * n];
            for (int y = 0; y < n; y++)
            {
                int row = y * n;
                for (int x = 0; x < n; x++)
                {
                    int x0 = Mathf.Max(0, x - radius);
                    int x1 = Mathf.Min(n - 1, x + radius);
                    float m = src[row + x0];
                    for (int i = x0 + 1; i <= x1; i++)
                    {
                        float v = src[row + i];
                        if (v < m)
                        {
                            m = v;
                        }
                    }

                    tmp[row + x] = m;
                }
            }

            for (int x = 0; x < n; x++)
            {
                for (int y = 0; y < n; y++)
                {
                    int y0 = Mathf.Max(0, y - radius);
                    int y1 = Mathf.Min(n - 1, y + radius);
                    float m = tmp[y0 * n + x];
                    for (int i = y0 + 1; i <= y1; i++)
                    {
                        float v = tmp[i * n + x];
                        if (v < m)
                        {
                            m = v;
                        }
                    }

                    dst[y * n + x] = m;
                }
            }

            return dst;
        }

        static float Grade01(
            float[] heights, int n, int x, int y, float heightM, float meters, int step)
        {
            int x0 = Mathf.Max(0, x - step);
            int x1 = Mathf.Min(n - 1, x + step);
            int y0 = Mathf.Max(0, y - step);
            int y1 = Mathf.Min(n - 1, y + step);
            float dx = (heights[y * n + x1] - heights[y * n + x0]) * heightM /
                Mathf.Max(1e-4f, (x1 - x0) * meters);
            float dz = (heights[y1 * n + x] - heights[y0 * n + x]) * heightM /
                Mathf.Max(1e-4f, (y1 - y0) * meters);
            return Mathf.Clamp01(Mathf.Sqrt(dx * dx + dz * dz));
        }

        /// <summary>
        /// R sand, G grass, B forest, A built-up. Matches EnsureTerrainLayers order.
        /// </summary>
        static void AccumulateLanduse(
            float[,,] maps, Color32[] pix, int width, int y, int x, int px, int py, float weight)
        {
            if (weight <= 0f)
            {
                return;
            }

            byte code = pix[py * width + px].r;
            int layer = code switch
            {
                10 or 95 => 2,
                20 or 30 or 40 or 90 => 1,
                50 => 3,
                _ => 0
            };
            maps[y, x, layer] += weight;
        }

        /// <summary>
        /// Sand / grass / forest weights from height + slope when the landuse index is missing.
        /// Channel 2 is the forest slot (steep ground), not the old rock albedo.
        /// </summary>
        static void PaintAlphamapsFromSlope(TerrainData data, float[,] heights01, TerrainScale scale)
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

            ApplyRidgeAlphamap(maps, heights01, scale);
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
            // ~0 flat, ~1 at ~45°+ cliffs for Moorea scale.
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
            // Control UV over ~16.5 km — small radius ≈ multi-meter paint dabs.
            material.SetFloat("_KuwaharaRadius", 0.00055f);
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
            material.SetFloat("_ShadowLift", 1.05f);
            material.SetColor("_ShadowTint", new Color(0.36f, 0.46f, 0.62f, 1f));
            material.SetColor("_ShadowWarm", new Color(0.58f, 0.48f, 0.3f, 1f));
            material.SetFloat("_ShadowWobble", 0.55f);
            material.SetFloat("_ShadowBrushScale", 1.35f);
            Texture2D paint = TestSceneUtility.EnsureSkyOilCanvasTexture();
            if (paint != null)
            {
                material.SetTexture("_PaintMap", paint);
            }

            material.SetFloat("_PaintTile", 1600f);
            material.SetFloat("_PaintContrast", 1.75f);
            material.SetFloat("_PaintRelief", 1f);
            material.SetColor("_SpecularColor", new Color(0.5f, 0.45f, 0.38f, 1f));
            material.SetFloat("_SpecularThreshold", 0.88f);
            material.SetColor("_AmbientColor", new Color(0.510f, 0.786f, 1f, 1f));
            // fwidth edges ink every terrain patch border under oil shade steps.
            material.SetFloat("_InternalEdge", 0f);
            material.DisableKeyword("_INTERNAL_EDGE_ON");
            material.SetFloat("_EdgeStrength", 0.4f);
            material.SetColor("_EdgeColor", new Color(0.28f, 0.22f, 0.16f, 1f));
            if (brush != null)
            {
                material.SetTexture("_OutlineBrushMap", brush);
                material.SetTextureScale("_OutlineBrushMap", new Vector2(2.2f, 2.2f));
            }

            material.EnableKeyword("_RECEIVE_SHADOWS");
            material.SetFloat("_ReceiveShadows", 1f);
            ApplyRidgeRock(material);
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

            // Replaced by TerrainOilNPRMoorea.mat
            if (AssetDatabase.LoadAssetAtPath<Object>(LegacyTerrainLitMatPath) != null)
            {
                AssetDatabase.DeleteAsset(LegacyTerrainLitMatPath);
            }
        }

        [MenuItem("CustomSRP/Apply Moorea Oil Skybox")]
        public static void ApplyOilSkybox()
        {
            Texture2D canvas = TestSceneUtility.EnsureOilCanvasTexture();
            Material skyMat = TestSceneUtility.CreateOrUpdateOilSkybox(
                SkyMatPath, canvas, timeOfDay: 12f);
            AttachSky(skyMat, keepCurrentTime: true);
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            Debug.Log("[CustomSRP] Oil skybox assigned. Periods: 黎明 / 白天 / 黄昏 / 夜晚.");
        }

        static void BuildScene(
            TerrainData terrainData,
            Material terrainMat,
            Material oceanMat,
            Material skyMat,
            TerrainScale scale)
        {
            GameObject terrainGo = Terrain.CreateTerrainGameObject(terrainData);
            terrainGo.name = "MooreaTerrain";
            terrainGo.transform.position = Vector3.zero;

            var terrain = terrainGo.GetComponent<Terrain>();
            terrain.terrainData = terrainData;
            terrain.materialTemplate = terrainMat;
            // Must stay beyond scenic camera distance or Unity swaps Basemap (no oil umbra).
            terrain.basemapDistance = Mathf.Max(25000f, scale.SizeX * 2f);
            // Oil shading does not need a 5 px height mesh across 16 km.
            terrain.heightmapPixelError = 20f;
            terrain.heightmapMinimumLODSimplification = 1;
            // Requires TerrainInstancing.hlsl + multi_compile_instancing on TerrainOilNPR.
            terrain.drawInstanced = true;
            terrain.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            terrain.drawHeightmap = true;
            terrain.allowAutoConnect = false;
            terrain.drawTreesAndFoliage = false;
            terrain.enableHeightmapLODFrustumCulling = true;
            GameObjectUtility.SetStaticEditorFlags(terrainGo, 0);
            terrain.Flush();

            var sea = new GameObject("Sea");
            sea.transform.position = new Vector3(0f, 0.35f, 0f);
            sea.AddComponent<MeshFilter>();
            var seaRenderer = sea.AddComponent<MeshRenderer>();
            seaRenderer.sharedMaterial = oceanMat;
            seaRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            var surface = sea.AddComponent<OilOceanSurface>();
            surface.gridScale = scale.SizeX;
            // ~90 quads across the island. The fragment shader paints the short crests.
            surface.vertexDistance = 180f;
            surface.followCamera = true;
            surface.terrain = terrain;
            surface.waveHeight = 6.5f;
            surface.boundsPadding = 4f;
            surface.Rebuild();

            // Warm sun for yellow glint strokes (Van Gogh seascape).
            Light sun = TestSceneUtility.CreateDirectionalLight(
                "Directional Light",
                new Vector3(28f, -38f, 18f),
                new Color(1f, 0.95f, 0.78f),
                intensity: 1.25f,
                shadows: LightShadows.Hard,
                shadowStrength: 1f,
                shadowBias: 0.05f,
                shadowNormalBias: 0.8f);
            AttachSky(skyMat, sun, keepCurrentTime: false);

            // Lower seat so foreground water reads like thick impasto strokes.
            float cx = scale.SizeX * 0.5f;
            float cz = scale.SizeZ * 0.5f;
            var camPos = new Vector3(cx - 4200f, 620f, cz - 5200f);
            var lookTarget = new Vector3(cx - 400f, 40f, cz - 200f);
            var look = Quaternion.LookRotation(lookTarget - camPos, Vector3.up);
            TestSceneUtility.EnsureMainCamera(camPos, look.eulerAngles);
            ConfigureOilCamera(scale);
            FrameSceneViewOnTerrain(terrainGo, scale);

            sea.transform.SetAsFirstSibling();
        }

        static void AttachSky(Material skyMat, Light sun = null, bool keepCurrentTime = false)
        {
            if (skyMat == null)
            {
                return;
            }

            RenderSettings.skybox = skyMat;
            sun ??= FindDirectionalLight();
            if (sun == null)
            {
                return;
            }

            RenderSettings.sun = sun;
            var driver = sun.GetComponent<OilSkyboxTime>();
            if (driver == null)
            {
                driver = sun.gameObject.AddComponent<OilSkyboxTime>();
            }

            float time = keepCurrentTime ? driver.timeOfDay : 12f;
            driver.skyboxMaterial = skyMat;
            driver.sun = sun;
            driver.driveSun = true;
            driver.animate = false;
            driver.SetTimeOfDay(time);

            Material cloudMat = TestSceneUtility.CreateOrUpdateOilCloud(CloudMatPath);
            var layer = sun.GetComponent<OilCloudLayer>();
            if (layer == null)
            {
                layer = sun.gameObject.AddComponent<OilCloudLayer>();
            }

            layer.material = cloudMat;
            layer.Apply();
        }

        static Light FindDirectionalLight()
        {
            Light[] lights = Object.FindObjectsByType<Light>(FindObjectsSortMode.None);
            for (int i = 0; i < lights.Length; i++)
            {
                if (lights[i].type == LightType.Directional)
                {
                    return lights[i];
                }
            }

            return null;
        }

        static void FrameSceneViewOnTerrain(GameObject terrainGo, TerrainScale scale)
        {
            var view = SceneView.lastActiveSceneView;
            if (view == null)
            {
                return;
            }

            var bounds = new Bounds(
                new Vector3(scale.SizeX * 0.5f, scale.HeightY * 0.25f, scale.SizeZ * 0.5f),
                new Vector3(scale.SizeX, scale.HeightY, scale.SizeZ));
            view.Frame(bounds, false);
            Selection.activeGameObject = terrainGo;
        }

        static void ConfigureOilCamera(TerrainScale scale)
        {
            var cam = Object.FindAnyObjectByType<Camera>();
            if (cam == null)
            {
                return;
            }

            cam.farClipPlane = Mathf.Max(8000f, scale.SizeX * 3.5f);
            cam.nearClipPlane = 1f;
            cam.fieldOfView = 55f;
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
            // Depth copy feeds shore foam soft-intersection on OilOceanNPR.
            settings.FindPropertyRelative("copyColor").boolValue = false;
            settings.FindPropertyRelative("copyDepth").boolValue = true;
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


