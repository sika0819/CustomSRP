using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

using UnityGameFramework.Runtime;
namespace Game.Editor
{
    /// <summary>MapItem 层级预制体与 AllMapItem / DaoYu1 抽检。</summary>
    public static class MapItemEditorMenu
    {
        const string MapItemPrefabPath = "Assets/AssetRaw/Island/MapItem.prefab";
        const string IslandTerrainDataPath = "Assets/Terrain/Island/IslandTerrainData.asset";
        const string SnapFlagRelPath = "Temp/mapitem_snap_flowers_flat.flag";
        static readonly string[] FlatFlowerPrefabPaths =
        {
            "Assets/GameMain/Environment/Prefabs/DaoYu1/Zacao/SM_Env_Flowers_Flat_01.prefab",
            "Assets/GameMain/Environment/Prefabs/DaoYu1/Zacao/SM_Env_Flowers_Flat_02.prefab",
            "Assets/GameMain/Environment/Prefabs/DaoYu1/Zacao/SM_Env_Flowers_Flat_03.prefab",
        };

        [InitializeOnLoadMethod]
        static void WatchSnapFlag()
        {
            EditorApplication.delayCall += TryConsumeSnapFlag;
            EditorApplication.update -= PollSnapFlag;
            EditorApplication.update += PollSnapFlag;
        }

        static double _nextSnapPoll;

        static void PollSnapFlag()
        {
            if (EditorApplication.timeSinceStartup < _nextSnapPoll)
                return;
            _nextSnapPoll = EditorApplication.timeSinceStartup + 1.5;
            TryConsumeSnapFlag();
        }

        static void TryConsumeSnapFlag()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                return;

            string flag = Path.GetFullPath(
                Path.Combine(Application.dataPath, "..", SnapFlagRelPath));
            if (!File.Exists(flag))
                return;
            try { File.Delete(flag); } catch { /* ignore */ }
            Log.Info("[MapItem] Snap flag — Flowers_Flat terrain + no shadows");
            SnapFlowersFlatToTerrainAndDisableShadows();
        }

        public const string FxSetupFlagRelPath = "Temp/mapitem_fx_setup.flag";

        [InitializeOnLoadMethod]
        static void WatchFxSetupFlag()
        {
            EditorApplication.delayCall += TryConsumeFxSetupFlag;
            EditorApplication.update -= PollFxSetupFlag;
            EditorApplication.update += PollFxSetupFlag;
        }

        static double _nextFxSetupPoll;

        static void PollFxSetupFlag()
        {
            if (EditorApplication.timeSinceStartup < _nextFxSetupPoll)
                return;
            _nextFxSetupPoll = EditorApplication.timeSinceStartup + 1.5;
            TryConsumeFxSetupFlag();
        }

        static void TryConsumeFxSetupFlag()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                return;

            string flag = Path.GetFullPath(
                Path.Combine(Application.dataPath, "..", FxSetupFlagRelPath));
            if (!File.Exists(flag))
                return;
            try { File.Delete(flag); } catch { /* ignore */ }
            Log.Info("[MapItem] FX setup flag — rebuild MapItem hierarchy + Yoo IslandFX");
            SetupMapItemAtmosphereExtras();
        }

        /// <summary>
        /// 重建带 FX_Day/FX_Night 的 MapItem 层级，并刷新 Yoo Collector（含 IslandFX）。
        /// Flag：Temp/mapitem_fx_setup.flag
        /// </summary>
        [MenuItem("GameMain/Boluo/Setup MapItem Atmosphere Extras (FX + Collector)", false, 119)]
        public static void SetupMapItemAtmosphereExtras()
        {
            BuildMapItemHierarchyPrefab();
            if (AssetDatabase.LoadAssetAtPath<TextAsset>(SceneAssets.MapItemExtrasAssetPath) == null)
                Log.Warning("[MapItem] Missing " + SceneAssets.MapItemExtrasAssetPath);
            else
                AssetDatabase.ImportAsset(SceneAssets.MapItemExtrasAssetPath);
            YooAssetCollectorSetup.SetupAll();
            int missing = ValidateMapItemExtrasPrefabs();
            Log.Info(
                "[MapItem] Atmosphere extras ready. missingPrefabs=" + missing +
                " — Play island to spawn Huodui/SunBeam under MapItem.");
        }

        [MenuItem("GameMain/Boluo/Validate MapItemExtras.bytes Prefabs", false, 118)]
        public static void ValidateMapItemExtrasMenu()
        {
            int missing = ValidateMapItemExtrasPrefabs();
            Log.Info("[MapItem] MapItemExtras validate missing=" + missing);
        }

        static int ValidateMapItemExtrasPrefabs()
        {
            var text = AssetDatabase.LoadAssetAtPath<TextAsset>(SceneAssets.MapItemExtrasAssetPath);
            if (text == null || text.bytes == null)
            {
                Log.Error("[MapItem] Missing " + SceneAssets.MapItemExtrasAssetPath);
                return -1;
            }

            var items = new List<MapItemData>(64);
            if (!MapItemData.ReadBinaryTo(text.bytes, items))
            {
                Log.Error("[MapItem] MapItemExtras.bytes parse failed");
                return -1;
            }

            int missing = 0;
            var seen = new HashSet<string>();
            for (int i = 0; i < items.Count; i++)
            {
                MapItemData d = items[i];
                string path;
                if (d.Parent == "FX_Day" || d.Parent == "FX_Night"
                    || (d.PrefabName != null && d.PrefabName.StartsWith("FX_", System.StringComparison.Ordinal)))
                    path = SceneAssets.FxEditorPath(d.PrefabName);
                else
                    path = SceneAssets.DaoYu1EditorPath(
                        d.PrefabName == "Huodui" ? "Other" : d.Parent, d.PrefabName);

                if (string.IsNullOrEmpty(path) || !seen.Add(path))
                    continue;
                if (AssetDatabase.LoadAssetAtPath<GameObject>(path) != null)
                    continue;
                missing++;
                Log.Warning("[MapItem] extras prefab missing: " + path);
            }

            Log.Info("[MapItem] MapItemExtras items=" + items.Count + " uniquePrefabs=" + seen.Count);
            return missing;
        }

        [MenuItem("GameMain/Boluo/Build MapItem Hierarchy Prefab", false, 120)]
        public static void BuildMapItemHierarchyPrefab()
        {
            Directory.CreateDirectory("Assets/AssetRaw/Island");

            var root = new GameObject("MapItem");
            for (int i = 0; i < EnvMapItemSpawner.DefaultCategories.Length; i++)
            {
                string name = EnvMapItemSpawner.DefaultCategories[i];
                var child = new GameObject(name);
                child.transform.SetParent(root.transform, false);
            }

            PrefabUtility.SaveAsPrefabAsset(root, MapItemPrefabPath);
            Object.DestroyImmediate(root);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Log.Info("[MapItem] Wrote " + MapItemPrefabPath);
        }

        [MenuItem("GameMain/Boluo/Snap Flowers Flat To Terrain + No Shadows", false, 122)]
        public static void SnapFlowersFlatToTerrainAndDisableShadows()
        {
            int prefabShadows = DisableFlatFlowerPrefabShadows();
            int snapped = BakeFlatFlowerHeightsInAllMapItem();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Log.Info(
                $"[MapItem] Flowers_Flat: prefabNoShadow={prefabShadows} " +
                $"allMapItemSnapped={snapped}");
        }

        static int DisableFlatFlowerPrefabShadows()
        {
            int touched = 0;
            for (int i = 0; i < FlatFlowerPrefabPaths.Length; i++)
            {
                string path = FlatFlowerPrefabPaths[i];
                GameObject root = PrefabUtility.LoadPrefabContents(path);
                if (root == null)
                    continue;

                bool changed = false;
                var renderers = root.GetComponentsInChildren<Renderer>(true);
                for (int r = 0; r < renderers.Length; r++)
                {
                    Renderer mr = renderers[r];
                    if (mr == null || mr.shadowCastingMode == ShadowCastingMode.Off)
                        continue;
                    mr.shadowCastingMode = ShadowCastingMode.Off;
                    changed = true;
                }

                if (changed)
                {
                    PrefabUtility.SaveAsPrefabAsset(root, path);
                    touched++;
                }

                PrefabUtility.UnloadPrefabContents(root);
            }

            return touched;
        }

        static int BakeFlatFlowerHeightsInAllMapItem()
        {
            var text = AssetDatabase.LoadAssetAtPath<TextAsset>(SceneAssets.AllMapItemAssetPath);
            if (text == null || text.bytes == null)
            {
                Log.Error("[MapItem] Missing " + SceneAssets.AllMapItemAssetPath);
                return 0;
            }

            var terrainData = AssetDatabase.LoadAssetAtPath<TerrainData>(IslandTerrainDataPath);
            if (terrainData == null)
            {
                Log.Error("[MapItem] Missing " + IslandTerrainDataPath);
                return 0;
            }

            Vector3 terrainOrigin = Vector3.zero;
            Terrain active = Terrain.activeTerrain;
            if (active != null && active.terrainData == terrainData)
                terrainOrigin = active.transform.position;

            var items = new List<MapItemData>(2560);
            if (!MapItemData.ReadBinaryTo(text.bytes, items))
            {
                Log.Error("[MapItem] AllMapItem.bytes parse failed");
                return 0;
            }

            int snapped = 0;
            float maxAbsDy = 0f;
            for (int i = 0; i < items.Count; i++)
            {
                MapItemData d = items[i];
                if (!EnvGroundFit.IsFlatGroundCard(d.PrefabName))
                    continue;

                Vector3 pos = d.Pos;
                float before = pos.y;
                if (!EnvGroundFit.TrySnapY(ref pos, terrainData, terrainOrigin))
                    continue;

                float dy = Mathf.Abs(pos.y - before);
                if (dy > maxAbsDy)
                    maxAbsDy = dy;
                if (dy < 0.001f)
                    continue;

                d.Pos = pos;
                items[i] = d;
                snapped++;
            }

            if (snapped == 0)
            {
                Log.Info(
                    "[MapItem] Flowers_Flat height bake: no Y change " +
                    $"(maxAbsDy={maxAbsDy:F3}, terrainOrigin={terrainOrigin})");
                return 0;
            }

            string absPath = Path.GetFullPath(
                Path.Combine(Application.dataPath, "..", SceneAssets.AllMapItemAssetPath));
            using (var fs = File.Create(absPath))
                MapItemData.WriteBinary(items, fs);

            AssetDatabase.ImportAsset(SceneAssets.AllMapItemAssetPath);
            Log.Info(
                $"[MapItem] Flowers_Flat height bake snapped={snapped} maxAbsDy={maxAbsDy:F3}");
            return snapped;
        }

        [MenuItem("GameMain/Boluo/Validate MapItem Data + DaoYu1 Prefabs", false, 121)]
        public static void ValidateMapItemData()
        {
            int errors = 0;
            var text = AssetDatabase.LoadAssetAtPath<TextAsset>(SceneAssets.AllMapItemAssetPath);
            if (text == null || text.bytes == null)
            {
                Log.Error("[MapItem] Missing " + SceneAssets.AllMapItemAssetPath);
                return;
            }

            var items = new List<MapItemData>(2560);
            if (!MapItemData.ReadBinaryTo(text.bytes, items))
            {
                Log.Error("[MapItem] AllMapItem.bytes parse failed");
                return;
            }

            var missing = new HashSet<string>();
            var ok = new HashSet<string>();
            for (int i = 0; i < items.Count; i++)
            {
                MapItemData d = items[i];
                string path = SceneAssets.DaoYu1EditorPath(d.Parent, d.PrefabName);
                if (string.IsNullOrEmpty(path))
                {
                    errors++;
                    continue;
                }

                if (ok.Contains(path) || missing.Contains(path))
                    continue;

                if (AssetDatabase.LoadAssetAtPath<GameObject>(path) == null)
                {
                    missing.Add(path);
                    errors++;
                }
                else
                {
                    ok.Add(path);
                }
            }

            // Enable GPU Instancing on OilNPR env materials (Native / SRP Batcher path).
            int instancingEnabled = 0;
            string[] matGuids = AssetDatabase.FindAssets(
                "t:Material",
                new[] { "Assets/GameMain/Environment" });
            for (int i = 0; i < matGuids.Length; i++)
            {
                string matPath = AssetDatabase.GUIDToAssetPath(matGuids[i]);
                var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
                if (mat == null || mat.shader == null)
                    continue;
                if (!mat.shader.name.StartsWith("CustomSRP/", System.StringComparison.Ordinal))
                    continue;
                if (mat.enableInstancing)
                    continue;
                mat.enableInstancing = true;
                EditorUtility.SetDirty(mat);
                instancingEnabled++;
            }

            if (instancingEnabled > 0)
                AssetDatabase.SaveAssets();

            Log.Info(
                $"[MapItem] Validate items={items.Count} uniquePrefabsOk={ok.Count} " +
                $"missing={missing.Count} instancingEnabled={instancingEnabled}");
            if (missing.Count > 0)
            {
                int shown = 0;
                foreach (string path in missing)
                {
                    Log.Warning("[MapItem] missing prefab: " + path);
                    if (++shown >= 20)
                    {
                        Log.Warning($"[MapItem] ... and {missing.Count - shown} more");
                        break;
                    }
                }
            }

            if (errors == 0)
                Log.Info("[MapItem] Validate OK");
        }

        /// <summary>
        /// 叶片：Alpha Clip + ShadowCaster Clip + 接收阴影。部署前调用，写入材质供 Yoo 打变体。
        /// </summary>
        [MenuItem("GameMain/Boluo/Ensure Foliage Clip + Shadows", false, 123)]
        public static void EnsureFoliageClipAndShadows()
        {
            int touched = 0;
            string[] matGuids = AssetDatabase.FindAssets(
                "t:Material",
                new[] { "Assets/GameMain/Environment" });
            for (int i = 0; i < matGuids.Length; i++)
            {
                string matPath = AssetDatabase.GUIDToAssetPath(matGuids[i]);
                var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
                if (mat == null || mat.shader == null || mat.shader.name != "CustomSRP/OilNPR")
                    continue;

                string n = mat.name.ToLowerInvariant();
                bool foliage = n.Contains("shu") || n.Contains("guanmu") || n.Contains("huacao")
                               || n.Contains("leaf") || n.Contains("fern") || n.Contains("grass")
                               || n.Contains("bush") || n.Contains("palm") || n.Contains("zacao")
                               || n.Contains("billboard") || n.Contains("contour")
                               || n.StartsWith("sm_env_");
                bool clip = mat.IsKeywordEnabled("_CLIPPING")
                            || (mat.HasProperty("_Clipping") && mat.GetFloat("_Clipping") > 0.5f);
                if (!foliage && !clip)
                    continue;

                bool dirty = false;
                if (mat.HasProperty("_Clipping") && mat.GetFloat("_Clipping") < 0.5f)
                {
                    mat.SetFloat("_Clipping", 1f);
                    dirty = true;
                }

                if (!mat.IsKeywordEnabled("_CLIPPING"))
                {
                    mat.EnableKeyword("_CLIPPING");
                    dirty = true;
                }

                if (mat.HasProperty("_Shadows"))
                {
                    if (Mathf.Abs(mat.GetFloat("_Shadows") - 1f) > 0.01f)
                    {
                        mat.SetFloat("_Shadows", 1f);
                        dirty = true;
                    }

                    if (!mat.IsKeywordEnabled("_SHADOWS_CLIP"))
                    {
                        mat.EnableKeyword("_SHADOWS_CLIP");
                        dirty = true;
                    }

                    mat.DisableKeyword("_SHADOWS_DITHER");
                    if (!mat.GetShaderPassEnabled("ShadowCaster"))
                    {
                        mat.SetShaderPassEnabled("ShadowCaster", true);
                        dirty = true;
                    }
                }

                if (mat.HasProperty("_ReceiveShadows"))
                {
                    if (mat.GetFloat("_ReceiveShadows") < 0.5f)
                    {
                        mat.SetFloat("_ReceiveShadows", 1f);
                        dirty = true;
                    }

                    if (!mat.IsKeywordEnabled("_RECEIVE_SHADOWS"))
                    {
                        mat.EnableKeyword("_RECEIVE_SHADOWS");
                        dirty = true;
                    }
                }

                if (dirty)
                {
                    EditorUtility.SetDirty(mat);
                    touched++;
                }
            }

            if (touched > 0)
                AssetDatabase.SaveAssets();
            Log.Info("[MapItem] EnsureFoliageClipAndShadows touched=" + touched);
        }
    }
}
