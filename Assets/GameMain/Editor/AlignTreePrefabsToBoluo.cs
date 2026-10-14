using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Game
{
    /// <summary>
    /// 将 DaoYu1 树 Prefab 的 Transform.localY / LODGroup.localReferencePoint
    /// 对齐到 Boluo 工程同名 Prefab（不改 mesh / 材质 / 其它组件）。
    /// 菜单: Window > Game > Align Tree Prefabs To Boluo
    /// </summary>
    public static class AlignTreePrefabsToBoluo
    {
        const string kLocalPrefabs = "Assets/GameMain/Environment/Prefabs/DaoYu1";
        static readonly string[] kFolders = { "Songshu", "Lvshu", "Yeshu", "Guanmu" };

        /// <summary>
        /// Boluo 树 Prefab 根目录（本机并列工程）。可按机器改。
        /// </summary>
        const string kBoluoPrefabsAbs =
            "/Users/sika/Desktop/code/BoluoUnity/UnityProject/Assets/AssetRaw/DaoYu1/Prefabs";

        [MenuItem("Window/Game/Align Tree Prefabs To Boluo")]
        public static void MenuAlign()
        {
            if (!Directory.Exists(kBoluoPrefabsAbs))
            {
                EditorUtility.DisplayDialog("Align Tree Prefabs",
                    $"Boluo Prefabs 目录不存在:\n{kBoluoPrefabsAbs}", "OK");
                return;
            }

            int n = AlignAll(dryRun: false);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            EditorUtility.DisplayDialog("Align Tree Prefabs", $"已对齐 {n} 个 Prefab（未改 mesh）", "OK");
        }

        public static int AlignAll(bool dryRun)
        {
            int fixedCount = 0;
            foreach (var folder in kFolders)
            {
                string localDir = Path.Combine(kLocalPrefabs, folder).Replace('\\', '/');
                string boluoDir = Path.Combine(kBoluoPrefabsAbs, folder);
                if (!Directory.Exists(localDir) || !Directory.Exists(boluoDir)) continue;

                foreach (var localPath in Directory.GetFiles(localDir, "*.prefab"))
                {
                    string name = Path.GetFileName(localPath);
                    string boluoPath = Path.Combine(boluoDir, name);
                    if (!File.Exists(boluoPath)) continue;

                    if (AlignOne(localPath.Replace('\\', '/'), boluoPath, dryRun))
                        fixedCount++;
                }
            }

            Debug.Log($"[AlignTreeBoluo] {(dryRun ? "dryRun" : "done")}: {fixedCount} prefabs");
            return fixedCount;
        }

        static bool AlignOne(string localAssetPath, string boluoAbsPath, bool dryRun)
        {
            // Boluo 侧用 YAML 解析（在工程外，不能 LoadPrefabContents）
            if (!TryReadBoluoTransforms(boluoAbsPath, out var boluoY, out var boluoLrp))
            {
                Debug.LogWarning($"[AlignTreeBoluo] parse fail: {boluoAbsPath}");
                return false;
            }

            var root = PrefabUtility.LoadPrefabContents(localAssetPath);
            if (root == null) return false;

            bool dirty = false;
            try
            {
                // Root + 所有子 Transform：按名字对齐 localY（X/Z 不动）
                var all = root.GetComponentsInChildren<Transform>(true);
                foreach (var t in all)
                {
                    if (!boluoY.TryGetValue(t.name, out float by)) continue;
                    var lp = t.localPosition;
                    if (Mathf.Abs(lp.y - by) <= 0.0001f) continue;
                    if (!dryRun)
                        t.localPosition = new Vector3(lp.x, by, lp.z);
                    dirty = true;
                }

                var lod = root.GetComponentInChildren<LODGroup>(true);
                if (lod != null && boluoLrp.HasValue)
                {
                    var cur = lod.localReferencePoint;
                    var target = boluoLrp.Value;
                    if ((cur - target).sqrMagnitude > 1e-8f)
                    {
                        if (!dryRun)
                        {
                            var so = new SerializedObject(lod);
                            var prop = so.FindProperty("m_LocalReferencePoint");
                            if (prop != null)
                            {
                                prop.vector3Value = target;
                                so.ApplyModifiedPropertiesWithoutUndo();
                            }
                        }

                        dirty = true;
                    }
                }

                if (dirty && !dryRun)
                    PrefabUtility.SaveAsPrefabAsset(root, localAssetPath);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }

            if (dirty)
                Debug.Log($"[AlignTreeBoluo] {Path.GetFileName(localAssetPath)}");
            return dirty;
        }

        static bool TryReadBoluoTransforms(string absPath, out Dictionary<string, float> nameToY,
            out Vector3? lrp)
        {
            nameToY = new Dictionary<string, float>();
            lrp = null;
            string text;
            try
            {
                text = File.ReadAllText(absPath);
            }
            catch
            {
                return false;
            }

            var lines = text.Split('\n');
            var goNames = new Dictionary<string, string>(); // fileID -> name
            for (int i = 0; i < lines.Length; i++)
            {
                if (!lines[i].StartsWith("--- !u!1 &", StringComparison.Ordinal)) continue;
                string fid = lines[i].Substring("--- !u!1 &".Length).Trim();
                for (int j = i; j < Math.Min(i + 30, lines.Length); j++)
                {
                    const string prefix = "  m_Name: ";
                    if (lines[j].StartsWith(prefix, StringComparison.Ordinal))
                    {
                        goNames[fid] = lines[j].Substring(prefix.Length).Trim();
                        break;
                    }
                }
            }

            for (int i = 0; i < lines.Length; i++)
            {
                if (!lines[i].StartsWith("--- !u!4 &", StringComparison.Ordinal)) continue;
                string goFid = null;
                float? y = null;
                for (int j = i; j < Math.Min(i + 40, lines.Length); j++)
                {
                    if (lines[j].StartsWith("  m_GameObject: {fileID: ", StringComparison.Ordinal))
                    {
                        int a = lines[j].IndexOf(": ", StringComparison.Ordinal) + 2;
                        int b = lines[j].IndexOf('}', a);
                        goFid = lines[j].Substring(a, b - a).Trim();
                    }

                    if (lines[j].StartsWith("  m_LocalPosition:", StringComparison.Ordinal))
                    {
                        // m_LocalPosition: {x: 0, y: 1.6, z: 0}
                        int yi = lines[j].IndexOf("y: ", StringComparison.Ordinal);
                        if (yi >= 0)
                        {
                            yi += 3;
                            int ye = lines[j].IndexOf(',', yi);
                            if (ye < 0) ye = lines[j].IndexOf('}', yi);
                            if (float.TryParse(lines[j].Substring(yi, ye - yi).Trim(),
                                    System.Globalization.NumberStyles.Float,
                                    System.Globalization.CultureInfo.InvariantCulture, out float fy))
                                y = fy;
                        }
                    }
                }

                if (goFid != null && y.HasValue && goNames.TryGetValue(goFid, out string nm))
                    nameToY[nm] = y.Value;
            }

            for (int i = 0; i < lines.Length; i++)
            {
                if (!lines[i].Contains("m_LocalReferencePoint:", StringComparison.Ordinal)) continue;
                // m_LocalReferencePoint: {x: a, y: b, z: c}
                var m = System.Text.RegularExpressions.Regex.Match(lines[i],
                    @"m_LocalReferencePoint:\s*\{x:\s*([^,]+),\s*y:\s*([^,]+),\s*z:\s*([^}]+)\}");
                if (!m.Success) continue;
                if (float.TryParse(m.Groups[1].Value.Trim(), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float x)
                    && float.TryParse(m.Groups[2].Value.Trim(), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float yv)
                    && float.TryParse(m.Groups[3].Value.Trim(), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float z))
                {
                    lrp = new Vector3(x, yv, z);
                    break;
                }
            }

            return nameToY.Count > 0;
        }
    }
}
