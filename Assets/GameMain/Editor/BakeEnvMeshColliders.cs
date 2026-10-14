#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Game.Editor
{
    /// <summary>
    /// 把 DaoYu1 预制体上的 MeshCollider 全部预烘焙成独立可读 Collision Mesh，
    /// 避免真机 Instantiate 时 "mesh has been marked as non-accessible"。
    /// 菜单: Window/Game/Bake Env Mesh Colliders
    /// 批处理: -executeMethod Game.Editor.BakeEnvMeshColliders.BatchBake
    /// </summary>
    public static class BakeEnvMeshColliders
    {
        const string PrefabRoot = "Assets/GameMain/Environment/Prefabs/DaoYu1";
        const string CollisionDir = "Assets/GameMain/Environment/Models/Collision";

        const MeshColliderCookingOptions LeanCooking =
            MeshColliderCookingOptions.CookForFasterSimulation
            | MeshColliderCookingOptions.EnableMeshCleaning
            | MeshColliderCookingOptions.UseFastMidphase;

        [MenuItem("Window/Game/Bake Env Mesh Colliders")]
        public static void MenuBake()
        {
            int n = BakeAll();
            EditorUtility.DisplayDialog(
                "Bake Env Mesh Colliders",
                $"Updated {n} MeshCollider(s).\nOutput: {CollisionDir}",
                "OK");
        }

        /// <summary>供 -executeMethod 批处理。</summary>
        public static void BatchBake()
        {
            int n = BakeAll();
            Debug.Log($"[BakeEnvMeshColliders] BatchBake updated {n} MeshCollider(s)");
        }

        public static int BakeAll()
        {
            EnsureFolder(CollisionDir);

            string[] guids = AssetDatabase.FindAssets("t:Prefab", new[] { PrefabRoot });
            int updated = 0;
            var touchedImporters = new HashSet<string>();

            try
            {
                for (int i = 0; i < guids.Length; i++)
                {
                    string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                    EditorUtility.DisplayProgressBar(
                        "Bake Env Mesh Colliders",
                        path,
                        (float)i / Mathf.Max(1, guids.Length));

                    updated += BakePrefab(path, touchedImporters);
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"[BakeEnvMeshColliders] Done. colliders={updated} prefabs={guids.Length}");
            return updated;
        }

        static int BakePrefab(string prefabPath, HashSet<string> touchedImporters)
        {
            GameObject root = PrefabUtility.LoadPrefabContents(prefabPath);
            int updated = 0;
            bool dirty = false;

            try
            {
                // ShiTou / large rocks may lack a MeshCollider on the prefab — add one
                // before baking so runtime never cooks the non-readable render mesh.
                // Includes size-gated rocks (e.g. SM_Env_Rock_01) that EnvRockColliders
                // would otherwise AddComponent at runtime on device.
                if (IsRockPrefabNeedingBake(prefabPath))
                    dirty |= EnsureLargeRockMeshColliders(root);

                MeshCollider[] colliders = root.GetComponentsInChildren<MeshCollider>(true);
                for (int i = 0; i < colliders.Length; i++)
                {
                    MeshCollider mc = colliders[i];
                    if (mc == null)
                        continue;

                    // Always copy from the render MeshFilter — never from an existing
                    // *_Collision asset (those can be empty after a bad prior bake).
                    Mesh source = ResolveRenderMesh(mc);
                    if (source == null)
                    {
                        Debug.LogWarning(
                            $"[BakeEnvMeshColliders] Missing mesh on {prefabPath} / {GetPath(mc.transform)}. Removing MeshCollider.");
                        Object.DestroyImmediate(mc);
                        dirty = true;
                        updated++;
                        continue;
                    }

                    EnsureModelReadableForCopy(source, touchedImporters);
                    // Re-resolve after possible ModelImporter reimport.
                    source = ResolveRenderMesh(mc);
                    if (source == null || source.vertexCount <= 0)
                    {
                        Debug.LogError(
                            $"[BakeEnvMeshColliders] Source mesh unreadable/empty on {prefabPath} / {GetPath(mc.transform)}");
                        continue;
                    }

                    string baseName = SanitizeFileName(StripCollisionSuffix(source.name));
                    baseName += "_Collision";

                    string assetPath = $"{CollisionDir}/{baseName}.asset";
                    Mesh baked = CreateOrUpdateCollisionMesh(source, assetPath, convex: false);
                    if (baked == null || baked.vertexCount <= 0)
                        continue;

                    if (mc.sharedMesh != baked
                        || mc.cookingOptions != LeanCooking
                        || mc.convex
                        || (mc.sharedMesh != null && mc.sharedMesh.vertexCount <= 0))
                    {
                        mc.sharedMesh = baked;
                        mc.convex = false;
                        mc.cookingOptions = LeanCooking;
                        dirty = true;
                        updated++;
                    }
                }

                if (dirty)
                    PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }

            return updated;
        }

        static bool IsLargeRockPrefabPath(string prefabPath)
        {
            string name = Path.GetFileNameWithoutExtension(prefabPath);
            return name.IndexOf("Cliff", System.StringComparison.OrdinalIgnoreCase) >= 0
                   || name.IndexOf("Cave", System.StringComparison.OrdinalIgnoreCase) >= 0
                   || name.IndexOf("Ramp", System.StringComparison.OrdinalIgnoreCase) >= 0
                   || name.IndexOf("Spike", System.StringComparison.OrdinalIgnoreCase) >= 0
                   || name.IndexOf("Large", System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Prefab paths that must ship a baked readable *_Collision mesh.
        /// All ShiTou + named large rocks — matches runtime <see cref="EnvStreamingConfig.ShouldHaveRockCollider"/>.
        /// </summary>
        static bool IsRockPrefabNeedingBake(string prefabPath)
        {
            if (string.IsNullOrEmpty(prefabPath))
                return false;
            string norm = prefabPath.Replace('\\', '/');
            if (norm.IndexOf("/Prefabs/DaoYu1/ShiTou/", System.StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            return IsLargeRockPrefabPath(prefabPath);
        }

        /// <summary>Add a MeshCollider per MeshFilter when the large-rock prefab has none.</summary>
        static bool EnsureLargeRockMeshColliders(GameObject root)
        {
            if (root.GetComponentInChildren<MeshCollider>(true) != null)
                return false;

            bool dirty = false;
            MeshFilter[] filters = root.GetComponentsInChildren<MeshFilter>(true);
            for (int i = 0; i < filters.Length; i++)
            {
                MeshFilter mf = filters[i];
                if (mf == null || mf.sharedMesh == null)
                    continue;

                MeshCollider mc = mf.gameObject.AddComponent<MeshCollider>();
                mc.sharedMesh = mf.sharedMesh;
                mc.convex = false;
                mc.cookingOptions = LeanCooking;
                dirty = true;
            }

            return dirty;
        }

        /// <summary>Render mesh for baking — skip existing *_Collision collider meshes.</summary>
        static Mesh ResolveRenderMesh(MeshCollider mc)
        {
            MeshFilter mf = mc.GetComponent<MeshFilter>();
            if (mf != null && mf.sharedMesh != null && !IsCollisionMeshAsset(mf.sharedMesh)
                && mf.sharedMesh.vertexCount > 0)
                return mf.sharedMesh;

            // Collider may sit on a sibling of LOD MeshFilters — search whole prefab root.
            Transform root = mc.transform.root;
            MeshFilter[] filters = root != null
                ? root.GetComponentsInChildren<MeshFilter>(true)
                : mc.GetComponentsInChildren<MeshFilter>(true);

            Mesh best = null;
            int bestVerts = 0;
            for (int i = 0; i < filters.Length; i++)
            {
                Mesh mesh = filters[i] != null ? filters[i].sharedMesh : null;
                if (mesh == null || IsCollisionMeshAsset(mesh) || mesh.vertexCount <= 0)
                    continue;
                // Prefer highest-detail (most verts) render mesh for cooking.
                if (mesh.vertexCount > bestVerts)
                {
                    best = mesh;
                    bestVerts = mesh.vertexCount;
                }
            }

            if (best != null)
                return best;

            Mesh shared = mc.sharedMesh;
            if (shared != null && !IsCollisionMeshAsset(shared) && shared.vertexCount > 0)
                return shared;

            return null;
        }

        static bool IsCollisionMeshAsset(Mesh mesh)
        {
            if (mesh == null)
                return false;
            if (mesh.name.EndsWith("_Collision", System.StringComparison.Ordinal))
                return true;
            string path = AssetDatabase.GetAssetPath(mesh);
            return !string.IsNullOrEmpty(path)
                   && path.Replace('\\', '/').IndexOf("/Models/Collision/", System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static string StripCollisionSuffix(string name)
        {
            if (string.IsNullOrEmpty(name))
                return "Mesh";
            if (name.EndsWith("_Collision", System.StringComparison.Ordinal))
                return name.Substring(0, name.Length - "_Collision".Length);
            return name;
        }

        static Mesh CreateOrUpdateCollisionMesh(Mesh source, string assetPath, bool convex)
        {
            // Never DeleteAsset while prefabs still reference the mesh — that destroys
            // in-memory MeshColliders mid-bake. Overwrite in place instead.
            Vector3[] verts;
            int[] tris;
            Vector3[] normals;
            try
            {
                verts = source.vertices;
                tris = source.triangles;
                normals = source.normals;
            }
            catch (System.Exception ex)
            {
                Debug.LogError(
                    $"[BakeEnvMeshColliders] Cannot read mesh '{source.name}' for {assetPath}: {ex.Message}");
                return null;
            }

            if (verts == null || verts.Length == 0 || tris == null || tris.Length == 0)
            {
                Debug.LogError(
                    $"[BakeEnvMeshColliders] Source mesh '{source.name}' has no geometry for {assetPath}");
                return null;
            }

            Mesh existing = AssetDatabase.LoadAssetAtPath<Mesh>(assetPath);
            Mesh dst;
            if (existing != null)
            {
                dst = existing;
                dst.Clear();
            }
            else
            {
                dst = new Mesh { name = Path.GetFileNameWithoutExtension(assetPath) };
            }

            dst.indexFormat = source.indexFormat;
            dst.vertices = verts;
            dst.triangles = tris;
            if (normals != null && normals.Length == verts.Length)
                dst.normals = normals;
            dst.bounds = source.bounds;
            dst.RecalculateBounds();

            if (existing == null)
                AssetDatabase.CreateAsset(dst, assetPath);

            var so = new SerializedObject(dst);
            SerializedProperty readable = so.FindProperty("m_IsReadable");
            if (readable != null)
            {
                readable.boolValue = true;
                so.ApplyModifiedPropertiesWithoutUndo();
            }

            try
            {
                Physics.BakeMesh(dst.GetEntityId(), convex, LeanCooking);
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning(
                    $"[BakeEnvMeshColliders] Physics.BakeMesh skipped for {assetPath}: {ex.Message}");
            }

            EditorUtility.SetDirty(dst);
            return dst;
        }

        static void EnsureModelReadableForCopy(Mesh source, HashSet<string> touchedImporters)
        {
            string path = AssetDatabase.GetAssetPath(source);
            if (string.IsNullOrEmpty(path))
                return;

            // Already a Mesh .asset — flip readable flag if needed.
            if (path.EndsWith(".asset", System.StringComparison.OrdinalIgnoreCase))
            {
                if (!source.isReadable)
                {
                    // Force readable via SerializedObject on the mesh asset.
                    var so = new SerializedObject(source);
                    SerializedProperty p = so.FindProperty("m_IsReadable");
                    if (p != null)
                    {
                        p.boolValue = true;
                        so.ApplyModifiedPropertiesWithoutUndo();
                        EditorUtility.SetDirty(source);
                        AssetDatabase.SaveAssetIfDirty(source);
                    }
                }

                return;
            }

            var importer = AssetImporter.GetAtPath(path) as ModelImporter;
            if (importer == null)
                return;

            bool needReimport = false;
            if (!importer.isReadable)
            {
                importer.isReadable = true;
                needReimport = true;
            }

            // Also enable ModelImporter pre-bake for any future direct MeshCollider use.
            // Unity 6 ModelImporter exposes these as serialized fields.
            var soImp = new SerializedObject(importer);
            SerializedProperty preConvex = soImp.FindProperty("preBakeConvexCollisionMesh");
            SerializedProperty preTri = soImp.FindProperty("preBakeTriangleCollisionMesh");
            if (preConvex != null && !preConvex.boolValue)
            {
                preConvex.boolValue = true;
                needReimport = true;
            }

            if (preTri != null && !preTri.boolValue)
            {
                preTri.boolValue = true;
                needReimport = true;
            }

            if (needReimport && touchedImporters.Add(path))
            {
                soImp.ApplyModifiedPropertiesWithoutUndo();
                importer.SaveAndReimport();
            }
            else if (needReimport)
            {
                soImp.ApplyModifiedPropertiesWithoutUndo();
            }
        }

        static void EnsureFolder(string assetFolder)
        {
            if (AssetDatabase.IsValidFolder(assetFolder))
                return;

            string parent = Path.GetDirectoryName(assetFolder)?.Replace('\\', '/');
            string name = Path.GetFileName(assetFolder);
            if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(name))
                return;

            if (!AssetDatabase.IsValidFolder(parent))
                EnsureFolder(parent);

            AssetDatabase.CreateFolder(parent, name);
        }

        static string SanitizeFileName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return "Mesh";

            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name.Trim();
        }

        static string GetPath(Transform t)
        {
            if (t.parent == null)
                return t.name;
            return GetPath(t.parent) + "/" + t.name;
        }
    }
}
#endif
