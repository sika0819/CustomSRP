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
    /// 布局对齐 Particles（Varied Objects + Distortion 粒子，便于验 _CameraBufferSize）；
    /// 左右分屏对比 Inherit vs Override 0.5；Post FX Bloom 用 bufferSize。
    /// 布局数据在 RenderScaleSceneData/*.json（自 ParticlesSceneData）。
    /// </summary>
    public static class CreateRenderScaleScene
    {
        const string ScenePath = TestSceneUtility.ScenesPath + "/RenderScale.unity";
        const string DataFolderRelative = "CustomSRP/Editor/RenderScaleSceneData";
        const string OpaqueMatPath = TestSceneUtility.MaterialsPath + "/LitRenderScaleOpaque.mat";
        const string SingleMatPath = TestSceneUtility.MaterialsPath + "/UnlitParticlesSingle.mat";
        const string FlipbookMatPath = TestSceneUtility.MaterialsPath + "/UnlitParticlesFlipbook.mat";
        const string PostFxPath = TestSceneUtility.Root + "/Settings/PostFXSettingsRenderScale.asset";
        const string SingleTexPath = TestSceneUtility.TexturesPath + "/ParticleSingle.png";
        const string SingleDistortionPath =
            TestSceneUtility.TexturesPath + "/ParticleSingleDistortion.png";
        const string FlipbookTexPath = TestSceneUtility.TexturesPath + "/ParticleFlipbook.png";
        const string FlipbookDistortionPath =
            TestSceneUtility.TexturesPath + "/ParticleFlipbookDistortion.png";

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
                    TestSceneUtility.Root + "/Editor/RenderScaleSceneData/.force-rebuild";
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

        [MenuItem("CustomSRP/Create Render Scale Scene")]
        public static void Create()
        {
            TestSceneUtility.EnsureStandardFolders();
            TestSceneUtility.EnsureFolder(TestSceneUtility.Root + "/Editor", "RenderScaleSceneData");
            TestSceneUtility.EnsureFolder(TestSceneUtility.Root, "Settings");
            CustomSrpBootstrap.EnsureCameraRendererShaderAssigned(
                AssetDatabase.LoadAssetAtPath<CustomRenderPipelineAsset>(
                    "Assets/CustomSRP/Settings/CustomRenderPipelineAsset.asset"));

            SceneData data = LoadSceneData();
            ConfigureTextures();
            SceneMaterials mats = EnsureMaterials();
            PostFXSettings postFx = EnsurePostFx();

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            ApplyAmbient(data.Meta.ambient);
            BuildSceneContents(mats, data, postFx);
            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            TestSceneUtility.AddToBuildSettings(ScenePath, makeFirst: false);
            Debug.Log(
                "[CustomSRP] Created Render Scale scene " +
                "(Particles layout + split Inherit/Override 0.5): " +
                ScenePath);
            EditorSceneManager.OpenScene(ScenePath);
        }

        static PostFXSettings EnsurePostFx()
        {
            var settings = AssetDatabase.LoadAssetAtPath<PostFXSettings>(PostFxPath);
            if (settings == null)
            {
                settings = ScriptableObject.CreateInstance<PostFXSettings>();
                AssetDatabase.CreateAsset(settings, PostFxPath);
            }

            var so = new SerializedObject(settings);
            Shader shader = Shader.Find("Hidden/CustomSRP/Post FX Stack");
            so.FindProperty("shader").objectReferenceValue = shader;

            SerializedProperty bloom = so.FindProperty("bloom");
            bloom.FindPropertyRelative("ignoreRenderScale").boolValue = false;
            bloom.FindPropertyRelative("maxIterations").intValue = 2;
            bloom.FindPropertyRelative("downscaleLimit").intValue = 2;
            bloom.FindPropertyRelative("bicubicUpsampling").boolValue = true;
            bloom.FindPropertyRelative("threshold").floatValue = 0.5f;
            bloom.FindPropertyRelative("thresholdKnee").floatValue = 0.5f;
            bloom.FindPropertyRelative("intensity").floatValue = 1f;
            bloom.FindPropertyRelative("fadeFireflies").boolValue = true;
            bloom.FindPropertyRelative("mode").enumValueIndex = 0; // Additive
            bloom.FindPropertyRelative("scatter").floatValue = 0.7f;

            SerializedProperty tone = so.FindProperty("toneMapping");
            tone.FindPropertyRelative("mode").enumValueIndex = 2; // Neutral

            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(settings);
            return settings;
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
            public int allowHDR = 1;
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
            public Material Single;
            public Material Flipbook;
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

        static void ConfigureTextures()
        {
            ConfigureTextureImporter(SingleTexPath, normalMap: false);
            ConfigureTextureImporter(FlipbookTexPath, normalMap: false);
            ConfigureTextureImporter(SingleDistortionPath, normalMap: true);
            ConfigureTextureImporter(FlipbookDistortionPath, normalMap: true);
        }

        static void ConfigureTextureImporter(string path, bool normalMap)
        {
            if (!File.Exists(path))
            {
                Debug.LogWarning("[CustomSRP] Missing texture: " + path);
                return;
            }

            AssetDatabase.ImportAsset(path);
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null)
            {
                return;
            }

            importer.textureType =
                normalMap ? TextureImporterType.NormalMap : TextureImporterType.Default;
            importer.sRGBTexture = !normalMap;
            importer.alphaSource = TextureImporterAlphaSource.FromInput;
            importer.alphaIsTransparency = !normalMap;
            importer.mipmapEnabled = true;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.filterMode = FilterMode.Bilinear;
            importer.SaveAndReimport();
        }

        static SceneMaterials EnsureMaterials()
        {
            Texture2D uvAlpha = TestSceneUtility.EnsureUvAlphaTexture();

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

            Texture2D singleTex = AssetDatabase.LoadAssetAtPath<Texture2D>(SingleTexPath);
            Texture2D singleDist =
                AssetDatabase.LoadAssetAtPath<Texture2D>(SingleDistortionPath);
            Texture2D flipbookTex = AssetDatabase.LoadAssetAtPath<Texture2D>(FlipbookTexPath);
            Texture2D flipbookDist =
                AssetDatabase.LoadAssetAtPath<Texture2D>(FlipbookDistortionPath);

            Material single = EnsureParticlesMaterial(
                SingleMatPath,
                singleTex,
                singleDist,
                flipbookBlending: false,
                distortionStrength: 0.01f);
            Material flipbook = EnsureParticlesMaterial(
                FlipbookMatPath,
                flipbookTex,
                flipbookDist,
                flipbookBlending: true,
                distortionStrength: 0.07f);

            return new SceneMaterials
            {
                Opaque = opaque,
                Clip = clip,
                Transparent = transparent,
                Single = single,
                Flipbook = flipbook
            };
        }

        static Material EnsureParticlesMaterial(
            string path,
            Texture2D baseMap,
            Texture2D distortionMap,
            bool flipbookBlending,
            float distortionStrength)
        {
            Shader shader = Shader.Find("CustomSRP/Particles/Unlit");
            if (shader == null)
            {
                throw new System.InvalidOperationException(
                    "CustomSRP/Particles/Unlit shader not found.");
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

            material.SetTexture("_BaseMap", baseMap);
            material.SetTexture("_DistortionMap", distortionMap);
            material.SetColor("_BaseColor", Color.white);
            material.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            material.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            material.SetFloat("_ZWrite", 0f);
            material.SetFloat("_Clipping", 0f);
            material.DisableKeyword("_CLIPPING");
            material.SetFloat("_Shadows", 3f);
            material.DisableKeyword("_SHADOWS_CLIP");
            material.DisableKeyword("_SHADOWS_DITHER");
            material.SetShaderPassEnabled("ShadowCaster", false);
            material.renderQueue = (int)RenderQueue.Transparent;
            material.SetOverrideTag("RenderType", "Transparent");
            material.enableInstancing = true;

            SetToggle(material, "_VertexColors", "_VERTEX_COLORS", true);
            SetToggle(material, "_FlipbookBlending", "_FLIPBOOK_BLENDING", flipbookBlending);
            SetToggle(material, "_NearFade", "_NEAR_FADE", true);
            material.SetFloat("_NearFadeDistance", 1f);
            material.SetFloat("_NearFadeRange", 1f);
            SetToggle(material, "_SoftParticles", "_SOFT_PARTICLES", true);
            material.SetFloat("_SoftParticlesDistance", 0f);
            material.SetFloat("_SoftParticlesRange", 1f);
            SetToggle(material, "_Distortion", "_DISTORTION", true);
            material.SetFloat("_DistortionStrength", distortionStrength);
            material.SetFloat("_DistortionBlend", 0f);
            EditorUtility.SetDirty(material);
            return material;
        }

        static void SetToggle(Material mat, string property, string keyword, bool enabled)
        {
            if (mat.HasProperty(property))
            {
                mat.SetFloat(property, enabled ? 1f : 0f);
            }

            if (enabled)
            {
                mat.EnableKeyword(keyword);
            }
            else
            {
                mat.DisableKeyword(keyword);
            }
        }

        static void ApplyAmbient(AmbientData ambient)
        {
            if (ambient == null)
            {
                RenderSettings.ambientMode = AmbientMode.Skybox;
                RenderSettings.ambientSkyColor = new Color(0.212f, 0.227f, 0.259f);
                RenderSettings.ambientIntensity = 1f;
                return;
            }

            RenderSettings.ambientMode = (AmbientMode)ambient.mode;
            RenderSettings.ambientSkyColor = ToColor(ambient.skyColor);
            RenderSettings.ambientIntensity = ambient.intensity;
        }

        static void BuildSceneContents(SceneMaterials mats, SceneData data, PostFXSettings postFx)
        {
            BuildCameras(data.Meta.camera, postFx);
            BuildLights(data.Lights);
            BuildGeometry(mats, data.Objects);
            BuildParticleSystems(mats);
        }

        static void BuildGeometry(SceneMaterials mats, List<ObjectEntry> objects)
        {
            var objectNames = new HashSet<string>();
            var parentNames = new HashSet<string>();
            foreach (ObjectEntry entry in objects)
            {
                objectNames.Add(entry.name);
                if (!string.IsNullOrEmpty(entry.parent))
                {
                    parentNames.Add(entry.parent);
                }
            }

            var parents = new Dictionary<string, Transform>();
            foreach (string parentName in parentNames)
            {
                if (objectNames.Contains(parentName) || parents.ContainsKey(parentName))
                {
                    continue;
                }

                // Empty group roots (e.g. Varied Objects).
                parents[parentName] = new GameObject(parentName).transform;
            }

            var created = new Dictionary<string, GameObject>();
            foreach (ObjectEntry entry in objects)
            {
                Material mat = ResolveMaterial(mats, entry.mat);
                GameObject go = CreatePrimitive(entry.mesh, entry.name, mat);
                go.GetComponent<MeshRenderer>().shadowCastingMode =
                    (ShadowCastingMode)entry.castShadows;
                ApplyProps(go, entry);
                created[entry.name] = go;

                if (parentNames.Contains(entry.name))
                {
                    // Mesh used as parent (e.g. Pincushion).
                    parents[entry.name] = go.transform;
                }
            }

            // JSON stores local TRS; parent first with worldPositionStays=false.
            foreach (ObjectEntry entry in objects)
            {
                if (!created.TryGetValue(entry.name, out GameObject go))
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(entry.parent) &&
                    parents.TryGetValue(entry.parent, out Transform parent))
                {
                    go.transform.SetParent(parent, false);
                }

                go.transform.localPosition = ToVec3(entry.pos);
                go.transform.localRotation = ToRotation(entry.euler, entry.rot);
                go.transform.localScale = ToVec3(entry.scale);
            }
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

        static void BuildCameras(TransformData cam, PostFXSettings postFx)
        {
            Vector3 pos = ToVec3(cam.pos);
            Quaternion rot = ToRotation(cam.euler, cam.rot);
            bool allowHdr = cam.allowHDR != 0;

            // Left: Inherit global renderScale (default 1). Right: Override 0.5.
            Camera left = CreateCamera(
                "Main Camera", pos, rot, depth: 0f,
                rect: new Rect(0f, 0f, 0.5f, 1f), allowHdr);
            ConfigureCrp(
                left,
                copyColor: true,
                copyDepth: true,
                renderScaleMode: 0, // Inherit
                renderScale: 1f,
                overridePostFx: true,
                postFx: postFx);

            Camera right = CreateCamera(
                "Camera RenderScale Override", pos, rot, depth: 1f,
                rect: new Rect(0.5f, 0f, 0.5f, 1f), allowHdr);
            ConfigureCrp(
                right,
                copyColor: true,
                copyDepth: true,
                renderScaleMode: 2, // Override
                renderScale: 0.5f,
                overridePostFx: true,
                postFx: postFx);
        }

        static Camera CreateCamera(
            string name,
            Vector3 pos,
            Quaternion rot,
            float depth,
            Rect rect,
            bool allowHdr)
        {
            var go = new GameObject(name, typeof(Camera));
            var camera = go.GetComponent<Camera>();
            camera.transform.SetPositionAndRotation(pos, rot);
            camera.clearFlags = CameraClearFlags.Skybox;
            camera.backgroundColor = new Color(0.192f, 0.302f, 0.475f, 0f);
            camera.depth = depth;
            camera.rect = rect;
            camera.allowHDR = allowHdr;
            camera.fieldOfView = 60f;
            camera.nearClipPlane = 0.3f;
            camera.farClipPlane = 1000f;
            return camera;
        }

        static void ConfigureCrp(
            Camera camera,
            bool copyColor,
            bool copyDepth,
            int renderScaleMode,
            float renderScale,
            bool overridePostFx,
            PostFXSettings postFx)
        {
            var crp = camera.gameObject.AddComponent<CustomRenderPipelineCamera>();
            var so = new SerializedObject(crp);
            SerializedProperty settings = so.FindProperty("settings");
            settings.FindPropertyRelative("copyColor").boolValue = copyColor;
            settings.FindPropertyRelative("copyDepth").boolValue = copyDepth;
            settings.FindPropertyRelative("renderScaleMode").enumValueIndex = renderScaleMode;
            settings.FindPropertyRelative("renderScale").floatValue = renderScale;
            settings.FindPropertyRelative("overridePostFX").boolValue = overridePostFx;
            settings.FindPropertyRelative("postFXSettings").objectReferenceValue = postFx;
            so.ApplyModifiedPropertiesWithoutUndo();
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

        static void BuildParticleSystems(SceneMaterials mats)
        {
            // Reference: both emitters share the same pose under the plane (rotated -90° X).
            Vector3 pos = new Vector3(0.53f, -3.63f, -0.42f);
            Quaternion rot = Quaternion.Euler(-90f, 0f, 0f);

            GameObject single = BuildParticleSystem(
                "Particles Single",
                mats.Single,
                pos,
                rot,
                startSize: 1f,
                startSpeed: 3f,
                flipbook: false,
                active: false);

            BuildParticleSystem(
                "Particles Flipbook",
                mats.Flipbook,
                pos,
                rot,
                startSize: 2f,
                startSpeed: 2f,
                flipbook: true,
                active: true);

            _ = single;
        }

        static GameObject BuildParticleSystem(
            string name,
            Material mat,
            Vector3 pos,
            Quaternion rot,
            float startSize,
            float startSpeed,
            bool flipbook,
            bool active)
        {
            var go = new GameObject(name);
            go.SetActive(active);
            go.transform.SetPositionAndRotation(pos, rot);
            var ps = go.AddComponent<ParticleSystem>();

            var main = ps.main;
            main.loop = true;
            main.playOnAwake = true;
            main.duration = 5f;
            main.startLifetime = 5f;
            main.startSpeed = startSpeed;
            main.startSize = startSize;
            main.startColor = new ParticleSystem.MinMaxGradient(Color.black, Color.white);
            main.maxParticles = 1000;
            main.simulationSpace = ParticleSystemSimulationSpace.Local;
            main.gravityModifier = 0f;
            if (flipbook)
            {
                main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
            }

            var emission = ps.emission;
            emission.enabled = true;
            emission.rateOverTime = 100f;

            var shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 25f;
            shape.radius = 1f;
            shape.radiusThickness = 1f;
            shape.arc = 360f;
            shape.length = 5f;

            var colorOverLifetime = ps.colorOverLifetime;
            colorOverLifetime.enabled = true;
            var gradient = new Gradient();
            gradient.SetKeys(
                new[]
                {
                    new GradientColorKey(Color.white, 0f),
                    new GradientColorKey(Color.white, 1f)
                },
                new[]
                {
                    new GradientAlphaKey(0f, 0f),
                    new GradientAlphaKey(1f, 6554f / 65535f),
                    new GradientAlphaKey(1f, 58982f / 65535f),
                    new GradientAlphaKey(0f, 1f)
                });
            colorOverLifetime.color = gradient;

            var rotation = ps.rotationOverLifetime;
            rotation.enabled = true;
            if (flipbook)
            {
                rotation.z = new ParticleSystem.MinMaxCurve(0.08726646f, 0.7853981f);
            }

            var tsa = ps.textureSheetAnimation;
            tsa.enabled = true;
            if (flipbook)
            {
                tsa.numTilesX = 4;
                tsa.numTilesY = 4;
                tsa.animation = ParticleSystemAnimationType.WholeSheet;
                tsa.startFrame = new ParticleSystem.MinMaxCurve(0f, 0.9375f);
                tsa.frameOverTime = new ParticleSystem.MinMaxCurve(1f);
                tsa.cycleCount = 1;
            }
            else
            {
                tsa.numTilesX = 1;
                tsa.numTilesY = 1;
            }

            var renderer = ps.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            renderer.sharedMaterial = mat;
            renderer.maxParticleSize = 0.5f;
            renderer.sortMode = ParticleSystemSortMode.Distance;
            renderer.allowRoll = true;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.enableGPUInstancing = true;
            if (flipbook)
            {
                renderer.flip = new Vector3(0.5f, 0.5f, 0f);
            }

            var streams = new List<ParticleSystemVertexStream>
            {
                ParticleSystemVertexStream.Position,
                ParticleSystemVertexStream.Color,
                ParticleSystemVertexStream.UV
            };
            if (flipbook)
            {
                streams.Add(ParticleSystemVertexStream.UV2);
                streams.Add(ParticleSystemVertexStream.AnimBlend);
            }

            renderer.SetActiveVertexStreams(streams);
            return go;
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
