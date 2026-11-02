#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace Game.Editor
{
    /// <summary>
    /// Rebuild mid/low tree mesh LODs from the near (high) mesh via Blender Decimate.
    /// Menu: Window/Game/Rebuild Tree LOD From High
    /// Batch: -executeMethod Game.Editor.RebuildTreeLodFromHigh.BatchGenerateAll
    /// Flag: Temp/rebuild_tree_lod_generate_all / Temp/rebuild_tree_lod_commit_&lt;id&gt;
    /// </summary>
    public sealed class RebuildTreeLodFromHigh : EditorWindow
    {
        // OBJ work + backups live under Assets/_scratch so Mesh previews are importable.
        const string ScratchRel = "Assets/_scratch/tree_lod_rebuild";
        const string OakRebuiltRel = "Assets/GameMain/Environment/Models/OakRebuilt";
        const string BlenderScriptRel = "Tools/rebuild_tree_lod.py";
        const string BlenderApp = "/Applications/Blender.app/Contents/MacOS/Blender";
        const string FlagGenerateAll = "Temp/rebuild_tree_lod_generate_all";
        const string PreviewRootName = "__TreeLodRebuildPreview";

        static bool s_Watching;
        Vector2 _scroll;
        int _selected;
        string _lastLog = "";
        float _barkRatioOverride = -1f;
        float _leafRatioOverride = -1f;

        [Serializable]
        public sealed class Job
        {
            public string Id;
            public string Label;
            public string[] PrefabPaths;
            public string HighMeshPath; // empty = resolve from prefab LOD0
            public string LowMeshPath; // TFF asset path; empty for Oak FBX
            public string CommitMeshPath;
            public string PreviewMeshPath;
            public bool IsOak;
            public string OakHighChildNameContains; // e.g. _LOD1
            public string OakLowChildNameContains; // e.g. _LOD2
        }

        [MenuItem("Window/Game/Rebuild Tree LOD From High")]
        public static void Open()
        {
            GetWindow<RebuildTreeLodFromHigh>("Rebuild Tree LOD").Show();
        }

        [InitializeOnLoadMethod]
        static void Boot()
        {
            if (s_Watching)
                return;
            s_Watching = true;
            EditorApplication.update += WatchFlags;
        }

        static void WatchFlags()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlaying)
                return;

            if (File.Exists(FlagGenerateAll))
            {
                try { File.Delete(FlagGenerateAll); } catch { return; }
                try
                {
                    Debug.Log("[RebuildTreeLod] " + GenerateAll());
                }
                catch (Exception ex)
                {
                    Debug.LogError("[RebuildTreeLod] GenerateAll failed: " + ex);
                }
                return;
            }

            string[] files;
            try { files = Directory.GetFiles("Temp", "rebuild_tree_lod_*"); }
            catch { return; }

            foreach (string file in files)
            {
                string name = Path.GetFileName(file);
                if (name.StartsWith("rebuild_tree_lod_generate_", StringComparison.Ordinal)
                    && name != "rebuild_tree_lod_generate_all")
                {
                    string id = name.Substring("rebuild_tree_lod_generate_".Length);
                    try { File.Delete(file); } catch { continue; }
                    try { Debug.Log("[RebuildTreeLod] " + GenerateOne(id)); }
                    catch (Exception ex) { Debug.LogError(ex); }
                    return;
                }

                if (name.StartsWith("rebuild_tree_lod_commit_", StringComparison.Ordinal))
                {
                    string id = name.Substring("rebuild_tree_lod_commit_".Length);
                    try { File.Delete(file); } catch { continue; }
                    try { Debug.Log("[RebuildTreeLod] " + CommitOne(id)); }
                    catch (Exception ex) { Debug.LogError(ex); }
                    return;
                }
            }
        }

        /// <summary>Unity -batchmode entry.</summary>
        public static void BatchGenerateAll()
        {
            string report = GenerateAll();
            Debug.Log("[RebuildTreeLod] BatchGenerateAll\n" + report);
            EditorApplication.Exit(0);
        }

        public static void BatchGenerateOne()
        {
            string id = Environment.GetEnvironmentVariable("TREE_LOD_JOB_ID");
            if (string.IsNullOrEmpty(id) && File.Exists("Temp/rebuild_tree_lod_job_id.txt"))
                id = File.ReadAllText("Temp/rebuild_tree_lod_job_id.txt").Trim();
            if (string.IsNullOrEmpty(id))
            {
                Debug.LogError("[RebuildTreeLod] TREE_LOD_JOB_ID missing");
                EditorApplication.Exit(2);
                return;
            }

            string report = GenerateOne(id);
            Debug.Log("[RebuildTreeLod] BatchGenerateOne " + id + "\n" + report);
            EditorApplication.Exit(0);
        }

        public static void BatchCommitOne()
        {
            string id = Environment.GetEnvironmentVariable("TREE_LOD_JOB_ID");
            if (string.IsNullOrEmpty(id) && File.Exists("Temp/rebuild_tree_lod_job_id.txt"))
                id = File.ReadAllText("Temp/rebuild_tree_lod_job_id.txt").Trim();
            string report = CommitOne(id);
            Debug.Log("[RebuildTreeLod] BatchCommitOne " + id + "\n" + report);
            EditorApplication.Exit(0);
        }

        void OnGUI()
        {
            Job[] jobs = BuildJobs();
            EditorGUILayout.LabelField("Jobs", jobs.Length.ToString());
            _barkRatioOverride = EditorGUILayout.FloatField("Bark ratio override (-1=auto)", _barkRatioOverride);
            _leafRatioOverride = EditorGUILayout.FloatField("Leaf ratio override (-1=auto)", _leafRatioOverride);

            _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.Height(280));
            for (int i = 0; i < jobs.Length; i++)
            {
                bool on = EditorGUILayout.ToggleLeft(jobs[i].Label + "  [" + jobs[i].Id + "]", _selected == i);
                if (on)
                    _selected = i;
            }
            EditorGUILayout.EndScrollView();

            using (new EditorGUI.DisabledScope(jobs.Length == 0))
            {
                if (GUILayout.Button("Generate selected preview"))
                {
                    _lastLog = GenerateOne(jobs[_selected].Id, _barkRatioOverride, _leafRatioOverride);
                }

                if (GUILayout.Button("Generate ALL previews"))
                {
                    _lastLog = GenerateAll(_barkRatioOverride, _leafRatioOverride);
                }

                if (GUILayout.Button("Spawn side-by-side preview in scene"))
                {
                    _lastLog = SpawnPreview(jobs[_selected]);
                }

                if (GUILayout.Button("Confirm replace selected"))
                {
                    if (EditorUtility.DisplayDialog(
                            "Confirm LOD replace",
                            "Overwrite low LOD for " + jobs[_selected].Label + "?",
                            "Replace", "Cancel"))
                    {
                        _lastLog = CommitOne(jobs[_selected].Id);
                    }
                }

                if (GUILayout.Button("Clear scene preview"))
                {
                    ClearPreview();
                    _lastLog = "cleared preview";
                }
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Log", EditorStyles.boldLabel);
            EditorGUILayout.TextArea(_lastLog ?? "", GUILayout.MinHeight(160));
        }

        public static string GenerateAll(float barkOverride = -1f, float leafOverride = -1f)
        {
            var sb = new StringBuilder();
            foreach (Job job in BuildJobs())
            {
                sb.AppendLine("==== " + job.Id + " ====");
                try
                {
                    sb.AppendLine(GenerateOne(job.Id, barkOverride, leafOverride));
                }
                catch (Exception ex)
                {
                    sb.AppendLine("FAIL " + ex.Message);
                }
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            return sb.ToString();
        }

        public static string GenerateOne(string id, float barkOverride = -1f, float leafOverride = -1f)
        {
            Job job = FindJob(id);
            if (job == null)
                return "unknown job id: " + id;

            EnsureDirs(job);
            Mesh high = ResolveHighMesh(job);
            Mesh oldLow = ResolveOldLowMesh(job);
            if (high == null)
                return "high mesh missing for " + id;
            high = EnsureReadableMesh(high);
            if (oldLow != null)
                oldLow = EnsureReadableMesh(oldLow);
            if (high.subMeshCount < 1)
                return "high mesh has no submeshes: " + id;

            string workDir = WorkDir(job.Id);
            string highObj = Path.Combine(workDir, "high", job.Id + "_high.obj");
            string lowObj = Path.Combine(workDir, "low", job.Id + "_low.obj");
            string reportPath = Path.Combine(workDir, "report.json");
            Directory.CreateDirectory(Path.GetDirectoryName(highObj) ?? workDir);
            Directory.CreateDirectory(Path.GetDirectoryName(lowObj) ?? workDir);

            int barkTarget = -1;
            int leafTarget = -1;
            if (oldLow != null && oldLow.subMeshCount >= 1)
            {
                barkTarget = oldLow.GetTriangles(0).Length / 3;
                if (oldLow.subMeshCount >= 2)
                    leafTarget = oldLow.GetTriangles(1).Length / 3;
            }

            WriteMeshObj(high, highObj);

            var args = new StringBuilder();
            args.Append("--python ").Append(Quote(Abs(BlenderScriptRel)));
            args.Append(" -- --high ").Append(Quote(Abs(highObj)));
            args.Append(" --out ").Append(Quote(Abs(lowObj)));
            args.Append(" --report ").Append(Quote(Abs(reportPath)));
            if (barkOverride > 0f)
                args.Append(" --bark-ratio ").Append(barkOverride.ToString(CultureInfo.InvariantCulture));
            else if (barkTarget > 0)
                args.Append(" --target-bark-tris ").Append(barkTarget);
            if (leafOverride > 0f)
                args.Append(" --leaf-ratio ").Append(leafOverride.ToString(CultureInfo.InvariantCulture));
            else if (leafTarget > 0)
                args.Append(" --target-leaf-tris ").Append(leafTarget);

            string blenderLog = RunBlender(args.ToString());
            if (!File.Exists(lowObj))
                return "Blender did not write low OBJ.\n" + blenderLog;

            Mesh preview = ReadMeshObj(lowObj, job.Id + "_preview");
            if (preview == null)
                return "failed to parse low OBJ";

            preview.name = Path.GetFileNameWithoutExtension(job.PreviewMeshPath);
            SaveMeshAsset(preview, job.PreviewMeshPath);

            var sb = new StringBuilder();
            sb.AppendLine("job=" + job.Id);
            sb.AppendLine("high=" + high.name + " verts=" + high.vertexCount + " sub=" + high.subMeshCount);
            if (oldLow != null)
                sb.AppendLine("oldLow=" + oldLow.name + " verts=" + oldLow.vertexCount);
            sb.AppendLine("preview=" + job.PreviewMeshPath + " verts=" + preview.vertexCount
                          + " tris0=" + (preview.GetTriangles(0).Length / 3)
                          + (preview.subMeshCount > 1 ? " tris1=" + (preview.GetTriangles(1).Length / 3) : ""));
            sb.AppendLine("bounds=" + preview.bounds);
            if (File.Exists(reportPath))
                sb.AppendLine(File.ReadAllText(reportPath));
            sb.AppendLine(blenderLog);
            return sb.ToString();
        }

        public static string CommitOne(string id)
        {
            Job job = FindJob(id);
            if (job == null)
                return "unknown job id: " + id;

            // Prefer offline commit script (keeps TFF GUID, patches Oak prefabs).
            string py = Abs("Tools/commit_tree_lod_preview.py");
            if (File.Exists(py))
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "python3",
                    Arguments = Quote(py) + " " + id,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WorkingDirectory = Directory.GetCurrentDirectory(),
                };
                using (var proc = Process.Start(psi))
                {
                    if (proc == null)
                        return "failed to start commit script";
                    string stdout = proc.StandardOutput.ReadToEnd();
                    string stderr = proc.StandardError.ReadToEnd();
                    proc.WaitForExit(120000);
                    AssetDatabase.Refresh();
                    RecalcLodGroups(job);
                    AssetDatabase.SaveAssets();
                    return "exit=" + proc.ExitCode + "\n" + stdout + stderr;
                }
            }

            Mesh preview = AssetDatabase.LoadAssetAtPath<Mesh>(job.PreviewMeshPath);
            if (preview == null)
                return "preview missing, generate first: " + job.PreviewMeshPath;

            string backupDir = Path.Combine(ScratchRel, "backup", job.Id);
            Directory.CreateDirectory(backupDir);

            if (job.IsOak)
            {
                Directory.CreateDirectory(OakRebuiltRel);
                Mesh committed = Instantiate(preview);
                committed.name = Path.GetFileNameWithoutExtension(job.CommitMeshPath);
                SaveMeshAsset(committed, job.CommitMeshPath);

                int rewired = 0;
                foreach (string prefabPath in job.PrefabPaths)
                    rewired += RewireOakLowMesh(prefabPath, job, committed);

                RecalcLodGroups(job);
                AssetDatabase.SaveAssets();
                return "oak commit " + job.CommitMeshPath + " rewired=" + rewired;
            }

            Mesh low = AssetDatabase.LoadAssetAtPath<Mesh>(job.LowMeshPath);
            if (low == null)
                return "low mesh missing: " + job.LowMeshPath;

            string backupPath = Path.Combine(backupDir, Path.GetFileName(job.LowMeshPath));
            if (File.Exists(job.LowMeshPath))
            {
                File.Copy(job.LowMeshPath, backupPath, true);
                if (File.Exists(job.LowMeshPath + ".meta"))
                    File.Copy(job.LowMeshPath + ".meta", backupPath + ".meta", true);
            }

            ReplaceMeshData(low, preview);
            EditorUtility.SetDirty(low);
            RecalcLodGroups(job);
            AssetDatabase.SaveAssets();
            return "tff commit replaced " + job.LowMeshPath + " (backup " + backupPath + ")";
        }

        static string SpawnPreview(Job job)
        {
            ClearPreview();
            Mesh high = ResolveHighMesh(job);
            Mesh oldLow = ResolveOldLowMesh(job);
            Mesh preview = AssetDatabase.LoadAssetAtPath<Mesh>(job.PreviewMeshPath);
            if (high == null)
                return "no high";
            if (preview == null)
                return "no preview — generate first";

            Material[] mats = ResolveMaterials(job);
            var root = new GameObject(PreviewRootName);
            float gap = Mathf.Max(high.bounds.size.x, high.bounds.size.z) * 1.4f + 1f;
            CreatePreviewChild(root.transform, "High", high, mats, new Vector3(-gap, 0f, 0f));
            if (oldLow != null)
                CreatePreviewChild(root.transform, "OldLow", oldLow, mats, Vector3.zero);
            CreatePreviewChild(root.transform, "NewLow", preview, mats, new Vector3(gap, 0f, 0f));
            Selection.activeGameObject = root;
            SceneView.lastActiveSceneView?.FrameSelected();
            return "spawned " + PreviewRootName;
        }

        static void CreatePreviewChild(Transform parent, string name, Mesh mesh, Material[] mats, Vector3 pos)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            var mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            if (mats != null && mats.Length > 0)
            {
                var arr = new Material[Mathf.Max(mesh.subMeshCount, 1)];
                for (int i = 0; i < arr.Length; i++)
                    arr[i] = mats[Mathf.Min(i, mats.Length - 1)];
                mr.sharedMaterials = arr;
            }
        }

        static void ClearPreview()
        {
            var existing = GameObject.Find(PreviewRootName);
            if (existing != null)
                DestroyImmediate(existing);
        }

        static Material[] ResolveMaterials(Job job)
        {
            foreach (string prefabPath in job.PrefabPaths)
            {
                var root = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                if (root == null)
                    continue;
                var mrs = root.GetComponentsInChildren<MeshRenderer>(true);
                foreach (var mr in mrs)
                {
                    if (mr == null || mr.sharedMaterials == null)
                        continue;
                    if (mr.sharedMaterials.Length >= 2)
                        return mr.sharedMaterials;
                }
            }

            return null;
        }

        static int RewireOakLowMesh(string prefabPath, Job job, Mesh newMesh)
        {
            GameObject root = PrefabUtility.LoadPrefabContents(prefabPath);
            int count = 0;
            try
            {
                var filters = root.GetComponentsInChildren<MeshFilter>(true);
                foreach (var mf in filters)
                {
                    if (mf == null || mf.gameObject == null)
                        continue;
                    string n = mf.gameObject.name;
                    if (!string.IsNullOrEmpty(job.OakLowChildNameContains)
                        && n.IndexOf(job.OakLowChildNameContains, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        mf.sharedMesh = newMesh;
                        count++;
                    }
                }

                PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }

            return count;
        }

        static void RecalcLodGroups(Job job)
        {
            foreach (string prefabPath in job.PrefabPaths)
            {
                GameObject root = PrefabUtility.LoadPrefabContents(prefabPath);
                try
                {
                    var lod = root.GetComponentInChildren<LODGroup>(true);
                    if (lod != null)
                    {
                        var renderers = root.GetComponentsInChildren<Renderer>(true);
                        if (renderers.Length > 0)
                        {
                            Bounds b = renderers[0].bounds;
                            for (int i = 1; i < renderers.Length; i++)
                                b.Encapsulate(renderers[i].bounds);
                            Vector3 localCenter = root.transform.InverseTransformPoint(b.center);
                            lod.localReferencePoint = localCenter;
                        }

                        lod.RecalculateBounds();
                        EditorUtility.SetDirty(lod);
                    }

                    PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
                }
                finally
                {
                    PrefabUtility.UnloadPrefabContents(root);
                }
            }
        }

        static void ReplaceMeshData(Mesh dst, Mesh src)
        {
            dst.Clear();
            dst.vertices = src.vertices;
            dst.normals = src.normals;
            dst.tangents = src.tangents;
            dst.colors = src.colors;
            dst.uv = src.uv;
            dst.uv2 = src.uv2;
            dst.subMeshCount = src.subMeshCount;
            for (int i = 0; i < src.subMeshCount; i++)
                dst.SetTriangles(src.GetTriangles(i), i, true);
            dst.bounds = src.bounds;
            dst.RecalculateBounds();
        }

        static void SaveMeshAsset(Mesh mesh, string assetPath)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(assetPath) ?? "Assets");
            Mesh existing = AssetDatabase.LoadAssetAtPath<Mesh>(assetPath);
            if (existing != null)
            {
                ReplaceMeshData(existing, mesh);
                existing.name = mesh.name;
                EditorUtility.SetDirty(existing);
            }
            else
            {
                AssetDatabase.CreateAsset(Object.Instantiate(mesh), assetPath);
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(assetPath);
        }

        static Mesh EnsureReadableMesh(Mesh mesh)
        {
            if (mesh == null)
                return null;
            try
            {
                _ = mesh.vertices;
                return mesh;
            }
            catch (Exception)
            {
                // Fall through — mark ModelImporter readable when possible.
            }

            string path = AssetDatabase.GetAssetPath(mesh);
            if (string.IsNullOrEmpty(path))
                return mesh;
            var importer = AssetImporter.GetAtPath(path) as ModelImporter;
            if (importer == null)
                return mesh;
            if (!importer.isReadable)
            {
                importer.isReadable = true;
                importer.SaveAndReimport();
                string meshName = mesh.name;
                foreach (Object o in AssetDatabase.LoadAllAssetsAtPath(path))
                {
                    if (o is Mesh m && m.name == meshName)
                        return m;
                }
            }

            return mesh;
        }

        static Mesh ResolveHighMesh(Job job)
        {
            if (!string.IsNullOrEmpty(job.HighMeshPath))
            {
                var m = AssetDatabase.LoadAssetAtPath<Mesh>(job.HighMeshPath);
                if (m != null)
                    return m;
            }

            foreach (string prefabPath in job.PrefabPaths)
            {
                var root = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                if (root == null)
                    continue;
                var lod = root.GetComponentInChildren<LODGroup>(true);
                if (lod != null)
                {
                    var lods = lod.GetLODs();
                    if (lods != null && lods.Length > 0 && lods[0].renderers != null)
                    {
                        foreach (var r in lods[0].renderers)
                        {
                            if (r == null)
                                continue;
                            var mf = r.GetComponent<MeshFilter>();
                            if (mf != null && mf.sharedMesh != null)
                                return mf.sharedMesh;
                        }
                    }
                }

                if (!string.IsNullOrEmpty(job.OakHighChildNameContains))
                {
                    foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
                    {
                        if (mf.gameObject.name.IndexOf(job.OakHighChildNameContains, StringComparison.OrdinalIgnoreCase) >= 0
                            && mf.sharedMesh != null)
                            return mf.sharedMesh;
                    }
                }
            }

            return null;
        }

        static Mesh ResolveOldLowMesh(Job job)
        {
            if (!string.IsNullOrEmpty(job.LowMeshPath))
            {
                var m = AssetDatabase.LoadAssetAtPath<Mesh>(job.LowMeshPath);
                if (m != null)
                    return m;
            }

            foreach (string prefabPath in job.PrefabPaths)
            {
                var root = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                if (root == null)
                    continue;
                var lod = root.GetComponentInChildren<LODGroup>(true);
                if (lod != null)
                {
                    var lods = lod.GetLODs();
                    if (lods != null && lods.Length > 1 && lods[1].renderers != null)
                    {
                        foreach (var r in lods[1].renderers)
                        {
                            if (r == null)
                                continue;
                            var mf = r.GetComponent<MeshFilter>();
                            if (mf != null && mf.sharedMesh != null)
                                return mf.sharedMesh;
                        }
                    }
                }

                if (!string.IsNullOrEmpty(job.OakLowChildNameContains))
                {
                    foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
                    {
                        if (mf.gameObject.name.IndexOf(job.OakLowChildNameContains, StringComparison.OrdinalIgnoreCase) >= 0
                            && mf.sharedMesh != null)
                            return mf.sharedMesh;
                    }
                }
            }

            return null;
        }

        static Job FindJob(string id)
        {
            foreach (Job j in BuildJobs())
            {
                if (string.Equals(j.Id, id, StringComparison.OrdinalIgnoreCase))
                    return j;
            }

            return null;
        }

        static void EnsureDirs(Job job)
        {
            Directory.CreateDirectory(WorkDir(job.Id));
            Directory.CreateDirectory(Path.Combine(WorkDir(job.Id), "high"));
            Directory.CreateDirectory(Path.Combine(WorkDir(job.Id), "low"));
            Directory.CreateDirectory(Path.Combine(WorkDir(job.Id), "preview"));
            Directory.CreateDirectory(Path.Combine(ScratchRel, "backup"));
            if (job.IsOak)
                Directory.CreateDirectory(OakRebuiltRel);
        }

        static string WorkDir(string id) => Path.Combine(ScratchRel, id);

        static string Abs(string projectRelative)
        {
            if (Path.IsPathRooted(projectRelative))
                return projectRelative;
            return Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), projectRelative));
        }

        static string Quote(string path) => "\"" + path.Replace("\"", "\\\"") + "\"";

        static string RunBlender(string args)
        {
            if (!File.Exists(BlenderApp))
                throw new FileNotFoundException("Blender not found", BlenderApp);
            if (!File.Exists(BlenderScriptRel))
                throw new FileNotFoundException("Blender script missing", BlenderScriptRel);

            // factory-startup avoids loading user addons; Metal backend is required on macOS Blender 5.2.
            string fullArgs = "--factory-startup --background " + args;
            var psi = new ProcessStartInfo
            {
                FileName = BlenderApp,
                Arguments = fullArgs,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Directory.GetCurrentDirectory(),
            };
            using var proc = Process.Start(psi);
            if (proc == null)
                throw new InvalidOperationException("failed to start Blender");
            string stdout = proc.StandardOutput.ReadToEnd();
            string stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(10 * 60 * 1000);
            return "exit=" + proc.ExitCode + "\n" + stdout + "\n" + stderr;
        }

        static void WriteMeshObj(Mesh mesh, string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            var sb = new StringBuilder(mesh.vertexCount * 48);
            sb.AppendLine("# rebuild_tree_lod high export");
            Vector3[] verts = mesh.vertices;
            Vector3[] norms = mesh.normals;
            Vector2[] uvs = mesh.uv;
            bool hasN = norms != null && norms.Length == verts.Length;
            bool hasUv = uvs != null && uvs.Length == verts.Length;

            for (int i = 0; i < verts.Length; i++)
            {
                Vector3 v = verts[i];
                sb.Append("v ").Append(F(v.x)).Append(' ').Append(F(v.y)).Append(' ').Append(F(v.z)).Append('\n');
            }

            if (hasN)
            {
                for (int i = 0; i < norms.Length; i++)
                {
                    Vector3 n = norms[i];
                    sb.Append("vn ").Append(F(n.x)).Append(' ').Append(F(n.y)).Append(' ').Append(F(n.z)).Append('\n');
                }
            }

            if (hasUv)
            {
                for (int i = 0; i < uvs.Length; i++)
                {
                    Vector2 uv = uvs[i];
                    sb.Append("vt ").Append(F(uv.x)).Append(' ').Append(F(uv.y)).Append('\n');
                }
            }

            int subCount = Mathf.Max(1, mesh.subMeshCount);
            for (int s = 0; s < subCount; s++)
            {
                string part = s == 0 ? "Bark" : "Leaf";
                if (s > 1)
                    part = "Part" + s;
                sb.Append("o ").Append(part).Append('\n');
                sb.Append("g ").Append(part).Append('\n');
                int[] tris = mesh.GetTriangles(s);
                for (int t = 0; t < tris.Length; t += 3)
                {
                    int i0 = tris[t] + 1;
                    int i1 = tris[t + 1] + 1;
                    int i2 = tris[t + 2] + 1;
                    if (hasUv && hasN)
                    {
                        sb.Append("f ")
                            .Append(i0).Append('/').Append(i0).Append('/').Append(i0).Append(' ')
                            .Append(i1).Append('/').Append(i1).Append('/').Append(i1).Append(' ')
                            .Append(i2).Append('/').Append(i2).Append('/').Append(i2).Append('\n');
                    }
                    else if (hasUv)
                    {
                        sb.Append("f ")
                            .Append(i0).Append('/').Append(i0).Append(' ')
                            .Append(i1).Append('/').Append(i1).Append(' ')
                            .Append(i2).Append('/').Append(i2).Append('\n');
                    }
                    else if (hasN)
                    {
                        sb.Append("f ")
                            .Append(i0).Append("//").Append(i0).Append(' ')
                            .Append(i1).Append("//").Append(i1).Append(' ')
                            .Append(i2).Append("//").Append(i2).Append('\n');
                    }
                    else
                    {
                        sb.Append("f ").Append(i0).Append(' ').Append(i1).Append(' ').Append(i2).Append('\n');
                    }
                }
            }

            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        }

        static string F(float v) => v.ToString("G9", CultureInfo.InvariantCulture);

        sealed class ObjData
        {
            public readonly List<Vector3> Verts = new();
            public readonly List<Vector3> Normals = new();
            public readonly List<Vector2> Uvs = new();
            public readonly List<List<int>> SubTris = new();
            public readonly List<string> SubNames = new();
        }

        static Mesh ReadMeshObj(string path, string meshName)
        {
            if (!File.Exists(path))
                return null;

            var data = new ObjData();
            var pos = new List<Vector3>();
            var nrm = new List<Vector3>();
            var uv = new List<Vector2>();
            int currentSub = -1;

            void EnsureSub(string name)
            {
                for (int i = 0; i < data.SubNames.Count; i++)
                {
                    if (string.Equals(data.SubNames[i], name, StringComparison.OrdinalIgnoreCase))
                    {
                        currentSub = i;
                        return;
                    }
                }

                data.SubNames.Add(name);
                data.SubTris.Add(new List<int>());
                currentSub = data.SubNames.Count - 1;
            }

            foreach (string raw in File.ReadLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#')
                    continue;
                string[] sp = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                if (sp.Length == 0)
                    continue;
                switch (sp[0])
                {
                    case "v" when sp.Length >= 4:
                        pos.Add(new Vector3(Parse(sp[1]), Parse(sp[2]), Parse(sp[3])));
                        break;
                    case "vn" when sp.Length >= 4:
                        nrm.Add(new Vector3(Parse(sp[1]), Parse(sp[2]), Parse(sp[3])));
                        break;
                    case "vt" when sp.Length >= 3:
                        uv.Add(new Vector2(Parse(sp[1]), Parse(sp[2])));
                        break;
                    case "o":
                    case "g":
                        if (sp.Length >= 2)
                            EnsureSub(sp[1]);
                        break;
                    case "f":
                        if (currentSub < 0)
                            EnsureSub("Bark");
                        var face = new List<int>();
                        for (int i = 1; i < sp.Length; i++)
                        {
                            string[] idx = sp[i].Split('/');
                            int vi = int.Parse(idx[0], CultureInfo.InvariantCulture);
                            if (vi < 0)
                                vi = pos.Count + vi + 1;
                            int ti = idx.Length > 1 && idx[1].Length > 0
                                ? int.Parse(idx[1], CultureInfo.InvariantCulture)
                                : 0;
                            if (ti < 0)
                                ti = uv.Count + ti + 1;
                            int ni = idx.Length > 2 && idx[2].Length > 0
                                ? int.Parse(idx[2], CultureInfo.InvariantCulture)
                                : 0;
                            if (ni < 0)
                                ni = nrm.Count + ni + 1;

                            Vector3 p = pos[vi - 1];
                            Vector3 n = ni > 0 && ni <= nrm.Count ? nrm[ni - 1] : Vector3.up;
                            Vector2 t = ti > 0 && ti <= uv.Count ? uv[ti - 1] : Vector2.zero;
                            int vertIndex = data.Verts.Count;
                            data.Verts.Add(p);
                            data.Normals.Add(n);
                            data.Uvs.Add(t);
                            face.Add(vertIndex);
                        }

                        for (int i = 1; i + 1 < face.Count; i++)
                        {
                            data.SubTris[currentSub].Add(face[0]);
                            data.SubTris[currentSub].Add(face[i]);
                            data.SubTris[currentSub].Add(face[i + 1]);
                        }

                        break;
                }
            }

            if (data.Verts.Count == 0)
                return null;

            // Prefer Bark then Leaf order
            int barkIdx = IndexOfSub(data, "Bark");
            int leafIdx = IndexOfSub(data, "Leaf");
            var order = new List<int>();
            if (barkIdx >= 0)
                order.Add(barkIdx);
            if (leafIdx >= 0 && leafIdx != barkIdx)
                order.Add(leafIdx);
            for (int i = 0; i < data.SubTris.Count; i++)
            {
                if (!order.Contains(i))
                    order.Add(i);
            }

            var mesh = new Mesh { name = meshName };
            mesh.indexFormat = data.Verts.Count > 65535
                ? UnityEngine.Rendering.IndexFormat.UInt32
                : UnityEngine.Rendering.IndexFormat.UInt16;
            mesh.SetVertices(data.Verts);
            mesh.SetNormals(data.Normals);
            mesh.SetUVs(0, data.Uvs);
            mesh.subMeshCount = order.Count;
            for (int i = 0; i < order.Count; i++)
                mesh.SetTriangles(data.SubTris[order[i]], i, true);
            mesh.RecalculateBounds();
            if (data.Normals.Count == 0)
                mesh.RecalculateNormals();
            mesh.RecalculateTangents();
            return mesh;
        }

        static int IndexOfSub(ObjData data, string name)
        {
            for (int i = 0; i < data.SubNames.Count; i++)
            {
                if (data.SubNames[i].IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
                    return i;
            }

            return -1;
        }

        static float Parse(string s) => float.Parse(s, CultureInfo.InvariantCulture);

        public static Job[] BuildJobs()
        {
            const string songshu = "Assets/GameMain/Environment/Prefabs/DaoYu1/Songshu/";
            const string lvshu = "Assets/GameMain/Environment/Prefabs/DaoYu1/Lvshu/";
            const string tff = "Assets/GameMain/Environment/Models/TffOilUv/";
            const string larchUv = "Assets/GameMain/Environment/Models/LarchUv/";
            const string previewRoot = ScratchRel + "/";

            var list = new List<Job>();

            void AddTff(string id, string label, string prefab, string highAsset, string lowAsset)
            {
                list.Add(new Job
                {
                    Id = id,
                    Label = label,
                    PrefabPaths = new[] { prefab },
                    HighMeshPath = highAsset,
                    LowMeshPath = lowAsset,
                    CommitMeshPath = lowAsset,
                    PreviewMeshPath = previewRoot + id + "/preview/" + id + "_LOD1_preview.asset",
                    IsOak = false,
                });
            }

            for (int i = 1; i <= 8; i++)
            {
                string id = "GrandFir" + i;
                AddTff(
                    id,
                    "Grand Fir " + i,
                    songshu + "Grand Fir " + i + ".prefab",
                    tff + "GrandFir" + i + "_LOD0.asset",
                    tff + "GrandFir" + i + "_LOD1.asset");
            }

            AddTff("Spruce3", "Spruce 3", lvshu + "Spruce 3.prefab",
                tff + "Spruce3_LOD0.asset", tff + "Spruce3_LOD1.asset");
            AddTff("Larch3", "Larch 3", lvshu + "Larch 3.prefab",
                larchUv + "Larch3_LOD0.asset", larchUv + "Larch3_LOD1.asset");
            AddTff("Larch4", "Larch 4", lvshu + "Larch 4.prefab",
                larchUv + "Larch4_LOD0.asset", larchUv + "Larch4_LOD1.asset");
            AddTff("Larch7", "Larch 7", lvshu + "Larch 7.prefab",
                larchUv + "Larch7_LOD0.asset", larchUv + "Larch7_LOD1.asset");

            void AddOak(string id, string label, string[] prefabs, string oakIndex)
            {
                list.Add(new Job
                {
                    Id = id,
                    Label = label,
                    PrefabPaths = prefabs,
                    HighMeshPath = "",
                    LowMeshPath = "",
                    CommitMeshPath = OakRebuiltRel + "/SM_OakTree_" + oakIndex + "_LOD2.asset",
                    PreviewMeshPath = previewRoot + id + "/preview/SM_OakTree_" + oakIndex + "_LOD2_preview.asset",
                    IsOak = true,
                    OakHighChildNameContains = "_LOD1",
                    OakLowChildNameContains = "_LOD2",
                });
            }

            AddOak("Oak01", "Forest_01 / Oak_01", new[] { lvshu + "SM_Env_Tree_Forest_01.prefab" }, "01");
            AddOak("Oak02", "Forest_02 / Oak_02", new[] { lvshu + "SM_Env_Tree_Forest_02.prefab" }, "02");
            AddOak("Oak03", "Meadow_01 / Oak_03", new[] { lvshu + "SM_Env_Tree_Meadow_01.prefab" }, "03");
            AddOak("Oak04", "Forest_03 / Oak_04", new[] { lvshu + "SM_Env_Tree_Forest_03.prefab" }, "04");
            AddOak("Oak05", "Meadow_02 + Golden Larch 1 / Oak_05",
                new[] { lvshu + "SM_Env_Tree_Meadow_02.prefab", lvshu + "Golden Larch 1.prefab" }, "05");
            AddOak("Oak06", "Fruit_01 + Golden Larch 2 / Oak_06",
                new[] { lvshu + "SM_Env_Tree_Fruit_01.prefab", lvshu + "Golden Larch 2.prefab" }, "06");
            AddOak("Oak07", "Golden Larch 5 / Oak_07", new[] { lvshu + "Golden Larch 5.prefab" }, "07");

            return list.ToArray();
        }
    }
}
#endif
