#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace CustomSRP.Editor
{
    /// <summary>
    /// Port of Boluo MapItemCrossBillboardContourMesh: grid cells over atlas alpha
    /// so cards hug the crown (no full-rect cutout). RGB dilate into transparent
    /// pixels so any remaining clip does not pick black fringe.
    /// </summary>
    public static class OilTreeBillboardContourMesh
    {
        public const float DefaultCutoff = 0.32f;
        const int DilatePx = 10;
        const int GridX = 56;
        const int GridY = 72;

        public static void FillSilhouetteAndDilate(Texture2D tex, float cutoff = DefaultCutoff)
        {
            if (tex == null)
                return;
            var px = tex.GetPixels32();
            int w = tex.width;
            int h = tex.height;
            if (w < 4 || h < 4 || px == null || px.Length != w * h)
                return;
            int mid = w / 2;
            ProcessFrame(px, w, h, 0, mid, cutoff);
            ProcessFrame(px, w, h, mid, w, cutoff);
            tex.SetPixels32(px);
        }

        public static Mesh Build(
            Texture2D atlas,
            Vector2 quadSize,
            float yPivotOffset,
            float cutoff = DefaultCutoff)
        {
            float width = Mathf.Max(0.01f, quadSize.x);
            float height = Mathf.Max(0.01f, quadSize.y);
            float y0 = -yPivotOffset;
            if (atlas == null)
                return FallbackQuad(width, height, y0);

            var px = atlas.GetPixels32();
            int tw = atlas.width;
            int th = atlas.height;
            if (tw < 8 || th < 8 || px == null || px.Length != tw * th)
                return FallbackQuad(width, height, y0);

            GetFrameUvCrop(width, height, out float u0a, out float u0b, out float v0, out float v1);
            var mesh = new Mesh { name = "OilTreeBillboardContour" };
            var verts = new List<Vector3>(64);
            var uvs = new List<Vector2>(64);
            var normals = new List<Vector3>(64);
            var tris = new List<int>(96);

            bool front = AppendGridFrame(
                px, tw, th, 0, tw / 2, u0a, u0b, v0, v1,
                width, height, y0, Vector3.back, cutoff,
                verts, uvs, normals, tris);
            bool side = AppendGridFrame(
                px, tw, th, tw / 2, tw, u0a + 0.5f, u0b + 0.5f, v0, v1,
                width, height, y0, Vector3.left, cutoff,
                verts, uvs, normals, tris);
            if (!front && !side)
            {
                Object.DestroyImmediate(mesh);
                return FallbackQuad(width, height, y0);
            }

            if (verts.Count > 65535)
                mesh.indexFormat = IndexFormat.UInt32;
            mesh.SetVertices(verts);
            mesh.SetUVs(0, uvs);
            mesh.SetNormals(normals);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        public static bool TryRecoverFullQuad(Mesh source, out Vector2 quadSize, out float yPivotOffset)
        {
            quadSize = new Vector2(4f, 8f);
            yPivotOffset = 0f;
            if (source == null)
                return false;

            // Prefer legacy 8-vert full cards; contour meshes still expose crown AABB.
            var b = source.bounds;
            if (b.size.x < 0.01f || b.size.y < 0.01f)
                return false;
            float horiz = Mathf.Max(b.size.x, b.size.z);
            quadSize = new Vector2(horiz, b.size.y);
            yPivotOffset = -b.min.y;
            return true;
        }

        static bool AppendGridFrame(
            Color32[] px, int tw, int th, int x0, int x1,
            float ua, float ub, float v0, float v1,
            float width, float height, float y0, Vector3 nrm, float cutoff,
            List<Vector3> verts, List<Vector2> uvs, List<Vector3> normals, List<int> tris)
        {
            int fw = x1 - x0;
            if (fw < 8 || th < 8)
                return false;

            byte cut = (byte)Mathf.Clamp(Mathf.RoundToInt(cutoff * 255f), 1, 254);
            int col0 = Mathf.Clamp(Mathf.FloorToInt(ua * tw) - 1, x0, x1 - 1);
            int col1 = Mathf.Clamp(Mathf.CeilToInt(ub * tw) + 1, x0 + 1, x1);
            int row0 = Mathf.Clamp(Mathf.FloorToInt(v0 * th) - 1, 0, th - 1);
            int row1 = Mathf.Clamp(Mathf.CeilToInt(v1 * th) + 1, 1, th);
            int pxW = Mathf.Max(2, col1 - col0);
            int pxH = Mathf.Max(2, row1 - row0);
            int gx = Mathf.Min(GridX, pxW);
            int gy = Mathf.Min(GridY, pxH);
            float hw = width * 0.5f;
            bool any = false;

            for (int gyi = 0; gyi < gy; gyi++)
            {
                int yA = row0 + gyi * pxH / gy;
                int yB = row0 + (gyi + 1) * pxH / gy;
                if (yB <= yA)
                    yB = yA + 1;
                for (int gxi = 0; gxi < gx; gxi++)
                {
                    int xA = col0 + gxi * pxW / gx;
                    int xB = col0 + (gxi + 1) * pxW / gx;
                    if (xB <= xA)
                        xB = xA + 1;
                    if (!CellHasCoverage(px, tw, xA, xB, yA, yB, cut))
                        continue;

                    float uA = xA / (float)tw;
                    float uB = xB / (float)tw;
                    float vA = yA / (float)th;
                    float vB = yB / (float)th;
                    float x0w = Mathf.Lerp(-hw, hw, Mathf.InverseLerp(ua, ub, uA));
                    float x1w = Mathf.Lerp(-hw, hw, Mathf.InverseLerp(ua, ub, uB));
                    float y0w = Mathf.Lerp(y0, y0 + height, Mathf.InverseLerp(v0, v1, vA));
                    float y1w = Mathf.Lerp(y0, y0 + height, Mathf.InverseLerp(v0, v1, vB));
                    Vector3 bl, tl, tr, br;
                    if (Mathf.Abs(nrm.z) > 0.5f)
                    {
                        bl = new Vector3(x0w, y0w, 0f);
                        tl = new Vector3(x0w, y1w, 0f);
                        tr = new Vector3(x1w, y1w, 0f);
                        br = new Vector3(x1w, y0w, 0f);
                    }
                    else
                    {
                        bl = new Vector3(0f, y0w, -x0w);
                        tl = new Vector3(0f, y1w, -x0w);
                        tr = new Vector3(0f, y1w, -x1w);
                        br = new Vector3(0f, y0w, -x1w);
                    }

                    AddQuad(verts, uvs, normals, tris, bl, tl, tr, br, nrm, uA, uB, vA, vB);
                    any = true;
                }
            }

            return any;
        }

        static bool CellHasCoverage(Color32[] px, int tw, int xA, int xB, int yA, int yB, byte cut)
        {
            int xMax = Mathf.Min(xB, tw);
            for (int y = yA; y < yB; y++)
            {
                int row = y * tw;
                for (int x = xA; x < xMax; x++)
                {
                    if (px[row + x].a >= cut)
                        return true;
                }
            }

            return false;
        }

        static void ProcessFrame(Color32[] px, int w, int h, int x0, int x1, float cutoff)
        {
            int fw = x1 - x0;
            if (fw < 4)
                return;
            byte cut = (byte)Mathf.Clamp(Mathf.RoundToInt(cutoff * 255f), 1, 254);
            int n = fw * h;
            var opaque = new bool[n];
            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                int mrow = y * fw;
                for (int x = 0; x < fw; x++)
                    opaque[mrow + x] = px[row + x0 + x].a >= cut;
            }

            var rgb = new Color32[n];
            var hasRgb = new bool[n];
            for (int i = 0; i < n; i++)
            {
                if (!opaque[i])
                    continue;
                int y = i / fw;
                int x = i % fw;
                rgb[i] = px[y * w + x0 + x];
                hasRgb[i] = true;
            }

            for (int pass = 0; pass < DilatePx; pass++)
            {
                bool grew = false;
                for (int y = 0; y < h; y++)
                {
                    int mrow = y * fw;
                    for (int x = 0; x < fw; x++)
                    {
                        int i = mrow + x;
                        if (hasRgb[i])
                            continue;
                        Color32 sample = default;
                        if (TrySample(x - 1, y, ref sample)
                            || TrySample(x + 1, y, ref sample)
                            || TrySample(x, y - 1, ref sample)
                            || TrySample(x, y + 1, ref sample))
                        {
                            rgb[i] = sample;
                            hasRgb[i] = true;
                            grew = true;
                        }
                    }
                }

                if (!grew)
                    break;

                bool TrySample(int sx, int sy, ref Color32 dest)
                {
                    if ((uint)sx >= (uint)fw || (uint)sy >= (uint)h)
                        return false;
                    int si = sy * fw + sx;
                    if (!hasRgb[si])
                        return false;
                    dest = rgb[si];
                    return true;
                }
            }

            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                int mrow = y * fw;
                for (int x = 0; x < fw; x++)
                {
                    int i = mrow + x;
                    if (opaque[i] || !hasRgb[i])
                        continue;
                    int pi = row + x0 + x;
                    var c = rgb[i];
                    c.a = px[pi].a;
                    px[pi] = c;
                }
            }
        }

        static Mesh FallbackQuad(float width, float height, float y0)
        {
            float hw = width * 0.5f;
            GetFrameUvCrop(width, height, out float u0a, out float u0b, out float v0, out float v1);
            var verts = new List<Vector3>(8);
            var uvs = new List<Vector2>(8);
            var nrms = new List<Vector3>(8);
            var tris = new List<int>(12);
            AddQuad(
                verts, uvs, nrms, tris,
                new Vector3(-hw, y0, 0f),
                new Vector3(-hw, y0 + height, 0f),
                new Vector3(hw, y0 + height, 0f),
                new Vector3(hw, y0, 0f),
                Vector3.back, u0a, u0b, v0, v1);
            AddQuad(
                verts, uvs, nrms, tris,
                new Vector3(0f, y0, hw),
                new Vector3(0f, y0 + height, hw),
                new Vector3(0f, y0 + height, -hw),
                new Vector3(0f, y0, -hw),
                Vector3.left, u0a + 0.5f, u0b + 0.5f, v0, v1);
            var mesh = new Mesh { name = "OilTreeBillboardCross" };
            mesh.SetVertices(verts);
            mesh.SetUVs(0, uvs);
            mesh.SetNormals(nrms);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        static void AddQuad(
            List<Vector3> verts, List<Vector2> uvs, List<Vector3> nrms, List<int> tris,
            Vector3 bl, Vector3 tl, Vector3 tr, Vector3 br, Vector3 n,
            float ua, float ub, float v0, float v1)
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
            nrms.Add(n);
            nrms.Add(n);
            nrms.Add(n);
            nrms.Add(n);
            tris.Add(i);
            tris.Add(i + 1);
            tris.Add(i + 3);
            tris.Add(i + 1);
            tris.Add(i + 2);
            tris.Add(i + 3);
        }

        static void GetFrameUvCrop(float width, float height, out float ua, out float ub, out float v0, out float v1)
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
    }
}
#endif
