using System.IO;
using CustomSRP;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

using UnityGameFramework.Runtime;
namespace CustomSRP.Editor
{
    /// <summary>
    /// Boluo TerrainData on CustomSRP/TerrainOilNPR.
    /// Oil splat is three layers: sand, grass, rock. Extra source layers fold into those.
    /// Writes Resources prefabs for Launch runtime spawn (no IslandTerrain.unity play scene).
    /// Menu: CustomSRP → Create Island Terrain Prefabs
    /// </summary>
    public static class CreateIslandTerrainScene
    {
        const string DataPath = "Assets/Terrain/Island/IslandTerrainData.asset";
        const string MatPath = TestSceneUtility.MaterialsPath + "/TerrainOilNPRIsland.mat";
        const string SkyMatPath = TestSceneUtility.MaterialsPath + "/OilSkybox.mat";
        const string TerrainPrefabPath = "Assets/AssetRaw/Island/IslandTerrain.prefab";
        const string OceanPrefabPath = "Assets/AssetRaw/Ocean/IslandOcean.prefab";
        const string LaunchScene = "Assets/GameMain/Scenes/Launch.unity";

        [MenuItem("CustomSRP/Create Island Terrain Prefabs")]
        public static void Create()
        {
            var data = AssetDatabase.LoadAssetAtPath<TerrainData>(DataPath);
            if (data == null)
            {
                Log.Error($"[CustomSRP] Missing TerrainData at {DataPath}");
                return;
            }

            int folded = FoldToThreeOilLayers(data, data);
            Material material = CreateMaterial(data);
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            GameObject terrainGo = Terrain.CreateTerrainGameObject(data);
            terrainGo.name = "Terrain";
            terrainGo.transform.position = Vector3.zero;
            var terrain = terrainGo.GetComponent<Terrain>();
            terrain.terrainData = data;
            terrain.materialTemplate = material;
            // Basemap pass is not the oil shader. Keep it farther than the camera.
            terrain.basemapDistance = Mathf.Max(4000f, data.size.x * 2.5f);
            // Match Boluo Terrain.prefab: MinimumLODSimplification=0 + MaximumLOD=0.
            // MinimumLOD=1 forces ≥2× coarser heightmesh up close → river-bank stairs.
            terrain.heightmapPixelError = 5f;
            terrain.heightmapMinimumLODSimplification = 0;
            terrain.heightmapMaximumLOD = 0;
            terrain.drawInstanced = true;
            terrain.shadowCastingMode = ShadowCastingMode.On;
            terrain.drawHeightmap = true;
            terrain.allowAutoConnect = false;
            terrain.drawTreesAndFoliage = false;
            terrain.enableHeightmapLODFrustumCulling = true;
            GameObjectUtility.SetStaticEditorFlags(terrainGo, 0);
            terrain.Flush();

            AttachSkyAndCamera(data);
            AttachBoluoOceanAndCloud();
            BakeRuntimePrefabs(terrainGo);
            AssetDatabase.SaveAssets();

            try
            {
                OilTerrainGrassBakeTool.Bake();
            }
            catch (System.Exception e)
            {
                Log.Warning("[CustomSRP] OilTerrainGrass bake skipped: " + e.Message);
            }

            if (File.Exists(LaunchScene))
            {
                EditorSceneManager.OpenScene(LaunchScene, OpenSceneMode.Single);
            }

            Log.Info(
                $"[CustomSRP] Island Terrain {data.size.x:0}×{data.size.z:0} m, " +
                $"height {data.size.y:0} m, layers folded {folded} → {TerrainPrefabPath} + {OceanPrefabPath}");
        }

        static void BakeRuntimePrefabs(GameObject terrainGo)
        {
            Directory.CreateDirectory("Assets/AssetRaw/Island");
            Directory.CreateDirectory("Assets/AssetRaw/Ocean");

            Light sun = Object.FindAnyObjectByType<Light>();
            GameObject lightGo = sun != null && sun.type == LightType.Directional ? sun.gameObject : null;
            GameObject seaGo = GameObject.Find("Sea");
            if (seaGo == null)
                seaGo = GameObject.Find("IslandOcean");
            if (seaGo == null)
                seaGo = GameObject.Find("IslandBeach");

            var root = new GameObject("IslandTerrain");
            terrainGo.transform.SetParent(root.transform, true);
            if (lightGo != null)
                lightGo.transform.SetParent(root.transform, true);

            PrefabUtility.SaveAsPrefabAsset(root, TerrainPrefabPath);

            if (seaGo != null)
            {
                string previous = seaGo.name;
                seaGo.name = "IslandOcean";
                PrefabUtility.SaveAsPrefabAsset(seaGo, OceanPrefabPath);
                seaGo.name = previous;
            }

            Object.DestroyImmediate(root);
        }

        const int FeatherRadius = 8;

        /// <summary>
        /// Folds <paramref name="source"/> into sand / grass / rock on <paramref name="destination"/>.
        /// Path and the ocean-floor sand paint both become sand, so the beach stays where it was painted.
        /// Returns how many source layers were merged. Zero when the destination is already three layers.
        /// </summary>
        public static int FoldToThreeOilLayers(TerrainData source, TerrainData destination)
        {
            if (destination != null && destination.alphamapLayers <= 3 &&
                destination.terrainLayers != null && destination.terrainLayers.Length <= 3 &&
                source == destination)
            {
                return 0;
            }

            if (source == null)
            {
                source = destination;
            }

            TerrainLayer[] layers = source.terrainLayers;
            int sourceCount = layers != null ? layers.Length : 0;
            int width = source.alphamapWidth;
            int height = source.alphamapHeight;
            int maps = source.alphamapLayers;
            float[,,] src = source.GetAlphamaps(0, 0, width, height);
            var bucketOf = new int[maps];
            for (int i = 0; i < maps; i++)
            {
                TerrainLayer layer = i < sourceCount ? layers[i] : null;
                bucketOf[i] = BucketFor(layer);
            }

            var dst = new float[height, width, 3];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    for (int i = 0; i < maps; i++)
                    {
                        dst[y, x, bucketOf[i]] += src[y, x, i];
                    }

                    Normalize3(dst, y, x);
                }
            }

            Feather(dst, FeatherRadius);

            TerrainLayer sand = AssetDatabase.LoadAssetAtPath<TerrainLayer>(
                "Assets/Terrain/Island/Layers/SWS_OceanFloorSmooth.terrainlayer");
            TerrainLayer grass = AssetDatabase.LoadAssetAtPath<TerrainLayer>(
                "Assets/Terrain/Island/Layers/Grass.terrainlayer");
            TerrainLayer rock = AssetDatabase.LoadAssetAtPath<TerrainLayer>(
                "Assets/Terrain/Island/Layers/SWS_DemoRock.terrainlayer");
            TerrainLayer path = AssetDatabase.LoadAssetAtPath<TerrainLayer>(
                "Assets/Terrain/Island/Layers/Path.terrainlayer");
            if (sand != null && path != null)
            {
                // The beach the scene already shows is the path paint texture.
                sand.diffuseTexture = path.diffuseTexture;
                sand.normalMapTexture = path.normalMapTexture;
                sand.maskMapTexture = path.maskMapTexture;
                sand.tileSize = path.tileSize;
                sand.smoothness = path.smoothness;
                EditorUtility.SetDirty(sand);
            }

            destination.terrainLayers = new[] { sand, grass, rock };
            destination.SetAlphamaps(0, 0, dst);
            EditorUtility.SetDirty(destination);
            return sourceCount;
        }

        static void Normalize3(float[,,] map, int y, int x)
        {
            float sum = map[y, x, 0] + map[y, x, 1] + map[y, x, 2];
            if (sum < 1e-5f)
            {
                map[y, x, 0] = 1f;
                map[y, x, 1] = 0f;
                map[y, x, 2] = 0f;
                return;
            }

            float inv = 1f / sum;
            map[y, x, 0] *= inv;
            map[y, x, 1] *= inv;
            map[y, x, 2] *= inv;
        }

        static void Feather(float[,,] map, int radius)
        {
            int height = map.GetLength(0);
            int width = map.GetLength(1);
            var kernel = new float[radius * 2 + 1];
            float sigma = radius * 0.45f;
            float weight = 0f;
            for (int i = -radius; i <= radius; i++)
            {
                float g = Mathf.Exp(-(i * i) / (2f * sigma * sigma));
                kernel[i + radius] = g;
                weight += g;
            }

            float inv = 1f / weight;
            for (int i = 0; i < kernel.Length; i++)
            {
                kernel[i] *= inv;
            }

            var tmp = new float[height, width, 3];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    for (int c = 0; c < 3; c++)
                    {
                        float acc = 0f;
                        for (int k = -radius; k <= radius; k++)
                        {
                            int sx = x + k;
                            if (sx < 0)
                            {
                                sx = 0;
                            }
                            else if (sx >= width)
                            {
                                sx = width - 1;
                            }

                            acc += map[y, sx, c] * kernel[k + radius];
                        }

                        tmp[y, x, c] = acc;
                    }
                }
            }

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    for (int c = 0; c < 3; c++)
                    {
                        float acc = 0f;
                        for (int k = -radius; k <= radius; k++)
                        {
                            int sy = y + k;
                            if (sy < 0)
                            {
                                sy = 0;
                            }
                            else if (sy >= height)
                            {
                                sy = height - 1;
                            }

                            acc += tmp[sy, x, c] * kernel[k + radius];
                        }

                        map[y, x, c] = acc;
                    }

                    Normalize3(map, y, x);
                }
            }
        }

        static int BucketFor(TerrainLayer layer)
        {
            string name = layer != null ? layer.name : string.Empty;
            if (Contains(name, "Rock"))
            {
                return 2;
            }

            if (Contains(name, "Grass"))
            {
                return 1;
            }

            return 0;
        }

        static bool Contains(string name, string hint)
        {
            return name.IndexOf(hint, System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static Material CreateMaterial(TerrainData data)
        {
            Shader shader = Shader.Find("CustomSRP/TerrainOilNPR");
            if (shader == null)
            {
                throw new System.InvalidOperationException(
                    "[CustomSRP] Shader CustomSRP/TerrainOilNPR is missing or failed to compile");
            }

            var material = AssetDatabase.LoadAssetAtPath<Material>(MatPath);
            if (material == null)
            {
                material = new Material(shader);
                AssetDatabase.CreateAsset(material, MatPath);
            }
            else
            {
                material.shader = shader;
            }

            Texture2D brush = TestSceneUtility.EnsureOilBrushTexture();
            material.SetColor("_BaseColor", Color.white);
            material.DisableKeyword("_CANVAS_ON");
            material.DisableKeyword("_INTERNAL_EDGE_ON");
            material.SetFloat("_ShadeSteps", 4f);
            material.SetFloat("_ShadeLift", 0.42f);
            material.SetFloat("_ShadowLift", 1.05f);
            material.SetFloat("_ShadowWobble", 0.55f);
            material.SetFloat("_ShadowBrushScale", 1.35f);
            material.SetColor("_SpecularColor", new Color(0.5f, 0.45f, 0.38f, 1f));
            material.SetColor("_AmbientColor", new Color(0.510f, 0.786f, 1f, 1f));
            material.SetFloat("_ReceiveShadows", 1f);
            material.EnableKeyword("_RECEIVE_SHADOWS");
            // Rock is alphamap B. Residual ridge would paint a layer this pass does not have.
            material.SetFloat("_RidgeAmount", 0f);
            // Painted weights, not mask-height cuts. The alphamap already carries the feather.
            material.SetFloat("_HeightTransition", 0f);
            material.SetFloat("_TerrainHeight", data.size.y);
            if (brush != null)
            {
                material.SetTexture("_OutlineBrushMap", brush);
            }

            material.enableInstancing = false;
            material.renderQueue = 1900;
            EditorUtility.SetDirty(material);
            return material;
        }

        // Boluo DaoYu Water Grid + default beach spawn (GameControl.InitPlayerPos).
        const float BoluoSeaY = 5.7f;
        static readonly Vector3 BoluoSeaPos = new Vector3(350f, BoluoSeaY, 470f);
        static readonly Vector3 BoluoSpawnPos = new Vector3(610.0767f, 10.39065f, 706.1781f);
        const float BoluoSpawnYaw = 130.443f + 95f;

        static void AttachSkyAndCamera(TerrainData data)
        {
            Texture2D canvas = TestSceneUtility.EnsureOilCanvasTexture();
            Material skyMat = TestSceneUtility.CreateOrUpdateOilSkybox(SkyMatPath, canvas, 12f);
            Light sun = TestSceneUtility.CreateDirectionalLight(
                "Directional Light",
                new Vector3(42f, -36f, 12f),
                new Color(1f, 0.95f, 0.78f),
                intensity: 1.15f,
                shadows: LightShadows.Hard,
                shadowStrength: 1f,
                shadowBias: 0.04f,
                shadowNormalBias: 0.4f);
            RenderSettings.skybox = skyMat;
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            RenderSettings.fogDensity = 0.003f;
            RenderSettings.fogColor = new Color(0.67f, 0.76f, 0.82f, 1f);
            RenderSettings.sun = sun;
            var driver = sun.gameObject.AddComponent<OilSkyboxTime>();
            driver.skyboxMaterial = skyMat;
            driver.sun = sun;
            driver.driveSun = true;
            driver.animate = false;
            driver.SetTimeOfDay(12f);

            var camPos = BoluoSpawnPos + Vector3.up * 3f;
            TestSceneUtility.EnsureMainCamera(camPos, new Vector3(0f, BoluoSpawnYaw, 0f));
            var cam = Object.FindAnyObjectByType<Camera>();
            cam.nearClipPlane = 0.3f;
            cam.farClipPlane = Mathf.Max(1800f, data.size.x * 2f);
            cam.fieldOfView = 55f;
            cam.allowHDR = true;
            cam.allowMSAA = false;
            cam.clearFlags = CameraClearFlags.Skybox;
            var crp = cam.gameObject.AddComponent<CustomRenderPipelineCamera>();
            var so = new SerializedObject(crp);
            SerializedProperty settings = so.FindProperty("settings");
            // Oil ocean refraction reads the opaque color, shore foam reads the depth.
            settings.FindPropertyRelative("copyColor").boolValue = true;
            settings.FindPropertyRelative("copyDepth").boolValue = true;
            // Inherit pipeline PostFXSettings (oil filter + light bloom).
            settings.FindPropertyRelative("overridePostFX").boolValue = false;
            settings.FindPropertyRelative("postFXSettings").objectReferenceValue =
                AssetDatabase.LoadAssetAtPath<PostFXSettings>(
                    "Assets/CustomSRP/Settings/PostFXSettings.asset");
            settings.FindPropertyRelative("renderScaleMode").enumValueIndex = 2;
            // Runtime IslandCameraSrpSetup overrides by device (GMJ 1/0.75, else 0.5/0.375).
            settings.FindPropertyRelative("renderScale").floatValue = 1f;
            settings.FindPropertyRelative("allowFXAA").boolValue = false;
            settings.FindPropertyRelative("keepAlpha").boolValue = false;
            settings.FindPropertyRelative("maskLights").boolValue = false;
            so.ApplyModifiedPropertiesWithoutUndo();

            var view = SceneView.lastActiveSceneView;
            if (view != null)
            {
                view.Frame(
                    new Bounds(
                        new Vector3(data.size.x * 0.5f, data.size.y * 0.25f, data.size.z * 0.5f),
                        new Vector3(data.size.x, data.size.y, data.size.z)),
                    false);
            }
        }

        static void AttachBoluoOceanAndCloud()
        {
            Material oceanMat = TestSceneUtility.CreateOrUpdateOilOcean(
                TestSceneUtility.MaterialsPath + "/LitMooreaOcean.mat");
            var sea = new GameObject("Sea");
            sea.transform.position = BoluoSeaPos;
            sea.AddComponent<MeshFilter>();
            var seaRenderer = sea.AddComponent<MeshRenderer>();
            seaRenderer.sharedMaterial = oceanMat;
            seaRenderer.shadowCastingMode = ShadowCastingMode.Off;
            var surface = sea.AddComponent<OilOceanSurface>();
            // Match Boluo DaoYu WaterGrid: scale 2500, pos (350,5.7,470), no camera follow.
            // Oil uses coarse verts (swell in shader); Boluo mesh uses vertexDistance 10.
            surface.gridScale = 2500f;
            surface.vertexDistance = 500f;
            surface.followCamera = false;
            surface.waveHeight = 0.2f;
            surface.boundsPadding = 4f;
            surface.Rebuild();

            Light sun = RenderSettings.sun;
            if (sun == null)
                return;

            Material cloudMat = TestSceneUtility.CreateOrUpdateOilCloud(
                TestSceneUtility.MaterialsPath + "/OilCloud.mat");
            var layer = sun.GetComponent<OilCloudLayer>();
            if (layer == null)
                layer = sun.gameObject.AddComponent<OilCloudLayer>();
            layer.material = cloudMat;
            layer.coverage = 0.85f;
            layer.wind = 0.2f;
            layer.ringScale = 1.85f;
            layer.followSeaLevel = true;
            layer.seaLevel = 0f;
            layer.ringHeight = 0f;
            layer.highRing = true;
            layer.Apply();
        }
    }
}
