using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Game
{
    /// <summary>
    /// Align each tree's LOD_Billboard mat to its LOD0 leaf mat:
    /// secondary color fields + _BaseColor tint so opaque atlas mean ≈ leaf mean.
    /// </summary>
    public static class SyncBillboardColorsFromLeaf
    {
        const string PrefabsRoot = "Assets/GameMain/Environment/Prefabs/DaoYu1";
        static readonly string[] Folders = { "Songshu", "Lvshu" };

        static readonly string[] SyncProps =
        {
            "_TopColor", "_BottomColor", "_AmbientColor", "_ShadowTint", "_ShadowWarm",
            "_SpecularColor", "_OutlineColor"
        };

        const float AlphaThreshold = 0.35f;
        const float TintMin = 0.55f;
        const float TintMax = 1.55f;
        const int MaxSampleEdge = 512;

        [MenuItem("Window/Game/Sync Billboard Colors From Leaf")]
        public static void RunFromMenu()
        {
            int n = SyncAll();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            EditorUtility.DisplayDialog(
                "Sync Billboard Colors",
                $"已同步 {n} 个 Billboard 材质（次要色 + atlas 主色 tint 对齐 LOD0 树叶）。",
                "OK");
        }

        public static int SyncAll()
        {
            int updated = 0;
            var seen = new HashSet<string>();

            foreach (string folder in Folders)
            {
                string dir = Path.Combine(PrefabsRoot, folder).Replace('\\', '/');
                if (!Directory.Exists(dir))
                    continue;

                foreach (string file in Directory.GetFiles(dir, "*.prefab", SearchOption.AllDirectories))
                {
                    string path = file.Replace('\\', '/');
                    var root = PrefabUtility.LoadPrefabContents(path);
                    if (root == null)
                        continue;

                    try
                    {
                        if (!TryGetLeafAndBillboardMats(root, out Material leaf, out List<Material> billboards))
                            continue;

                        for (int i = 0; i < billboards.Count; i++)
                        {
                            Material bb = billboards[i];
                            if (bb == null)
                                continue;
                            string assetPath = AssetDatabase.GetAssetPath(bb);
                            if (string.IsNullOrEmpty(assetPath) || !seen.Add(assetPath))
                                continue;

                            if (CopyColors(leaf, bb) | AlignBaseColorTint(leaf, bb))
                            {
                                EditorUtility.SetDirty(bb);
                                updated++;
                                Debug.Log($"[SyncBillboardColors] {assetPath} <- {leaf.name}");
                            }
                        }
                    }
                    finally
                    {
                        PrefabUtility.UnloadPrefabContents(root);
                    }
                }
            }

            return updated;
        }

        static bool TryGetLeafAndBillboardMats(
            GameObject root, out Material leaf, out List<Material> billboards)
        {
            leaf = null;
            billboards = new List<Material>();

            var lod = root.GetComponentInChildren<LODGroup>(true);
            if (lod == null)
                return false;

            LOD[] lods = lod.GetLODs();
            if (lods == null || lods.Length == 0 || lods[0].renderers == null)
                return false;

            for (int i = 0; i < lods[0].renderers.Length; i++)
            {
                var mr = lods[0].renderers[i] as MeshRenderer;
                if (mr == null || mr.sharedMaterials == null)
                    continue;
                for (int m = 0; m < mr.sharedMaterials.Length; m++)
                {
                    var mat = mr.sharedMaterials[m];
                    if (mat == null)
                        continue;
                    string n = mat.name;
                    if (n.Contains("Leaf") || n.Contains("Vegetation"))
                    {
                        leaf = mat;
                        break;
                    }
                }

                if (leaf != null)
                    break;
            }

            if (leaf == null)
                return false;

            var transforms = root.GetComponentsInChildren<Transform>(true);
            for (int t = 0; t < transforms.Length; t++)
            {
                if (transforms[t].name != "LOD_Billboard")
                    continue;
                var mr = transforms[t].GetComponent<MeshRenderer>();
                if (mr == null || mr.sharedMaterials == null || mr.sharedMaterials.Length == 0)
                    continue;
                if (mr.sharedMaterials[0] != null)
                    billboards.Add(mr.sharedMaterials[0]);
            }

            return billboards.Count > 0;
        }

        static bool CopyColors(Material leaf, Material bb)
        {
            bool dirty = false;
            for (int i = 0; i < SyncProps.Length; i++)
            {
                string p = SyncProps[i];
                if (!leaf.HasProperty(p) || !bb.HasProperty(p))
                    continue;
                Color src = leaf.GetColor(p);
                Color dst = bb.GetColor(p);
                if (ColorsNearlyEqual(src, dst))
                    continue;
                bb.SetColor(p, src);
                dirty = true;
            }

            return dirty;
        }

        /// <summary>
        /// bb._BaseColor *= leafMean / bbMean so lit albedo mean tracks near leaves.
        /// Prefers green-dominant opaque pixels so trunk/bark in the atlas does not skew.
        /// </summary>
        static bool AlignBaseColorTint(Material leaf, Material bb)
        {
            if (!bb.HasProperty("_BaseColor"))
                return false;

            Texture leafTex = GetAlbedoTexture(leaf);
            Texture bbTex = GetAlbedoTexture(bb);
            if (!TryMeanLeafyOpaque(leafTex, out Color leafMean)
                || !TryMeanLeafyOpaque(bbTex, out Color bbMean))
            {
                Debug.LogWarning(
                    $"[SyncBillboardColors] skip base tint (no readable albedo): " +
                    $"{bb.name} <- {leaf.name}");
                return false;
            }

            Color leafBase = leaf.HasProperty("_BaseColor") ? leaf.GetColor("_BaseColor") : Color.white;
            Color tint = new Color(
                ClampTint(leafBase.r * leafMean.r / Mathf.Max(bbMean.r, 1e-3f)),
                ClampTint(leafBase.g * leafMean.g / Mathf.Max(bbMean.g, 1e-3f)),
                ClampTint(leafBase.b * leafMean.b / Mathf.Max(bbMean.b, 1e-3f)),
                1f);

            Color cur = bb.GetColor("_BaseColor");
            if (ColorsNearlyEqual(cur, tint))
                return false;

            bb.SetColor("_BaseColor", tint);
            if (bb.HasProperty("_Color"))
                bb.SetColor("_Color", tint);
            if (bb.HasProperty("_MainColor"))
                bb.SetColor("_MainColor", tint);

            Debug.Log(
                $"[SyncBillboardColors] base tint {bb.name}: " +
                $"leafMean={Fmt(leafMean)} bbMean={Fmt(bbMean)} → _BaseColor={Fmt(tint)}");
            return true;
        }

        static Texture GetAlbedoTexture(Material mat)
        {
            if (mat == null)
                return null;
            if (mat.HasProperty("_BaseMap"))
                return mat.GetTexture("_BaseMap");
            if (mat.HasProperty("_MainTex"))
                return mat.GetTexture("_MainTex");
            return null;
        }

        static bool TryMeanLeafyOpaque(Texture tex, out Color mean)
        {
            mean = Color.white;
            if (tex == null)
                return false;

            string path = AssetDatabase.GetAssetPath(tex);
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return false;

            // Decode source file (PNG/TGA) — avoids flipping TextureImporter.isReadable.
            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(path);
            }
            catch
            {
                return false;
            }

            var readable = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            try
            {
                if (!readable.LoadImage(bytes, markNonReadable: false))
                    return false;

                int w = readable.width;
                int h = readable.height;
                if (w < 2 || h < 2)
                    return false;

                if (w > MaxSampleEdge || h > MaxSampleEdge)
                {
                    int nw = w > h ? MaxSampleEdge : Mathf.Max(2, w * MaxSampleEdge / h);
                    int nh = h > w ? MaxSampleEdge : Mathf.Max(2, h * MaxSampleEdge / w);
                    var scaled = new Texture2D(nw, nh, TextureFormat.RGBA32, false);
                    try
                    {
                        // Bilinear via RenderTexture would need GPU; box-sample CPU.
                        Color[] src = readable.GetPixels();
                        Color[] dst = new Color[nw * nh];
                        float sx = (float)w / nw;
                        float sy = (float)h / nh;
                        for (int y = 0; y < nh; y++)
                        {
                            int sy0 = Mathf.Min(h - 1, (int)(y * sy));
                            for (int x = 0; x < nw; x++)
                            {
                                int sx0 = Mathf.Min(w - 1, (int)(x * sx));
                                dst[y * nw + x] = src[sy0 * w + sx0];
                            }
                        }

                        scaled.SetPixels(dst);
                        Object.DestroyImmediate(readable);
                        readable = scaled;
                        w = nw;
                        h = nh;
                    }
                    catch
                    {
                        Object.DestroyImmediate(scaled);
                        return false;
                    }
                }

                Color[] px = readable.GetPixels();
                double r = 0, g = 0, b = 0;
                int n = 0;
                double rAll = 0, gAll = 0, bAll = 0;
                int nAll = 0;

                for (int i = 0; i < px.Length; i++)
                {
                    Color c = px[i];
                    if (c.a < AlphaThreshold)
                        continue;
                    rAll += c.r;
                    gAll += c.g;
                    bAll += c.b;
                    nAll++;

                    // Leafy: green-ish opaque (skip bark / trunk / sky holes).
                    if (c.g > c.r * 0.85f && c.g > c.b * 0.7f && c.g > 0.08f)
                    {
                        r += c.r;
                        g += c.g;
                        b += c.b;
                        n++;
                    }
                }

                if (n < 50)
                {
                    if (nAll < 10)
                        return false;
                    mean = new Color(
                        (float)(rAll / nAll),
                        (float)(gAll / nAll),
                        (float)(bAll / nAll),
                        1f);
                    return true;
                }

                mean = new Color((float)(r / n), (float)(g / n), (float)(b / n), 1f);
                return true;
            }
            finally
            {
                if (readable != null)
                    Object.DestroyImmediate(readable);
            }
        }

        static float ClampTint(float v) => Mathf.Clamp(v, TintMin, TintMax);

        static string Fmt(Color c) =>
            $"({c.r:F3},{c.g:F3},{c.b:F3})";

        static bool ColorsNearlyEqual(Color a, Color b)
        {
            const float eps = 1e-4f;
            return Mathf.Abs(a.r - b.r) <= eps
                && Mathf.Abs(a.g - b.g) <= eps
                && Mathf.Abs(a.b - b.b) <= eps
                && Mathf.Abs(a.a - b.a) <= eps;
        }
    }
}
