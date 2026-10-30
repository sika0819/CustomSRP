using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Game.Editor
{
    /// <summary>
    /// 把片树 BillboardMesh 的「不透明轮廓」对齐到近树 LOD0 包围盒，
    /// 使 Full↔Billboard 切换时高度/宽度轮廓一致。只改 mesh 顶点与 bounds，不改贴图 UV。
    /// 菜单: Window > Game > Fit Tree Billboard Opaque To Near Bounds
    /// 批处理: -executeMethod Game.Editor.FitTreeBillboardOpaqueToNearBounds.ExecuteMethodEntry
    /// </summary>
    public static class FitTreeBillboardOpaqueToNearBounds
    {
        const string PrefabRoot = "Assets/GameMain/Environment/Prefabs/DaoYu1";
        static readonly string[] Folders = { "Songshu", "Lvshu" };
        const float AlphaThreshold = 10f / 255f;
        /// <summary>相对近树尺寸允许的误差；超过才改 mesh。</summary>
        const float FixEpsilon = 0.02f;

        [MenuItem("Window/Game/Fit Tree Billboard Opaque To Near Bounds")]
        public static void MenuFit()
        {
            string report = FitAll(dryRun: false);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            EditorUtility.DisplayDialog("Fit Billboard Opaque", report, "OK");
        }

        [MenuItem("Window/Game/Fit Tree Billboard Opaque To Near Bounds (Dry Run)")]
        public static void MenuDryRun()
        {
            string report = FitAll(dryRun: true);
            EditorUtility.DisplayDialog("Fit Billboard Opaque (Dry Run)", report, "OK");
        }

        /// <summary>Batchmode entry.</summary>
        public static void ExecuteMethodEntry()
        {
            string report = FitAll(dryRun: false);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"[FitBbOpaque]\n{report}");
            if (Application.isBatchMode)
                EditorApplication.Exit(0);
        }

        public static string FitAll(bool dryRun)
        {
            var sb = new StringBuilder();
            int checkedCount = 0;
            int fixedCount = 0;
            int skipped = 0;

            foreach (string folder in Folders)
            {
                string dir = $"{PrefabRoot}/{folder}";
                if (!Directory.Exists(dir))
                    continue;

                string[] guids = AssetDatabase.FindAssets("t:Prefab", new[] { dir });
                for (int g = 0; g < guids.Length; g++)
                {
                    string path = AssetDatabase.GUIDToAssetPath(guids[g]);
                    if (string.IsNullOrEmpty(path) || !path.EndsWith(".prefab"))
                        continue;

                    checkedCount++;
                    int status = TryFitOne(path, dryRun, out string line);
                    if (!string.IsNullOrEmpty(line))
                        sb.AppendLine(line);
                    if (status > 0)
                        fixedCount++;
                    else if (status < 0)
                        skipped++;
                }
            }

            sb.Insert(0,
                $"{(dryRun ? "dryRun" : "done")}: checked={checkedCount} fixed={fixedCount} skipped={skipped}\n");
            Debug.Log($"[FitBbOpaque]\n{sb}");
            return sb.ToString();
        }

        /// <returns>1 fixed / would-fix, 0 already ok, -1 skipped/error.</returns>
        static int TryFitOne(string prefabPath, bool dryRun, out string line)
        {
            line = null;
            string name = Path.GetFileNameWithoutExtension(prefabPath);
            var root = PrefabUtility.LoadPrefabContents(prefabPath);
            if (root == null)
            {
                line = $"skip  {name}: load fail";
                return -1;
            }

            try
            {
                Transform bb = FindNamed(root.transform, "LOD_Billboard");
                if (bb == null)
                {
                    line = $"skip  {name}: no LOD_Billboard";
                    return -1;
                }

                var filter = bb.GetComponent<MeshFilter>();
                var mr = bb.GetComponent<MeshRenderer>();
                if (filter == null || filter.sharedMesh == null || mr == null || mr.sharedMaterial == null)
                {
                    line = $"skip  {name}: missing mesh/mat";
                    return -1;
                }

                Mesh mesh = filter.sharedMesh;
                string meshPath = AssetDatabase.GetAssetPath(mesh);
                if (string.IsNullOrEmpty(meshPath) || !meshPath.EndsWith(".asset"))
                {
                    line = $"skip  {name}: mesh not a .asset";
                    return -1;
                }

                if (!TryNearBounds(root, bb, out Bounds nearBounds))
                {
                    line = $"skip  {name}: no near bounds";
                    return -1;
                }

                Texture2D albedo = mr.sharedMaterial.GetTexture("_BaseMap") as Texture2D
                    ?? mr.sharedMaterial.mainTexture as Texture2D;
                if (albedo == null)
                {
                    line = $"skip  {name}: no albedo";
                    return -1;
                }

                if (!TryOpaqueUvRect(albedo, out Rect opaqueUv))
                {
                    line = $"skip  {name}: no opaque in left atlas half";
                    return -1;
                }

                Vector3[] verts = mesh.vertices;
                Vector2[] uvs = mesh.uv;
                if (verts == null || uvs == null || verts.Length < 4 || uvs.Length != verts.Length)
                {
                    line = $"skip  {name}: bad verts/uvs count={verts?.Length}";
                    return -1;
                }

                if (!TryUvToWorldExtents(verts, uvs, opaqueUv,
                        out float curOpqYMin, out float curOpqYMax,
                        out _, out _))
                {
                    line = $"skip  {name}: uv map fail";
                    return -1;
                }

                // Only match height. AABB width is a 3D box diagonal and would widen
                // the front silhouette; keep aspect via uniform XY scale.
                float nearYMin = nearBounds.min.y;
                float nearYMax = nearBounds.max.y;
                float nearH = nearYMax - nearYMin;
                float curH = curOpqYMax - curOpqYMin;
                float dH = Mathf.Abs(curH - nearH);
                float dY0 = Mathf.Abs(curOpqYMin - nearYMin);
                if (dH <= FixEpsilon && dY0 <= FixEpsilon)
                {
                    line = $"ok    {name} opqH={curH:F2} nearH={nearH:F2}";
                    return 0;
                }

                if (curH < 1e-5f)
                {
                    line = $"skip  {name}: degenerate opaque height";
                    return -1;
                }

                float s = nearH / curH;
                float ty = nearYMin - curOpqYMin * s;
                Vector3[] newVerts = new Vector3[verts.Length];
                float minY = float.PositiveInfinity;
                float maxY = float.NegativeInfinity;
                float radius = 0f;
                for (int i = 0; i < verts.Length; i++)
                {
                    float x = verts[i].x * s;
                    float y = verts[i].y * s + ty;
                    newVerts[i] = new Vector3(x, y, 0f);
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                    radius = Mathf.Max(radius, Mathf.Abs(x));
                }

                line =
                    $"{(dryRun ? "would" : "fix")}  {name} " +
                    $"opqH {curH:F2}->{nearH:F2} " +
                    $"y0 {curOpqYMin:F2}->{nearYMin:F2} scale={s:F4}";

                if (dryRun)
                    return 1;

                Mesh assetMesh = AssetDatabase.LoadAssetAtPath<Mesh>(meshPath);
                if (assetMesh == null)
                {
                    line = $"skip  {name}: reload mesh asset fail";
                    return -1;
                }

                assetMesh.vertices = newVerts;
                assetMesh.bounds = new Bounds(
                    new Vector3(0f, (minY + maxY) * 0.5f, 0f),
                    new Vector3(radius * 2f, Mathf.Max(0.01f, maxY - minY), radius * 2f));
                EditorUtility.SetDirty(assetMesh);
                filter.sharedMesh = assetMesh;
                PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
                return 1;
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        static bool TryNearBounds(GameObject root, Transform billboard, out Bounds bounds)
        {
            bounds = default;
            bool any = false;

            // Prefer LOD0 renderers when LODGroup exists.
            var lod = root.GetComponentInChildren<LODGroup>(true);
            var keep = new HashSet<Renderer>();
            if (lod != null)
            {
                var lods = lod.GetLODs();
                if (lods != null && lods.Length > 0 && lods[0].renderers != null)
                {
                    for (int i = 0; i < lods[0].renderers.Length; i++)
                    {
                        if (lods[0].renderers[i] != null)
                            keep.Add(lods[0].renderers[i]);
                    }
                }
            }

            var mrs = root.GetComponentsInChildren<MeshRenderer>(true);
            for (int i = 0; i < mrs.Length; i++)
            {
                var r = mrs[i];
                if (r == null)
                    continue;
                if (r.transform == billboard || IsUnder(r.transform, billboard))
                    continue;
                string n = r.gameObject.name;
                if (n.IndexOf("Billboard", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;
                if (keep.Count > 0 && !keep.Contains(r))
                    continue;

                if (!any)
                {
                    bounds = r.bounds;
                    any = true;
                }
                else
                    bounds.Encapsulate(r.bounds);
            }

            return any && bounds.size.y > 0.01f;
        }

        static bool IsUnder(Transform t, Transform ancestor)
        {
            while (t != null)
            {
                if (t == ancestor)
                    return true;
                t = t.parent;
            }

            return false;
        }

        static bool TryOpaqueUvRect(Texture2D albedo, out Rect uv)
        {
            uv = default;
            Texture2D readable = albedo;
            bool temp = false;
            if (!albedo.isReadable)
            {
                readable = DuplicateReadable(albedo);
                if (readable == null)
                    return false;
                temp = true;
            }

            try
            {
                int w = readable.width;
                int h = readable.height;
                if (w < 4 || h < 4)
                    return false;

                // Left atlas half (frame 0). GetPixels: y=0 is bottom → V≈0.
                int half = w / 2;
                Color32[] px = readable.GetPixels32();
                int minX = half, maxX = -1, minY = h, maxY = -1;
                for (int y = 0; y < h; y++)
                {
                    int row = y * w;
                    for (int x = 0; x < half; x++)
                    {
                        if (px[row + x].a < 10)
                            continue;
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
                }

                if (maxX < minX || maxY < minY)
                    return false;

                // Convert texel centers to UV.
                float u0 = (minX + 0.5f) / w;
                float u1 = (maxX + 0.5f) / w;
                float v0 = (minY + 0.5f) / h;
                float v1 = (maxY + 0.5f) / h;
                uv = Rect.MinMaxRect(u0, v0, u1, v1);
                return true;
            }
            finally
            {
                if (temp && readable != null)
                    Object.DestroyImmediate(readable);
            }
        }

        static Texture2D DuplicateReadable(Texture2D src)
        {
            string path = AssetDatabase.GetAssetPath(src);
            if (string.IsNullOrEmpty(path))
                return null;

            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null)
                return null;

            bool wasReadable = importer.isReadable;
            TextureImporterCompression wasComp = importer.textureCompression;
            if (!wasReadable || wasComp != TextureImporterCompression.Uncompressed)
            {
                importer.isReadable = true;
                importer.textureCompression = TextureImporterCompression.Uncompressed;
                importer.SaveAndReimport();
            }

            var copy = Object.Instantiate(AssetDatabase.LoadAssetAtPath<Texture2D>(path));
            copy.name = src.name + "_ReadableTmp";

            if (!wasReadable || wasComp != TextureImporterCompression.Uncompressed)
            {
                importer.isReadable = wasReadable;
                importer.textureCompression = wasComp;
                importer.SaveAndReimport();
            }

            return copy;
        }

        static bool TryUvToWorldExtents(
            Vector3[] verts, Vector2[] uvs, Rect opaqueUv,
            out float yMin, out float yMax, out float xMin, out float xMax)
        {
            yMin = yMax = xMin = xMax = 0f;
            // Fit plane x=a*u+b, y=c*v+d from the 4 corners (least squares over all verts).
            // For a proper quad this is exact.
            float su = 0, suu = 0, sx = 0, sux = 0;
            float sv = 0, svv = 0, sy = 0, svy = 0;
            int n = verts.Length;
            for (int i = 0; i < n; i++)
            {
                float u = uvs[i].x;
                float v = uvs[i].y;
                float x = verts[i].x;
                float y = verts[i].y;
                su += u;
                suu += u * u;
                sx += x;
                sux += u * x;
                sv += v;
                svv += v * v;
                sy += y;
                svy += v * y;
            }

            float detU = n * suu - su * su;
            float detV = n * svv - sv * sv;
            if (Mathf.Abs(detU) < 1e-8f || Mathf.Abs(detV) < 1e-8f)
                return false;

            float a = (n * sux - su * sx) / detU;
            float b = (sx - a * su) / n;
            float c = (n * svy - sv * sy) / detV;
            float d = (sy - c * sv) / n;

            float x0 = a * opaqueUv.xMin + b;
            float x1 = a * opaqueUv.xMax + b;
            float y0 = c * opaqueUv.yMin + d;
            float y1 = c * opaqueUv.yMax + d;
            xMin = Mathf.Min(x0, x1);
            xMax = Mathf.Max(x0, x1);
            yMin = Mathf.Min(y0, y1);
            yMax = Mathf.Max(y0, y1);
            return yMax > yMin && xMax > xMin;
        }

        static Transform FindNamed(Transform root, string name)
        {
            if (root.name == name)
                return root;
            for (int i = 0; i < root.childCount; i++)
            {
                Transform f = FindNamed(root.GetChild(i), name);
                if (f != null)
                    return f;
            }

            return null;
        }
    }
}
