using System.IO;
using CustomSRP.Examples;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace CustomSRP.Editor
{
    /// <summary>
    /// 四角分屏 + Rendering Layer 灯光遮罩、中心 Overlay 预乘混合、RT 相机 + Canvas RawImage。
    /// </summary>
    public static class CreateMultipleCamerasScene
    {
        const string ScenePath = TestSceneUtility.ScenesPath + "/MultipleCameras.unity";
        const string OpaqueMatPath = TestSceneUtility.MaterialsPath + "/LitMultipleCamerasOpaque.mat";
        const string EmissionMatPath = TestSceneUtility.MaterialsPath + "/LitMultipleCamerasEmission.mat";
        const string RtPath = TestSceneUtility.Root + "/Textures/MultipleCamerasRT.renderTexture";
        const string ColdPostFxPath =
            TestSceneUtility.Root + "/Settings/PostFXSettingsCold.asset";
        const string GlowPostFxPath =
            TestSceneUtility.Root + "/Settings/PostFXSettingsGlow.asset";

        // Matches Project Settings Tags and Layers / Rendering Layers ("Layer N" = bit N-1).
        const int Layer1 = 1 << 0;
        const int Layer2 = 1 << 1;
        const int Layer3 = 1 << 2;
        const int Layer4 = 1 << 3;
        const int Layer5 = 1 << 4;
        const int Layer6 = 1 << 5;
        const int Layer7 = 1 << 6;

        // Same pose as reference Main Camera / quadrant cameras.
        static readonly Vector3 SharedCamPos = new Vector3(0.87f, 9.19f, -8.14f);
        static readonly Quaternion SharedCamRot = new Quaternion(
            0.40444717f, -0.009613395f, 0.004224701f, 0.9145011f);

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
                    TestSceneUtility.Root + "/Editor/MultipleCamerasSceneData/.force-rebuild";
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

        [MenuItem("CustomSRP/Create Multiple Cameras Scene")]
        public static void Create()
        {
            TestSceneUtility.EnsureStandardFolders();
            TestSceneUtility.EnsureFolder(TestSceneUtility.Root, "Settings");
            TestSceneUtility.EnsureFolder(TestSceneUtility.Root + "/Editor", "MultipleCamerasSceneData");

            Material opaque = EnsureOpaque();
            Material emission = EnsureEmission();
            RenderTexture rt = EnsureRenderTexture();
            PostFXSettings coldFx = EnsureColdPostFx();
            PostFXSettings glowFx = EnsureGlowPostFx();

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientSkyColor = new Color(0.08f, 0.08f, 0.1f);
            RenderSettings.ambientIntensity = 1f;

            BuildGeometry(opaque, emission);
            BuildMeshBall(opaque);
            BuildLights();
            BuildCameras(rt, coldFx, glowFx);
            BuildRtCanvas(rt);

            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            TestSceneUtility.AddToBuildSettings(ScenePath, makeFirst: false);
            Debug.Log(
                "[CustomSRP] Created Multiple Cameras scene " +
                "(2x2 split + overlay blend + RT canvas): " + ScenePath);
            EditorSceneManager.OpenScene(ScenePath);
        }

        static Material EnsureOpaque()
        {
            return TestSceneUtility.CreateOrUpdateLit(
                OpaqueMatPath,
                new Color(0.55f, 0.55f, 0.58f, 1f),
                TestSceneUtility.SurfaceType.Opaque,
                metallic: 0.05f,
                smoothness: 0.55f,
                enableGpuInstancing: true);
        }

        static Material EnsureEmission()
        {
            Material mat = TestSceneUtility.CreateOrUpdateLit(
                EmissionMatPath,
                new Color(0.2f, 0.2f, 0.22f, 1f),
                TestSceneUtility.SurfaceType.Opaque,
                metallic: 0f,
                smoothness: 0.4f);
            if (mat.HasProperty("_EmissionColor"))
            {
                mat.SetColor("_EmissionColor", new Color(4f, 2.2f, 0.6f, 1f));
                EditorUtility.SetDirty(mat);
            }

            return mat;
        }

        static RenderTexture EnsureRenderTexture()
        {
            var rt = AssetDatabase.LoadAssetAtPath<RenderTexture>(RtPath);
            if (rt == null)
            {
                rt = new RenderTexture(256, 144, 0, RenderTextureFormat.DefaultHDR)
                {
                    name = "MultipleCamerasRT",
                    antiAliasing = 1,
                    filterMode = FilterMode.Bilinear
                };
                rt.Create();
                AssetDatabase.CreateAsset(rt, RtPath);
            }

            return rt;
        }

        static PostFXSettings EnsureColdPostFx()
        {
            var settings = AssetDatabase.LoadAssetAtPath<PostFXSettings>(ColdPostFxPath);
            Shader shader = Shader.Find("Hidden/CustomSRP/Post FX Stack");
            if (settings == null)
            {
                settings = ScriptableObject.CreateInstance<PostFXSettings>();
                AssetDatabase.CreateAsset(settings, ColdPostFxPath);
            }

            var so = new SerializedObject(settings);
            so.FindProperty("shader").objectReferenceValue = shader;
            SerializedProperty bloom = so.FindProperty("bloom");
            bloom.FindPropertyRelative("maxIterations").intValue = 8;
            bloom.FindPropertyRelative("downscaleLimit").intValue = 2;
            bloom.FindPropertyRelative("intensity").floatValue = 0.25f;
            bloom.FindPropertyRelative("threshold").floatValue = 0.8f;
            bloom.FindPropertyRelative("thresholdKnee").floatValue = 0.5f;
            bloom.FindPropertyRelative("mode").enumValueIndex = 1;
            bloom.FindPropertyRelative("scatter").floatValue = 0.7f;
            bloom.FindPropertyRelative("fadeFireflies").boolValue = true;
            bloom.FindPropertyRelative("bicubicUpsampling").boolValue = true;

            SerializedProperty whiteBalance = so.FindProperty("whiteBalance");
            whiteBalance.FindPropertyRelative("temperature").floatValue = -40f;
            whiteBalance.FindPropertyRelative("tint").floatValue = 10f;

            SerializedProperty tone = so.FindProperty("toneMapping");
            tone.FindPropertyRelative("mode").enumValueIndex = 2; // Neutral

            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(settings);
            return settings;
        }

        static PostFXSettings EnsureGlowPostFx()
        {
            var settings = AssetDatabase.LoadAssetAtPath<PostFXSettings>(GlowPostFxPath);
            Shader shader = Shader.Find("Hidden/CustomSRP/Post FX Stack");
            if (settings == null)
            {
                settings = ScriptableObject.CreateInstance<PostFXSettings>();
                AssetDatabase.CreateAsset(settings, GlowPostFxPath);
            }

            var so = new SerializedObject(settings);
            so.FindProperty("shader").objectReferenceValue = shader;
            SerializedProperty bloom = so.FindProperty("bloom");
            bloom.FindPropertyRelative("maxIterations").intValue = 2;
            bloom.FindPropertyRelative("downscaleLimit").intValue = 2;
            bloom.FindPropertyRelative("intensity").floatValue = 0.8f;
            bloom.FindPropertyRelative("threshold").floatValue = 1f;
            bloom.FindPropertyRelative("thresholdKnee").floatValue = 0.5f;
            bloom.FindPropertyRelative("mode").enumValueIndex = 0; // Additive
            bloom.FindPropertyRelative("scatter").floatValue = 0.7f;
            bloom.FindPropertyRelative("fadeFireflies").boolValue = true;
            bloom.FindPropertyRelative("bicubicUpsampling").boolValue = true;

            SerializedProperty colorAdj = so.FindProperty("colorAdjustments");
            colorAdj.FindPropertyRelative("contrast").floatValue = 100f;

            SerializedProperty tone = so.FindProperty("toneMapping");
            tone.FindPropertyRelative("mode").enumValueIndex = 1; // ACES

            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(settings);
            return settings;
        }

        static void BuildGeometry(Material opaque, Material emission)
        {
            GameObject ground = TestSceneUtility.CreatePlane(
                "Ground",
                Vector3.zero,
                new Vector3(2.5f, 1f, 2.5f),
                opaque);
            ground.GetComponent<MeshRenderer>().renderingLayerMask = uint.MaxValue;

            // Shared scene props — Everything so maskLights (not geometry) drives per-camera look.
            CreateProp("Cube_A", PrimitiveType.Cube, new Vector3(-2.2f, 0.6f, 0.5f),
                new Vector3(1.2f, 1.2f, 1.2f), opaque, new Color(0.75f, 0.35f, 0.3f), uint.MaxValue);
            CreateProp("Cube_B", PrimitiveType.Cube, new Vector3(2.0f, 0.75f, -0.8f),
                new Vector3(1.5f, 1.5f, 1.5f), opaque, new Color(0.3f, 0.45f, 0.8f), uint.MaxValue);
            CreateProp("Sphere_C", PrimitiveType.Sphere, new Vector3(0.2f, 0.7f, 2.2f),
                Vector3.one * 1.4f, opaque, new Color(0.35f, 0.7f, 0.4f), uint.MaxValue);

            CreateProp("Emit_Shared", PrimitiveType.Sphere, new Vector3(-3.5f, 1.2f, -1.5f),
                Vector3.one * 0.7f, emission, null, uint.MaxValue);

            // Overlay-only emitters (Layer 7) — reference Overlay mask includes bit 64.
            CreateProp("Emit_Overlay", PrimitiveType.Sphere, new Vector3(0f, 1.5f, -1.2f),
                Vector3.one * 0.55f, emission, null, (uint)Layer7);
            CreateProp("Emit_Overlay_2", PrimitiveType.Cube, new Vector3(1.2f, 1.2f, 0.4f),
                Vector3.one * 0.45f, emission, null, (uint)Layer7);

            // Bottom-right exclusive prop (Layer 4) — only BR camera draws it as geometry.
            CreateProp("Cube_Layer4", PrimitiveType.Cube, new Vector3(3.2f, 1.0f, 1.8f),
                Vector3.one * 0.8f, opaque, new Color(0.9f, 0.7f, 0.2f), (uint)Layer4);
        }

        static void BuildMeshBall(Material opaque)
        {
            // Instanced path defaults to Layer 1 — visible on TL/TR/BL (masks include Layer1),
            // filtered out on BR (Layer4 only). Matches reference teaching case.
            var go = new GameObject("Mesh Ball");
            go.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            var ball = go.AddComponent<MeshBall>();
            var tmp = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Mesh sphereMesh = tmp.GetComponent<MeshFilter>().sharedMesh;
            Object.DestroyImmediate(tmp);

            var so = new SerializedObject(ball);
            so.FindProperty("mesh").objectReferenceValue = sphereMesh;
            so.FindProperty("material").objectReferenceValue = opaque;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        static void CreateProp(
            string name,
            PrimitiveType type,
            Vector3 position,
            Vector3 scale,
            Material material,
            Color? baseColor,
            uint renderingLayerMask)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = name;
            go.transform.SetPositionAndRotation(position, Quaternion.identity);
            go.transform.localScale = scale;
            var renderer = go.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.renderingLayerMask = renderingLayerMask;

            if (baseColor.HasValue)
            {
                var props = go.AddComponent<PerObjectMaterialProperties>();
                var so = new SerializedObject(props);
                so.FindProperty("baseColor").colorValue = baseColor.Value;
                so.FindProperty("metallic").floatValue = 0.1f;
                so.FindProperty("smoothness").floatValue = 0.6f;
                so.ApplyModifiedPropertiesWithoutUndo();
            }
        }

        static void BuildLights()
        {
            // Directional fills — layer bits aligned with reference Light A/B/C/D spirit.
            CreateDirectional(
                "Light A",
                new Vector3(40f, -30f, 0f),
                new Color(1f, 0.96f, 0.84f),
                intensity: 1f,
                Layer2);
            CreateDirectional(
                "Light B",
                new Vector3(50f, 40f, 0f),
                new Color(1f, 0.995f, 0.88f),
                intensity: 1f,
                Layer5);
            CreateDirectional(
                "Light C",
                new Vector3(25f, 160f, 0f),
                new Color(1f, 0.53f, 0.6f),
                intensity: 0.35f,
                Layer1);
            CreateDirectional(
                "Light D",
                new Vector3(20f, -150f, 0f),
                new Color(0.15f, 1f, 0.2f),
                intensity: 0.25f,
                Layer1);

            CreatePoint(
                "Point Layer3",
                new Vector3(2.5f, 2.2f, 0.5f),
                new Color(0.45f, 0.7f, 1f),
                intensity: 12f,
                range: 14f,
                Layer3);
            CreatePoint(
                "Point Layer4",
                new Vector3(-1.5f, 2.8f, 1.5f),
                new Color(1f, 0.85f, 0.35f),
                intensity: 14f,
                range: 16f,
                Layer4);
            CreatePoint(
                "Point Layer6",
                new Vector3(0.5f, 3.2f, -2f),
                new Color(0.3f, 0.75f, 1f),
                intensity: 18f,
                range: 18f,
                Layer6);
            CreatePoint(
                "Point Overlay",
                new Vector3(0f, 2.5f, -0.5f),
                new Color(1f, 0.55f, 0.2f),
                intensity: 10f,
                range: 10f,
                Layer7);

            CreateSpot(
                "Spot Layer5",
                new Vector3(-2f, 4.5f, -1f),
                new Vector3(50f, 30f, 0f),
                new Color(0.5f, 0.9f, 1f),
                intensity: 16f,
                range: 14f,
                Layer5);
        }

        static void CreateDirectional(
            string name, Vector3 euler, Color color, float intensity, int layerMask)
        {
            Light light = TestSceneUtility.CreateDirectionalLight(
                name, euler, color, intensity, LightShadows.Soft);
            light.renderingLayerMask = layerMask;
        }

        static void CreatePoint(
            string name, Vector3 position, Color color, float intensity, float range, int layerMask)
        {
            var go = new GameObject(name, typeof(Light));
            go.transform.position = position;
            var light = go.GetComponent<Light>();
            light.type = LightType.Point;
            light.color = color;
            light.intensity = intensity;
            light.range = range;
            light.shadows = LightShadows.None;
            light.renderingLayerMask = layerMask;
        }

        static void CreateSpot(
            string name,
            Vector3 position,
            Vector3 euler,
            Color color,
            float intensity,
            float range,
            int layerMask)
        {
            var go = new GameObject(name, typeof(Light));
            go.transform.SetPositionAndRotation(position, Quaternion.Euler(euler));
            var light = go.GetComponent<Light>();
            light.type = LightType.Spot;
            light.color = color;
            light.intensity = intensity;
            light.range = range;
            light.spotAngle = 55f;
            light.shadows = LightShadows.None;
            light.renderingLayerMask = layerMask;
        }

        static void BuildCameras(RenderTexture rt, PostFXSettings coldFx, PostFXSettings glowFx)
        {
            // Top Left — Layer1|Layer2 (bits 3); global Post FX.
            Camera topLeft = CreateGameCamera(
                "Main Camera Top Left",
                SharedCamPos,
                SharedCamRot,
                depth: -1f,
                rect: new Rect(0f, 0.5f, 0.5f, 0.5f),
                clearFlags: CameraClearFlags.Skybox);
            topLeft.tag = "MainCamera";
            topLeft.gameObject.AddComponent<AudioListener>();
            ConfigureCrpCamera(
                topLeft,
                maskLights: true,
                renderingLayerMask: Layer1 | Layer2,
                finalSrc: BlendMode.One,
                finalDst: BlendMode.Zero);

            // Top Right — Layer1|Layer3 (bits 5).
            Camera topRight = CreateGameCamera(
                "Camera Top Right",
                SharedCamPos,
                SharedCamRot,
                depth: -1f,
                rect: new Rect(0.5f, 0.5f, 0.5f, 0.5f),
                clearFlags: CameraClearFlags.Skybox);
            ConfigureCrpCamera(
                topRight,
                maskLights: true,
                renderingLayerMask: Layer1 | Layer3,
                finalSrc: BlendMode.One,
                finalDst: BlendMode.Zero);

            // Bottom Left — Layer1|Layer5|Layer6 (bits 49); override FX off.
            Camera bottomLeft = CreateGameCamera(
                "Camera Bottom Left",
                SharedCamPos,
                SharedCamRot,
                depth: -1f,
                rect: new Rect(0f, 0f, 0.5f, 0.5f),
                clearFlags: CameraClearFlags.Skybox);
            ConfigureCrpCamera(
                bottomLeft,
                maskLights: true,
                renderingLayerMask: Layer1 | Layer5 | Layer6,
                finalSrc: BlendMode.One,
                finalDst: BlendMode.Zero,
                overridePostFx: true,
                postFx: null);

            // Bottom Right — Layer4 only (bits 8); Cold Post FX.
            Camera bottomRight = CreateGameCamera(
                "Camera Bottom Right",
                SharedCamPos,
                SharedCamRot,
                depth: -1f,
                rect: new Rect(0.5f, 0f, 0.5f, 0.5f),
                clearFlags: CameraClearFlags.Skybox);
            ConfigureCrpCamera(
                bottomRight,
                maskLights: true,
                renderingLayerMask: Layer4,
                finalSrc: BlendMode.One,
                finalDst: BlendMode.Zero,
                overridePostFx: true,
                postFx: coldFx);

            // Overlay — solid clear alpha 0, Glow FX, premultiplied final blend.
            Camera overlay = CreateGameCamera(
                "Overlay Camera",
                new Vector3(0f, 0f, -7f),
                Quaternion.identity,
                depth: 0f,
                rect: new Rect(0.375f, 0.25f, 0.25f, 0.5f),
                clearFlags: CameraClearFlags.SolidColor);
            overlay.backgroundColor = new Color(0f, 0f, 0f, 0f);
            ConfigureCrpCamera(
                overlay,
                maskLights: true,
                renderingLayerMask: Layer5 | Layer7,
                finalSrc: BlendMode.One,
                finalDst: BlendMode.OneMinusSrcAlpha,
                overridePostFx: true,
                postFx: glowFx);

            // RT camera — full view into render texture (shown on Canvas RawImage).
            Camera rtCam = CreateGameCamera(
                "Render Texture Camera",
                SharedCamPos,
                SharedCamRot,
                depth: 0f,
                rect: new Rect(0f, 0f, 1f, 1f),
                clearFlags: CameraClearFlags.SolidColor);
            rtCam.backgroundColor = new Color(0f, 0f, 0f, 0f);
            rtCam.targetTexture = rt;
            ConfigureCrpCamera(
                rtCam,
                maskLights: false,
                renderingLayerMask: -1,
                finalSrc: BlendMode.One,
                finalDst: BlendMode.Zero,
                overridePostFx: true,
                postFx: coldFx);
        }

        static Camera CreateGameCamera(
            string name,
            Vector3 position,
            Quaternion rotation,
            float depth,
            Rect rect,
            CameraClearFlags clearFlags)
        {
            var go = new GameObject(name, typeof(Camera));
            var cam = go.GetComponent<Camera>();
            cam.transform.SetPositionAndRotation(position, rotation);
            cam.clearFlags = clearFlags;
            cam.backgroundColor = new Color(0.192f, 0.302f, 0.475f, 0f);
            cam.depth = depth;
            cam.rect = rect;
            cam.allowHDR = true;
            cam.nearClipPlane = 0.3f;
            cam.farClipPlane = 1000f;
            cam.fieldOfView = 60f;
            return cam;
        }

        static void ConfigureCrpCamera(
            Camera camera,
            bool maskLights,
            int renderingLayerMask,
            BlendMode finalSrc,
            BlendMode finalDst,
            bool overridePostFx = false,
            PostFXSettings postFx = null)
        {
            var crp = camera.gameObject.AddComponent<CustomRenderPipelineCamera>();
            var so = new SerializedObject(crp);
            SerializedProperty settings = so.FindProperty("settings");
            settings.FindPropertyRelative("maskLights").boolValue = maskLights;
            settings.FindPropertyRelative("renderingLayerMask").intValue =
                renderingLayerMask;
            settings.FindPropertyRelative("overridePostFX").boolValue = overridePostFx;
            settings.FindPropertyRelative("postFXSettings").objectReferenceValue = postFx;

            SerializedProperty blend = settings.FindPropertyRelative("finalBlendMode");
            blend.FindPropertyRelative("source").intValue = (int)finalSrc;
            blend.FindPropertyRelative("destination").intValue = (int)finalDst;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        static void BuildRtCanvas(RenderTexture rt)
        {
            var canvasGo = new GameObject(
                "Canvas",
                typeof(Canvas),
                typeof(CanvasScaler),
                typeof(GraphicRaycaster));
            var canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 10;

            var scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);

            var imageGo = new GameObject("RawImage", typeof(RectTransform), typeof(RawImage));
            imageGo.transform.SetParent(canvasGo.transform, false);
            var rect = imageGo.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.02f, 0.02f);
            rect.anchorMax = new Vector2(0.02f, 0.02f);
            rect.pivot = new Vector2(0f, 0f);
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = new Vector2(320f, 180f);

            var raw = imageGo.GetComponent<RawImage>();
            raw.texture = rt;
            raw.color = Color.white;
        }
    }
}
