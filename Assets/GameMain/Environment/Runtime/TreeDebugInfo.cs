using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityGameFramework.Runtime;

namespace Game
{
    /// <summary>
    /// 调试工具：显示场景中树的 world position、Billboard local position、mesh bounds。
    /// 用法：在游戏运行时将此脚本挂在任意 GameObject 上。
    /// </summary>
    public class TreeDebugInfo : MonoBehaviour
    {
        public string CategoryFilter = "Songshu,Lvshu";
        public int MaxItems = 10;
        bool _scanned;
        readonly List<TreeInfo> _trees = new List<TreeInfo>();

        struct TreeInfo
        {
            public string Name;
            public Vector3 WorldPos;
            public float BillboardLocalY;
            public float MeshBottomLocal;
            public float WorldBottom;
            public bool BillboardActive;
        }

        void Update()
        {
            if (Input.GetKeyDown(KeyCode.F3))
                ScanTrees();
        }

        void OnGUI()
        {
            if (!_scanned || _trees.Count == 0) return;

            GUILayout.BeginArea(new Rect(10, 10, 700, Screen.height - 20));
            GUILayout.Label($"=== 树调试信息 ({_trees.Count} trees) [F3重新扫描] ===", GUI.skin.box);
            GUILayout.Space(4);

            foreach (var t in _trees)
            {
                string status = t.WorldBottom > 0.1f ? $"⚠ FLOATING ({t.WorldBottom:F2}m above ground!)"
                          : t.WorldBottom < -0.5f ? $"⬇ SUNKEN ({t.WorldBottom:F2}m below ground)"
                          : "✓ OK";
                string bbStatus = t.BillboardActive ? "BB=ON" : "BB=OFF";

                GUILayout.Label(
                    $"{t.Name}: WorldY={t.WorldPos.y:F2} | BB.localY={t.BillboardLocalY:F2} | MeshBottom={t.MeshBottomLocal:F2} | WorldBottom={t.WorldBottom:F2} | {status} [{bbStatus}]",
                    GUI.skin.label);
            }

            GUILayout.EndArea();
        }

        [ContextMenu("Scan Trees")]
        public void ScanTrees()
        {
            _trees.Clear();
            var categories = CategoryFilter.Split(',');
            var roots = FindObjectsByType<Transform>();

            int count = 0;
            foreach (var t in roots)
            {
                if (count >= MaxItems) break;

                string cat = t.name;
                bool match = false;
                foreach (var c in categories)
                    if (cat == c.Trim())
                        { match = true; break; }

                if (!match) continue;

                // 找子节点的树 Prefab
                for (int i = 0; i < t.childCount && count < MaxItems; i++)
                {
                    var child = t.GetChild(i);
                    var lodGroup = child.GetComponentInChildren<LODGroup>(true);
                    if (lodGroup == null) continue;

                    var billboard = FindChild(child, "LOD_Billboard");
                    var meshFilter = billboard != null ? billboard.GetComponent<MeshFilter>() : null;
                    float meshBottomLocal = 0f;
                    float billboardLocalY = 0f;

                    if (meshFilter != null && meshFilter.sharedMesh != null)
                    {
                        var bounds = meshFilter.sharedMesh.bounds;
                        meshBottomLocal = bounds.center.y - bounds.extents.y;
                    }

                    if (billboard != null)
                        billboardLocalY = billboard.localPosition.y;

                    float worldBottom = child.position.y + billboardLocalY + meshBottomLocal;

                    _trees.Add(new TreeInfo
                    {
                        Name = child.name,
                        WorldPos = child.position,
                        BillboardLocalY = billboardLocalY,
                        MeshBottomLocal = meshBottomLocal,
                        WorldBottom = worldBottom,
                        BillboardActive = billboard != null && billboard.gameObject.activeSelf,
                    });
                    count++;
                }
            }

            _scanned = true;
            Debug.Log($"[TreeDebug] Scanned {count} trees");
        }

        static Transform FindChild(Transform parent, string name)
        {
            if (parent == null) return null;
            foreach (Transform child in parent)
            {
                if (child.name == name) return child;
                var result = FindChild(child, name);
                if (result != null) return result;
            }
            return null;
        }
    }
}
