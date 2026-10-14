using System;
using System.IO;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

namespace Game
{
    /// <summary>
    /// <summary>
    /// 把 AllMapItem.bytes 中所有树 (Songshu/Lvshu/Yeshu/Guanmu) 的 Y
    /// 修正到 IslandTerrain 实际地表高度（含 TreeBuryMeters 下压）。
    /// 按条目下标写回（禁止 Parent|Obj 合并），同 Prefab 多实例各自保留地形高。
    /// 菜单: Window -> Game -> Snap Trees To Terrain
    /// </summary>
    public class SnapAllTreesToTerrain : EditorWindow
    {
        const string kAllMapItemPath = "Assets/AssetRaw/Configs/AllMapItem.bytes";
        const string kTerrainPrefabPath = "Assets/AssetRaw/Island/IslandTerrain.prefab";

        static readonly HashSet<string> kTreeCategories = new HashSet<string>
        {
            "Songshu", "Lvshu", "Yeshu", "Guanmu"
        };

        [MenuItem("Window/Game/Snap Trees To Terrain")]
        public static void MenuOpen() => GetWindow<SnapAllTreesToTerrain>(true, "Snap Trees");

        /// <summary>供 -executeMethod 批处理：无对话框落库。</summary>
        public static void BatchSnap()
        {
            int n = AutoScanAndApply(dryRun: false);
            AssetDatabase.Refresh();
            Debug.Log($"[SnapTrees] BatchSnap wrote {n} entries");
        }

        public static int AutoScanAndApply(bool dryRun)
        {
            var changes = ScanAll();
            if (changes.Count == 0) return 0;
            if (dryRun) return changes.Count;
            ApplyChangesWithoutDialog(changes);
            return changes.Count;
        }

        struct Change
        {
            /// <summary>AllMapItem 列表下标，唯一标识一条摆放。</summary>
            public int ItemIndex;
            public string Parent, Obj;
            public float OldY, NewY, Delta;
        }

        class RawItem
        {
            public int Index, ParentIdx, ObjIdx;
            public float Px, Py, Pz, Rx, Ry, Rz, Sx, Sy, Sz;
            public bool Active;
            public string Parent, Obj;
        }

        readonly List<Change> _changes = new List<Change>();
        bool _scanned;

        void OnGUI()
        {
            minSize = new Vector2(520, 380);
            GUILayout.Space(8);
            GUILayout.Label("远树/近树 全部贴地形 (改 AllMapItem.bytes)", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "读取 IslandTerrain 高度图，按条目修正 Songshu/Lvshu/Yeshu/Guanmu 的 Y。\n" +
                $"写入值 = 地表高 + TreeBuryMeters({EnvGroundFit.TreeBuryMeters:F2}m)。\n" +
                "同 Prefab 多实例各自写回，不会合并成同一个高度。\n" +
                "地形范围外的树不改动。下次岛加载生效。",
                MessageType.Info);
            GUILayout.Space(8);

            GUI.backgroundColor = new Color(0.8f, 1f, 0.8f);
            if (GUILayout.Button("扫描 + 预览", GUILayout.Height(28)))
            {
                _changes.Clear();
                _changes.AddRange(ScanAll());
                _scanned = true;
            }
            GUI.backgroundColor = Color.white;

            GUILayout.Space(4);
            GUI.backgroundColor = new Color(1f, 0.8f, 0.8f);
            GUI.enabled = _scanned && _changes.Count > 0;
            if (GUILayout.Button("落库写回 AllMapItem.bytes", GUILayout.Height(36)))
            {
                ApplyChanges(_changes);
                _scanned = false;
            }
            GUI.enabled = true;
            GUI.backgroundColor = Color.white;

            GUILayout.Space(8);
            if (_changes.Count > 0)
            {
                float maxD = 0;
                foreach (var c in _changes)
                    if (Mathf.Abs(c.Delta) > maxD) maxD = Mathf.Abs(c.Delta);
                EditorGUILayout.LabelField($"待改 {_changes.Count} | 最大|Δ| {maxD:F2}", EditorStyles.boldLabel);

                var scroll = GUILayout.BeginScrollView(Vector2.zero, GUILayout.Height(200));
                int show = Mathf.Min(_changes.Count, 50);
                for (int i = 0; i < show; i++)
                {
                    var c = _changes[i];
                    string s = c.Delta >= 0 ? "+" : "";
                    Color col = Mathf.Abs(c.Delta) > 3f ? new Color(1f, 0.5f, 0.5f) :
                                Mathf.Abs(c.Delta) < 0.1f ? new Color(0.6f, 1f, 0.6f) : Color.white;
                    GUI.color = col;
                    GUILayout.Label(
                        $"  [{c.ItemIndex}] {c.Parent}/{c.Obj}: {c.OldY:F2} → {c.NewY:F2}  ({s}{c.Delta:F2})",
                        EditorStyles.miniLabel);
                }
                GUI.color = Color.white;
                if (_changes.Count > show)
                    GUILayout.Label($"  … 还有 {_changes.Count - show} 条", EditorStyles.miniLabel);
                GUILayout.EndScrollView();
            }
        }

        static List<Change> ScanAll()
        {
            var changes = new List<Change>();

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(kTerrainPrefabPath);
            if (prefab == null)
            {
                Debug.LogError("[SnapTrees] Terrain prefab not found");
                return changes;
            }

            var terrain = prefab.GetComponentInChildren<Terrain>(true);
            if (terrain == null || terrain.terrainData == null)
            {
                Debug.LogError("[SnapTrees] Terrain null");
                return changes;
            }

            var td = terrain.terrainData;
            var origin = terrain.transform.position;
            var size = td.size;

            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(kAllMapItemPath);
            }
            catch (Exception ex)
            {
                Debug.LogError("[SnapTrees] " + ex.Message);
                return changes;
            }

            var (items, strings) = ParseAllMapItem(bytes);
            if (items == null)
            {
                Debug.LogError("[SnapTrees] Magic mismatch");
                return changes;
            }

            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                if (!kTreeCategories.Contains(it.Parent)) continue;

                float nx = (it.Px - origin.x) / size.x;
                float nz = (it.Pz - origin.z) / size.z;
                if (nx < 0f || nx > 1f || nz < 0f || nz > 1f) continue;

                float ty = origin.y + td.GetInterpolatedHeight(nx, nz) + EnvGroundFit.TreeBuryMeters;
                float delta = ty - it.Py;
                if (Mathf.Abs(delta) < 0.01f) continue;

                changes.Add(new Change
                {
                    ItemIndex = i,
                    Parent = it.Parent,
                    Obj = it.Obj,
                    OldY = it.Py,
                    NewY = ty,
                    Delta = delta
                });
            }

            changes.Sort((a, b) => Mathf.Abs(b.Delta).CompareTo(Mathf.Abs(a.Delta)));
            float maxD = 0;
            foreach (var c in changes)
                if (Mathf.Abs(c.Delta) > maxD) maxD = Mathf.Abs(c.Delta);
            Debug.Log($"[SnapTrees] Scan: {changes.Count} to fix, max|Δ|={maxD:F2}");
            return changes;
        }

        static void ApplyChanges(List<Change> changes)
        {
            if (changes.Count == 0) return;
            if (!EditorUtility.DisplayDialog("确认落库",
                    $"将重写 AllMapItem.bytes，改动 {changes.Count} 个树条目（按条目下标，不合并同名 Prefab）。\n\n⚠ 建议确认版本控制！",
                    "确定", "取消"))
                return;
            ApplyChangesWithoutDialog(changes);
        }

        static void ApplyChangesWithoutDialog(List<Change> changes)
        {
            byte[] bytes = File.ReadAllBytes(kAllMapItemPath);
            var (items, strings) = ParseAllMapItem(bytes);
            if (items == null) return;

            // 按列表下标写回，禁止 Parent|Obj 合并（同 Prefab 多实例必须各自保留地形高）
            var lut = new Dictionary<int, float>(changes.Count);
            foreach (var c in changes)
                lut[c.ItemIndex] = c.NewY;

            int changed = 0;
            foreach (var kv in lut)
            {
                int i = kv.Key;
                if (i < 0 || i >= items.Count) continue;
                float ny = kv.Value;
                if (Mathf.Abs(items[i].Py - ny) > 0.001f)
                {
                    items[i].Py = ny;
                    changed++;
                }
            }

            byte[] outBytes = WriteAllMapItem(items, strings);

            string backup = kAllMapItemPath + ".bak";
            File.Copy(kAllMapItemPath, backup, true);
            File.WriteAllBytes(kAllMapItemPath, outBytes);
            Debug.Log($"[SnapTrees] Done: {changed} items written by ItemIndex (backup: {backup})");
        }

        static (List<RawItem> items, List<string> strings) ParseAllMapItem(byte[] bytes)
        {
            using var ms = new MemoryStream(bytes, false);
            using var br = new BinaryReader(ms, System.Text.Encoding.UTF8);
            if (br.ReadInt32() != 0x314D4C42) return (null, null);

            int count = br.ReadInt32();
            int strCount = br.ReadInt32();
            var strings = new List<string>(strCount);
            for (int i = 0; i < strCount; i++) strings.Add(br.ReadString());

            var items = new List<RawItem>(count);
            for (int i = 0; i < count; i++)
            {
                var it = new RawItem
                {
                    Index = br.ReadInt32(),
                    ParentIdx = br.ReadInt32(),
                    ObjIdx = br.ReadInt32(),
                    Px = br.ReadSingle(),
                    Py = br.ReadSingle(),
                    Pz = br.ReadSingle(),
                    Rx = br.ReadSingle(),
                    Ry = br.ReadSingle(),
                    Rz = br.ReadSingle(),
                    Sx = br.ReadSingle(),
                    Sy = br.ReadSingle(),
                    Sz = br.ReadSingle(),
                    Active = br.ReadBoolean(),
                };
                it.Parent = strings[it.ParentIdx];
                it.Obj = strings[it.ObjIdx];
                items.Add(it);
            }

            return (items, strings);
        }

        static byte[] WriteAllMapItem(List<RawItem> items, List<string> strings)
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms, System.Text.Encoding.UTF8);
            bw.Write(0x314D4C42);
            bw.Write(items.Count);
            bw.Write(strings.Count);
            foreach (var s in strings) bw.Write(s);
            foreach (var it in items)
            {
                bw.Write(it.Index);
                bw.Write(it.ParentIdx);
                bw.Write(it.ObjIdx);
                bw.Write(it.Px);
                bw.Write(it.Py);
                bw.Write(it.Pz);
                bw.Write(it.Rx);
                bw.Write(it.Ry);
                bw.Write(it.Rz);
                bw.Write(it.Sx);
                bw.Write(it.Sy);
                bw.Write(it.Sz);
                bw.Write(it.Active);
            }

            return ms.ToArray();
        }
    }
}
