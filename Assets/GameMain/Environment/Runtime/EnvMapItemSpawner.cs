using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using YooAsset;

using UnityGameFramework.Runtime;
namespace Game
{
    /// <summary>
    /// MapItem 根节点铺装：读 AllMapItem、建分类层级、启动流式或全量路径。
    /// </summary>
    public sealed class EnvMapItemSpawner : MonoBehaviour
    {
        public static readonly string[] DefaultCategories =
        {
            "BanksChecker", "Common", "Guanmu", "Hua", "JianZhu", "Lvshu", "Other",
            "Qiao", "ShiTou", "Songshu", "Yeshu", "ZaWu", "Zacao", "Zhibei", "texiao",
            "FX_Day", "FX_Night",
        };

        readonly Dictionary<string, Transform> _categoryParents = new Dictionary<string, Transform>(32);
        readonly List<MapItemData> _allMapItems = new List<MapItemData>(2560);
        readonly List<AssetHandle> _ownedHandles = new List<AssetHandle>(8);

        EnvItemStreamerLite _streamer;
        EnvMeshColliderLod _colliderLod;
        Transform _focus;
        bool _started;
        bool _released;
        int _fullSpawnedCount;
        int _extrasSpawnedCount;

        public int MapItemCount => _allMapItems.Count;

        /// <summary>Loaded AllMapItem rows (for ambience zone catalog / proximity).</summary>
        public IReadOnlyList<MapItemData> AllMapItems => _allMapItems;

        public int SpawnedCount =>
            (_streamer != null ? _streamer.SpawnedCount : _fullSpawnedCount) + _extrasSpawnedCount;

        public int ExtrasSpawnedCount => _extrasSpawnedCount;

        public bool IsReady { get; private set; }

        /// <summary>Forward to streamer trim; no-op on full-spawn path.</summary>
        public int TrimUnusedPrefabHandles() =>
            _streamer != null ? _streamer.TrimUnusedPrefabHandles() : 0;

        /// <summary>
        /// 切预设后把焦点附近未生成的 MapItem 铺齐；全量路径无操作。
        /// 截图 / READY 前调用，避免画面空缺。
        /// </summary>
        public IEnumerator CatchUpAroundFocus(System.Action<float, string> report = null)
        {
            if (_streamer == null || !IsReady)
                yield break;

            yield return _streamer.CatchUpAroundFocus(report);

            if (_colliderLod != null && _focus != null)
            {
                float colRadius = Mathf.Max(48f, EnvStreamingConfig.MeshColliderEnableRadius);
                _colliderLod.ForceRefreshNear(_focus.position, colRadius);
            }
        }

        public IEnumerator Run(
            Transform focus,
            System.Action<float, string> reportProgress,
            System.Action onNearReady = null)
        {
            if (_started)
                yield break;
            _started = true;
            _focus = focus != null ? focus : ResolveFocus();
            EnsureCategoryHierarchy();

            // Progress stays within World phase: 0.80 → TerrainPrepareMax(0.86).
            reportProgress?.Invoke(0.80f, "正在读取地图摆放...");
            yield return LoadAllMapItemBytes();
            if (_allMapItems.Count == 0)
                Log.Error("[MapItem] AllMapItem empty or failed to load");

            yield return EnvRockShoreWet.PreloadCoroutine(_ownedHandles);

            _colliderLod = gameObject.GetComponent<EnvMeshColliderLod>()
                           ?? gameObject.AddComponent<EnvMeshColliderLod>();
            _colliderLod.Begin(_focus);

            const float fillMin = 0.81f;
            const float fillMax = BootLoadingService.TerrainPrepareMax;
            bool nearSignaled = false;
            void SignalNearReady()
            {
                if (nearSignaled)
                    return;
                nearSignaled = true;
                ForceRefreshCollidersNearFocus();
                onNearReady?.Invoke();
            }

            if (_allMapItems.Count > 0)
            {
                if (EnvStreamingConfig.ResolveStreamingEnabled())
                {
                    _streamer = gameObject.GetComponent<EnvItemStreamerLite>()
                                ?? gameObject.AddComponent<EnvItemStreamerLite>();
                    _streamer.Initialize(_allMapItems, _categoryParents, transform, _focus, _colliderLod);
                    yield return _streamer.PrefillAroundFocus(
                        fillMin, fillMax, reportProgress, SignalNearReady);
                    _streamer.BeginStreamingAfterPrefill();
                }
                else
                {
                    yield return FullSpawnBudgeted(fillMin, fillMax, reportProgress);
                    SignalNearReady();
                }
            }
            else
            {
                SignalNearReady();
            }

            reportProgress?.Invoke(fillMax, "正在铺装氛围特效...");
            _extrasSpawnedCount = 0;
            yield return EnvMapItemExtras.Spawn(
                transform, _categoryParents, _ownedHandles, n => _extrasSpawnedCount = n);

            // extras 后再扫一拍碰撞（Near 时已 Force 过一拍）。
            ForceRefreshCollidersNearFocus();

            IsReady = true;
            Log.Info(
                $"[MapItem] ready items={_allMapItems.Count} spawned={SpawnedCount} " +
                $"extras={_extrasSpawnedCount} streaming={EnvStreamingConfig.ResolveStreamingEnabled()}");
        }

        void ForceRefreshCollidersNearFocus()
        {
            if (_colliderLod == null || _focus == null)
                return;
            float colRadius = Mathf.Max(48f, EnvStreamingConfig.MeshColliderEnableRadius);
            _colliderLod.ForceRefreshNear(_focus.position, colRadius);
        }

        public void Release()
        {
            if (_released)
                return;
            _released = true;
            if (_streamer != null)
            {
                _streamer.Release();
                _streamer = null;
            }
            else
            {
                // Full-spawn path: instances live under category nodes.
                for (int i = transform.childCount - 1; i >= 0; i--)
                {
                    Transform cat = transform.GetChild(i);
                    for (int c = cat.childCount - 1; c >= 0; c--)
                        Destroy(cat.GetChild(c).gameObject);
                }
            }

            for (int i = 0; i < _ownedHandles.Count; i++)
                _ownedHandles[i]?.Release();
            _ownedHandles.Clear();
            _allMapItems.Clear();
            _fullPrefabCache.Clear();
            _fullSpawnedCount = 0;
            _extrasSpawnedCount = 0;
            IsReady = false;
            _started = false;
        }

        void EnsureCategoryHierarchy()
        {
            _categoryParents.Clear();
            for (int i = 0; i < transform.childCount; i++)
            {
                Transform child = transform.GetChild(i);
                _categoryParents[child.name] = child;
            }

            for (int i = 0; i < DefaultCategories.Length; i++)
            {
                string name = DefaultCategories[i];
                if (_categoryParents.ContainsKey(name))
                    continue;
                var go = new GameObject(name);
                go.transform.SetParent(transform, false);
                _categoryParents[name] = go.transform;
            }
        }

        IEnumerator LoadAllMapItemBytes()
        {
            _allMapItems.Clear();
            TextAsset text = null;
            AssetHandle handle = null;

            if (YooResourceModule.IsInitialized && YooResourceModule.DefaultPackage != null
                && YooResourceModule.DefaultPackage.CheckLocationValid(SceneAssets.AllMapItemLocation))
            {
                yield return YooResourceModule.LoadAssetCoroutine<TextAsset>(
                    SceneAssets.AllMapItemLocation,
                    (asset, h) =>
                    {
                        text = asset;
                        handle = h;
                    });
                if (handle != null)
                    _ownedHandles.Add(handle);
            }

#if UNITY_EDITOR
            if (text == null)
                text = UnityEditor.AssetDatabase.LoadAssetAtPath<TextAsset>(SceneAssets.AllMapItemAssetPath);
#endif

            // 真机兜底：StreamingAssets/AllMapItem.bytes（Yoo 地址未进包时仍能铺岛）
            if (text == null)
            {
                string streamingPath = Path.Combine(Application.streamingAssetsPath, "AllMapItem.bytes");
#if UNITY_ANDROID && !UNITY_EDITOR
                using (var req = UnityEngine.Networking.UnityWebRequest.Get(streamingPath))
                {
                    yield return req.SendWebRequest();
                    if (req.result == UnityEngine.Networking.UnityWebRequest.Result.Success
                        && req.downloadHandler != null
                        && req.downloadHandler.data != null
                        && req.downloadHandler.data.Length > 0)
                    {
                        if (!MapItemData.ReadBinaryTo(req.downloadHandler.data, _allMapItems))
                            _allMapItems.Clear();
                        yield break;
                    }
                }
#else
                if (File.Exists(streamingPath))
                {
                    byte[] bytes = File.ReadAllBytes(streamingPath);
                    if (!MapItemData.ReadBinaryTo(bytes, _allMapItems))
                        _allMapItems.Clear();
                    yield break;
                }
#endif
            }

            if (text == null || text.bytes == null)
            {
                Log.Error(
                    "[MapItem] missing " + SceneAssets.AllMapItemLocation +
                    " / " + SceneAssets.AllMapItemAssetPath +
                    " / StreamingAssets/AllMapItem.bytes");
                yield break;
            }

            if (!MapItemData.ReadBinaryTo(text.bytes, _allMapItems))
                _allMapItems.Clear();
        }

        IEnumerator FullSpawnBudgeted(float progressMin, float progressMax, System.Action<float, string> report)
        {
            yield return FullInstantiateAll(progressMin, progressMax, report);
        }

        IEnumerator FullInstantiateAll(float progressMin, float progressMax, System.Action<float, string> report)
        {
            if (_streamer != null)
            {
                _streamer.Release();
                Destroy(_streamer);
                _streamer = null;
            }

            _fullSpawnedCount = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int total = _allMapItems.Count;
            for (int i = 0; i < total; i++)
            {
                MapItemData d = _allMapItems[i];
                string category = d.Parent ?? "null";
                string location = SceneAssets.DaoYu1Location(category, d.PrefabName);
                string editorPath = SceneAssets.DaoYu1EditorPath(category, d.PrefabName);
                GameObject prefab = null;
                yield return LoadPrefabOnce(location, editorPath, go => prefab = go);
                if (prefab == null)
                    continue;

                Transform parent = transform;
                if (category != "null" && _categoryParents.TryGetValue(category, out Transform cat))
                    parent = cat;

                GameObject instance = Instantiate(prefab, parent, false);
                instance.name = i + " " + prefab.name;
                Vector3 pos = d.Pos;
                EnvGroundFit.TrySnapMapItemY(ref pos, category, d.PrefabName);
                if (EnvGroundFit.IsFlatGroundCard(d.PrefabName))
                    EnvGroundFit.DisableCastShadows(instance);

                instance.transform.position = pos;
                instance.transform.localEulerAngles = d.Rotation;
                instance.transform.localScale = d.Scale;
                instance.SetActive(d.IsActive);
                if (EnvStreamingConfig.IsRockCategory(category))
                {
                    EnvRockColliders.Ensure(instance, d.PrefabName);
                    EnvRockShoreWet.ApplyIfNearShore(instance);
                }
                if (EnvStreamingConfig.IsBridgeCategory(category))
                    EnvBridgePillarFit.Ensure(instance);
                if (EnvStreamingConfig.IsFarFieldLandmark(d.PrefabName))
                    EnvLandmarkColliders.Ensure(instance, d.PrefabName);
                _colliderLod?.Register(instance, pos);
                _fullSpawnedCount++;

                if (sw.ElapsedMilliseconds >= EnvStreamingConfig.EffectiveFinalizePerFrameBudgetMs)
                {
                    float t = total > 0 ? (float)(i + 1) / total : 1f;
                    report?.Invoke(Mathf.Lerp(progressMin, progressMax, t), "正在铺装环境...");
                    yield return null;
                    sw.Restart();
                }
            }

            report?.Invoke(progressMax, "正在铺装环境...");
        }

        readonly Dictionary<string, GameObject> _fullPrefabCache = new Dictionary<string, GameObject>(192);

        IEnumerator LoadPrefabOnce(string location, string editorPath, System.Action<GameObject> onDone)
        {
            if (!string.IsNullOrEmpty(location) && _fullPrefabCache.TryGetValue(location, out GameObject cached)
                && cached != null)
            {
                onDone?.Invoke(cached);
                yield break;
            }

            GameObject prefab = null;
            if (YooResourceModule.IsInitialized && YooResourceModule.DefaultPackage != null
                && !string.IsNullOrEmpty(location)
                && YooResourceModule.DefaultPackage.CheckLocationValid(location))
            {
                yield return YooResourceModule.LoadAssetCoroutine<GameObject>(location, (asset, handle) =>
                {
                    prefab = asset;
                    if (handle != null)
                        _ownedHandles.Add(handle);
                });
            }

#if UNITY_EDITOR
            if (prefab == null && !string.IsNullOrEmpty(editorPath))
                prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(editorPath);
#endif
            if (prefab != null && !string.IsNullOrEmpty(location))
                _fullPrefabCache[location] = prefab;
            onDone?.Invoke(prefab);
        }

        static Transform ResolveFocus()
        {
            Camera cam = Camera.main;
            if (cam != null)
                return cam.transform;
            var go = new GameObject("MapItemFocus");
            go.transform.position = IslandSpawnPose.DefaultPos;
            return go.transform;
        }

        void OnDestroy()
        {
            Release();
        }
    }
}
