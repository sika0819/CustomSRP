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
    /// Plane（Opague 灰）+ 弱 Directional + 74 Point + 3 Spot；Ambient=黑；
    /// 另加 MeshBall（本仓 Examples/MeshBall，Play 可见）。
    /// 布局数据在 PointAndSpotLightsSceneData/*.json。
    /// 本管线实时 Other Lights（Point/Spot）已接入；阴影验收见 PointAndSpotShadows。
    /// Asset「Use Lights Per Object」开时每物体最多 8 盏 other lights。
    /// </summary>
    public static class CreatePointAndSpotLightsScene
    {
        const string ScenePath = TestSceneUtility.ScenesPath + "/PointAndSpotLights.unity";
        const string LightingSettingsPath =
            TestSceneUtility.Root + "/Settings/PointAndSpotLightsLightingSettings.asset";
        const string DataFolderRelative = "CustomSRP/Editor/PointAndSpotLightsSceneData";
        const string OpaqueMatPath = TestSceneUtility.MaterialsPath + "/LitManyLightsOpaque.mat";

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
                    TestSceneUtility.Root + "/Editor/PointAndSpotLightsSceneData/.force-rebuild";
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

        [MenuItem("CustomSRP/Create Point and Spot Lights Scene")]
        public static void Create()
        {
            TestSceneUtility.EnsureStandardFolders();
            SceneData data = LoadSceneData();
            Material opaque = EnsureOpaqueMaterial();
            Material instanced = EnsureInstancedMaterial();

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            // Mixed = Shadowmask（本场布局约定）
            TestSceneUtility.EnsureGiLightingSettings(
                LightingSettingsPath,
                MixedLightingMode.Shadowmask);
            ApplyAmbient(data.Meta.ambient);
            BuildSceneContents(opaque, instanced, data);
            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            TestSceneUtility.AddToBuildSettings(ScenePath, makeFirst: false);
            Debug.Log(
                $"[CustomSRP] Created Point and Spot Lights scene " +
                $"(layout from local Many Lights / PointAndSpotLightsSceneData): {ScenePath}");
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
            public float[] scale;
            public string mesh;
            public string mat;
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

        /// <summary>
        /// 默认灰 0.5、Metallic 0、Smoothness 0.5、Instancing。
        /// </summary>
        static Material EnsureOpaqueMaterial()
        {
            Material mat = TestSceneUtility.CreateOrUpdateLit(
                OpaqueMatPath,
                new Color(0.5f, 0.5f, 0.5f, 1f),
                TestSceneUtility.SurfaceType.Opaque,
                metallic: 0f,
                smoothness: 0.5f,
                enableGpuInstancing: true);
            return mat;
        }

        static Material EnsureInstancedMaterial()
        {
            Texture2D albedo = TestSceneUtility.EnsureLitAlbedoTexture();
            string path = TestSceneUtility.MaterialsPath + "/LitInstanced.mat";
            Material instanced = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (instanced == null)
            {
                instanced = TestSceneUtility.CreateOrUpdateLit(
                    path,
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

            return instanced;
        }

        static void ApplyAmbient(AmbientData ambient)
        {
            if (ambient == null)
            {
                RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Skybox;
                RenderSettings.ambientSkyColor = Color.black;
                RenderSettings.ambientIntensity = 0f;
                return;
            }

            RenderSettings.ambientMode = (UnityEngine.Rendering.AmbientMode)ambient.mode;
            RenderSettings.ambientSkyColor = ToColor(ambient.skyColor);
            RenderSettings.ambientIntensity = ambient.intensity;
        }

        static void BuildSceneContents(Material opaque, Material instanced, SceneData data)
        {
            EnsureCamera(data.Meta.camera);
            BuildLights(data.Lights);

            foreach (ObjectEntry entry in data.Objects)
            {
                GameObject go = CreatePrimitive(entry.mesh, entry.name, opaque);
                go.transform.SetPositionAndRotation(ToVec3(entry.pos), ToRotation(entry.euler, null));
                go.transform.localScale = ToVec3(entry.scale);
            }

            // MeshBall：半径 10 的实例球团（仅 Play 可见）
            BuildMeshBall(instanced);
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
                main.transform.SetPositionAndRotation(ToVec3(cam.pos), ToRotation(cam.euler, cam.rot));
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
                var q = new Quaternion(rot[0], rot[1], rot[2], rot[3]);
                return q.normalized;
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
