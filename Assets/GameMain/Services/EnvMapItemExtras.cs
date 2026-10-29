using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityGameFramework.Runtime;
using YooAsset;

namespace Game
{
    /// <summary>
    /// 从 MapItemExtras.bytes（BLM1）铺篝火 / FX；Common/texiao 供 inland pieces 回退。
    /// 挂在 Island 场景里。坐标表跟着场景销毁，不在静态字段里活过退登。
    /// 铺出来的实例和 YooAsset 句柄仍由地图铺装方持有。
    /// </summary>
    public sealed class EnvMapItemExtras : MonoBehaviour
    {
        public static readonly string[] ExtraCategories = { "FX_Day", "FX_Night" };

        readonly List<MapItemData> _loaded = new List<MapItemData>(64);

        /// <summary>最近一次 <see cref="Spawn"/> 读到的 extras。篝火不在 AllMapItem 里，音效锚点用这份。</summary>
        public IReadOnlyList<MapItemData> LoadedItems => _loaded;

        public static EnvMapItemExtras Instance { get; private set; }

        public static EnvMapItemExtras Ensure()
        {
            EnvMapItemExtras service = IslandServiceHost.Ensure(Instance, "EnvMapItemExtras");
            Instance = service;
            return service;
        }

        void Awake()
        {
            Instance = this;
            GameEntry.MapItemExtras = this;
        }

        void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
            if (GameEntry.MapItemExtras == this)
                GameEntry.MapItemExtras = null;
            Clear();
        }

        public void Clear() => _loaded.Clear();

        public IEnumerator LoadDataCoroutine(List<MapItemData> output)
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
                    (asset, _) => text = asset,
                    trackLeave: true);
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
        public IEnumerator Spawn(
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
                AtmospherePropService.Instance?.RegisterByName(dayRoot);
            if (categoryParents.TryGetValue("FX_Night", out Transform nightRoot) && nightRoot != null)
                AtmospherePropService.Instance?.RegisterByName(nightRoot);

            var items = new List<MapItemData>(64);
            yield return LoadDataCoroutine(items);
            _loaded.Clear();
            if (items.Count > 0)
                _loaded.AddRange(items);
            if (items.Count == 0)
            {
                onCount?.Invoke(0);
                yield break;
            }

            var prefabCache = new Dictionary<string, GameObject>(16);
            int placed = 0;

            for (int i = 0; i < items.Count; i++)
            {
                // 退登卸场景后不要继续 Instantiate。
                if (mapItemRoot == null)
                {
                    Log.Info("[MapItemExtras] abort — mapItemRoot gone");
                    onCount?.Invoke(placed);
                    yield break;
                }

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
                            },
                            trackLeave: true);
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

                // 先无父 Instantiate，再 AttachSpawned 摘挂：二次进岛避免插进旧兄弟环。
                GameObject instance = Object.Instantiate(prefab);
                EnvItemStreamerLite.AttachSpawned(instance, parent, placed, false);
                instance.name = d.PrefabName;
                instance.transform.position = d.Pos;
                instance.transform.localEulerAngles = d.Rotation;
                instance.transform.localScale = d.Scale;
                instance.SetActive(d.IsActive);

                if (d.PrefabName == "Huodui")
                    BindHuoduiFire(instance);

                AtmospherePropService.Instance?.RegisterByName(instance.transform);
                placed++;
            }

            Log.Info("[MapItemExtras] spawned=" + placed + " data=" + items.Count
                      + " registry=" + (AtmospherePropService.Instance != null ? AtmospherePropService.Instance.PropCount : 0));
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

        /// <summary>
        /// Huodui 预制体上已有 FireController 和 flameMaterial。
        /// 火焰卡片和点光源仍由 <see cref="FireController.EnsureRuntimeBindings"/> 在运行时补上。
        /// </summary>
        static void BindHuoduiFire(GameObject huodui)
        {
            if (huodui == null)
                return;

            FireController fire = huodui.GetComponent<FireController>();
            if (fire == null)
            {
                Log.Warning("[MapItemExtras] Huodui prefab has no FireController.");
                return;
            }

            fire.EnsureRuntimeBindings();
        }
    }
}
