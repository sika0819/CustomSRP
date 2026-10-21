#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Game.Editor
{
    /// <summary>
    /// 把 CanoeExtracted 里已拆好的 OBJ（米制 + 重展 UV）拷成 Mesh .asset，并接到独木舟 Prefab。
    /// 菜单: Window/Game/Wire Extracted Canoe
    /// 触发文件: Temp/run_canoe_extract
    /// </summary>
    public static class ExtractCanoeAtlasTool
    {
        const string OutDir = "Assets/GameMain/Environment/Models/CanoeExtracted";
        const string PrefabPath = "Assets/GameMain/Environment/Prefabs/DaoYu1/Other/SM_Veh_Canoe_Animated.prefab";
        const string TriggerPath = "Temp/run_canoe_extract";

        const string BodyName = "SM_Veh_Outrigger_Canoe_01";
        const string CanopyName = "SM_Veh_Outrigger_Canoe_01_Canopy_01";

        // Unity FBX useFileScale: Synty cm → metres
        const float FileScale = 0.01f;

        static bool s_Watching;

        [InitializeOnLoadMethod]
        static void Boot()
        {
            if (s_Watching)
                return;
            s_Watching = true;
            EditorApplication.update += WatchTrigger;
            EditorApplication.delayCall += TryRunTrigger;
        }

        static void WatchTrigger()
        {
            if (!File.Exists(TriggerPath))
                return;
            TryRunTrigger();
        }

        static void TryRunTrigger()
        {
            if (!File.Exists(TriggerPath))
                return;
            try { File.Delete(TriggerPath); }
            catch { return; }

            try
            {
                string report;
                if (File.Exists($"{OutDir}/{BodyName}.obj"))
                {
                    try { report = Wire(); }
                    catch (System.Exception wireEx)
                    {
                        Debug.LogWarning($"[ExtractCanoeAtlas] Wire failed ({wireEx.Message}); falling back to scale fix.");
                        report = FixScaleOnly();
                    }
                }
                else
                {
                    report = FixScaleOnly();
                }

                Debug.Log($"[ExtractCanoeAtlas] OK\n{report}");
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[ExtractCanoeAtlas] Failed: {ex}");
            }
        }

        [MenuItem("Window/Game/Wire Extracted Canoe")]
        public static void MenuWire()
        {
            string report = Wire();
            EditorUtility.DisplayDialog("Wire Extracted Canoe", report, "OK");
        }

        [MenuItem("Window/Game/Fix Canoe Mesh Scale (x0.01)")]
        public static void MenuFixScale()
        {
            string report = FixScaleOnly();
            EditorUtility.DisplayDialog("Fix Canoe Mesh Scale", report, "OK");
        }

        public static string FixScaleOnly()
        {
            int n = 0;
            n += ScaleAssetIfHuge(BodyName) ? 1 : 0;
            n += ScaleAssetIfHuge(CanopyName) ? 1 : 0;
            RefreshPrefabLod();
            AssetDatabase.SaveAssets();
            return $"Scaled {n} mesh asset(s) by {FileScale}.";
        }

        public static string Wire()
        {
            EnsureObjMetas();

            AssetDatabase.ImportAsset($"{OutDir}/{BodyName}.obj", ImportAssetOptions.ForceUpdate);
            AssetDatabase.ImportAsset($"{OutDir}/{CanopyName}.obj", ImportAssetOptions.ForceUpdate);
            AssetDatabase.ImportAsset($"{OutDir}/{BodyName}.png", ImportAssetOptions.ForceUpdate);
            AssetDatabase.ImportAsset($"{OutDir}/{CanopyName}.png", ImportAssetOptions.ForceUpdate);

            Mesh bodyMesh = CopyObjToAsset(BodyName);
            Mesh canopyMesh = CopyObjToAsset(CanopyName);

            // Safety: if OBJ somehow still in cm, bake down to metres.
            ScaleMeshIfHuge(bodyMesh);
            ScaleMeshIfHuge(canopyMesh);

            Material bodyMat = EnsureMaterial(BodyName);
            Material canopyMat = EnsureMaterial(CanopyName);

            UpdatePrefab(bodyMesh, canopyMesh, bodyMat, canopyMat);

            DeleteIfExists($"{OutDir}/{BodyName}.obj");
            DeleteIfExists($"{OutDir}/{CanopyName}.obj");
            DeleteIfExists($"{OutDir}/{BodyName}.mtl");
            DeleteIfExists($"{OutDir}/{CanopyName}.mtl");
            DeleteIfExists($"{OutDir}/_extract_guids.txt");

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            return $"Wired prefab → {PrefabPath}\n" +
                   $"Body bounds size={bodyMesh.bounds.size} ({bodyMesh.vertexCount} verts)\n" +
                   $"Canopy bounds size={canopyMesh.bounds.size} ({canopyMesh.vertexCount} verts)";
        }

        static void EnsureObjMetas()
        {
            // Unity needs .meta present; if missing, ImportAsset still works after Refresh.
            AssetDatabase.Refresh();
        }

        static Mesh CopyObjToAsset(string name)
        {
            string objPath = $"{OutDir}/{name}.obj";
            Mesh src = null;
            foreach (Object o in AssetDatabase.LoadAllAssetsAtPath(objPath))
            {
                if (o is Mesh m)
                {
                    src = m;
                    break;
                }
            }

            if (src == null)
                throw new System.InvalidOperationException($"No mesh in {objPath}. Wait for import.");

            string assetPath = $"{OutDir}/{name}.asset";
            Mesh dst = AssetDatabase.LoadAssetAtPath<Mesh>(assetPath);
            if (dst == null)
            {
                dst = Object.Instantiate(src);
                dst.name = name;
                AssetDatabase.CreateAsset(dst, assetPath);
            }
            else
            {
                dst.Clear();
                EditorUtility.CopySerialized(src, dst);
                dst.name = name;
            }

            var so = new SerializedObject(dst);
            SerializedProperty readable = so.FindProperty("m_IsReadable");
            if (readable != null)
            {
                readable.boolValue = true;
                so.ApplyModifiedPropertiesWithoutUndo();
            }

            EditorUtility.SetDirty(dst);
            return dst;
        }

        static bool ScaleAssetIfHuge(string name)
        {
            string assetPath = $"{OutDir}/{name}.asset";
            Mesh mesh = AssetDatabase.LoadAssetAtPath<Mesh>(assetPath);
            if (mesh == null)
                return false;
            bool scaled = ScaleMeshIfHuge(mesh);
            if (scaled)
                EditorUtility.SetDirty(mesh);
            return scaled;
        }

        static bool ScaleMeshIfHuge(Mesh mesh)
        {
            Vector3 size = mesh.bounds.size;
            float longest = Mathf.Max(size.x, Mathf.Max(size.y, size.z));
            // Original canoe length ≈ 5.6m; cm-space is ≈ 560.
            if (longest < 50f)
                return false;

            Vector3[] verts = mesh.vertices;
            for (int i = 0; i < verts.Length; i++)
                verts[i] *= FileScale;
            mesh.vertices = verts;
            mesh.RecalculateBounds();
            mesh.RecalculateNormals();
            return true;
        }

        static Material EnsureMaterial(string name)
        {
            string matPath = $"{OutDir}/{name}.mat";
            string texPath = $"{OutDir}/{name}.png";
            Texture2D tex = AssetDatabase.LoadAssetAtPath<Texture2D>(texPath);
            Material mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            Material source = AssetDatabase.LoadAssetAtPath<Material>(
                "Assets/GameMain/Environment/Materials/Zawu_01.mat");

            if (mat == null)
            {
                mat = source != null ? new Material(source) : new Material(Shader.Find("Custom RP/Oil NPR"));
                mat.name = name;
                AssetDatabase.CreateAsset(mat, matPath);
            }
            else if (source != null)
            {
                mat.shader = source.shader;
                mat.CopyPropertiesFromMaterial(source);
            }

            if (tex != null)
            {
                if (mat.HasProperty("_BaseMap"))
                    mat.SetTexture("_BaseMap", tex);
                if (mat.HasProperty("_MainTex"))
                    mat.SetTexture("_MainTex", tex);
            }

            if (mat.HasProperty("_UseBaseMap"))
                mat.SetFloat("_UseBaseMap", 1f);
            if (mat.HasProperty("_Clipping"))
                mat.SetFloat("_Clipping", 0f);
            if (mat.HasProperty("_AlphaClip"))
                mat.SetFloat("_AlphaClip", 0f);
            mat.DisableKeyword("_CLIPPING");
            mat.DisableKeyword("_SHADOWS_CLIP");

            EditorUtility.SetDirty(mat);
            return mat;
        }

        static void UpdatePrefab(Mesh bodyMesh, Mesh canopyMesh, Material bodyMat, Material canopyMat)
        {
            GameObject root = PrefabUtility.LoadPrefabContents(PrefabPath);
            try
            {
                foreach (MeshFilter mf in root.GetComponentsInChildren<MeshFilter>(true))
                {
                    if (mf.gameObject.name.Contains("Canopy"))
                        mf.sharedMesh = canopyMesh;
                    else
                        mf.sharedMesh = bodyMesh;
                }

                foreach (MeshRenderer mr in root.GetComponentsInChildren<MeshRenderer>(true))
                {
                    if (mr.gameObject.name.Contains("Canopy"))
                        mr.sharedMaterial = canopyMat;
                    else
                        mr.sharedMaterial = bodyMat;
                }

                var lod = root.GetComponent<LODGroup>();
                if (lod != null)
                    lod.RecalculateBounds();

                PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        static void RefreshPrefabLod()
        {
            GameObject root = PrefabUtility.LoadPrefabContents(PrefabPath);
            try
            {
                var lod = root.GetComponent<LODGroup>();
                if (lod != null)
                    lod.RecalculateBounds();
                PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        static void DeleteIfExists(string assetPath)
        {
            if (File.Exists(assetPath) || AssetDatabase.LoadMainAssetAtPath(assetPath) != null)
                AssetDatabase.DeleteAsset(assetPath);
        }
    }
}
#endif
