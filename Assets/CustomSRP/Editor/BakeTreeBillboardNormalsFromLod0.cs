#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace CustomSRP.Editor
{
    /// <summary>
    /// Bake LOD0 near-tree object-space normals into existing billboard normal atlases.
    /// Front+side ortho (2 frames → 2048×H). Does not touch albedo / mesh / shader assets.
    /// </summary>
    public static class BakeTreeBillboardNormalsFromLod0
    {
        const string PrefabsRoot = "Assets/GameMain/Environment/Prefabs/DaoYu1";
        static readonly string[] Folders = { "Songshu", "Lvshu" };

        const int AtlasWidth = 2048;
        const int FrameCount = 2;
        const float FramePadding = 1.06f;
        const float Cutoff = 0.32f;
        const int DilatePx = 10;
        const int BakeLayer = 30;
        const string BakeShaderName = "Hidden/CustomSRP/BillboardNormalBake";

        static readonly Color FlatNormalBg = new Color(0.5f, 0.5f, 1f, 0f);
        static readonly string[] NormalTexProps =
        {
            "_Leaf_Normal",
            "_Trunk_Normal",
            "_Normal_Map",
            "_BumpMap",
            "_NormalMap",
        };

        const string BackupRel = "_scratch/billboard_normal_backup";
        const string BakeFlagRelPath = "Temp/bake_tree_billboard_normals.flag";

        static bool _flagBakeQueued;

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
            try { File.Delete(flag); } catch { return; }

            _flagBakeQueued = true;
            Debug.Log("[BakeTreeBillboardNormalsFromLod0] Flag detected — baking OS normal atlases");
            try
            {
                ExecuteMethodEntry();
            }
            finally
            {
                _flagBakeQueued = false;
            }
        }

        [MenuItem("Window/Game/Bake Tree Billboard Normals From LOD0")]
        public static void BakeAllMenu()
        {
            int ok = BakeAll(out string report);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            EditorUtility.DisplayDialog(
                "Bake Tree Billboard Normals",
                $"完成 {ok} 棵。\n\n{report}",
                "OK");
        }

        /// <summary>Batchmode: -executeMethod CustomSRP.Editor.BakeTreeBillboardNormalsFromLod0.ExecuteMethodEntry</summary>
        public static void ExecuteMethodEntry()
        {
            int ok = BakeAll(out string report);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"[BakeTreeBillboardNormalsFromLod0] done ok={ok}\n{report}");
        }

        public static int BakeAll(out string report)
        {
            var sb = new StringBuilder();
            var paths = CollectPrefabPaths();
            int ok = 0;
            EnsureBackupDir();

            var bakeShader = Shader.Find(BakeShaderName);
            if (bakeShader == null)
            {
                report = $"FAIL missing shader {BakeShaderName}";
                Debug.LogError($"[BakeTreeBillboardNormalsFromLod0] {report}");
                return 0;
            }

            int cachedMip = QualitySettings.globalTextureMipmapLimit;
            QualitySettings.globalTextureMipmapLimit = 0;

            try
            {
                for (int i = 0; i < paths.Count; i++)
                {
                    string path = paths[i];
                    string name = Path.GetFileNameWithoutExtension(path);
                    EditorUtility.DisplayProgressBar(
                        "Bake Tree Billboard Normals",
                        name,
                        (i + 1f) / paths.Count);

                    try
                    {
                        if (BakeOne(path, bakeShader, sb))
                            ok++;
                    }
                    catch (System.Exception ex)
                    {
                        sb.AppendLine($"FAIL {name}: {ex.Message}");
                        Debug.LogException(ex);
                    }
                }
            }
            finally
            {
                QualitySettings.globalTextureMipmapLimit = cachedMip;
                EditorUtility.ClearProgressBar();
            }

            sb.AppendLine($"OK {ok}/{paths.Count}");
            report = sb.ToString();
            Debug.Log($"[BakeTreeBillboardNormalsFromLod0]\n{report}");
            return ok;
        }

        static List<string> CollectPrefabPaths()
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
                    string normalPath = BillboardNormalPath(path, name);
                    // Prefer trees that already have a billboard folder (albedo or normal).
                    string albedoPath = BillboardAlbedoPath(path, name);
                    if (!File.Exists(ToAbs(albedoPath)) && !File.Exists(ToAbs(normalPath)))
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

        static string BillboardNormalPath(string prefabPath, string prefabName) =>
            $"{BillboardFolder(prefabPath, prefabName)}/{prefabName}_Billboard_Normal.png";

        static string BillboardMatPath(string prefabPath, string prefabName) =>
            $"{BillboardFolder(prefabPath, prefabName)}/{prefabName}_Billboard.mat";

        static bool BakeOne(string prefabPath, Shader bakeShader, StringBuilder sb)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab == null)
            {
                sb.AppendLine($"FAIL load {prefabPath}");
                return false;
            }

            string name = prefab.name;
            string normalPath = BillboardNormalPath(prefabPath, name);

            if (!TryBakeNormalAtlas(prefab, bakeShader, out Texture2D atlas, out Vector2 quadSize))
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

                DilateRgbIntoTransparent(atlas, Cutoff);
                BackupExisting(normalPath, name);
                WriteNormalPng(atlas, normalPath);
                WireBillboardMaterial(prefabPath, name, normalPath);
                sb.AppendLine($"OK {name} size={quadSize.x:F1}x{quadSize.y:F1}");
                return true;
            }
            finally
            {
                Object.DestroyImmediate(atlas);
            }
        }

        /// <summary>Reject flat/garbage captures (all ~flat normal or no opaque).</summary>
        static bool AtlasLooksValid(Texture2D atlas)
        {
            if (atlas == null)
                return false;
            var px = atlas.GetPixels32();
            if (px == null || px.Length < 64)
                return false;

            int opaque = 0;
            long sumR = 0, sumG = 0, sumB = 0;
            for (int i = 0; i < px.Length; i++)
            {
                if (px[i].a < 80)
                    continue;
                opaque++;
                sumR += px[i].r;
                sumG += px[i].g;
                sumB += px[i].b;
            }

            if (opaque < px.Length * 0.005f)
                return false;

            float meanR = sumR / (float)opaque;
            float meanG = sumG / (float)opaque;
            float meanB = sumB / (float)opaque;
            double varAcc = 0;
            for (int i = 0; i < px.Length; i++)
            {
                if (px[i].a < 80)
                    continue;
                double dr = px[i].r - meanR;
                double dg = px[i].g - meanG;
                double db = px[i].b - meanB;
                varAcc += dr * dr + dg * dg + db * db;
            }

            double std = System.Math.Sqrt(varAcc / opaque);
            // Flat (128,128,255) has ~0 variance; real OS normals should vary.
            return std >= 4.0;
        }

        static bool TryBakeNormalAtlas(
            GameObject prefab, Shader bakeShader, out Texture2D atlas, out Vector2 quadSize)
        {
            atlas = null;
            quadSize = Vector2.one;
            GameObject sample = null;
            GameObject camPivot = null;
            RenderTexture frameRt = null;
            var temps = new List<Material>(16);
            var replacements = new List<(MeshRenderer mr, Material[] original)>(8);

            try
            {
                sample = Object.Instantiate(prefab, Vector3.zero, Quaternion.identity);
                sample.hideFlags = HideFlags.HideAndDontSave;
                sample.name = prefab.name + "_NrmBbBake";
                sample.transform.localScale = Vector3.one;
                PrepareLod0Only(sample);

                if (!TryBounds(sample, out Bounds bounds) || bounds.size.sqrMagnitude < 1e-6f)
                    return false;

                float horiz = Mathf.Max(bounds.size.x, bounds.size.z);
                float vert = Mathf.Max(0.01f, bounds.size.y);
                quadSize = new Vector2(Mathf.Max(0.01f, horiz * FramePadding), vert * FramePadding);

                int frameRes = AtlasWidth / FrameCount;
                float maxSize = Mathf.Max(quadSize.x, quadSize.y);

                camPivot = new GameObject("NrmBbCamPivot") { hideFlags = HideFlags.HideAndDontSave };
                var camGo = new GameObject("NrmBbCam") { hideFlags = HideFlags.HideAndDontSave };
                camGo.transform.SetParent(camPivot.transform, false);
                var cam = camGo.AddComponent<Camera>();
                cam.orthographic = true;
                cam.orthographicSize = maxSize * 0.5f;
                cam.nearClipPlane = 0.01f;
                cam.farClipPlane = maxSize * 2f;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = FlatNormalBg;
                cam.allowMSAA = false;
                cam.allowHDR = false;
                cam.enabled = false;
                cam.forceIntoRenderTexture = true;
                cam.transform.localPosition = new Vector3(0f, bounds.center.y, -maxSize);

                frameRt = RenderTexture.GetTemporary(
                    frameRes, frameRes, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
                cam.targetTexture = frameRt;

                SetLayerRecursive(sample.transform, BakeLayer);
                cam.cullingMask = 1 << BakeLayer;

                var mrs = sample.GetComponentsInChildren<MeshRenderer>(true);
                for (int i = 0; i < mrs.Length; i++)
                {
                    var mr = mrs[i];
                    if (mr == null || !mr.enabled)
                        continue;
                    var srcs = mr.sharedMaterials;
                    if (srcs == null || srcs.Length == 0)
                        continue;
                    var masks = new Material[srcs.Length];
                    for (int s = 0; s < srcs.Length; s++)
                    {
                        var maskMat = new Material(bakeShader) { hideFlags = HideFlags.HideAndDontSave };
                        BindBakeMaterial(maskMat, srcs[s]);
                        temps.Add(maskMat);
                        masks[s] = maskMat;
                    }

                    replacements.Add((mr, srcs));
                    mr.sharedMaterials = masks;
                }

                if (replacements.Count == 0)
                    return false;

                float step = 360f / FrameCount;
                var read = new Texture2D(frameRes, frameRes, TextureFormat.RGBA32, false, true);
                atlas = new Texture2D(AtlasWidth, frameRes, TextureFormat.RGBA32, false, true);

                for (int f = 0; f < FrameCount; f++)
                {
                    camPivot.transform.rotation = Quaternion.AngleAxis(step * f, Vector3.up);
                    cam.Render();
                    var prev = RenderTexture.active;
                    RenderTexture.active = frameRt;
                    read.ReadPixels(new Rect(0, 0, frameRes, frameRes), 0, 0, false);
                    read.Apply(false, false);
                    RenderTexture.active = prev;
                    atlas.SetPixels(f * frameRes, 0, frameRes, frameRes, read.GetPixels());
                }

                atlas.Apply(false, false);
                Object.DestroyImmediate(read);
                return true;
            }
            finally
            {
                for (int i = 0; i < replacements.Count; i++)
                    replacements[i].mr.sharedMaterials = replacements[i].original;
                for (int i = 0; i < temps.Count; i++)
                    Object.DestroyImmediate(temps[i]);
                if (frameRt != null)
                    RenderTexture.ReleaseTemporary(frameRt);
                if (sample != null)
                    Object.DestroyImmediate(sample);
                if (camPivot != null)
                    Object.DestroyImmediate(camPivot);
            }
        }

        static void BindBakeMaterial(Material dest, Material src)
        {
            Texture tex = null;
            if (src != null)
            {
                if (src.HasProperty("_BaseMap"))
                    tex = src.GetTexture("_BaseMap");
                if (tex == null && src.HasProperty("_MainTex"))
                    tex = src.GetTexture("_MainTex");
                if (tex == null && src.HasProperty("_Texture"))
                    tex = src.GetTexture("_Texture");
                if (tex == null)
                    tex = src.mainTexture;
            }

            dest.SetTexture("_BaseMap", tex != null ? tex : Texture2D.whiteTexture);
            if (src != null && src.HasProperty("_BaseMap"))
            {
                dest.SetTextureScale("_BaseMap", src.GetTextureScale("_BaseMap"));
                dest.SetTextureOffset("_BaseMap", src.GetTextureOffset("_BaseMap"));
            }

            float cutoff = Cutoff;
            if (src != null)
            {
                if (src.HasProperty("_Cutoff"))
                    cutoff = src.GetFloat("_Cutoff");
                else if (src.HasProperty("_AlphaClip"))
                    cutoff = src.GetFloat("_AlphaClip");
            }

            dest.SetFloat("_Cutoff", cutoff);
            BindBakeBump(dest, src);
        }

        static void BindBakeBump(Material dest, Material src)
        {
            dest.SetFloat("_HasBumpMap", 0f);
            dest.SetFloat("_BumpScale", 1f);
            if (src == null)
                return;

            Texture bump = null;
            if (src.HasProperty("_Leaf_Normal") && src.HasProperty("_Enable_Leaf_Normal") &&
                src.GetFloat("_Enable_Leaf_Normal") > 0.5f)
                bump = src.GetTexture("_Leaf_Normal");
            else if (src.HasProperty("_Trunk_Normal") && src.HasProperty("_Enable_Trunk_Normal") &&
                     src.GetFloat("_Enable_Trunk_Normal") > 0.5f)
                bump = src.GetTexture("_Trunk_Normal");
            else
            {
                for (int i = 0; i < NormalTexProps.Length; i++)
                {
                    string prop = NormalTexProps[i];
                    if (prop == "_Leaf_Normal" || prop == "_Trunk_Normal")
                        continue;
                    if (!src.HasProperty(prop))
                        continue;
                    var t = src.GetTexture(prop);
                    if (t == null)
                        continue;
                    // OilNPR leaf/bark: skip empty / default; real tangent maps only.
                    bump = t;
                    break;
                }
            }

            if (src.HasProperty("_Enable_Normal_Texture") && src.GetFloat("_Enable_Normal_Texture") < 0.5f)
                bump = null;

            // Near OilNPR foliage uses _BumpMap as billboard OS slot — usually null.
            // If assigned, treat as tangent bump for bake (rare bark maps).
            float bumpScale = 1f;
            if (src.HasProperty("_Leaf_Normal_Strength"))
                bumpScale = src.GetFloat("_Leaf_Normal_Strength");
            else if (src.HasProperty("_Trunk_Normal_Strength"))
                bumpScale = src.GetFloat("_Trunk_Normal_Strength");
            else if (src.HasProperty("_Normal_Intensity"))
                bumpScale = src.GetFloat("_Normal_Intensity");
            else if (src.HasProperty("_BumpScale"))
                bumpScale = src.GetFloat("_BumpScale");

            if (bump == null)
                return;

            dest.SetTexture("_BumpMap", bump);
            dest.SetFloat("_HasBumpMap", 1f);
            dest.SetFloat("_BumpScale", bumpScale);
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
                    mr.shadowCastingMode = ShadowCastingMode.Off;
                    mr.receiveShadows = false;
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

        static void SetLayerRecursive(Transform t, int layer)
        {
            t.gameObject.layer = layer;
            for (int i = 0; i < t.childCount; i++)
                SetLayerRecursive(t.GetChild(i), layer);
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

        static void BackupExisting(string normalAssetPath, string prefabName)
        {
            string abs = ToAbs(normalAssetPath);
            if (!File.Exists(abs))
                return;
            string backupAbs = Path.Combine(
                Application.dataPath, "..", BackupRel, prefabName + "_Billboard_Normal.png");
            Directory.CreateDirectory(Path.GetDirectoryName(backupAbs) ?? ".");
            File.Copy(abs, backupAbs, overwrite: true);
        }

        static void WriteNormalPng(Texture2D src, string assetPath)
        {
            string abs = ToAbs(assetPath);
            Directory.CreateDirectory(Path.GetDirectoryName(abs) ?? ".");
            File.WriteAllBytes(abs, src.EncodeToPNG());
            AssetDatabase.ImportAsset(assetPath);

            var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (importer == null)
                return;

            // OS normals in RGB + coverage in A. Never TextureImporterType.NormalMap (DXT5nm).
            importer.textureType = TextureImporterType.Default;
            importer.convertToNormalmap = false;
            importer.sRGBTexture = false;
            importer.alphaIsTransparency = false;
            importer.mipmapEnabled = true;
            importer.mipMapsPreserveCoverage = false;
            importer.alphaTestReferenceValue = 0.5f;
            importer.textureCompression = TextureImporterCompression.Compressed;
            importer.maxTextureSize = 2048;
            importer.crunchedCompression = false;

            void Plat(string platName, int max, bool over, TextureImporterFormat fmt)
            {
                var s = importer.GetPlatformTextureSettings(platName);
                s.name = platName;
                s.overridden = over;
                s.maxTextureSize = max;
                s.format = fmt;
                s.textureCompression = TextureImporterCompression.Compressed;
                s.compressionQuality = 50;
                s.crunchedCompression = false;
                importer.SetPlatformTextureSettings(s);
            }

            Plat("DefaultTexturePlatform", 2048, false, TextureImporterFormat.Automatic);
            Plat("Standalone", 2048, true, TextureImporterFormat.Automatic);
            Plat("Android", 1024, true, TextureImporterFormat.ASTC_6x6);
            Plat("iPhone", 1024, true, TextureImporterFormat.ASTC_6x6);
            importer.SaveAndReimport();
        }

        static void WireBillboardMaterial(string prefabPath, string prefabName, string normalAssetPath)
        {
            string matPath = BillboardMatPath(prefabPath, prefabName);
            var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            if (mat == null)
                return;

            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(normalAssetPath);
            if (tex == null)
                return;

            bool dirty = false;
            if (mat.HasProperty("_BumpMap") && mat.GetTexture("_BumpMap") != tex)
            {
                mat.SetTexture("_BumpMap", tex);
                dirty = true;
            }

            if (mat.HasProperty("_NormalStrength"))
            {
                float s = mat.GetFloat("_NormalStrength");
                if (s < 0.01f)
                {
                    mat.SetFloat("_NormalStrength", 0.85f);
                    dirty = true;
                }
            }

            if (dirty)
                EditorUtility.SetDirty(mat);
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
    }
}
#endif
