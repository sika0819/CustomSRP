#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using System.Text;
using CustomSRP;
using Game;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace CustomSRP.Editor
{
    /// <summary>
    /// Bake LOD0 near-tree albedo (no lighting) into single-mat billboard atlases,
    /// and fit the Y-card quad onto that ortho frame so the impostor sits on the
    /// same bounds as the near mesh. Does not modify .shader / normals.
    /// </summary>
    public static class BakeTreeBillboardFromLod0
    {
        const string PrefabsRoot = "Assets/GameMain/Environment/Prefabs/DaoYu1";
        static readonly string[] Folders = { "Songshu", "Lvshu" };

        const int AtlasWidth = 2048;
        const int FrameCount = 2;
        const float FramePadding = 1.06f;
        const float Cutoff = 0.32f;
        const int DilatePx = 16;
        const int FringeFillPx = 96;
        const int BakeLayer = 30;

        static readonly Color BakeClearKey = new Color(1f, 0f, 1f, 0f);
        static readonly int CustomFogEnabledId = Shader.PropertyToID("_CustomFogEnabled");
        static readonly int OilAtmosphereFogAmountId = Shader.PropertyToID("_OilAtmosphereFogAmount");
        static readonly int OilPeriodAmbientId = Shader.PropertyToID("_OilPeriodAmbient");
        static readonly int OilSkyPeriodId = Shader.PropertyToID("_OilSkyPeriod");
        static readonly int OilSkySunDirId = Shader.PropertyToID("_OilSkySunDir");
        const string AlbedoCaptureKeyword = "OIL_BILLBOARD_ALBEDO_CAPTURE";

        const string BackupRel = "_scratch/billboard_albedo_backup_noon";
        const string BakeFlagRelPath = "Temp/bake_tree_billboard_noon.flag";

        static bool _flagBakeQueued;
        static HashSet<string> _nameFilter;

        [InitializeOnLoadMethod]
        static void WatchBakeFlag()
        {
            EditorApplication.update -= PollBakeFlag;
            EditorApplication.update += PollBakeFlag;
        }

        static void PollBakeFlag()
        {
            if (_flagBakeQueued || EditorApplication.isCompiling || EditorApplication.isUpdating
                || EditorApplication.isPlaying)
                return;

            string flag = Path.GetFullPath(Path.Combine(Application.dataPath, "..", BakeFlagRelPath));
            if (!File.Exists(flag))
                return;

            HashSet<string> filter = null;
            try
            {
                string body = File.ReadAllText(flag);
                filter = ParseNameFilter(body);
                File.Delete(flag);
            }
            catch
            {
                return;
            }

            _flagBakeQueued = true;
            _nameFilter = filter;
            string filterDesc = filter == null || filter.Count == 0
                ? "all"
                : string.Join(", ", filter);
            Debug.Log($"[BakeTreeBillboardFromLod0] Flag detected — baking albedo atlases ({filterDesc})");
            try
            {
                ExecuteMethodEntry();
            }
            finally
            {
                _nameFilter = null;
                _flagBakeQueued = false;
            }
        }

        /// <summary>
        /// Flag body: empty = all trees; otherwise one prefab name per line
        /// (e.g. SM_Env_Tree_Forest_01). Lines starting with # are ignored.
        /// </summary>
        static HashSet<string> ParseNameFilter(string body)
        {
            if (string.IsNullOrWhiteSpace(body))
                return null;
            var set = new HashSet<string>(System.StringComparer.Ordinal);
            using (var reader = new StringReader(body))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    line = line.Trim();
                    if (line.Length == 0 || line[0] == '#')
                        continue;
                    set.Add(line);
                }
            }

            return set.Count == 0 ? null : set;
        }

        [MenuItem("Window/Game/Bake Tree Billboards From LOD0 (Albedo)")]
        public static void BakeAllMenu()
        {
            int ok = BakeAll(out string report);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            EditorUtility.DisplayDialog(
                "Bake Tree Billboards",
                $"完成 {ok} 棵。\n\n{report}",
                "OK");
        }

        /// <summary>Batchmode: -executeMethod CustomSRP.Editor.BakeTreeBillboardFromLod0.ExecuteMethodEntry</summary>
        public static void ExecuteMethodEntry()
        {
            int ok = BakeAll(out string report);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"[BakeTreeBillboardFromLod0] done ok={ok}\n{report}");
        }

        public static int BakeAll(out string report)
        {
            var sb = new StringBuilder();
            var paths = CollectPrefabPaths(_nameFilter);
            int ok = 0;
            EnsureBackupDir();

            if (paths.Count == 0)
            {
                sb.AppendLine("FAIL no prefabs matched filter");
                report = sb.ToString();
                Debug.LogWarning($"[BakeTreeBillboardFromLod0]\n{report}");
                return 0;
            }

            int cachedMip = QualitySettings.globalTextureMipmapLimit;
            QualitySettings.globalTextureMipmapLimit = 0;
            // Do not ForceUpdate OilNPR here — reimport drops the albedo-capture
            // variant and the first ortho frame often comes back empty (Y-card UV
            // lives in that left atlas half).

            using (var env = new BakeEnvironmentScope())
            {
                for (int i = 0; i < paths.Count; i++)
                {
                    string path = paths[i];
                    string name = Path.GetFileNameWithoutExtension(path);
                    EditorUtility.DisplayProgressBar(
                        "Bake Tree Billboards (Albedo)",
                        name,
                        (i + 1f) / paths.Count);

                    try
                    {
                        if (BakeOne(path, sb))
                            ok++;
                    }
                    catch (System.Exception ex)
                    {
                        sb.AppendLine($"FAIL {name}: {ex.Message}");
                        Debug.LogException(ex);
                    }
                }
            }

            QualitySettings.globalTextureMipmapLimit = cachedMip;
            EditorUtility.ClearProgressBar();
            sb.AppendLine($"OK {ok}/{paths.Count}");
            report = sb.ToString();
            Debug.Log($"[BakeTreeBillboardFromLod0]\n{report}");
            return ok;
        }

        static List<string> CollectPrefabPaths(HashSet<string> nameFilter = null)
        {
            var list = new List<string>(32);
            for (int f = 0; f < Folders.Length; f++)
            {
                string dir = $"{PrefabsRoot}/{Folders[f]}";
                if (!Directory.Exists(dir))
                    continue;
                string[] files = Directory.GetFiles(dir, "*.prefab", SearchOption.TopDirectoryOnly);
                for (int i = 0; i < files.Length; i++)
                {
                    string path = files[i].Replace('\\', '/');
                    string name = Path.GetFileNameWithoutExtension(path);
                    if (nameFilter != null && nameFilter.Count > 0 && !nameFilter.Contains(name))
                        continue;
                    string albedo = BillboardAlbedoPath(path, name);
                    if (!File.Exists(ToAbs(albedo)))
                        continue;
                    list.Add(path);
                }
            }

            list.Sort();
            return list;
        }

        static string BillboardFolder(string prefabPath, string prefabName)
        {
            string dir = Path.GetDirectoryName(prefabPath)?.Replace('\\', '/') ?? PrefabsRoot;
            return $"{dir}/{prefabName}_Billboard";
        }

        static string BillboardAlbedoPath(string prefabPath, string prefabName) =>
            $"{BillboardFolder(prefabPath, prefabName)}/{prefabName}_Billboard_Albedo.png";

        static bool BakeOne(string prefabPath, StringBuilder sb)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab == null)
            {
                sb.AppendLine($"FAIL load {prefabPath}");
                return false;
            }

            string name = prefab.name;
            string albedoPath = BillboardAlbedoPath(prefabPath, name);
            if (!File.Exists(ToAbs(albedoPath)))
            {
                sb.AppendLine($"SKIP no albedo {name}");
                return false;
            }

            if (!TryBakeAtlas(prefab, out Texture2D atlas, out Vector2 quadSize))
            {
                sb.AppendLine($"FAIL bake {name}");
                return false;
            }

            try
            {
                if (!AtlasLooksValid(atlas))
                {
                    sb.AppendLine($"FAIL empty/flat atlas {name}");
                    return false;
                }

                FillFringeFromNeighbors(atlas);
                DilateRgbIntoTransparent(atlas, Cutoff);
                BackupExisting(albedoPath, name);
                WritePng(atlas, albedoPath);
                sb.AppendLine($"OK {name} size={quadSize.x:F1}x{quadSize.y:F1}");
                return true;
            }
            finally
            {
                Object.DestroyImmediate(atlas);
            }
        }

        /// <summary>
        /// Reject flat/garbage captures (CoverOnly loading cover, -nographics, etc.).
        /// CoverOnly clear is ~RGB(23,18,13) with 100% opaque — must not pass.
        /// </summary>
        static bool AtlasLooksValid(Texture2D atlas)
        {
            if (atlas == null)
                return false;
            var px = atlas.GetPixels32();
            if (px == null || px.Length < 64)
                return false;
            int opaque = 0;
            int chromaOk = 0;
            long sumR = 0, sumG = 0, sumB = 0;
            Color32 first = px[0];
            bool allSame = true;
            for (int i = 0; i < px.Length; i++)
            {
                Color32 c = px[i];
                if (c.a >= 80)
                {
                    opaque++;
                    sumR += c.r;
                    sumG += c.g;
                    sumB += c.b;
                    int max = c.r > c.g ? (c.r > c.b ? c.r : c.b) : (c.g > c.b ? c.g : c.b);
                    int min = c.r < c.g ? (c.r < c.b ? c.r : c.b) : (c.g < c.b ? c.g : c.b);
                    if (max - min >= 20)
                        chromaOk++;
                }

                if (allSame
                    && (c.r != first.r || c.g != first.g || c.b != first.b || c.a != first.a))
                    allSame = false;
            }

            // Real foliage has transparency around the card and green/brown chroma.
            float opaqueFrac = opaque / (float)px.Length;
            if (allSame || opaqueFrac < 0.01f || opaqueFrac > 0.92f)
                return false;
            if (opaque < 64)
                return false;
            float chromaFrac = chromaOk / (float)opaque;
            if (chromaFrac < 0.05f)
                return false;
            float avg = (sumR + sumG + sumB) / (3f * opaque);
            // CoverOnly / empty RT tends to sit near black-brown with no leaf signal.
            if (avg < 18f)
                return false;

            // Y-card UVs live in the left atlas half — both frames must have foliage.
            int mid = atlas.width / 2;
            if (mid >= 8 && !FrameHalfLooksPopulated(px, atlas.width, atlas.height, 0, mid))
                return false;
            if (mid >= 8
                && !FrameHalfLooksPopulated(px, atlas.width, atlas.height, mid, atlas.width))
                return false;
            return true;
        }

        static bool FrameHalfLooksPopulated(Color32[] px, int w, int h, int x0, int x1)
        {
            int opaque = 0;
            int chroma = 0;
            int n = (x1 - x0) * h;
            if (n <= 0)
                return false;
            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                for (int x = x0; x < x1; x++)
                {
                    Color32 c = px[row + x];
                    if (c.a < 80)
                        continue;
                    opaque++;
                    int max = c.r > c.g ? (c.r > c.b ? c.r : c.b) : (c.g > c.b ? c.g : c.b);
                    int min = c.r < c.g ? (c.r < c.b ? c.r : c.b) : (c.g < c.b ? c.g : c.b);
                    if (max - min >= 20)
                        chroma++;
                }
            }

            float opaqueFrac = opaque / (float)n;
            return opaqueFrac >= 0.01f && chroma >= opaque * 0.05f;
        }

        static bool FrameTexLooksPopulated(Texture2D tex)
        {
            if (tex == null)
                return false;
            return FrameHalfLooksPopulated(
                tex.GetPixels32(), tex.width, tex.height, 0, tex.width);
        }

        static bool TryBakeAtlas(GameObject prefab, out Texture2D atlas, out Vector2 quadSize)
        {
            atlas = null;
            quadSize = Vector2.one;
            GameObject sample = null;
            GameObject camPivot = null;
            RenderTexture frameRt = null;

            try
            {
                sample = Object.Instantiate(prefab, Vector3.zero, Quaternion.identity);
                sample.hideFlags = HideFlags.HideAndDontSave;
                sample.name = prefab.name + "_NoonBbBake";
                sample.transform.localScale = Vector3.one;
                PrepareLod0Only(sample);

                if (!TryBounds(sample, out Bounds bounds) || bounds.size.sqrMagnitude < 1e-6f)
                    return false;

                FitBillboardQuadToBakeFrame(sample, bounds);

                float horiz = Mathf.Max(bounds.size.x, bounds.size.z);
                float vert = Mathf.Max(0.01f, bounds.size.y);
                quadSize = new Vector2(Mathf.Max(0.01f, horiz * FramePadding), vert * FramePadding);

                int frameRes = AtlasWidth / FrameCount;
                float maxSize = Mathf.Max(quadSize.x, quadSize.y);

                camPivot = new GameObject("NoonBbCamPivot") { hideFlags = HideFlags.HideAndDontSave };
                var camGo = new GameObject("NoonBbCam") { hideFlags = HideFlags.HideAndDontSave };
                camGo.transform.SetParent(camPivot.transform, false);
                var cam = camGo.AddComponent<Camera>();
                // Preview bypasses Game-only locomotion bookkeeping; targetTexture
                // also bypasses CoverOnly early-out in CameraRenderer.
                cam.cameraType = CameraType.Preview;
                cam.orthographic = true;
                cam.orthographicSize = maxSize * 0.5f;
                cam.nearClipPlane = 0.01f;
                cam.farClipPlane = maxSize * 2f;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = BakeClearKey;
                cam.allowMSAA = false;
                cam.allowHDR = false;
                cam.enabled = false;
                cam.forceIntoRenderTexture = true;
                cam.transform.localPosition = new Vector3(0f, bounds.center.y, -maxSize);

                frameRt = RenderTexture.GetTemporary(
                    frameRes, frameRes, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                cam.targetTexture = frameRt;

                SetLayerRecursive(sample.transform, BakeLayer);
                cam.cullingMask = 1 << BakeLayer;

                float step = 360f / FrameCount;
                var read = new Texture2D(frameRes, frameRes, TextureFormat.RGBA32, false, false);
                atlas = new Texture2D(AtlasWidth, frameRes, TextureFormat.RGBA32, false, false);

                // Warm up SRP + keyword variants; first real capture after CoverOnly
                // clear / domain reload can otherwise land as a punched-clear frame.
                camPivot.transform.rotation = Quaternion.identity;
                for (int warm = 0; warm < 3; warm++)
                    cam.Render();

                var framePixels = new Color[FrameCount][];
                for (int f = 0; f < FrameCount; f++)
                {
                    camPivot.transform.rotation = Quaternion.AngleAxis(step * f, Vector3.up);
                    bool ok = false;
                    for (int attempt = 0; attempt < 6; attempt++)
                    {
                        cam.Render();
                        // One extra submit — some editor frames only flush on the next Render.
                        cam.Render();
                        var prev = RenderTexture.active;
                        RenderTexture.active = frameRt;
                        read.ReadPixels(new Rect(0, 0, frameRes, frameRes), 0, 0, false);
                        PunchClearKey(read);
                        read.Apply(false, false);
                        RenderTexture.active = prev;
                        if (FrameTexLooksPopulated(read))
                        {
                            framePixels[f] = read.GetPixels();
                            ok = true;
                            break;
                        }
                    }

                    if (!ok)
                        framePixels[f] = null;
                }

                // Y-card UVs are in atlas half 0. If only a later yaw landed, copy it left.
                if (framePixels[0] == null)
                {
                    for (int f = 1; f < FrameCount; f++)
                    {
                        if (framePixels[f] == null)
                            continue;
                        framePixels[0] = (Color[])framePixels[f].Clone();
                        Debug.LogWarning(
                            $"[BakeTreeBillboardFromLod0] {prefab.name}: frame0 empty, "
                            + $"copied frame{f} into left atlas half");
                        break;
                    }
                }

                for (int f = 1; f < FrameCount; f++)
                {
                    if (framePixels[f] == null && framePixels[0] != null)
                        framePixels[f] = (Color[])framePixels[0].Clone();
                }

                if (framePixels[0] == null)
                {
                    Object.DestroyImmediate(read);
                    Object.DestroyImmediate(atlas);
                    atlas = null;
                    return false;
                }

                for (int f = 0; f < FrameCount; f++)
                    atlas.SetPixels(f * frameRes, 0, frameRes, frameRes, framePixels[f]);

                atlas.Apply(false, false);
                Object.DestroyImmediate(read);
                return true;
            }
            finally
            {
                if (frameRt != null)
                    RenderTexture.ReleaseTemporary(frameRt);
                if (sample != null)
                    Object.DestroyImmediate(sample);
                if (camPivot != null)
                    Object.DestroyImmediate(camPivot);
            }
        }

        static void PrepareLod0Only(GameObject sample)
        {
            var keep = new HashSet<Renderer>();
            var lod = sample.GetComponentInChildren<LODGroup>(true);
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

                // Otherwise the ortho camera's screen height picks LOD_Billboard and
                // the bake photographs the old atlas instead of the near mesh.
                lod.enabled = false;
            }

            var renderers = sample.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                var r = renderers[i];
                if (r == null)
                    continue;
                string goName = r.gameObject.name;
                bool isBillboard =
                    goName == "LOD_Billboard"
                    || goName.IndexOf("Billboard", System.StringComparison.OrdinalIgnoreCase) >= 0
                    || goName.IndexOf("Impostor", System.StringComparison.OrdinalIgnoreCase) >= 0;
                bool use = !isBillboard && (keep.Count == 0 || keep.Contains(r));
                r.enabled = use;
                if (!use)
                    continue;
                ActivateAncestors(r.transform, sample.transform);
                if (r is MeshRenderer mr)
                {
                    mr.shadowCastingMode = ShadowCastingMode.On;
                    mr.receiveShadows = true;
                }
            }
        }

        static void ActivateAncestors(Transform t, Transform stop)
        {
            while (t != null)
            {
                t.gameObject.SetActive(true);
                if (t == stop)
                    break;
                t = t.parent;
            }
        }

        static bool TryBounds(GameObject root, out Bounds bounds)
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
        /// Place the Y-card on the same ortho frame the atlas was shot with.
        /// UV.x is inside the left frame (0..0.5 of a 2-frame atlas). Card stays on Z=0
        /// and the bounds box is a yaw cylinder so the facing quad is not culled.
        /// </summary>
        static void FitBillboardQuadToBakeFrame(GameObject sample, Bounds bounds)
        {
            Transform bb = null;
            var transforms = sample.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < transforms.Length; i++)
            {
                if (transforms[i].name == "LOD_Billboard")
                {
                    bb = transforms[i];
                    break;
                }
            }

            if (bb == null)
                return;
            var filter = bb.GetComponent<MeshFilter>();
            if (filter == null || filter.sharedMesh == null)
                return;

            Mesh mesh = filter.sharedMesh;
            Vector2[] uv = mesh.uv;
            Vector3[] verts = mesh.vertices;
            if (uv == null || verts == null || uv.Length == 0 || uv.Length != verts.Length)
                return;

            const float pad = FramePadding;
            Vector3 size = bounds.size;
            float horiz = Mathf.Max(size.x, size.z);
            float vert = Mathf.Max(0.01f, size.y);
            float frame = Mathf.Max(horiz * pad, vert * pad);
            float viewMinX = -frame * 0.5f;
            float viewMinY = bounds.center.y - frame * 0.5f;

            float minY = float.PositiveInfinity;
            float maxY = float.NegativeInfinity;
            float radius = 0f;
            for (int i = 0; i < verts.Length; i++)
            {
                float fracU = uv[i].x * 2f;
                float fracV = uv[i].y;
                float x = viewMinX + fracU * frame;
                float y = viewMinY + fracV * frame;
                verts[i] = new Vector3(x, y, 0f);
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
                radius = Mathf.Max(radius, Mathf.Abs(x));
            }

            mesh.vertices = verts;
            mesh.bounds = new Bounds(
                new Vector3(0f, (minY + maxY) * 0.5f, 0f),
                new Vector3(radius * 2f, Mathf.Max(0.01f, maxY - minY), radius * 2f));
            EditorUtility.SetDirty(mesh);
        }

        static void SetLayerRecursive(Transform t, int layer)
        {
            t.gameObject.layer = layer;
            for (int i = 0; i < t.childCount; i++)
                SetLayerRecursive(t.GetChild(i), layer);
        }

        static void PunchClearKey(Texture2D tex)
        {
            if (tex == null)
                return;
            var px = tex.GetPixels();
            const float threshSq = 0.18f * 0.18f;
            for (int i = 0; i < px.Length; i++)
            {
                float dr = px[i].r - BakeClearKey.r;
                float dg = px[i].g - BakeClearKey.g;
                float db = px[i].b - BakeClearKey.b;
                if (dr * dr + dg * dg + db * db < threshSq)
                    px[i] = Color.clear;
            }

            tex.SetPixels(px);
        }

        /// <summary>
        /// Opaque neutral-grey and magenta-tinted texels are clear-color bleed, not foliage.
        /// Copy RGB from the nearest real texel and keep alpha, so the card keeps its shape
        /// without a grey outline. Each atlas half is filled on its own.
        /// </summary>
        static void FillFringeFromNeighbors(Texture2D tex)
        {
            if (tex == null)
                return;
            var px = tex.GetPixels32();
            int w = tex.width;
            int h = tex.height;
            if (w < 4 || h < 4 || px == null || px.Length != w * h)
                return;
            int mid = w / 2;
            FillFringeFrame(px, w, h, 0, mid);
            FillFringeFrame(px, w, h, mid, w);
            tex.SetPixels32(px);
            tex.Apply(false, false);
        }

        static bool IsFringeTexel(Color32 c)
        {
            if (c.a < 128)
                return false;
            int max = System.Math.Max(c.r, System.Math.Max(c.g, c.b));
            int min = System.Math.Min(c.r, System.Math.Min(c.g, c.b));
            // Grey / magenta bleed is not a leaf or warm bark texel.
            if (max - min < 15)
                return true;
            bool green = c.g >= c.r && c.g >= c.b;
            bool yellow = c.r >= c.g && c.g >= c.b && c.r >= c.b + 20;
            return !(green || yellow);
        }

        static void FillFringeFrame(Color32[] px, int w, int h, int x0, int x1)
        {
            int fw = x1 - x0;
            if (fw < 4)
                return;
            int n = fw * h;
            var fringe = new bool[n];
            var has = new bool[n];
            var rgb = new Color32[n];
            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                int mrow = y * fw;
                for (int x = 0; x < fw; x++)
                {
                    int i = mrow + x;
                    Color32 c = px[row + x0 + x];
                    bool bad = IsFringeTexel(c);
                    fringe[i] = bad;
                    if (c.a >= 128 && !bad)
                    {
                        has[i] = true;
                        rgb[i] = c;
                    }
                }
            }

            for (int pass = 0; pass < FringeFillPx; pass++)
            {
                bool grew = false;
                var hasNext = (bool[])has.Clone();
                var rgbNext = (Color32[])rgb.Clone();
                for (int y = 0; y < h; y++)
                {
                    int mrow = y * fw;
                    for (int x = 0; x < fw; x++)
                    {
                        int i = mrow + x;
                        if (!fringe[i] || has[i])
                            continue;
                        Color32 sample = default;
                        if (TryFilled(x - 1, y, ref sample)
                            || TryFilled(x + 1, y, ref sample)
                            || TryFilled(x, y - 1, ref sample)
                            || TryFilled(x, y + 1, ref sample))
                        {
                            rgbNext[i] = sample;
                            hasNext[i] = true;
                            grew = true;
                        }
                    }
                }

                if (!grew)
                    break;
                has = hasNext;
                rgb = rgbNext;

                bool TryFilled(int sx, int sy, ref Color32 dest)
                {
                    if ((uint)sx >= (uint)fw || (uint)sy >= (uint)h)
                        return false;
                    int si = sy * fw + sx;
                    if (!has[si])
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
                    if (!fringe[i] || !has[i])
                        continue;
                    Color32 c = px[row + x0 + x];
                    c.r = rgb[i].r;
                    c.g = rgb[i].g;
                    c.b = rgb[i].b;
                    px[row + x0 + x] = c;
                }
            }
        }

        static void DilateRgbIntoTransparent(Texture2D tex, float cutoff)
        {
            if (tex == null)
                return;
            var px = tex.GetPixels32();
            int w = tex.width;
            int h = tex.height;
            if (w < 4 || h < 4 || px == null || px.Length != w * h)
                return;
            int mid = w / 2;
            DilateFrame(px, w, h, 0, mid, cutoff);
            DilateFrame(px, w, h, mid, w, cutoff);
            tex.SetPixels32(px);
            tex.Apply(false, false);
        }

        static void DilateFrame(Color32[] px, int w, int h, int x0, int x1, float cutoff)
        {
            int fw = x1 - x0;
            if (fw < 4)
                return;
            byte cut = (byte)Mathf.Clamp(Mathf.RoundToInt(cutoff * 255f), 1, 254);
            int n = fw * h;
            var opaque = new bool[n];
            var rgb = new Color32[n];
            var hasRgb = new bool[n];
            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                int mrow = y * fw;
                for (int x = 0; x < fw; x++)
                {
                    int i = mrow + x;
                    Color32 c = px[row + x0 + x];
                    opaque[i] = c.a >= cut;
                    if (opaque[i])
                    {
                        rgb[i] = c;
                        hasRgb[i] = true;
                    }
                }
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
                        if (TryNbr(x - 1, y, ref sample)
                            || TryNbr(x + 1, y, ref sample)
                            || TryNbr(x, y - 1, ref sample)
                            || TryNbr(x, y + 1, ref sample))
                        {
                            rgb[i] = sample;
                            hasRgb[i] = true;
                            grew = true;
                        }
                    }
                }

                if (!grew)
                    break;

                bool TryNbr(int sx, int sy, ref Color32 dest)
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
                    Color32 c = px[row + x0 + x];
                    c.r = rgb[i].r;
                    c.g = rgb[i].g;
                    c.b = rgb[i].b;
                    px[row + x0 + x] = c;
                }
            }
        }

        static void BackupExisting(string albedoAssetPath, string prefabName)
        {
            string abs = ToAbs(albedoAssetPath);
            if (!File.Exists(abs))
                return;
            string backupAbs = Path.Combine(
                Application.dataPath, "..", BackupRel, prefabName + "_Billboard_Albedo.png");
            Directory.CreateDirectory(Path.GetDirectoryName(backupAbs) ?? ".");
            File.Copy(abs, backupAbs, overwrite: true);
        }

        static void WritePng(Texture2D src, string assetPath)
        {
            string abs = ToAbs(assetPath);
            Directory.CreateDirectory(Path.GetDirectoryName(abs) ?? ".");
            File.WriteAllBytes(abs, src.EncodeToPNG());
            AssetDatabase.ImportAsset(assetPath);
            // Preserve existing importer settings; only ensure sRGB + alpha transparency.
            var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (importer == null)
                return;
            bool dirty = false;
            if (!importer.sRGBTexture)
            {
                importer.sRGBTexture = true;
                dirty = true;
            }

            if (!importer.alphaIsTransparency)
            {
                importer.alphaIsTransparency = true;
                dirty = true;
            }

            if (dirty)
                importer.SaveAndReimport();
        }

        static void EnsureBackupDir()
        {
            string abs = Path.Combine(Application.dataPath, "..", BackupRel);
            Directory.CreateDirectory(abs);
        }

        static string ToAbs(string assetPath)
        {
            if (assetPath.StartsWith("Assets/", System.StringComparison.Ordinal)
                || assetPath.StartsWith("Assets\\", System.StringComparison.Ordinal))
            {
                return Path.Combine(
                    Application.dataPath,
                    assetPath.Substring("Assets/".Length).Replace('\\', '/'));
            }

            return Path.GetFullPath(assetPath);
        }

        /// <summary>Temporary noon light + zero ambient / fog for the bake session.</summary>
        sealed class BakeEnvironmentScope : System.IDisposable
        {
            readonly AmbientMode _prevAmbientMode;
            readonly Color _prevAmbientLight;
            readonly SphericalHarmonicsL2 _prevProbe;
            readonly bool _prevFog;
            readonly float _prevFogEnabled;
            readonly float _prevAtmosFog;
            readonly Vector4 _prevPeriodAmbient;
            readonly float _prevSkyPeriod;
            readonly Vector4 _prevSunDir;
            readonly bool _prevAlbedoCapture;
            readonly bool _prevCoverOnly;
            readonly bool _prevPrefillSuppress;
            readonly GameObject _lightGo;

            public BakeEnvironmentScope()
            {
                _prevAmbientMode = RenderSettings.ambientMode;
                _prevAmbientLight = RenderSettings.ambientLight;
                _prevProbe = RenderSettings.ambientProbe;
                _prevFog = RenderSettings.fog;
                _prevFogEnabled = Shader.GetGlobalFloat(CustomFogEnabledId);
                _prevAtmosFog = Shader.GetGlobalFloat(OilAtmosphereFogAmountId);
                _prevPeriodAmbient = Shader.GetGlobalVector(OilPeriodAmbientId);
                _prevSkyPeriod = Shader.GetGlobalFloat(OilSkyPeriodId);
                _prevSunDir = Shader.GetGlobalVector(OilSkySunDirId);
                _prevAlbedoCapture = Shader.IsKeywordEnabled(AlbedoCaptureKeyword);
                _prevCoverOnly = IslandGpuReclaim.CoverOnly;
                _prevPrefillSuppress = EnvItemStreamerLite.PrefillSuppressRender;

                // Sticky CoverOnly after leave/login would bake the loading cover into atlases.
                IslandGpuReclaim.End();
                EnvItemStreamerLite.ReleaseRenderHold();

                RenderSettings.ambientMode = AmbientMode.Flat;
                RenderSettings.ambientLight = Color.black;
                RenderSettings.ambientProbe = default;
                RenderSettings.fog = false;
                Shader.SetGlobalFloat(CustomFogEnabledId, 0f);
                Shader.SetGlobalFloat(OilAtmosphereFogAmountId, 0f);
                Shader.SetGlobalVector(OilPeriodAmbientId, Vector4.zero);
                Shader.SetGlobalFloat(OilSkyPeriodId, 1f);

                Vector3 sunDir = OilSkyboxTime.SunDirectionFromDayT(12f / 24f);
                if (sunDir.sqrMagnitude < 1e-8f)
                    sunDir = new Vector3(0.2f, 0.9f, 0.2f).normalized;
                Shader.SetGlobalVector(OilSkySunDirId, sunDir);
                Shader.EnableKeyword(AlbedoCaptureKeyword);

                _lightGo = new GameObject("NoonBbBakeLight") { hideFlags = HideFlags.HideAndDontSave };
                var light = _lightGo.AddComponent<Light>();
                light.type = LightType.Directional;
                light.color = Color.white;
                light.intensity = 1.15f;
                light.shadows = LightShadows.Soft;
                // Light transform forward = -sunDir (Unity lights point along +Z of transform).
                _lightGo.transform.rotation = Quaternion.LookRotation(-sunDir, Vector3.up);
            }

            public void Dispose()
            {
                if (_lightGo != null)
                    Object.DestroyImmediate(_lightGo);

                RenderSettings.ambientMode = _prevAmbientMode;
                RenderSettings.ambientLight = _prevAmbientLight;
                RenderSettings.ambientProbe = _prevProbe;
                RenderSettings.fog = _prevFog;
                Shader.SetGlobalFloat(CustomFogEnabledId, _prevFogEnabled);
                Shader.SetGlobalFloat(OilAtmosphereFogAmountId, _prevAtmosFog);
                Shader.SetGlobalVector(OilPeriodAmbientId, _prevPeriodAmbient);
                Shader.SetGlobalFloat(OilSkyPeriodId, _prevSkyPeriod);
                Shader.SetGlobalVector(OilSkySunDirId, _prevSunDir);
                if (_prevAlbedoCapture)
                    Shader.EnableKeyword(AlbedoCaptureKeyword);
                else
                    Shader.DisableKeyword(AlbedoCaptureKeyword);

                // Do not re-arm CoverOnly/Prefill via Begin() — that fires leave reclaim.
                // Edit-mode bake clears sticky holds; play-mode re-entry sets them again.
                if (_prevCoverOnly || _prevPrefillSuppress)
                {
                    Debug.LogWarning(
                        "[BakeTreeBillboardFromLod0] Cleared sticky CoverOnly/Prefill for bake; "
                        + $"was cover={_prevCoverOnly} prefill={_prevPrefillSuppress}");
                }
            }
        }
    }
}
#endif
