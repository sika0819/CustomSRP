using CustomSRP;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace CustomSRP.Editor
{
    /// <summary>
    /// Boluo TerrainData on CustomSRP/TerrainOilNPR.
    /// The oil pass only samples four splats, so extra layers are folded into sand / grass / dirt / rock.
    /// Menu: CustomSRP → Create Island Terrain Scene
    /// </summary>
    public static class CreateIslandTerrainScene
    {
        const string ScenePath = TestSceneUtility.ScenesPath + "/IslandTerrain.unity";
        const string DataPath = "Assets/Terrain/Island/IslandTerrainData.asset";
        const string MatPath = TestSceneUtility.MaterialsPath + "/TerrainOilNPRIsland.mat";
        const string SkyMatPath = TestSceneUtility.MaterialsPath + "/OilSkybox.mat";

        [MenuItem("CustomSRP/Create Island Terrain Scene")]
        public static void Create()
        {
            var data = AssetDatabase.LoadAssetAtPath<TerrainData>(DataPath);
            if (data == null)
            {
                Debug.LogError($"[CustomSRP] Missing TerrainData at {DataPath}");
                return;
            }

            int folded = FoldToFourOilLayers(data);
            Material material = CreateMaterial(data);
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            GameObject terrainGo = Terrain.CreateTerrainGameObject(data);
            terrainGo.name = "IslandTerrain";
            terrainGo.transform.position = Vector3.zero;
            var terrain = terrainGo.GetComponent<Terrain>();
            terrain.terrainData = data;
            terrain.materialTemplate = material;
            // Basemap pass is not the oil shader. Keep it farther than the camera.
            terrain.basemapDistance = Mathf.Max(4000f, data.size.x * 2.5f);
            terrain.heightmapPixelError = 5f;
            terrain.heightmapMinimumLODSimplification = 1;
            terrain.drawInstanced = true;
            terrain.shadowCastingMode = ShadowCastingMode.On;
            terrain.drawHeightmap = true;
            terrain.allowAutoConnect = false;
            terrain.drawTreesAndFoliage = false;
            terrain.enableHeightmapLODFrustumCulling = true;
            GameObjectUtility.SetStaticEditorFlags(terrainGo, 0);
            terrain.Flush();

            AttachSkyAndCamera(data);
            EditorSceneManager.SaveScene(scene, ScenePath);
            TestSceneUtility.AddToBuildSettings(ScenePath);
            AssetDatabase.SaveAssets();
            Debug.Log(
                $"[CustomSRP] Island Terrain {data.size.x:0}×{data.size.z:0} m, " +
                $"height {data.size.y:0} m, layers folded {folded} → {ScenePath}");
        }

        /// <summary>
        /// Returns how many source layers were merged. Zero when the data is already four or fewer.
        /// </summary>
        static int FoldToFourOilLayers(TerrainData data)
        {
            TerrainLayer[] layers = data.terrainLayers;
            int sourceCount = layers != null ? layers.Length : 0;
            if (sourceCount <= 4 || data.alphamapLayers <= 4)
            {
                return 0;
            }

            int width = data.alphamapWidth;
            int height = data.alphamapHeight;
            int maps = data.alphamapLayers;
            float[,,] src = data.GetAlphamaps(0, 0, width, height);
            var bucketOf = new int[maps];
            TerrainLayer sand = null;
            TerrainLayer grass = null;
            TerrainLayer dirt = null;
            TerrainLayer rock = null;
            for (int i = 0; i < maps; i++)
            {
                TerrainLayer layer = i < sourceCount ? layers[i] : null;
                int bucket = BucketFor(layer);
                bucketOf[i] = bucket;
                if (layer == null)
                {
                    continue;
                }

                if (bucket == 0)
                {
                    sand = Prefer(sand, layer, "Ocean", "Sand");
                }
                else if (bucket == 1)
                {
                    grass = Prefer(grass, layer, "Grass");
                }
                else if (bucket == 2)
                {
                    dirt = Prefer(dirt, layer, "Path", "Dirt");
                }
                else
                {
                    rock = Prefer(rock, layer, "Rock");
                }
            }

            var dst = new float[height, width, 4];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    for (int i = 0; i < maps; i++)
                    {
                        dst[y, x, bucketOf[i]] += src[y, x, i];
                    }

                    float sum = dst[y, x, 0] + dst[y, x, 1] + dst[y, x, 2] + dst[y, x, 3];
                    if (sum < 1e-5f)
                    {
                        dst[y, x, 1] = 1f;
                        continue;
                    }

                    float inv = 1f / sum;
                    dst[y, x, 0] *= inv;
                    dst[y, x, 1] *= inv;
                    dst[y, x, 2] *= inv;
                    dst[y, x, 3] *= inv;
                }
            }

            TerrainLayer fallback = sand ?? grass ?? dirt ?? rock;
            data.terrainLayers = new[]
            {
                sand ?? fallback,
                grass ?? fallback,
                dirt ?? fallback,
                rock ?? fallback
            };
            data.SetAlphamaps(0, 0, dst);
            EditorUtility.SetDirty(data);
            return sourceCount;
        }

        static int BucketFor(TerrainLayer layer)
        {
            string name = layer != null ? layer.name : string.Empty;
            if (Contains(name, "Rock"))
            {
                return 3;
            }

            if (Contains(name, "Ocean") || Contains(name, "Sand"))
            {
                return 0;
            }

            if (Contains(name, "Path") || Contains(name, "Dirt"))
            {
                return 2;
            }

            return 1;
        }

        static TerrainLayer Prefer(TerrainLayer current, TerrainLayer candidate, params string[] hints)
        {
            if (current == null)
            {
                return candidate;
            }

            bool candidateHits = Hits(candidate.name, hints);
            bool currentHits = Hits(current.name, hints);
            if (candidateHits && !currentHits)
            {
                return candidate;
            }

            return current;
        }

        static bool Hits(string name, string[] hints)
        {
            for (int i = 0; i < hints.Length; i++)
            {
                if (Contains(name, hints[i]))
                {
                    return true;
                }
            }

            return false;
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

            Texture2D canvas = TestSceneUtility.EnsureOilCanvasTexture();
            Texture2D brush = TestSceneUtility.EnsureOilBrushTexture();
            material.SetColor("_BaseColor", Color.white);
            material.SetFloat("_Kuwahara", 1f);
            material.EnableKeyword("_KUWAHARA_ON");
            // Same ~9 m dab as Moorea (0.00055 over 16.5 km), in this terrain's control UV.
            float radius = Mathf.Clamp(9f / Mathf.Max(data.size.x, 1f), 0.0005f, 0.02f);
            material.SetFloat("_KuwaharaRadius", radius);
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

            material.SetFloat("_PaintTile", Mathf.Clamp(data.size.x * 0.4f, 200f, 1600f));
            material.SetFloat("_PaintContrast", 1.75f);
            material.SetFloat("_PaintRelief", 1f);
            material.SetColor("_SpecularColor", new Color(0.5f, 0.45f, 0.38f, 1f));
            material.SetFloat("_SpecularThreshold", 0.88f);
            material.SetColor("_AmbientColor", new Color(0.510f, 0.786f, 1f, 1f));
            material.SetFloat("_InternalEdge", 0f);
            material.DisableKeyword("_INTERNAL_EDGE_ON");
            material.SetFloat("_ReceiveShadows", 1f);
            material.EnableKeyword("_RECEIVE_SHADOWS");
            // Rock is alphamap A. Residual ridge would paint a fifth layer this pass does not have.
            material.SetFloat("_RidgeAmount", 0f);
            material.SetFloat("_TerrainWorldSize", data.size.x);
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
            RenderSettings.sun = sun;
            var driver = sun.gameObject.AddComponent<OilSkyboxTime>();
            driver.skyboxMaterial = skyMat;
            driver.sun = sun;
            driver.driveSun = true;
            driver.animate = false;
            driver.SetTimeOfDay(12f);

            float x = data.size.x * 0.5f;
            float ground = data.GetInterpolatedHeight(0.5f, 0.08f);
            var camPos = new Vector3(x, ground + 80f, -60f);
            var lookTarget = new Vector3(x, ground + 10f, data.size.z * 0.35f);
            var look = Quaternion.LookRotation(lookTarget - camPos, Vector3.up);
            TestSceneUtility.EnsureMainCamera(camPos, look.eulerAngles);
            var cam = Object.FindAnyObjectByType<Camera>();
            cam.nearClipPlane = 0.3f;
            cam.farClipPlane = Mathf.Max(1800f, data.size.x * 2f);
            cam.fieldOfView = 55f;
            cam.allowHDR = false;
            cam.allowMSAA = false;
            cam.clearFlags = CameraClearFlags.Skybox;
            var crp = cam.gameObject.AddComponent<CustomRenderPipelineCamera>();
            var so = new SerializedObject(crp);
            SerializedProperty settings = so.FindProperty("settings");
            // Oil ocean refraction reads the opaque color, shore foam reads the depth.
            settings.FindPropertyRelative("copyColor").boolValue = true;
            settings.FindPropertyRelative("copyDepth").boolValue = true;
            settings.FindPropertyRelative("overridePostFX").boolValue = true;
            settings.FindPropertyRelative("postFXSettings").objectReferenceValue = null;
            settings.FindPropertyRelative("renderScaleMode").enumValueIndex = 0;
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
    }
}
