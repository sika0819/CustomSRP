using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityGameFramework.Runtime;

namespace Game.Editor
{
    public static class SceneBankGateCatalogBuilder
    {
        [MenuItem("GameMain/Boluo/Rebuild Scene Bank Gate Catalog")]
        public static void Rebuild()
        {
            var text = AssetDatabase.LoadAssetAtPath<TextAsset>(SceneAssets.AllMapItemAssetPath);
            if (text == null || text.bytes == null)
            {
                Log.Error("[SceneBankGate] Missing " + SceneAssets.AllMapItemAssetPath);
                return;
            }

            var items = new List<MapItemData>(2560);
            if (!MapItemData.ReadBinaryTo(text.bytes, items))
            {
                Log.Error("[SceneBankGate] AllMapItem.bytes parse failed");
                return;
            }

            SceneBankGateCatalog catalog = AssetDatabase.LoadAssetAtPath<SceneBankGateCatalog>(
                SceneAssets.SceneBankGateCatalogAssetPath);
            if (catalog == null)
            {
                string dir = Path.GetDirectoryName(SceneAssets.SceneBankGateCatalogAssetPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                catalog = ScriptableObject.CreateInstance<SceneBankGateCatalog>();
                AssetDatabase.CreateAsset(catalog, SceneAssets.SceneBankGateCatalogAssetPath);
            }

            SceneBankGateEntry[] previous = catalog.Entries;
            var next = new List<SceneBankGateEntry>();
            int mapRows = 0;
            for (int i = 0; i < items.Count; i++)
            {
                MapItemData data = items[i];
                if (data == null || data.Parent != SceneBankGateCatalog.Category)
                    continue;
                mapRows++;
                SceneBankGateEntry old = Find(previous, data.Index);
                var entry = new SceneBankGateEntry
                {
                    MapItemIndex = data.Index,
                    ObjectName = string.IsNullOrEmpty(data.ObjName) ? data.PrefabName : data.ObjName,
                    Position = data.Pos,
                    Rotation = data.Rotation,
                    Scale = data.Scale,
                    BoxSize = old != null ? old.BoxSize : Vector3.zero,
                    BankNames = CopyBanks(old != null ? old.BankNames : null),
                };
                next.Add(entry);
            }

            if (previous != null)
            {
                for (int i = 0; i < previous.Length; i++)
                {
                    SceneBankGateEntry entry = previous[i];
                    if (entry != null && entry.MapItemIndex < 0)
                        next.Add(entry);
                }
            }

            catalog.Entries = next.ToArray();
            EditorUtility.SetDirty(catalog);
            AssetDatabase.SaveAssets();
            Log.Info(
                "[SceneBankGate] catalog rebuilt mapRows=" + mapRows +
                " entries=" + catalog.Entries.Length +
                " → " + SceneAssets.SceneBankGateCatalogAssetPath);
        }

        static SceneBankGateEntry Find(SceneBankGateEntry[] entries, int mapItemIndex)
        {
            if (entries == null)
                return null;
            for (int i = 0; i < entries.Length; i++)
            {
                SceneBankGateEntry entry = entries[i];
                if (entry != null && entry.MapItemIndex == mapItemIndex)
                    return entry;
            }

            return null;
        }

        static string[] CopyBanks(string[] banks)
        {
            if (banks == null || banks.Length == 0)
                return Array.Empty<string>();
            return (string[])banks.Clone();
        }
    }
}
