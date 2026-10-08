using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using CustomSRP.Examples;

namespace CustomSRP.Editor
{
    /// <summary>
    /// 多材质球体、PerObjectMaterialProperties、MeshBall GPU Instancing、透明 / Cutout。
    /// </summary>
    public static class CreateDrawCallsScene
    {
        const string ScenePath = TestSceneUtility.ScenesPath + "/DrawCalls.unity";

        [MenuItem("CustomSRP/Create Draw Calls Scene")]
        public static void Create()
        {
            TestSceneUtility.EnsureStandardFolders();
            Texture2D uvAlpha = TestSceneUtility.EnsureUvAlphaTexture();
            DrawCallsMaterials mats = EnsureMaterials(uvAlpha);

            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            BuildSceneContents(mats);
            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.Refresh();

            TestSceneUtility.AddToBuildSettings(ScenePath, makeFirst: false);
            Debug.Log($"[CustomSRP] Created Draw Calls scene: {ScenePath}");
            EditorSceneManager.OpenScene(ScenePath);
        }

        struct DrawCallsMaterials
        {
            public Material Red;
            public Material Green;
            public Material Yellow;
            public Material Blue;
            public Material Instanced;
            public Material Transparent;
            public Material Clip;
        }

        static DrawCallsMaterials EnsureMaterials(Texture2D uvAlpha)
        {
            string path = TestSceneUtility.MaterialsPath;
            var mats = new DrawCallsMaterials
            {
                Red = TestSceneUtility.CreateOrUpdateUnlit(
                    path + "/UnlitRed.mat",
                    new Color(0.9f, 0.15f, 0.15f, 1f),
                    TestSceneUtility.SurfaceType.Opaque),
                Green = TestSceneUtility.CreateOrUpdateUnlit(
                    path + "/UnlitGreen.mat",
                    new Color(0.2f, 0.8f, 0.25f, 1f),
                    TestSceneUtility.SurfaceType.Opaque),
                Yellow = TestSceneUtility.CreateOrUpdateUnlit(
                    path + "/UnlitYellow.mat",
                    new Color(0.95f, 0.85f, 0.15f, 1f),
                    TestSceneUtility.SurfaceType.Opaque),
                Blue = TestSceneUtility.CreateOrUpdateUnlit(
                    path + "/UnlitBlue.mat",
                    new Color(0.2f, 0.4f, 0.95f, 1f),
                    TestSceneUtility.SurfaceType.Opaque),
                Instanced = TestSceneUtility.CreateOrUpdateUnlit(
                    path + "/UnlitInstanced.mat",
                    Color.white,
                    TestSceneUtility.SurfaceType.Opaque,
                    enableGpuInstancing: true),
                Transparent = TestSceneUtility.CreateOrUpdateUnlit(
                    path + "/UnlitYellowTransparent.mat",
                    new Color(0.95f, 0.85f, 0.15f, 0.45f),
                    TestSceneUtility.SurfaceType.Transparent,
                    uvAlpha),
                Clip = TestSceneUtility.CreateOrUpdateUnlit(
                    path + "/UnlitClip.mat",
                    Color.white,
                    TestSceneUtility.SurfaceType.Clip,
                    uvAlpha,
                    enableGpuInstancing: true,
                    cutoff: 0.5f)
            };

            AssetDatabase.SaveAssets();
            return mats;
        }

        static void BuildSceneContents(DrawCallsMaterials mats)
        {
            TestSceneUtility.EnsureMainCamera(new Vector3(0f, 4f, -18f), new Vector3(12f, 0f, 0f));
            TestSceneUtility.EnsureDirectionalLight();

            Material[] palette = { mats.Red, mats.Green, mats.Yellow, mats.Blue };
            var root = new GameObject("Spheres");

            // ~76 spheres like the tutorial: shared materials → SRP Batcher friendly.
            const int count = 76;
            for (int i = 0; i < count; i++)
            {
                float angle = i * 0.55f;
                float radius = 1.2f + (i % 7) * 0.55f;
                var pos = new Vector3(
                    Mathf.Cos(angle) * radius,
                    (i % 5) * 0.35f,
                    Mathf.Sin(angle) * radius);

                var go = TestSceneUtility.CreateSphere(
                    $"Sphere_{i:D2}",
                    pos,
                    0.35f,
                    palette[i % palette.Length]);
                go.transform.SetParent(root.transform, true);

                // ~1/3 get per-object colors (breaks SRP Batcher for those; works with GPU Instancing).
                if (i % 3 == 0)
                {
                    go.GetComponent<MeshRenderer>().sharedMaterial = mats.Instanced;
                    var props = go.AddComponent<PerObjectMaterialProperties>();
                    var so = new SerializedObject(props);
                    so.FindProperty("baseColor").colorValue = new Color(
                        Random.value,
                        Random.value,
                        Random.value,
                        1f);
                    so.FindProperty("alphaCutoff").floatValue = 0.5f;
                    so.ApplyModifiedPropertiesWithoutUndo();
                }
            }

            // Transparent cluster
            var transparentRoot = new GameObject("TransparentSpheres");
            for (int i = 0; i < 8; i++)
            {
                var go = TestSceneUtility.CreateSphere(
                    $"Sphere_Transparent_{i}",
                    new Vector3(-6f + i * 0.7f, 1.2f, 4f),
                    0.4f,
                    mats.Transparent);
                go.transform.SetParent(transparentRoot.transform, true);
            }

            // Alpha-clipped spheres with per-object cutoff
            var clipRoot = new GameObject("ClipSpheres");
            for (int i = 0; i < 12; i++)
            {
                var go = TestSceneUtility.CreateSphere(
                    $"Sphere_Clip_{i}",
                    new Vector3(6f + (i % 4) * 0.8f, 0.8f + (i / 4) * 0.8f, 3f),
                    0.4f,
                    mats.Clip);
                go.transform.SetParent(clipRoot.transform, true);

                var props = go.AddComponent<PerObjectMaterialProperties>();
                var so = new SerializedObject(props);
                so.FindProperty("baseColor").colorValue = new Color(
                    0.7f + Random.value * 0.3f,
                    0.7f + Random.value * 0.3f,
                    0.7f + Random.value * 0.3f,
                    1f);
                so.FindProperty("alphaCutoff").floatValue = Mathf.Lerp(0.2f, 0.7f, Random.value);
                so.ApplyModifiedPropertiesWithoutUndo();
            }

            // MeshBall: 1023 GPU-instanced draws (Play Mode)
            var meshBallGo = new GameObject("MeshBall");
            meshBallGo.transform.position = new Vector3(0f, 0f, 12f);
            var meshBall = meshBallGo.AddComponent<MeshBall>();
            var temp = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Mesh sphereMesh = temp.GetComponent<MeshFilter>().sharedMesh;
            Object.DestroyImmediate(temp);

            var meshBallSo = new SerializedObject(meshBall);
            meshBallSo.FindProperty("mesh").objectReferenceValue = sphereMesh;
            meshBallSo.FindProperty("material").objectReferenceValue = mats.Clip;
            meshBallSo.ApplyModifiedPropertiesWithoutUndo();
        }
    }
}
