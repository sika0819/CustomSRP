using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

using UnityGameFramework.Runtime;
namespace Game.Editor
{
    /// <summary>
    /// House_01 / building_closed_01 与 closed_03 共用 House_01 mesh。
    /// 地图 ObjName 统一到带 MeshCollider 的 rpgpp_ag_building_closed_03，TRS 不动。
    /// </summary>
    public static class MergeHouseFamilyMenu
    {
        const string CanonPrefab = "rpgpp_ag_building_closed_03";
        public const string FlagRelPath = "Temp/merge_house_family.flag";

        static readonly Dictionary<string, string> ObjNameRewrite = new Dictionary<string, string>
        {
            { "House_01", "rpgpp_ag_building_closed_03 (House_01)" },
            { "rpgpp_ag_building_closed_01 (1)", "rpgpp_ag_building_closed_03 (was_01)" },
        };

        [InitializeOnLoadMethod]
        static void WatchFlag()
        {
            EditorApplication.delayCall += TryConsumeFlag;
        }

        static void TryConsumeFlag()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                EditorApplication.delayCall += TryConsumeFlag;
                return;
            }

            string flag = Path.GetFullPath(Path.Combine(Application.dataPath, "..", FlagRelPath));
            if (!File.Exists(flag))
                return;
            try { File.Delete(flag); } catch { /* ignore */ }

            // 外部改完 bytes 后强制再导入，避免 Yoo 打到旧 TextAsset。
            AssetDatabase.ImportAsset(SceneAssets.AllMapItemAssetPath, ImportAssetOptions.ForceUpdate);
            Merge();
            Log.Info("[HouseMerge] Flag consumed — AllMapItem reimported/merged");
        }

        [MenuItem("Game/MapItem/Merge House Family → building_closed_03")]
        public static void Merge()
        {
            var text = AssetDatabase.LoadAssetAtPath<TextAsset>(SceneAssets.AllMapItemAssetPath);
            if (text == null)
            {
                Log.Error("[HouseMerge] Missing " + SceneAssets.AllMapItemAssetPath);
                return;
            }

            var items = new List<MapItemData>(2560);
            if (!MapItemData.ReadBinaryTo(text.bytes, items))
            {
                Log.Error("[HouseMerge] AllMapItem.bytes parse failed");
                return;
            }

            var beforeTrs = new Dictionary<int, (Vector3 Pos, Vector3 Rot, Vector3 Scale)>();
            int changed = 0;
            for (int i = 0; i < items.Count; i++)
            {
                MapItemData d = items[i];
                if ((d.Parent ?? "") != "JianZhu")
                    continue;
                string prefab = d.PrefabName;
                if (prefab != "House_01"
                    && prefab != "rpgpp_ag_building_closed_01"
                    && prefab != CanonPrefab)
                    continue;

                beforeTrs[d.Index] = (d.Pos, d.Rotation, d.Scale);
                if (ObjNameRewrite.TryGetValue(d.ObjName, out string next))
                {
                    d.ObjName = next;
                    changed++;
                }
            }

            if (changed == 0)
            {
                Log.Info("[HouseMerge] nothing to rewrite (already merged?)");
                return;
            }

            WriteAllMapItem(items);

            var verify = new List<MapItemData>(items.Count);
            var rewritten = File.ReadAllBytes(Path.GetFullPath(
                Path.Combine(Application.dataPath, "..", SceneAssets.AllMapItemAssetPath)));
            if (!MapItemData.ReadBinaryTo(rewritten, verify))
            {
                Log.Error("[HouseMerge] verify parse failed");
                return;
            }

            int checkedCount = 0;
            for (int i = 0; i < verify.Count; i++)
            {
                MapItemData d = verify[i];
                if (!beforeTrs.TryGetValue(d.Index, out var trs))
                    continue;
                if (d.Pos != trs.Pos || d.Rotation != trs.Rot || d.Scale != trs.Scale)
                {
                    Log.Error("[HouseMerge] TRS drift index=" + d.Index);
                    return;
                }

                if (d.PrefabName != CanonPrefab)
                {
                    Log.Error("[HouseMerge] PrefabName not canon index=" + d.Index + " name=" + d.ObjName);
                    return;
                }

                checkedCount++;
            }

            Log.Info(
                "[HouseMerge] rewritten=" + changed + " verified=" + checkedCount +
                " canon=" + CanonPrefab);
            EditorUtility.DisplayDialog(
                "House Family Merge",
                "Rewrote " + changed + " ObjName(s) → " + CanonPrefab +
                "\nTRS verified for " + checkedCount + " house points.",
                "OK");
        }

        static void WriteAllMapItem(List<MapItemData> items)
        {
            string absPath = Path.GetFullPath(
                Path.Combine(Application.dataPath, "..", SceneAssets.AllMapItemAssetPath));
            using (var fs = File.Create(absPath))
                MapItemData.WriteBinary(items, fs);
            AssetDatabase.ImportAsset(SceneAssets.AllMapItemAssetPath);
        }
    }
}
