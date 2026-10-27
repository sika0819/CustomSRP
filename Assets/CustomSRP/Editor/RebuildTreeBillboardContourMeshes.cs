#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace CustomSRP.Editor
{
    /// <summary>
    /// Rebuild LOD_Billboard meshes as alpha contour grids (Boluo-style) so
    /// OilNPR can skip alpha clip without rectangular black cutout fringes.
    /// </summary>
    public static class RebuildTreeBillboardContourMeshes
    {
        const string PrefabsRoot = "Assets/GameMain/Environment/Prefabs/DaoYu1";
        static readonly string[] Folders = { "Songshu", "Lvshu" };
        const string FlagRelPath = "Temp/rebuild_tree_billboard_contour.flag";

        static bool _flagQueued;

        [InitializeOnLoadMethod]
        static void WatchFlag()
        {
            EditorApplication.update -= PollFlag;
            EditorApplication.update += PollFlag;
        }

        static void PollFlag()
        {
            if (_flagQueued || EditorApplication.isCompiling || EditorApplication.isUpdating
                || EditorApplication.isPlaying)
                return;

            string flag = Path.GetFullPath(Path.Combine(Application.dataPath, "..", FlagRelPath));
            if (!File.Exists(flag))
                return;
            try { File.Delete(flag); } catch { return; }

            _flagQueued = true;
            try
            {
                ExecuteMethodEntry();
            }
            finally
            {
                _flagQueued = false;
            }
        }

        [MenuItem("Window/Game/Rebuild Tree Billboard Contour Meshes")]
        public static void MenuRebuild()
        {
            int ok = RebuildAll(out string report);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            EditorUtility.DisplayDialog(
                "Rebuild Billboard Contours",
                $"完成 {ok} 棵。\n\n{report}",
                "OK");
        }

        /// <summary>Batchmode / flag: -executeMethod CustomSRP.Editor.RebuildTreeBillboardContourMeshes.ExecuteMethodEntry</summary>
        public static void ExecuteMethodEntry()
        {
            int ok = RebuildAll(out string report);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"[RebuildTreeBillboardContourMeshes] done ok={ok}\n{report}");
        }

        public static int RebuildAll(out string report)
        {
            var sb = new StringBuilder();
            var paths = CollectMeshPaths();
            int ok = 0;
            for (int i = 0; i < paths.Count; i++)
            {
                string meshPath = paths[i];
                string name = Path.GetFileNameWithoutExtension(meshPath);
                EditorUtility.DisplayProgressBar(
                    "Rebuild Billboard Contours",
                    name,
                    (i + 1f) / Mathf.Max(1, paths.Count));
                try
                {
                    if (RebuildOne(meshPath, sb))
                        ok++;
                }
                catch (System.Exception ex)
                {
                    sb.AppendLine($"FAIL {name}: {ex.Message}");
                    Debug.LogException(ex);
                }
            }

            EditorUtility.ClearProgressBar();
            sb.AppendLine($"OK {ok}/{paths.Count}");
            report = sb.ToString();
            return ok;
        }

        static List<string> CollectMeshPaths()
        {
            var list = new List<string>(32);
            for (int f = 0; f < Folders.Length; f++)
            {
                string dir = $"{PrefabsRoot}/{Folders[f]}";
                if (!Directory.Exists(dir))
                    continue;
                string[] files = Directory.GetFiles(
                    dir, "*BillboardMesh.asset", SearchOption.AllDirectories);
                for (int i = 0; i < files.Length; i++)
                {
                    string path = files[i].Replace('\\', '/');
                    int idx = path.IndexOf("Assets/", System.StringComparison.Ordinal);
                    if (idx >= 0)
                        path = path.Substring(idx);
                    list.Add(path);
                }
            }

            list.Sort();
            return list;
        }

        static bool RebuildOne(string meshPath, StringBuilder sb)
        {
            string folder = Path.GetDirectoryName(meshPath)?.Replace('\\', '/') ?? "";
            string baseName = Path.GetFileNameWithoutExtension(meshPath);
            // "Grand Fir 1_BillboardMesh" → "Grand Fir 1_Billboard_Albedo.png"
            string albedoName = baseName.EndsWith("BillboardMesh")
                ? baseName.Substring(0, baseName.Length - "Mesh".Length) + "_Albedo.png"
                : baseName + "_Albedo.png";
            string albedoPath = $"{folder}/{albedoName}";

            var existing = AssetDatabase.LoadAssetAtPath<Mesh>(meshPath);
            if (existing == null)
            {
                sb.AppendLine($"FAIL load mesh {meshPath}");
                return false;
            }

            Texture2D albedo = LoadReadableAlbedo(albedoPath);
            if (albedo == null)
            {
                sb.AppendLine($"FAIL albedo {albedoPath}");
                return false;
            }

            try
            {
                if (!OilTreeBillboardContourMesh.TryRecoverFullQuad(
                        existing, out Vector2 quadSize, out float yPivot))
                {
                    sb.AppendLine($"FAIL bounds {baseName}");
                    return false;
                }

                // Atlases are already dilated. Re-running dilate only bleeds RGB
                // further and rewrites every PNG. Mesh rebuild reads alpha only.
                Mesh generated = OilTreeBillboardContourMesh.Build(
                    albedo, quadSize, yPivot, OilTreeBillboardContourMesh.DefaultCutoff);
                if (generated == null || generated.vertexCount < 4)
                {
                    sb.AppendLine($"FAIL empty contour {baseName}");
                    if (generated != null)
                        Object.DestroyImmediate(generated);
                    return false;
                }

                generated.name = existing.name;
                EditorUtility.CopySerialized(generated, existing);
                existing.name = generated.name;
                Object.DestroyImmediate(generated);
                EditorUtility.SetDirty(existing);

                sb.AppendLine(
                    $"OK {baseName} verts={existing.vertexCount} size={quadSize.x:F1}x{quadSize.y:F1}");
                return true;
            }
            finally
            {
                Object.DestroyImmediate(albedo);
            }
        }

        static Texture2D LoadReadableAlbedo(string assetPath)
        {
            if (string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(assetPath)))
                return null;

            var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            bool prevReadable = false;
            var prevCompression = TextureImporterCompression.Compressed;
            bool touched = false;
            if (importer != null)
            {
                prevReadable = importer.isReadable;
                prevCompression = importer.textureCompression;
                if (!importer.isReadable)
                {
                    importer.isReadable = true;
                    touched = true;
                }

                if (importer.textureCompression != TextureImporterCompression.Uncompressed)
                {
                    importer.textureCompression = TextureImporterCompression.Uncompressed;
                    touched = true;
                }

                if (touched)
                    importer.SaveAndReimport();
            }

            var src = AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
            if (src == null)
                return null;

            var copy = new Texture2D(src.width, src.height, TextureFormat.RGBA32, false, false);
            copy.SetPixels32(src.GetPixels32());
            copy.Apply(false, false);

            if (importer != null && touched)
            {
                importer.isReadable = prevReadable;
                importer.textureCompression = prevCompression;
                importer.SaveAndReimport();
            }

            return copy;
        }
    }
}
#endif
