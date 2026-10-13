#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace CustomSRP.Editor
{
    /// <summary>
    /// 检查并删除 Prefab 中没有有效 Mesh 的 LOD_Billboard
    /// </summary>
    public static class LODBillboardFixUtility
    {
        [MenuItem("CustomSRP/Remove Empty LOD_Billboard")]
        public static void RemoveEmptyBillboardFromAllPrefabs()
        {
            var prefabGuids = AssetDatabase.FindAssets("t:Prefab", new[] { "Assets" });
            int processedCount = 0;
            int removedCount = 0;
            var removedObjects = new List<(string prefab, string name)>();

            foreach (var guid in prefabGuids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null) continue;

                var lodsToRemove = new List<GameObject>();
                FindEmptyLODBillboards(prefab, lodsToRemove);

                if (lodsToRemove.Count > 0)
                {
                    processedCount++;
                    foreach (var go in lodsToRemove)
                    {
                        Debug.Log($"[LODBillboardFix] Removing empty LOD_Billboard from: {path}");
                        Undo.DestroyObjectImmediate(go);
                        removedCount++;
                        removedObjects.Add((path, "LOD_Billboard"));
                    }
                    EditorUtility.SetDirty(prefab);
                }
            }

            AssetDatabase.SaveAssets();

            if (removedCount > 0)
            {
                Debug.Log($"[LODBillboardFix] Done! Processed {processedCount} prefabs, removed {removedCount} empty LOD_Billboards.");
            }
            else
            {
                Debug.Log("[LODBillboardFix] No empty LOD_Billboards found.");
            }
        }

        private static void FindEmptyLODBillboards(GameObject obj, List<GameObject> toRemove)
        {
            if (obj == null) return;

            // 检查是否是 LOD_Billboard
            if (obj.name == "LOD_Billboard")
            {
                // 检查是否有有效的 Mesh
                var meshFilter = obj.GetComponent<MeshFilter>();
                var skinnedMesh = obj.GetComponent<SkinnedMeshRenderer>();

                bool hasValidMesh = false;
                string meshInfo = "none";

                if (meshFilter != null && meshFilter.sharedMesh != null)
                {
                    meshInfo = meshFilter.sharedMesh.name;
                    
                    // 检查 mesh 是否可以被加载
                    var meshPath = AssetDatabase.GetAssetPath(meshFilter.sharedMesh);
                    if (!string.IsNullOrEmpty(meshPath))
                    {
                        hasValidMesh = true;
                    }
                }

                if (skinnedMesh != null && skinnedMesh.sharedMesh != null)
                {
                    meshInfo = $"SkinnedMesh: {skinnedMesh.sharedMesh.name}";
                    var meshPath = AssetDatabase.GetAssetPath(skinnedMesh.sharedMesh);
                    if (!string.IsNullOrEmpty(meshPath))
                    {
                        hasValidMesh = true;
                    }
                }

                if (!hasValidMesh)
                {
                    Debug.Log($"[LODBillboardFix] LOD_Billboard has no valid mesh (mesh: {meshInfo})");
                    toRemove.Add(obj);
                }
            }

            // 递归检查子对象
            foreach (Transform child in obj.transform)
            {
                FindEmptyLODBillboards(child.gameObject, toRemove);
            }
        }

        [MenuItem("CustomSRP/Check All LOD_Billboards")]
        public static void CheckAllLODBillboards()
        {
            var prefabGuids = AssetDatabase.FindAssets("t:Prefab", new[] { "Assets" });
            int foundCount = 0;
            int emptyCount = 0;
            var emptyList = new List<string>();

            foreach (var guid in prefabGuids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null) continue;

                CheckLODBillboards(prefab, path, ref foundCount, ref emptyCount, emptyList);
            }

            Debug.Log($"[LODBillboardFix] Check complete. Found {foundCount} LOD_Billboards, {emptyCount} are empty.");
            foreach (var info in emptyList)
            {
                Debug.LogWarning($"  Empty: {info}");
            }
        }

        private static void CheckLODBillboards(GameObject obj, string prefabPath, ref int foundCount, ref int emptyCount, List<string> emptyList)
        {
            if (obj == null) return;

            if (obj.name == "LOD_Billboard")
            {
                foundCount++;
                var meshFilter = obj.GetComponent<MeshFilter>();
                var skinnedMesh = obj.GetComponent<SkinnedMeshRenderer>();

                bool hasValidMesh = false;
                string meshInfo = "null";

                if (meshFilter != null)
                {
                    if (meshFilter.sharedMesh != null)
                    {
                        meshInfo = meshFilter.sharedMesh.name;
                        var meshPath = AssetDatabase.GetAssetPath(meshFilter.sharedMesh);
                        hasValidMesh = !string.IsNullOrEmpty(meshPath);
                    }
                }

                if (skinnedMesh != null && skinnedMesh.sharedMesh != null)
                {
                    meshInfo = $"Skinned: {skinnedMesh.sharedMesh.name}";
                    var meshPath = AssetDatabase.GetAssetPath(skinnedMesh.sharedMesh);
                    hasValidMesh = !string.IsNullOrEmpty(meshPath);
                }

                if (!hasValidMesh)
                {
                    emptyCount++;
                    emptyList.Add($"{prefabPath} (mesh: {meshInfo})");
                }
            }

            foreach (Transform child in obj.transform)
            {
                CheckLODBillboards(child.gameObject, prefabPath, ref foundCount, ref emptyCount, emptyList);
            }
        }
    }
}
#endif
