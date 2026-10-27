using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using YooAsset;

using UnityGameFramework.Runtime;
namespace Game
{
    /// <summary>
    /// 从 MapItemExtras.bytes（BLM1）铺篝火 / FX；Common/texiao 供 inland pieces 回退。
    /// </summary>
    public static class EnvMapItemExtras
    {
        public static readonly string[] ExtraCategories = { "FX_Day", "FX_Night" };

        static readonly List<MapItemData> Loaded = new List<MapItemData>(64);

        /// <summary>最近一次 <see cref="Spawn"/> 读到的 extras。篝火不在 AllMapItem 里，音效锚点用这份。</summary>
        public static IReadOnlyList<MapItemData> LoadedItems => Loaded;

        public static void ClearLoaded() => Loaded.Clear();

        public static IEnumerator LoadDataCoroutine(List<MapItemData> output)
        {
            output?.Clear();
            if (output == null)
                yield break;

            TextAsset text = null;
            if (YooResourceModule.IsInitialized && YooResourceModule.DefaultPackage != null
                && YooResourceModule.DefaultPackage.CheckLocationValid(SceneAssets.MapItemExtrasLocation))
            {
                yield return YooResourceModule.LoadAssetCoroutine<TextAsset>(
                    SceneAssets.MapItemExtrasLocation,
                    (asset, _) => text = asset);
            }

#if UNITY_EDITOR
            if (text == null)
                text = UnityEditor.AssetDatabase.LoadAssetAtPath<TextAsset>(SceneAssets.MapItemExtrasAssetPath);
#endif

            if (text == null)
            {
                string streamingPath = Path.Combine(Application.streamingAssetsPath, "MapItemExtras.bytes");
#if UNITY_ANDROID && !UNITY_EDITOR
                using (var req = UnityEngine.Networking.UnityWebRequest.Get(streamingPath))
                {
                    req.timeout = 15;
                    yield return req.SendWebRequest();
                    if (req.result != UnityEngine.Networking.UnityWebRequest.Result.Success)
                    {
                        Log.Error("[MapItemExtras] StreamingAssets read failed — " + req.error);
                    }
                    else if (req.downloadHandler?.data != null
                        && req.downloadHandler.data.Length > 0)
                    {
                        if (!MapItemData.ReadBinaryTo(req.downloadHandler.data, output))
                            output.Clear();
                        yield break;
                    }
                }
#else
                if (File.Exists(streamingPath))
                {
                    byte[] bytes = File.ReadAllBytes(streamingPath);
                    if (!MapItemData.ReadBinaryTo(bytes, output))
                        output.Clear();
                    yield break;
                }
#endif
            }

            if (text == null || text.bytes == null)
            {
                Log.Error("[MapItemExtras] missing " + SceneAssets.MapItemExtrasLocation);
                yield break;
            }

            if (!MapItemData.ReadBinaryTo(text.bytes, output))
                output.Clear();
        }

        /// <summary>
        /// 只铺氛围类（Other / FX_Day / FX_Night）；Common/texiao 由 IslandWorld inland 回退使用。
        /// </summary>
        public static IEnumerator Spawn(
            Transform mapItemRoot,
            Dictionary<string, Transform> categoryParents,
            List<AssetHandle> ownedHandles,
            System.Action<int> onCount)
        {
            if (mapItemRoot == null || categoryParents == null)
            {
                onCount?.Invoke(0);
                yield break;
            }

            if (categoryParents.TryGetValue("FX_Day", out Transform dayRoot) && dayRoot != null)
                AtmospherePropRegistry.RegisterByName(dayRoot);
            if (categoryParents.TryGetValue("FX_Night", out Transform nightRoot) && nightRoot != null)
                AtmospherePropRegistry.RegisterByName(nightRoot);

            var items = new List<MapItemData>(64);
            yield return LoadDataCoroutine(items);
            Loaded.Clear();
            if (items.Count > 0)
                Loaded.AddRange(items);
            if (items.Count == 0)
            {
                onCount?.Invoke(0);
                yield break;
            }

            var prefabCache = new Dictionary<string, GameObject>(16);
            int placed = 0;

            for (int i = 0; i < items.Count; i++)
            {
                MapItemData d = items[i];
                if (d.Parent == "Common" || d.Parent == "texiao")
                    continue;

                ResolvePaths(d, out string location, out string editorPath);
                if (string.IsNullOrEmpty(location) && string.IsNullOrEmpty(editorPath))
                    continue;

                string cacheKey = location ?? editorPath;
                if (!prefabCache.TryGetValue(cacheKey, out GameObject prefab) || prefab == null)
                {
                    prefab = null;
                    if (YooResourceModule.IsInitialized && YooResourceModule.DefaultPackage != null
                        && !string.IsNullOrEmpty(location)
                        && YooResourceModule.DefaultPackage.CheckLocationValid(location))
                    {
                        yield return YooResourceModule.LoadAssetCoroutine<GameObject>(
                            location,
                            (asset, handle) =>
                            {
                                prefab = asset;
                                if (handle != null)
                                    ownedHandles?.Add(handle);
                            });
                    }

#if UNITY_EDITOR
                    if (prefab == null && !string.IsNullOrEmpty(editorPath))
                        prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(editorPath);
#endif
                    if (prefab != null)
                        prefabCache[cacheKey] = prefab;
                }

                if (prefab == null)
                {
                    Log.Warning("[MapItemExtras] missing " + (location ?? editorPath));
                    continue;
                }

                Transform parent = mapItemRoot;
                if (!string.IsNullOrEmpty(d.Parent)
                    && categoryParents.TryGetValue(d.Parent, out Transform cat)
                    && cat != null)
                    parent = cat;

                GameObject instance = Object.Instantiate(prefab, parent, false);
                instance.name = d.PrefabName;
                instance.transform.position = d.Pos;
                instance.transform.localEulerAngles = d.Rotation;
                instance.transform.localScale = d.Scale;
                instance.SetActive(d.IsActive);

                if (d.PrefabName == "Huodui")
                    yield return EnsureFireControllerCoroutine(instance, ownedHandles);

                AtmospherePropRegistry.RegisterByName(instance.transform);
                placed++;
            }

            Log.Info("[MapItemExtras] spawned=" + placed + " data=" + items.Count
                      + " registry=" + AtmospherePropRegistry.Count);
            onCount?.Invoke(placed);
        }

        static void ResolvePaths(MapItemData d, out string location, out string editorPath)
        {
            string name = d.PrefabName;
            if (d.Parent == "FX_Day" || d.Parent == "FX_Night"
                || (name != null && name.StartsWith("FX_", System.StringComparison.Ordinal)))
            {
                location = SceneAssets.FxLocation(name);
                editorPath = SceneAssets.FxEditorPath(name);
                return;
            }

            string daoYuParent = name == "Huodui" ? "Other" : d.Parent;
            location = SceneAssets.DaoYu1Location(daoYuParent, name);
            editorPath = SceneAssets.DaoYu1EditorPath(daoYuParent, name);
        }

        static IEnumerator EnsureFireControllerCoroutine(GameObject huodui, List<AssetHandle> ownedHandles)
        {
            if (huodui == null)
                yield break;

            FireController fire = huodui.GetComponent<FireController>()
                                 ?? huodui.GetComponentInChildren<FireController>(true);
            if (fire == null)
                fire = huodui.AddComponent<FireController>();

            Material mat = null;
            if (YooResourceModule.IsInitialized && YooResourceModule.DefaultPackage != null
                && YooResourceModule.DefaultPackage.CheckLocationValid(SceneAssets.CampfireNoiseFireLocation))
            {
                yield return YooResourceModule.LoadAssetCoroutine<Material>(
                    SceneAssets.CampfireNoiseFireLocation,
                    (asset, handle) =>
                    {
                        mat = asset;
                        if (handle != null)
                            ownedHandles?.Add(handle);
                    });
            }

#if UNITY_EDITOR
            if (mat == null)
                mat = UnityEditor.AssetDatabase.LoadAssetAtPath<Material>(
                    SceneAssets.CampfireNoiseFireMatPath);
#endif

            if (mat != null)
                fire.BindFlameMaterial(mat);
            else
                Log.Warning("[MapItemExtras] Campfire_NoiseFire material missing — Huodui may have no flame.");

            fire.EnsureRuntimeBindings();
        }
    }
}
