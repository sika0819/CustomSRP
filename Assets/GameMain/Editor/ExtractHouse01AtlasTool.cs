#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Game.Editor
{
    /// <summary>
    /// 把 HouseExtracted 里重展 UV 后的 OBJ + House_01.png 接到 House_01 Prefab。
    /// 菜单: Window/Game/Wire Extracted House_01
    /// 触发: Temp/run_house01_extract
    /// </summary>
    public static class ExtractHouse01AtlasTool
    {
        const string OutDir = "Assets/GameMain/Environment/Models/HouseExtracted";
        const string PrefabPath = "Assets/GameMain/Environment/Prefabs/DaoYu1/JianZhu/House_01.prefab";
        const string TriggerPath = "Temp/run_house01_extract";
        const string MeshName = "House_01";
        const string SourceMatPath = "Assets/GameMain/Environment/Materials/Zawu_01.mat";
        const string MatPath = OutDir + "/House_01.mat";
        const string TexPath = OutDir + "/House_01.png";
        const string ObjPath = OutDir + "/House_01.obj";
        const string AssetPath = OutDir + "/House_01.asset";

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
                string report = Wire();
                Debug.Log($"[ExtractHouse01] OK\n{report}");
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[ExtractHouse01] Failed: {ex}");
            }
        }

        [MenuItem("Window/Game/Wire Extracted House_01")]
        public static void MenuWire()
        {
            string report = Wire();
            EditorUtility.DisplayDialog("Wire Extracted House_01", report, "OK");
        }

        public static string Wire()
        {
            if (!File.Exists(ObjPath))
                throw new System.InvalidOperationException($"Missing {ObjPath}");
            if (!File.Exists(TexPath))
                throw new System.InvalidOperationException($"Missing {TexPath}");

            AssetDatabase.ImportAsset(ObjPath, ImportAssetOptions.ForceUpdate);
            AssetDatabase.ImportAsset(TexPath, ImportAssetOptions.ForceUpdate);

            Mesh mesh = CopyObjToAsset();
            Material mat = EnsureMaterial();
            UpdatePrefab(mesh, mat);

            DeleteIfExists(ObjPath);
            DeleteIfExists(OutDir + "/House_01.mtl");

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            return $"Wired → {PrefabPath}\n" +
                   $"mesh bounds={mesh.bounds.size} verts={mesh.vertexCount} tris={mesh.triangles.Length / 3}\n" +
                   $"mat {MatPath} tex {TexPath}";
        }

        static Mesh CopyObjToAsset()
        {
            Mesh src = null;
            foreach (Object o in AssetDatabase.LoadAllAssetsAtPath(ObjPath))
            {
                if (o is Mesh m)
                {
                    src = m;
                    break;
                }
            }

            if (src == null)
                throw new System.InvalidOperationException($"No mesh in {ObjPath}");

            Mesh dst = AssetDatabase.LoadAssetAtPath<Mesh>(AssetPath);
            if (dst == null)
            {
                dst = Object.Instantiate(src);
                dst.name = MeshName;
                AssetDatabase.CreateAsset(dst, AssetPath);
            }
            else
            {
                dst.Clear();
                EditorUtility.CopySerialized(src, dst);
                dst.name = MeshName;
            }

            EnsureReadable(dst);
            EditorUtility.SetDirty(dst);
            return dst;
        }

        static void EnsureReadable(Mesh mesh)
        {
            var so = new SerializedObject(mesh);
            SerializedProperty readable = so.FindProperty("m_IsReadable");
            if (readable != null && !readable.boolValue)
            {
                readable.boolValue = true;
                so.ApplyModifiedPropertiesWithoutUndo();
            }
        }

        static Material EnsureMaterial()
        {
            Texture2D tex = AssetDatabase.LoadAssetAtPath<Texture2D>(TexPath);
            Material source = AssetDatabase.LoadAssetAtPath<Material>(SourceMatPath);
            Material mat = AssetDatabase.LoadAssetAtPath<Material>(MatPath);
            if (mat == null)
            {
                mat = source != null ? new Material(source) : new Material(Shader.Find("Custom RP/Oil NPR"));
                mat.name = MeshName;
                AssetDatabase.CreateAsset(mat, MatPath);
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

            if (mat.HasProperty("_BumpMap"))
                mat.SetTexture("_BumpMap", null);
            if (mat.HasProperty("_UseBaseMap"))
                mat.SetFloat("_UseBaseMap", 1f);
            if (mat.HasProperty("_Clipping"))
                mat.SetFloat("_Clipping", 0f);
            if (mat.HasProperty("_AlphaClip"))
                mat.SetFloat("_AlphaClip", 0f);
            mat.DisableKeyword("_CLIPPING");
            mat.DisableKeyword("_SHADOWS_CLIP");
            mat.DisableKeyword("_ALPHATEST_ON");

            EditorUtility.SetDirty(mat);
            return mat;
        }

        static void UpdatePrefab(Mesh mesh, Material mat)
        {
            GameObject root = PrefabUtility.LoadPrefabContents(PrefabPath);
            try
            {
                for (int i = root.transform.childCount - 1; i >= 0; i--)
                    Object.DestroyImmediate(root.transform.GetChild(i).gameObject);
                foreach (var c in root.GetComponents<Collider>())
                    Object.DestroyImmediate(c);

                var lodGo = new GameObject("House_01_LOD0");
                lodGo.transform.SetParent(root.transform, false);
                var mf = lodGo.AddComponent<MeshFilter>();
                mf.sharedMesh = mesh;
                var mr = lodGo.AddComponent<MeshRenderer>();
                mr.sharedMaterial = mat;

                var box = root.AddComponent<BoxCollider>();
                box.center = mesh.bounds.center;
                box.size = mesh.bounds.size;

                var meshCol = root.AddComponent<MeshCollider>();
                meshCol.sharedMesh = mesh;
                meshCol.convex = false;

                var lod = root.GetComponent<LODGroup>();
                if (lod == null)
                    lod = root.AddComponent<LODGroup>();
                lod.SetLODs(new[] { new LOD(0.05f, new Renderer[] { mr }) });
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
