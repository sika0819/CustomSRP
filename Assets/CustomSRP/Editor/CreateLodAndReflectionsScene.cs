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
    /// Plane + 65 LOD 球组 + 12 LOD 立方体金字塔 + 4 Reflection Probes + Directional Lights。
    /// 布局数据在 LodAndReflectionsSceneData/*.json。
    /// </summary>
    public static class CreateLodAndReflectionsScene
    {
        const string ScenePath = TestSceneUtility.ScenesPath + "/LodAndReflections.unity";
        const string LightingSettingsPath =
            TestSceneUtility.Root + "/Settings/LodAndReflectionsLightingSettings.asset";
        const string DataFolderRelative = "CustomSRP/Editor/LodAndReflectionsSceneData";

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
                    TestSceneUtility.Root + "/Editor/LodAndReflectionsSceneData/.force-rebuild";
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

        [MenuItem("CustomSRP/Create LOD and Reflections Scene")]
        public static void Create()
        {
            TestSceneUtility.EnsureStandardFolders();
            SceneData data = LoadSceneData();
            LodMaterials mats = EnsureMaterials(data.Meta.lodColors);

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            // Mixed 采样未接入，用 IndirectOnly 即可（与 BakedLight 一致）。
            TestSceneUtility.EnsureGiLightingSettings(
                LightingSettingsPath,
                MixedLightingMode.IndirectOnly);

            BuildSceneContents(mats, data);
            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            TestSceneUtility.AddToBuildSettings(ScenePath, makeFirst: false);
            Debug.Log(
                $"[CustomSRP] Created LOD and Reflections scene " +
                $"(layout from LodAndReflectionsSceneData): {ScenePath}");
            EditorSceneManager.OpenScene(ScenePath);
        }

        struct LodMaterials
        {
            public Material Shared;
            public Color Yellow;
            public Color Cyan;
            public Color Red;
        }

        class SceneData
        {
            public MetaFile Meta;
            public List<LightEntry> Lights;
            public List<ProbeEntry> Probes;
            public List<LodInstanceEntry> Spheres;
            public List<LodInstanceEntry> Cubes;
        }

        [System.Serializable]
        class MetaFile
        {
            public TransformData camera;
            public PlaneData plane;
            public ProbeSettings probe;
            public LodColors lodColors;
        }

        [System.Serializable]
        class TransformData
        {
            public float[] pos;
            public float[] euler;
        }

        [System.Serializable]
        class PlaneData
        {
            public float[] pos;
            public float[] scale;
            public float[] baseColor;
            public float metallic;
            public float smoothness;
        }

        [System.Serializable]
        class ProbeSettings
        {
            public float[] size;
            public int resolution = 128;
            public int importance = 1;
        }

        [System.Serializable]
        class LodColors
        {
            public float[] yellow;
            public float[] cyan;
            public float[] red;
        }

        [System.Serializable]
        class LightEntry
        {
            public string name;
            public bool active = true;
            public float[] color;
            public float intensity = 1f;
            public float[] euler;
            public int shadows = 2;
            public float shadowBias;
            public float shadowNormalBias = 1f;
            public int mapping = 1;
        }

        [System.Serializable]
        class ProbeEntry
        {
            public string name;
            public float[] pos;
        }

        [System.Serializable]
        class LodInstanceEntry
        {
            public string name;
            public float[] pos;
            public float scale = 1f;
            public float eulerY;
            public float metallic;
            public float smoothness = 0.5f;
        }

        [System.Serializable]
        class ListWrapperLights
        {
            public List<LightEntry> items;
        }

        [System.Serializable]
        class ListWrapperProbes
        {
            public List<ProbeEntry> items;
        }

        [System.Serializable]
        class ListWrapperLod
        {
            public List<LodInstanceEntry> items;
        }

        static SceneData LoadSceneData()
        {
            string folder = Path.Combine(Application.dataPath, DataFolderRelative);
            string metaJson = File.ReadAllText(Path.Combine(folder, "meta.json"));
            string lightsJson = File.ReadAllText(Path.Combine(folder, "lights.json"));
            string probesJson = File.ReadAllText(Path.Combine(folder, "probes.json"));
            string spheresJson = File.ReadAllText(Path.Combine(folder, "spheres.json"));
            string cubesJson = File.ReadAllText(Path.Combine(folder, "cubes.json"));

            return new SceneData
            {
                Meta = JsonUtility.FromJson<MetaFile>(metaJson),
                Lights = JsonUtility.FromJson<ListWrapperLights>(
                    "{\"items\":" + lightsJson + "}").items,
                Probes = JsonUtility.FromJson<ListWrapperProbes>(
                    "{\"items\":" + probesJson + "}").items,
                Spheres = JsonUtility.FromJson<ListWrapperLod>(
                    "{\"items\":" + spheresJson + "}").items,
                Cubes = JsonUtility.FromJson<ListWrapperLod>(
                    "{\"items\":" + cubesJson + "}").items
            };
        }

        static LodMaterials EnsureMaterials(LodColors colors)
        {
            string path = TestSceneUtility.MaterialsPath;
            // 共享白色 Lit；每 LOD 子物体用 PerObjectMaterialProperties 上色。
            Material shared = TestSceneUtility.CreateOrUpdateLit(
                path + "/LitLodShared.mat",
                Color.white,
                TestSceneUtility.SurfaceType.Opaque,
                metallic: 0f,
                smoothness: 0.5f);

            AssetDatabase.SaveAssets();
            return new LodMaterials
            {
                Shared = shared,
                Yellow = ToColor(colors.yellow),
                Cyan = ToColor(colors.cyan),
                Red = ToColor(colors.red)
            };
        }

        static void BuildSceneContents(LodMaterials mats, SceneData data)
        {
            TestSceneUtility.EnsureMainCamera(
                ToVec3(data.Meta.camera.pos),
                ToVec3(data.Meta.camera.euler));
            BuildLights(data.Lights);
            BuildPlane(mats, data.Meta.plane);

            var spheresRoot = new GameObject("LOD Group Spheres");
            foreach (LodInstanceEntry entry in data.Spheres)
            {
                CreateSphereLodGroup(entry, mats, spheresRoot.transform);
            }

            var cubesRoot = new GameObject("LOD Group Cubes");
            foreach (LodInstanceEntry entry in data.Cubes)
            {
                CreateCubeLodGroup(entry, mats, cubesRoot.transform);
            }

            BuildReflectionProbes(data.Probes, data.Meta.probe);
            ConfigureReflectionProbeUsage();
        }

        static void BuildLights(List<LightEntry> lights)
        {
            var root = new GameObject("Directional Lights");
            foreach (LightEntry entry in lights)
            {
                Light light = TestSceneUtility.CreateDirectionalLight(
                    entry.name,
                    ToVec3(entry.euler),
                    ToColor(entry.color),
                    entry.intensity,
                    (LightShadows)entry.shadows,
                    shadowStrength: 1f,
                    shadowBias: entry.shadowBias,
                    shadowNormalBias: entry.shadowNormalBias,
                    lightmapBakeType: (LightmapBakeType)entry.mapping);
                light.transform.SetParent(root.transform, true);
                light.transform.localPosition = new Vector3(0f, 30f, 0f);
                light.gameObject.SetActive(entry.active);
            }
        }

        static void BuildPlane(LodMaterials mats, PlaneData plane)
        {
            var go = TestSceneUtility.CreatePlane(
                "Plane",
                ToVec3(plane.pos),
                ToVec3(plane.scale),
                mats.Shared);
            ApplyProps(go, ToColor(plane.baseColor), plane.metallic, plane.smoothness);
            go.GetComponent<MeshRenderer>().reflectionProbeUsage = ReflectionProbeUsage.Simple;
        }

        static void CreateSphereLodGroup(
            LodInstanceEntry entry,
            LodMaterials mats,
            Transform parent)
        {
            var groupGo = new GameObject(entry.name);
            groupGo.transform.SetParent(parent, false);
            groupGo.transform.SetPositionAndRotation(
                ToVec3(entry.pos),
                Quaternion.Euler(0f, entry.eulerY, 0f));
            groupGo.transform.localScale = Vector3.one * entry.scale;

            Renderer yellow = CreateLodChildSphere(
                groupGo.transform, "LOD 0 Sphere Yellow", mats.Shared, mats.Yellow,
                entry.metallic, entry.smoothness);
            Renderer cyan = CreateLodChildSphere(
                groupGo.transform, "LOD 1 Sphere Cyan", mats.Shared, mats.Cyan,
                entry.metallic, entry.smoothness);
            Renderer red = CreateLodChildSphere(
                groupGo.transform, "LOD 2 Sphere Red", mats.Shared, mats.Red,
                entry.metallic, entry.smoothness);

            var lodGroup = groupGo.AddComponent<LODGroup>();
            lodGroup.fadeMode = LODFadeMode.CrossFade;
            lodGroup.animateCrossFading = true;
            lodGroup.SetLODs(new[]
            {
                new LOD(0.6f, new[] { yellow }) { fadeTransitionWidth = 1f },
                new LOD(0.3f, new[] { cyan }) { fadeTransitionWidth = 1f },
                new LOD(0.1f, new[] { red }) { fadeTransitionWidth = 1f }
            });
            lodGroup.RecalculateBounds();
        }

        static Renderer CreateLodChildSphere(
            Transform parent,
            string name,
            Material material,
            Color baseColor,
            float metallic,
            float smoothness)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localScale = Vector3.one;
            go.GetComponent<MeshRenderer>().sharedMaterial = material;
            Object.DestroyImmediate(go.GetComponent<Collider>());
            ApplyProps(go, baseColor, metallic, smoothness);
            return go.GetComponent<Renderer>();
        }

        /// <summary>
        /// Additive LOD：黄顶 / 青中 / 红底；比率 0.6 / 0.3 / 0.1。
        /// </summary>
        static void CreateCubeLodGroup(
            LodInstanceEntry entry,
            LodMaterials mats,
            Transform parent)
        {
            var groupGo = new GameObject(entry.name);
            groupGo.transform.SetParent(parent, false);
            groupGo.transform.SetPositionAndRotation(
                ToVec3(entry.pos),
                Quaternion.Euler(0f, entry.eulerY, 0f));
            groupGo.transform.localScale = Vector3.one * entry.scale;

            Renderer yellow = CreateLodChildCube(
                groupGo.transform, "LOD 0 Cube Yellow", mats.Shared, mats.Yellow,
                new Vector3(0f, 0.3125f, 0f), new Vector3(0.25f, 0.125f, 0.25f),
                entry.metallic, entry.smoothness);
            Renderer cyan = CreateLodChildCube(
                groupGo.transform, "LOD 0-1 Cube Cyan", mats.Shared, mats.Cyan,
                new Vector3(0f, 0.125f, 0f), new Vector3(0.5f, 0.25f, 0.5f),
                entry.metallic, entry.smoothness);
            Renderer red = CreateLodChildCube(
                groupGo.transform, "LOD 0-2 Cube Red", mats.Shared, mats.Red,
                new Vector3(0f, -0.25f, 0f), new Vector3(1f, 0.5f, 1f),
                entry.metallic, entry.smoothness);

            var lodGroup = groupGo.AddComponent<LODGroup>();
            lodGroup.fadeMode = LODFadeMode.CrossFade;
            lodGroup.animateCrossFading = true;
            lodGroup.SetLODs(new[]
            {
                new LOD(0.6f, new[] { yellow, cyan, red }) { fadeTransitionWidth = 1f },
                new LOD(0.3f, new[] { cyan, red }) { fadeTransitionWidth = 1f },
                new LOD(0.1f, new[] { red }) { fadeTransitionWidth = 1f }
            });
            lodGroup.RecalculateBounds();
        }

        static Renderer CreateLodChildCube(
            Transform parent,
            string name,
            Material material,
            Color baseColor,
            Vector3 localPos,
            Vector3 localScale,
            float metallic,
            float smoothness)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = localScale;
            go.GetComponent<MeshRenderer>().sharedMaterial = material;
            Object.DestroyImmediate(go.GetComponent<Collider>());
            ApplyProps(go, baseColor, metallic, smoothness);
            return go.GetComponent<Renderer>();
        }

        static void BuildReflectionProbes(List<ProbeEntry> probes, ProbeSettings settings)
        {
            var root = new GameObject("Reflection Probes");
            Vector3 size = ToVec3(settings.size);
            foreach (ProbeEntry entry in probes)
            {
                var go = new GameObject(entry.name);
                go.transform.SetParent(root.transform, false);
                go.transform.position = ToVec3(entry.pos);

                var probe = go.AddComponent<ReflectionProbe>();
                probe.mode = ReflectionProbeMode.Baked;
                probe.refreshMode = ReflectionProbeRefreshMode.OnAwake;
                probe.importance = settings.importance;
                probe.size = size;
                probe.center = Vector3.zero;
                probe.intensity = 1f;
                probe.boxProjection = false;
                probe.resolution = settings.resolution;
                probe.hdr = true;
                probe.shadowDistance = 100f;
                probe.clearFlags = ReflectionProbeClearFlags.Skybox;
            }
        }

        static void ConfigureReflectionProbeUsage()
        {
            foreach (MeshRenderer renderer in Object.FindObjectsByType<MeshRenderer>(
                         FindObjectsSortMode.None))
            {
                // BlendProbes 打断 Batcher；本场用 Simple。
                renderer.reflectionProbeUsage = ReflectionProbeUsage.Simple;
            }
        }

        static void ApplyProps(GameObject go, Color baseColor, float metallic, float smoothness)
        {
            var props = go.GetComponent<PerObjectMaterialProperties>();
            if (props == null)
            {
                props = go.AddComponent<PerObjectMaterialProperties>();
            }

            var so = new SerializedObject(props);
            so.FindProperty("baseColor").colorValue = baseColor;
            so.FindProperty("metallic").floatValue = metallic;
            so.FindProperty("smoothness").floatValue = smoothness;
            so.FindProperty("alphaCutoff").floatValue = 0.5f;
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
