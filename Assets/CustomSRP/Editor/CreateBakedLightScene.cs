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
    /// 绿地面 + 开口结构、静态 Cube / 动态 Sphere、Emissive、双 Directional + Point/Spot（烘焙）、
    /// Light Probe Inside/Outside、MeshBall。
    /// 物体/灯光/探针数据在 BakedLightSceneData/*.json。
    /// Shadow Masks 场景复用同一布局，仅 Mixed 模式不同（见 CreateShadowMasksScene）。
    /// </summary>
    public static class CreateBakedLightScene
    {
        const string ScenePath = TestSceneUtility.ScenesPath + "/BakedLight.unity";
        const string LightingSettingsPath =
            TestSceneUtility.Root + "/Settings/BakedLightLightingSettings.asset";
        const string DataFolderRelative = "CustomSRP/Editor/BakedLightSceneData";

        [MenuItem("CustomSRP/Create Baked Light Scene")]
        public static void Create()
        {
            CreateGiScene(
                ScenePath,
                LightingSettingsPath,
                MixedLightingMode.IndirectOnly,
                "Baked Light");
        }

        /// <summary>
        /// 供 Baked Light / Shadow Masks 共用：同布局 JSON，不同 Mixed 烘焙模式。
        /// </summary>
        internal static void CreateGiScene(
            string scenePath,
            string lightingSettingsPath,
            MixedLightingMode mixedMode,
            string logLabel)
        {
            TestSceneUtility.EnsureStandardFolders();
            Texture2D uvAlpha = TestSceneUtility.EnsureUvAlphaTexture();
            Texture2D albedo = TestSceneUtility.EnsureLitAlbedoTexture();
            BakedLightMaterials mats = EnsureMaterials(uvAlpha, albedo);

            SceneData data = LoadSceneData();

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            TestSceneUtility.EnsureGiLightingSettings(lightingSettingsPath, mixedMode);
            BuildSceneContents(mats, data);
            EditorSceneManager.SaveScene(scene, scenePath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            TestSceneUtility.AddToBuildSettings(scenePath, makeFirst: false);
            Debug.Log(
                $"[CustomSRP] Created {logLabel} scene (layout from BakedLightSceneData, " +
                $"mixed={mixedMode}): {scenePath}");
            EditorSceneManager.OpenScene(scenePath);
        }

        struct BakedLightMaterials
        {
            public Material Opaque;
            public Material Sphere;
            public Material Emission;
            public Material Instanced;
        }

        class SceneData
        {
            public MetaFile Meta;
            public List<ObjectEntry> Objects;
            public List<LightEntry> Lights;
            public Dictionary<string, List<float[]>> Probes;
        }

        [System.Serializable]
        class MetaFile
        {
            public TransformData camera;
        }

        [System.Serializable]
        class TransformData
        {
            public float[] pos;
            public float[] euler;
        }

        [System.Serializable]
        class ObjectEntry
        {
            public string name;
            public bool contributeGI;
            public float[] pos;
            public float[] euler;
            public float[] scale;
            public string mesh;
            public string mat;
            public float scaleLM = 1f;
            public PropsEntry props;
        }

        [System.Serializable]
        class PropsEntry
        {
            public float[] baseColor;
            public float[] emissionColor;
            public float metallic;
            public float smoothness;
            public float alphaCutoff = 0.5f;
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
        }

        [System.Serializable]
        class ObjectListWrapper
        {
            public List<ObjectEntry> items;
        }

        static SceneData LoadSceneData()
        {
            string folder = Path.Combine(Application.dataPath, DataFolderRelative);
            string objectsJson = File.ReadAllText(Path.Combine(folder, "objects.json"));
            string lightsJson = File.ReadAllText(Path.Combine(folder, "lights.json"));
            string probesJson = File.ReadAllText(Path.Combine(folder, "probes.json"));
            string metaJson = File.ReadAllText(Path.Combine(folder, "meta.json"));

            // Unity JsonUtility cannot deserialize top-level arrays — wrap.
            var objects = JsonUtility.FromJson<ObjectListWrapper>(
                "{\"items\":" + objectsJson + "}").items;
            var lights = JsonUtility.FromJson<ObjectListWrapperLights>(
                "{\"items\":" + lightsJson + "}").items;

            var probes = new Dictionary<string, List<float[]>>();
            ParseProbes(probesJson, probes);

            return new SceneData
            {
                Meta = JsonUtility.FromJson<MetaFile>(metaJson),
                Objects = objects,
                Lights = lights,
                Probes = probes
            };
        }

        [System.Serializable]
        class ObjectListWrapperLights
        {
            public List<LightEntry> items;
        }

        static void ParseProbes(string json, Dictionary<string, List<float[]>> dest)
        {
            // Expected: {"Light Probe Group Inside":[[x,y,z],...], ...}
            // Minimal parser for this fixed shape.
            int i = 0;
            while (i < json.Length)
            {
                int keyStart = json.IndexOf('"', i);
                if (keyStart < 0)
                {
                    break;
                }

                int keyEnd = json.IndexOf('"', keyStart + 1);
                string key = json.Substring(keyStart + 1, keyEnd - keyStart - 1);
                int arrStart = json.IndexOf('[', keyEnd);
                // Find matching close for outer array of points
                int depth = 0;
                int arrEnd = arrStart;
                for (int j = arrStart; j < json.Length; j++)
                {
                    if (json[j] == '[')
                    {
                        depth++;
                    }
                    else if (json[j] == ']')
                    {
                        depth--;
                        if (depth == 0)
                        {
                            arrEnd = j;
                            break;
                        }
                    }
                }

                string arr = json.Substring(arrStart, arrEnd - arrStart + 1);
                var points = new List<float[]>();
                foreach (System.Text.RegularExpressions.Match m in
                         System.Text.RegularExpressions.Regex.Matches(
                             arr, @"\[([-\d.eE+]+),\s*([-\d.eE+]+),\s*([-\d.eE]+)\]"))
                {
                    points.Add(new[]
                    {
                        float.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture),
                        float.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture),
                        float.Parse(m.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture)
                    });
                }

                dest[key] = points;
                i = arrEnd + 1;
            }
        }

        static BakedLightMaterials EnsureMaterials(Texture2D uvAlpha, Texture2D albedo)
        {
            string path = TestSceneUtility.MaterialsPath;

            Material instanced = AssetDatabase.LoadAssetAtPath<Material>(path + "/LitInstanced.mat");
            if (instanced == null)
            {
                instanced = TestSceneUtility.CreateOrUpdateLit(
                    path + "/LitInstanced.mat",
                    Color.white,
                    TestSceneUtility.SurfaceType.Opaque,
                    albedo,
                    metallic: 0f,
                    smoothness: 0.5f,
                    enableGpuInstancing: true);
            }

            // Reference Opague.mat — white base, per-object color via MPB.
            Material opaque = TestSceneUtility.CreateOrUpdateLit(
                path + "/LitBakeOpaque.mat",
                Color.white,
                TestSceneUtility.SurfaceType.Opaque,
                metallic: 0f,
                smoothness: 0.5f);

            // Reference sphere mat (subset with clip/alpha map) → LitClip + UVAlpha.
            Material sphere = TestSceneUtility.CreateOrUpdateLit(
                path + "/LitBakeSphere.mat",
                Color.white,
                TestSceneUtility.SurfaceType.Clip,
                uvAlpha,
                metallic: 0f,
                smoothness: 0.5f,
                cutoff: 0.5f);

            Material emission = TestSceneUtility.CreateOrUpdateLit(
                path + "/LitBakeEmission.mat",
                new Color(0.2f, 0.1f, 0.05f, 1f),
                TestSceneUtility.SurfaceType.Opaque,
                metallic: 0f,
                smoothness: 0.5f);

            AssetDatabase.SaveAssets();
            return new BakedLightMaterials
            {
                Opaque = opaque,
                Sphere = sphere,
                Emission = emission,
                Instanced = instanced
            };
        }

        static void BuildSceneContents(BakedLightMaterials mats, SceneData data)
        {
            var cam = data.Meta.camera;
            TestSceneUtility.EnsureMainCamera(ToVec3(cam.pos), ToVec3(cam.euler));
            BuildLights(data.Lights);

            var varied = new GameObject("Varied Objects");
            foreach (ObjectEntry entry in data.Objects)
            {
                Material mat = ResolveMaterial(mats, entry.mat);
                GameObject go = CreatePrimitive(entry.mesh, entry.name, mat);
                go.transform.SetPositionAndRotation(ToVec3(entry.pos), Quaternion.Euler(ToVec3(entry.euler)));
                go.transform.localScale = ToVec3(entry.scale);

                if (entry.name == "Ground")
                {
                    // Keep at root like reference.
                }
                else
                {
                    go.transform.SetParent(varied.transform, true);
                }

                TestSceneUtility.SetContributeGI(go, entry.contributeGI, entry.scaleLM);
                ApplyProps(go, entry.props);
            }

            BuildProbeGroup("Light Probe Group Inside", data.Probes);
            BuildProbeGroup("Light Probe Outside", data.Probes);
            BuildMeshBall(mats.Instanced);
        }

        static Material ResolveMaterial(BakedLightMaterials mats, string kind)
        {
            switch (kind)
            {
                case "emis":
                    return mats.Emission;
                case "sphere":
                    return mats.Sphere;
                default:
                    return mats.Opaque;
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

        static void ApplyProps(GameObject go, PropsEntry props)
        {
            if (props == null || props.baseColor == null || props.baseColor.Length < 4)
            {
                return;
            }

            var comp = go.AddComponent<PerObjectMaterialProperties>();
            var so = new SerializedObject(comp);
            so.FindProperty("baseColor").colorValue = ToColor(props.baseColor);
            so.FindProperty("alphaCutoff").floatValue = props.alphaCutoff;
            so.FindProperty("metallic").floatValue = props.metallic;
            so.FindProperty("smoothness").floatValue = props.smoothness;
            SerializedProperty emission = so.FindProperty("emissionColor");
            if (emission != null && props.emissionColor != null && props.emissionColor.Length >= 4)
            {
                emission.colorValue = ToColor(props.emissionColor);
            }

            so.ApplyModifiedPropertiesWithoutUndo();
        }

        static void BuildLights(List<LightEntry> lights)
        {
            foreach (LightEntry entry in lights)
            {
                var go = new GameObject(entry.name, typeof(Light));
                go.transform.SetPositionAndRotation(ToVec3(entry.pos), Quaternion.Euler(ToVec3(entry.euler)));
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

        static void BuildProbeGroup(string name, Dictionary<string, List<float[]>> probes)
        {
            if (!probes.TryGetValue(name, out List<float[]> points) || points == null || points.Count == 0)
            {
                return;
            }

            var go = new GameObject(name);
            var group = go.AddComponent<LightProbeGroup>();
            var positions = new Vector3[points.Count];
            for (int i = 0; i < points.Count; i++)
            {
                positions[i] = ToVec3(points[i]);
            }

            group.probePositions = positions;
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
