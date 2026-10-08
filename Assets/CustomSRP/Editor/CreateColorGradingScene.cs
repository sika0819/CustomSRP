using System.Collections.Generic;
using System.IO;
using CustomSRP.Examples;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace CustomSRP.Editor
{
    /// <summary>
    /// Plane + 多档 Emission 球/立方体（最强约 8）+ 弱 Directional + MeshBall；Ambient=黑。
    /// 布局数据在 ColorGradingSceneData/*.json（自 HdrSceneData 复制/重建）。
    /// 本场验收 Color Adjustments / White Balance / Split Toning / Channel Mixer /
    /// Shadows Midtones Highlights / Color LUT + Tone Mapping。
    /// </summary>
    public static class CreateColorGradingScene
    {
        const string ScenePath = TestSceneUtility.ScenesPath + "/ColorGrading.unity";
        const string DataFolderRelative = "CustomSRP/Editor/ColorGradingSceneData";
        const string OpaqueMatPath = TestSceneUtility.MaterialsPath + "/LitColorGradingOpaque.mat";

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
                    TestSceneUtility.Root + "/Editor/ColorGradingSceneData/.force-rebuild";
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

        [MenuItem("CustomSRP/Create Color Grading Scene")]
        public static void Create()
        {
            TestSceneUtility.EnsureStandardFolders();
            SceneData data = LoadSceneData();
            SceneMaterials mats = EnsureMaterials();

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            ApplyAmbient(data.Meta.ambient);
            BuildSceneContents(mats, data);
            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            TestSceneUtility.AddToBuildSettings(ScenePath, makeFirst: false);
            Debug.Log(
                $"[CustomSRP] Created Color Grading scene " +
                $"(layout from Tone Mapping Scene / ColorGradingSceneData): {ScenePath}");
            EditorSceneManager.OpenScene(ScenePath);
        }

        class SceneData
        {
            public MetaFile Meta;
            public List<ObjectEntry> Objects;
            public List<LightEntry> Lights;
        }

        [System.Serializable]
        class MetaFile
        {
            public TransformData camera;
            public AmbientData ambient;
        }

        [System.Serializable]
        class AmbientData
        {
            public int mode;
            public float[] skyColor;
            public float intensity;
        }

        [System.Serializable]
        class TransformData
        {
            public float[] pos;
            public float[] euler;
            public float[] rot;
        }

        [System.Serializable]
        class ObjectEntry
        {
            public string name;
            public float[] pos;
            public float[] euler;
            public float[] rot;
            public float[] scale;
            public string mesh;
            public string mat;
            public int castShadows = 1;
            public string parent;
            public float[] baseColor;
            public float alphaCutoff = 0.5f;
            public float metallic;
            public float smoothness = 0.5f;
            public float[] emissionColor;
        }

        [System.Serializable]
        class LightEntry
        {
            public string name;
            public int type;
            public float[] color;
            public float intensity;
            public float range;
            public float spot;
            public float innerSpot = 21.80208f;
            public int mapping;
            public int shadows;
            public float shadowBias;
            public float shadowNormalBias = 1f;
            public float shadowNearPlane = 0.2f;
            public float shadowStrength = 1f;
            public int shadowResolution = -1;
            public float[] pos;
            public float[] euler;
            public float[] rot;
        }

        [System.Serializable]
        class ObjectListWrapper
        {
            public List<ObjectEntry> items;
        }

        [System.Serializable]
        class LightListWrapper
        {
            public List<LightEntry> items;
        }

        struct SceneMaterials
        {
            public Material Opaque;
            public Material Clip;
            public Material Transparent;
            public Material Instanced;
        }

        static SceneData LoadSceneData()
        {
            string folder = Path.Combine(Application.dataPath, DataFolderRelative);
            string objectsJson = File.ReadAllText(Path.Combine(folder, "objects.json"));
            string lightsJson = File.ReadAllText(Path.Combine(folder, "lights.json"));
            string metaJson = File.ReadAllText(Path.Combine(folder, "meta.json"));

            var objects = JsonUtility.FromJson<ObjectListWrapper>(
                "{\"items\":" + objectsJson + "}").items;
            var lights = JsonUtility.FromJson<LightListWrapper>(
                "{\"items\":" + lightsJson + "}").items;

            return new SceneData
            {
                Meta = JsonUtility.FromJson<MetaFile>(metaJson),
                Objects = objects,
                Lights = lights
            };
        }

        static SceneMaterials EnsureMaterials()
        {
            Texture2D uvAlpha = TestSceneUtility.EnsureUvAlphaTexture();
            Texture2D albedo = TestSceneUtility.EnsureLitAlbedoTexture();

            Material opaque = TestSceneUtility.CreateOrUpdateLit(
                OpaqueMatPath,
                new Color(0.5f, 0.5f, 0.5f, 1f),
                TestSceneUtility.SurfaceType.Opaque,
                metallic: 0f,
                smoothness: 0.5f,
                enableGpuInstancing: true);

            Material clip = AssetDatabase.LoadAssetAtPath<Material>(
                TestSceneUtility.MaterialsPath + "/LitClip.mat");
            if (clip == null)
            {
                clip = TestSceneUtility.CreateOrUpdateLit(
                    TestSceneUtility.MaterialsPath + "/LitClip.mat",
                    Color.white,
                    TestSceneUtility.SurfaceType.Clip,
                    uvAlpha,
                    metallic: 0.1f,
                    smoothness: 0.6f,
                    enableGpuInstancing: true,
                    cutoff: 0.5f);
            }

            Material transparent = AssetDatabase.LoadAssetAtPath<Material>(
                TestSceneUtility.MaterialsPath + "/LitTransparent.mat");
            if (transparent == null)
            {
                transparent = TestSceneUtility.CreateOrUpdateLit(
                    TestSceneUtility.MaterialsPath + "/LitTransparent.mat",
                    new Color(0.75f, 0.9f, 1f, 0.35f),
                    TestSceneUtility.SurfaceType.TransparentPremultiply,
                    metallic: 0f,
                    smoothness: 0.9f);
            }

            string instancedPath = TestSceneUtility.MaterialsPath + "/LitInstanced.mat";
            Material instanced = AssetDatabase.LoadAssetAtPath<Material>(instancedPath);
            if (instanced == null)
            {
                instanced = TestSceneUtility.CreateOrUpdateLit(
                    instancedPath,
                    Color.white,
                    TestSceneUtility.SurfaceType.Opaque,
                    albedo,
                    metallic: 0f,
                    smoothness: 0.5f,
                    enableGpuInstancing: true);
            }
            else if (!instanced.enableInstancing)
            {
                instanced.enableInstancing = true;
                EditorUtility.SetDirty(instanced);
            }

            return new SceneMaterials
            {
                Opaque = opaque,
                Clip = clip,
                Transparent = transparent,
                Instanced = instanced
            };
        }

        static void ApplyAmbient(AmbientData ambient)
        {
            if (ambient == null)
            {
                RenderSettings.ambientMode = AmbientMode.Skybox;
                RenderSettings.ambientSkyColor = Color.black;
                RenderSettings.ambientIntensity = 0f;
                return;
            }

            RenderSettings.ambientMode = (AmbientMode)ambient.mode;
            RenderSettings.ambientSkyColor = ToColor(ambient.skyColor);
            RenderSettings.ambientIntensity = ambient.intensity;
        }

        static void BuildSceneContents(SceneMaterials mats, SceneData data)
        {
            EnsureCamera(data.Meta.camera);
            BuildLights(data.Lights);

            var parents = new Dictionary<string, Transform>();
            foreach (ObjectEntry entry in data.Objects)
            {
                if (string.IsNullOrEmpty(entry.parent) || parents.ContainsKey(entry.parent))
                {
                    continue;
                }

                var root = new GameObject(entry.parent);
                parents[entry.parent] = root.transform;
            }

            foreach (ObjectEntry entry in data.Objects)
            {
                Material mat = ResolveMaterial(mats, entry.mat);
                GameObject go = CreatePrimitive(entry.mesh, entry.name, mat);
                go.transform.SetPositionAndRotation(
                    ToVec3(entry.pos),
                    ToRotation(entry.euler, entry.rot));
                go.transform.localScale = ToVec3(entry.scale);

                if (!string.IsNullOrEmpty(entry.parent) &&
                    parents.TryGetValue(entry.parent, out Transform parent))
                {
                    go.transform.SetParent(parent, true);
                }

                var renderer = go.GetComponent<MeshRenderer>();
                renderer.shadowCastingMode = (ShadowCastingMode)entry.castShadows;

                ApplyProps(go, entry);
            }

            BuildMeshBall(mats.Instanced);
        }

        static Material ResolveMaterial(SceneMaterials mats, string kind)
        {
            return kind switch
            {
                "Clip" => mats.Clip,
                "Transparent" => mats.Transparent,
                _ => mats.Opaque
            };
        }

        static void ApplyProps(GameObject go, ObjectEntry entry)
        {
            if (entry.baseColor == null || entry.baseColor.Length < 3)
            {
                return;
            }

            var comp = go.AddComponent<PerObjectMaterialProperties>();
            var so = new SerializedObject(comp);
            so.FindProperty("baseColor").colorValue = ToColor(entry.baseColor);
            so.FindProperty("alphaCutoff").floatValue = entry.alphaCutoff;
            so.FindProperty("metallic").floatValue = entry.metallic;
            so.FindProperty("smoothness").floatValue = entry.smoothness;
            SerializedProperty emission = so.FindProperty("emissionColor");
            if (emission != null && entry.emissionColor != null && entry.emissionColor.Length >= 3)
            {
                emission.colorValue = ToColor(entry.emissionColor);
            }

            so.ApplyModifiedPropertiesWithoutUndo();
        }

        static void BuildMeshBall(Material instanced)
        {
            var meshBallGo = new GameObject("Mesh Ball");
            meshBallGo.transform.position = Vector3.zero;
            var meshBall = meshBallGo.AddComponent<MeshBall>();
            var temp = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Mesh sphereMesh = temp.GetComponent<MeshFilter>().sharedMesh;
            Object.DestroyImmediate(temp);

            var so = new SerializedObject(meshBall);
            so.FindProperty("mesh").objectReferenceValue = sphereMesh;
            so.FindProperty("material").objectReferenceValue = instanced;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        static void EnsureCamera(TransformData cam)
        {
            TestSceneUtility.EnsureMainCamera(ToVec3(cam.pos), Vector3.zero);
            var main = Object.FindFirstObjectByType<Camera>();
            if (main != null)
            {
                main.transform.SetPositionAndRotation(
                    ToVec3(cam.pos),
                    ToRotation(cam.euler, cam.rot));
                // Use Graphics Settings：RP allowHDR 打开时才真正走 HDR 中间缓冲
                main.allowHDR = true;
            }
        }

        static void BuildLights(List<LightEntry> lights)
        {
            foreach (LightEntry entry in lights)
            {
                var go = new GameObject(entry.name, typeof(Light));
                go.transform.SetPositionAndRotation(
                    ToVec3(entry.pos),
                    ToRotation(entry.euler, entry.rot));
                var light = go.GetComponent<Light>();
                light.type = (LightType)entry.type;
                light.color = ToColor(entry.color);
                light.intensity = entry.intensity;
                light.range = entry.range;
                light.spotAngle = entry.spot;
                light.innerSpotAngle = entry.innerSpot;
                light.lightmapBakeType = (LightmapBakeType)entry.mapping;
                light.shadows = (LightShadows)entry.shadows;
                light.shadowStrength = entry.shadowStrength;
                light.shadowBias = entry.shadowBias;
                light.shadowNormalBias = entry.shadowNormalBias;
                light.shadowNearPlane = entry.shadowNearPlane;
                light.shadowResolution = (LightShadowResolution)entry.shadowResolution;
            }
        }

        static GameObject CreatePrimitive(string mesh, string name, Material material)
        {
            PrimitiveType type = mesh switch
            {
                "Sphere" => PrimitiveType.Sphere,
                "Cube" => PrimitiveType.Cube,
                "Plane" => PrimitiveType.Plane,
                _ => PrimitiveType.Cube
            };
            var go = GameObject.CreatePrimitive(type);
            go.name = name;
            go.GetComponent<MeshRenderer>().sharedMaterial = material;
            return go;
        }

        static Quaternion ToRotation(float[] euler, float[] rot)
        {
            if (rot != null && rot.Length >= 4)
            {
                return new Quaternion(rot[0], rot[1], rot[2], rot[3]).normalized;
            }

            return Quaternion.Euler(ToVec3(euler));
        }

        static Vector3 ToVec3(float[] v)
        {
            if (v == null || v.Length < 3)
            {
                return Vector3.zero;
            }

            return new Vector3(v[0], v[1], v[2]);
        }

        static Color ToColor(float[] v)
        {
            if (v == null || v.Length < 3)
            {
                return Color.white;
            }

            float a = v.Length > 3 ? v[3] : 1f;
            return new Color(v[0], v[1], v[2], a);
        }
    }
}
