using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Game
{
    /// <summary>
    /// Match LOD_Billboard size/pivot to near-tree (LOD0) bounds × 1.06.
    /// 8-vert cross meshes are rebuilt. UV prefers albedo opaque bounds so wide
    /// canopies (Fruit_*) are not U-cropped by a tall/narrow LOD0 aspect; empty
    /// atlas padding is excluded so trunks sit on the ground.
    /// High-poly impostors (Bush / Maple Dead) are scaled in place — never Clear+rebuild,
    /// which left vertexCount/indexBuffer stale and blew up GPU geometry.
    /// Menu: Window > Game > Match Billboard Height To LOD0
    /// </summary>
    public static class MatchBillboardHeightToLod0
    {
        const float FramePadding = 1.06f;
        const float HeightEps = 0.05f;
        const int CrossVertCount = 8;
        /// <summary>Extra UV margin around opaque content (fraction of atlas).</summary>
        const float ContentUvPad = 0.008f;
        const string PrefabsRoot = "Assets/GameMain/Environment/Prefabs/DaoYu1";
        static readonly string[] Folders = { "Songshu", "Lvshu", "ZaWu", "Guanmu" };

        struct FrameUv
        {
            public float Ua, Ub, V0, V1;
            /// <summary>Atlas width/height (e.g. 2 for 2048×1024 dual frame).</summary>
            public float AtlasPixelAspect;
            public bool Valid => Ub > Ua + 1e-4f && V1 > V0 + 1e-4f;
            /// <summary>Painted content width/height in pixels (not raw Δu/Δv).</summary>
            public float ContentAspect =>
                Valid
                    ? (Ub - Ua) / (V1 - V0) * Mathf.Max(AtlasPixelAspect, 1e-4f)
                    : 1f;
        }

        [MenuItem("Window/Game/Match Billboard Height To LOD0")]
        public static void RunFromMenu()
        {
            int n = MatchAll(dryRun: false, out string report);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            EditorUtility.DisplayDialog(
                "Match Billboard Height",
                $"已对齐 {n} 个 Billboard mesh。\n\n详见 Console。",
                "OK");
            Debug.Log($"[MatchBillboardHeight]\n{report}");
        }

        /// <summary>Batchmode: -executeMethod Game.MatchBillboardHeightToLod0.ExecuteMethodEntry</summary>
        public static void ExecuteMethodEntry()
        {
            int n = MatchAll(dryRun: false, out string report);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"[MatchBillboardHeight] done count={n}\n{report}");
        }

        public static int MatchAll(bool dryRun, out string report)
        {
            var sb = new StringBuilder();
            int count = 0;
            var prefabPaths = CollectPrefabPaths();
            for (int i = 0; i < prefabPaths.Count; i++)
            {
                string path = prefabPaths[i];
                EditorUtility.DisplayProgressBar(
                    "Match Billboard Height",
                    $"{Path.GetFileNameWithoutExtension(path)} ({i + 1}/{prefabPaths.Count})",
                    (float)i / Mathf.Max(1, prefabPaths.Count));

                if (MatchOne(path, dryRun, sb))
                    count++;
            }

            EditorUtility.ClearProgressBar();
            report = sb.ToString();
            return count;
        }

        static List<string> CollectPrefabPaths()
        {
            var list = new List<string>();
            for (int f = 0; f < Folders.Length; f++)
            {
                string dir = Path.Combine(PrefabsRoot, Folders[f]).Replace('\\', '/');
                if (!Directory.Exists(dir))
                    continue;

                string[] files = Directory.GetFiles(dir, "*.prefab", SearchOption.TopDirectoryOnly);
                for (int i = 0; i < files.Length; i++)
                {
                    string path = files[i].Replace('\\', '/');
                    if (path.IndexOf("Palm", System.StringComparison.OrdinalIgnoreCase) >= 0)
                        continue;
                    list.Add(path);
                }
            }

            return list;
        }

        static bool MatchOne(string prefabPath, bool dryRun, StringBuilder report)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab == null)
                return false;

            var bbTf = FindBillboard(prefab.transform);
            if (bbTf == null)
                return false;

            var mf = bbTf.GetComponent<MeshFilter>();
            if (mf == null || mf.sharedMesh == null)
            {
                report.AppendLine($"SKIP no mesh {prefab.name}");
                return false;
            }

            string meshPath = AssetDatabase.GetAssetPath(mf.sharedMesh);
            if (string.IsNullOrEmpty(meshPath) || !meshPath.EndsWith(".asset"))
            {
                report.AppendLine($"SKIP non-asset mesh {prefab.name}");
                return false;
            }

            if (!TryResolveLod0Quad(prefab, out Vector2 quadSize, out float yPivot, out Bounds lodBounds))
            {
                report.AppendLine($"FAIL lod0 bounds {prefab.name}");
                return false;
            }

            FrameUv contentUv = default;
            var bbMr = bbTf.GetComponent<MeshRenderer>();
            if (bbMr != null && TrySampleAlbedoContentUv(bbMr.sharedMaterial, out contentUv))
            {
                // Tall/narrow LOD0 + square painted canopy → old U-crop chopped foliage.
                // Widen to content aspect so full crown stays in frame; keep LOD0 height.
                float wFromContent = quadSize.y * contentUv.ContentAspect;
                if (wFromContent > quadSize.x * 1.02f)
                {
                    report.AppendLine(
                        $"  contentUV widen {prefab.name} w {quadSize.x:F2}→{wFromContent:F2} " +
                        $"asp={contentUv.ContentAspect:F3} uvU=[{contentUv.Ua:F3},{contentUv.Ub:F3}] " +
                        $"V=[{contentUv.V0:F3},{contentUv.V1:F3}]");
                    quadSize.x = wFromContent;
                }
            }

            var cur = mf.sharedMesh.bounds;
            float curH = cur.size.y;
            float targetH = quadSize.y;
            float hRatio = curH > 1e-4f ? targetH / curH : 0f;
            bool heightOk = Mathf.Abs(hRatio - 1f) <= HeightEps;
            float curBottom = cur.min.y;
            float targetBottom = -yPivot;
            bool pivotOk = Mathf.Abs(curBottom - targetBottom) <= 0.15f;
            bool widthOk = Mathf.Abs(cur.size.x - quadSize.x) / Mathf.Max(quadSize.x, 0.01f) <= HeightEps;
            bool uvOk = !contentUv.Valid || CrossUvMatchesContent(mf.sharedMesh, contentUv);

            if (heightOk && pivotOk && widthOk && uvOk)
            {
                report.AppendLine(
                    $"OK {prefab.name} h={curH:F2}≈{targetH:F2} w={cur.size.x:F2}≈{quadSize.x:F2}");
                return false;
            }

            int vc = mf.sharedMesh.vertexCount;
            bool isCross = vc == CrossVertCount;
            string mode = isCross ? "cross-rebuild" : "impostor-scale";
            report.AppendLine(
                $"FIX {prefab.name} [{mode} vc={vc}] h {curH:F2}→{targetH:F2} (×{hRatio:F2}) " +
                $"w {cur.size.x:F2}→{quadSize.x:F2} bottom {curBottom:F2}→{targetBottom:F2} " +
                $"lod0=({lodBounds.size.x:F2},{lodBounds.size.y:F2},{lodBounds.size.z:F2})" +
                (contentUv.Valid ? " contentUV" : ""));

            if (dryRun)
                return true;

            var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(meshPath);
            if (mesh == null)
            {
                report.AppendLine($"  FAIL load mesh {meshPath}");
                return false;
            }

            if (isCross)
                ReplaceWithCrossBillboard(mesh, quadSize, yPivot, contentUv);
            else
                ScaleImpostorBillboard(mesh, quadSize, yPivot);

            mesh.name = Path.GetFileNameWithoutExtension(meshPath);
            EditorUtility.SetDirty(mesh);
            return true;
        }

        static Transform FindBillboard(Transform root)
        {
            if (root.name == "LOD_Billboard")
                return root;
            for (int i = 0; i < root.childCount; i++)
            {
                var found = FindBillboard(root.GetChild(i));
                if (found != null)
                    return found;
            }

            return null;
        }

        static bool TryResolveLod0Quad(
            GameObject prefab, out Vector2 quadSize, out float yPivot, out Bounds lodBounds)
        {
            quadSize = Vector2.one;
            yPivot = 0f;
            lodBounds = default;

            GameObject sample = null;
            try
            {
                sample = Object.Instantiate(prefab, Vector3.zero, Quaternion.identity);
                sample.hideFlags = HideFlags.HideAndDontSave;
                sample.name = prefab.name + "_BbHeightResolve";
                sample.transform.localScale = Vector3.one;
                PrepareLod0Only(sample);
                if (!TryEnabledBounds(sample, out lodBounds) || lodBounds.size.sqrMagnitude < 1e-6f)
                    return false;

                float horiz = Mathf.Max(lodBounds.size.x, lodBounds.size.z);
                float vert = Mathf.Max(0.01f, lodBounds.size.y);
                quadSize = new Vector2(Mathf.Max(0.01f, horiz * FramePadding), vert * FramePadding);
                yPivot = -(lodBounds.center.y - quadSize.y * 0.5f);
                return true;
            }
            finally
            {
                if (sample != null)
                    Object.DestroyImmediate(sample);
            }
        }

        static void PrepareLod0Only(GameObject sample)
        {
            var keep = new HashSet<Renderer>();
            var lod = sample.GetComponent<LODGroup>();
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

            var mrs = sample.GetComponentsInChildren<MeshRenderer>(true);
            for (int i = 0; i < mrs.Length; i++)
            {
                var mr = mrs[i];
                if (mr == null)
                    continue;
                bool isBb = mr.gameObject.name.IndexOf("Billboard", System.StringComparison.OrdinalIgnoreCase) >= 0
                    || mr.gameObject.name.IndexOf("Impostor", System.StringComparison.OrdinalIgnoreCase) >= 0;
                bool use = !isBb && (keep.Count == 0 || keep.Contains(mr));
                mr.enabled = use;
            }
        }

        static bool TryEnabledBounds(GameObject root, out Bounds bounds)
        {
            bounds = default;
            bool any = false;
            var mrs = root.GetComponentsInChildren<MeshRenderer>(true);
            for (int i = 0; i < mrs.Length; i++)
            {
                if (mrs[i] == null || !mrs[i].enabled)
                    continue;
                if (!any)
                {
                    bounds = mrs[i].bounds;
                    any = true;
                }
                else
                    bounds.Encapsulate(mrs[i].bounds);
            }

            return any;
        }

        /// <summary>
        /// Build into a fresh Mesh then CopySerialized onto the asset so vertexCount /
        /// indexBuffer / dataSize stay consistent (in-place Clear on large impostors corrupted YAML).
        /// </summary>
        static void ReplaceWithCrossBillboard(
            Mesh assetMesh, Vector2 quadSize, float yPivotOffset, FrameUv contentUv)
        {
            var built = new Mesh { name = assetMesh.name };
            BuildCrossBillboardInto(built, quadSize, yPivotOffset, contentUv);
            EditorUtility.CopySerialized(built, assetMesh);
            Object.DestroyImmediate(built);
        }

        /// <summary>
        /// Keep impostor topology + UVs; only fit AABB to target cross size/pivot.
        /// </summary>
        static void ScaleImpostorBillboard(Mesh mesh, Vector2 quadSize, float yPivotOffset)
        {
            var verts = mesh.vertices;
            if (verts == null || verts.Length == 0)
                return;

            var b = mesh.bounds;
            float srcW = Mathf.Max(b.size.x, b.size.z, 0.01f);
            float srcH = Mathf.Max(b.size.y, 0.01f);
            float sx = quadSize.x / srcW;
            float sy = quadSize.y / srcH;
            float yCenter = -yPivotOffset + quadSize.y * 0.5f;
            Vector3 c = b.center;

            for (int i = 0; i < verts.Length; i++)
            {
                Vector3 v = verts[i];
                v.x = (v.x - c.x) * sx;
                v.z = (v.z - c.z) * sx;
                v.y = (v.y - c.y) * sy + yCenter;
                verts[i] = v;
            }

            mesh.vertices = verts;
            mesh.RecalculateBounds();
        }

        static void BuildCrossBillboardInto(
            Mesh mesh, Vector2 quadSize, float yPivotOffset, FrameUv contentUv)
        {
            float width = Mathf.Max(0.01f, quadSize.x);
            float height = Mathf.Max(0.01f, quadSize.y);
            float y0 = -yPivotOffset;
            float hw = width * 0.5f;
            float u0a, u0b, v0, v1;
            if (contentUv.Valid)
            {
                u0a = contentUv.Ua;
                u0b = contentUv.Ub;
                v0 = contentUv.V0;
                v1 = contentUv.V1;
            }
            else
                GetFrameUvCrop(width, height, out u0a, out u0b, out v0, out v1);
            float u1a = u0a + 0.5f;
            float u1b = u0b + 0.5f;

            var verts = new List<Vector3>(8);
            var uvs = new List<Vector2>(8);
            var normals = new List<Vector3>(8);
            var tris = new List<int>(12);

            void AddQuad(Vector3 bl, Vector3 tl, Vector3 tr, Vector3 br, Vector3 n, float ua, float ub)
            {
                int i = verts.Count;
                verts.Add(bl);
                verts.Add(tl);
                verts.Add(tr);
                verts.Add(br);
                uvs.Add(new Vector2(ua, v0));
                uvs.Add(new Vector2(ua, v1));
                uvs.Add(new Vector2(ub, v1));
                uvs.Add(new Vector2(ub, v0));
                normals.Add(n);
                normals.Add(n);
                normals.Add(n);
                normals.Add(n);
                tris.Add(i);
                tris.Add(i + 1);
                tris.Add(i + 3);
                tris.Add(i + 1);
                tris.Add(i + 2);
                tris.Add(i + 3);
            }

            AddQuad(
                new Vector3(-hw, y0, 0f),
                new Vector3(-hw, y0 + height, 0f),
                new Vector3(hw, y0 + height, 0f),
                new Vector3(hw, y0, 0f),
                Vector3.back,
                u0a, u0b);
            AddQuad(
                new Vector3(0f, y0, hw),
                new Vector3(0f, y0 + height, hw),
                new Vector3(0f, y0 + height, -hw),
                new Vector3(0f, y0, -hw),
                Vector3.left,
                u1a, u1b);

            mesh.Clear();
            mesh.SetVertices(verts);
            mesh.SetUVs(0, uvs);
            mesh.SetNormals(normals);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateBounds();
        }

        /// <summary>
        /// Match mesh aspect without stretching the atlas: crop U or V inside each frame.
        /// Fallback when albedo content bounds are unavailable.
        /// </summary>
        static void GetFrameUvCrop(
            float width, float height, out float ua, out float ub, out float v0, out float v1)
        {
            const float frameU0 = 0f;
            const float frameU1 = 0.5f;
            float frameSpan = frameU1 - frameU0;
            if (width < height)
            {
                float uW = frameSpan * (width / height);
                float mid = (frameU0 + frameU1) * 0.5f;
                ua = mid - uW * 0.5f;
                ub = mid + uW * 0.5f;
                v0 = 0f;
                v1 = 1f;
            }
            else if (width > height)
            {
                ua = frameU0;
                ub = frameU1;
                float vH = height / width;
                v0 = (1f - vH) * 0.5f;
                v1 = v0 + vH;
            }
            else
            {
                ua = frameU0;
                ub = frameU1;
                v0 = 0f;
                v1 = 1f;
            }
        }

        static bool CrossUvMatchesContent(Mesh mesh, FrameUv content)
        {
            if (mesh == null || mesh.vertexCount < 4 || !content.Valid)
                return true;
            var uvs = mesh.uv;
            if (uvs == null || uvs.Length < 4)
                return false;
            // Front quad: verts 0..3, left-frame U.
            float ua = Mathf.Min(uvs[0].x, uvs[3].x);
            float ub = Mathf.Max(uvs[1].x, uvs[2].x);
            float v0 = Mathf.Min(uvs[0].y, uvs[3].y);
            float v1 = Mathf.Max(uvs[1].y, uvs[2].y);
            return Mathf.Abs(ua - content.Ua) <= 0.02f
                && Mathf.Abs(ub - content.Ub) <= 0.02f
                && Mathf.Abs(v0 - content.V0) <= 0.02f
                && Mathf.Abs(v1 - content.V1) <= 0.02f;
        }

        /// <summary>
        /// Sample left atlas frame opaque bounds → UV for both cross cards (+0.5 U for side).
        /// Loads PNG/TGA bytes so Read/Write on the importer is not required.
        /// </summary>
        static bool TrySampleAlbedoContentUv(Material mat, out FrameUv uv)
        {
            uv = default;
            if (mat == null)
                return false;
            Texture tex = null;
            if (mat.HasProperty("_BaseMap"))
                tex = mat.GetTexture("_BaseMap");
            if (tex == null && mat.HasProperty("_MainTex"))
                tex = mat.GetTexture("_MainTex");
            if (tex == null)
                return false;

            string assetPath = AssetDatabase.GetAssetPath(tex);
            if (string.IsNullOrEmpty(assetPath) || !File.Exists(assetPath))
                return false;

            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(assetPath);
            }
            catch
            {
                return false;
            }

            var tmp = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            try
            {
                if (!tmp.LoadImage(bytes, markNonReadable: false))
                    return false;
                int w = tmp.width;
                int h = tmp.height;
                if (w < 8 || h < 8)
                    return false;

                var pixels = tmp.GetPixels32();
                int halfW = w / 2;
                int minX = halfW, maxX = 0, minY = h, maxY = 0;
                bool any = false;
                for (int y = 0; y < h; y++)
                {
                    int row = y * w;
                    for (int x = 0; x < halfW; x++)
                    {
                        Color32 c = pixels[row + x];
                        if (c.a < 10)
                            continue;
                        int maxRgb = c.r > c.g ? (c.r > c.b ? c.r : c.b) : (c.g > c.b ? c.g : c.b);
                        if (maxRgb < 8)
                            continue;
                        any = true;
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
                }

                if (!any || maxX <= minX || maxY <= minY)
                    return false;

                // Unity UV: v=0 bottom. GetPixels32 row 0 = texture bottom.
                float padU = ContentUvPad;
                float padV = ContentUvPad;
                float ua = Mathf.Clamp01((minX / (float)w) - padU);
                float ub = Mathf.Clamp01(((maxX + 1) / (float)w) + padU);
                // Keep left card inside [0, 0.5].
                ub = Mathf.Min(ub, 0.5f - 1e-4f);
                ua = Mathf.Min(ua, ub - 1e-4f);
                float v0 = Mathf.Clamp01((minY / (float)h) - padV);
                float v1 = Mathf.Clamp01(((maxY + 1) / (float)h) + padV);
                if (ub <= ua || v1 <= v0)
                    return false;

                uv = new FrameUv
                {
                    Ua = ua,
                    Ub = ub,
                    V0 = v0,
                    V1 = v1,
                    AtlasPixelAspect = w / (float)h
                };
                return true;
            }
            finally
            {
                Object.DestroyImmediate(tmp);
            }
        }
    }
}
