using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using YooAsset;

using UnityGameFramework.Runtime;
namespace Game
{
    /// <summary>
    /// Island 场景里的 MapItem 铺装。读 AllMapItem，建分类，再交给
    /// <see cref="EnvItemStreamerLite"/> 流送；流送关掉时才全量实例化。
    /// 场景卸载走 <see cref="OnDestroy"/>，句柄留到卸场景成功。进程拆世界走 <see cref="Release"/>，才销毁子节点并 Release。
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
        readonly Dictionary<string, GameObject> _fullPrefabCache = new Dictionary<string, GameObject>(192);

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

        public IEnumerator Run(Transform focus, System.Action<float, string> reportProgress)
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

            yield return SceneBankGateSpawner.Build(_categoryParents, _allMapItems);

            // Begin 会把半径平方缓存下来，必须先套真机 72/96，否则用编辑器默认 90/120。
            EnvStreamingConfig.ApplyMobileTierDefaults();
            _colliderLod = gameObject.GetComponent<EnvMeshColliderLod>()
                           ?? gameObject.AddComponent<EnvMeshColliderLod>();
            _colliderLod.Begin(_focus);

            const float fillMin = 0.81f;
            const float fillMax = BootLoadingService.TerrainPrepareMax;
            if (_allMapItems.Count > 0)
            {
                if (EnvStreamingConfig.ResolveStreamingEnabled())
                {
                    _streamer = gameObject.GetComponent<EnvItemStreamerLite>()
                                ?? gameObject.AddComponent<EnvItemStreamerLite>();
                    _streamer.Initialize(
                        _allMapItems, _categoryParents, transform, _focus, _colliderLod);
                    yield return _streamer.PrefillAroundFocus(fillMin, fillMax, reportProgress);
                }
                else
                {
                    yield return FullInstantiateAll(fillMin, fillMax, reportProgress);
                }
            }

            // 首铺结束后立刻按焦点打开近处碰撞（不等 RefreshLoop 首拍）。
            if (_colliderLod != null && _focus != null)
            {
                float colRadius = Mathf.Max(48f, EnvStreamingConfig.MeshColliderEnableRadius);
                _colliderLod.ForceRefreshNear(_focus.position, colRadius);
            }

            // 近场就绪由 Prefill 空等一帧之后抬。extras 等草地结束再铺，不在这里 Instantiate。
            IsReady = true;
            _extrasPending = ShouldSpawnExtras();
            Log.Info(
                $"[MapItem] prefill ready items={_allMapItems.Count} spawned={SpawnedCount} " +
                $"streaming={EnvStreamingConfig.ResolveStreamingEnabled()} extras=deferred");
            reportProgress?.Invoke(fillMax, "正在进入岛屿...");
            yield return null;
            Log.Info("[MapItem] prefill frame resumed");
        }

        bool _extrasPending;
        bool _extrasStarted;

        static bool ShouldSpawnExtras()
        {
            if (GameEntry.MapItemExtras == null)
                return false;
            if (!IslandLeakProbe.Enabled)
                return true;
            bool on = IslandLeakProbe.Allow(IslandLeakProbe.Module.FxDay)
                || IslandLeakProbe.Allow(IslandLeakProbe.Module.FxNight)
                || IslandLeakProbe.Allow(IslandLeakProbe.Module.Texiao);
            if (!on)
                Log.Info("[MapItem] extras skipped (LeakProbe FX off)");
            return on;
        }

        /// <summary>揭罩后铺氛围特效。可重入；已铺过或无需铺则立刻结束。</summary>
        public IEnumerator SpawnDeferredExtras()
        {
            if (!_extrasPending || _extrasStarted || _released)
                yield break;
            _extrasStarted = true;
            _extrasPending = false;
            _extrasSpawnedCount = 0;
            Log.Info("[MapItem] deferred extras begin");
            if (GameEntry.MapItemExtras != null && !_released && this != null)
            {
                yield return GameEntry.MapItemExtras.Spawn(
                    transform, _categoryParents, _ownedHandles, n => _extrasSpawnedCount = n);
            }

            if (_released || this == null)
            {
                Log.Info("[MapItem] deferred extras aborted (released)");
                yield break;
            }

            Log.Info(
                $"[MapItem] ready items={_allMapItems.Count} spawned={SpawnedCount} " +
                $"extras={_extrasSpawnedCount} streaming={EnvStreamingConfig.ResolveStreamingEnabled()}");
        }

        /// <summary>
        /// 退登卸场景：停流送协程，不拆子节点，也不把 <c>_released</c> 置上。
        /// <see cref="OnDestroy"/> 还要走 <c>DropCatalog(releaseHandles: false)</c>，句柄留到卸场景成功。
        /// </summary>
        public void DetachForSceneUnload()
        {
            if (_released)
                return;
            if (_streamer != null)
            {
                _streamer.DetachForSceneUnload();
                _streamer = null;
            }
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
                        DestroyUnlinked(cat.GetChild(c).gameObject);
                }
            }

            DropCatalog(releaseHandles: true);
        }

        /// <summary>全量铺装这条路径拆世界时用。场景卸载走 OnDestroy，不进这里。</summary>
        void DestroyUnlinked(GameObject go)
        {
            if (go == null)
                return;
            go.hideFlags = HideFlags.None;
            Transform t = go.transform;
            if (t != null && t.parent != null)
                t.SetParent(null, false);
            DestroyImmediate(go);
        }

        void OnDestroy()
        {
            if (_released)
                return;
            _released = true;
            // 流送组件在同一个物体上，自己的 OnDestroy 会交还句柄。这里再调 Release() 会立刻拆子节点。
            DropCatalog(releaseHandles: false);
        }

        void DropCatalog(bool releaseHandles)
        {
            if (releaseHandles)
            {
                for (int i = 0; i < _ownedHandles.Count; i++)
                    GameEntry.ReleaseLeaveHandle(_ownedHandles[i]);
            }

            _ownedHandles.Clear();
            _allMapItems.Clear();
            _fullPrefabCache.Clear();
            _fullSpawnedCount = 0;
            _extrasSpawnedCount = 0;
            _extrasPending = false;
            _extrasStarted = false;
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
                    },
                    trackLeave: true);
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
                    req.timeout = 15;
                    yield return req.SendWebRequest();
                    if (req.result != UnityEngine.Networking.UnityWebRequest.Result.Success)
                    {
                        Log.Error("[MapItem] StreamingAssets AllMapItem.bytes failed — " + req.error);
                    }
                    else if (req.downloadHandler != null
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
                // 区域门由 SceneBankGateSpawner 生成，不加载缺失的 BanksChecker 预制体。
                if (category == SceneBankGateCatalog.Category)
                    continue;
                if (!IslandLeakProbe.AllowCategory(category))
                    continue;

                string location = SceneAssets.DaoYu1Location(category, d.PrefabName);
                string editorPath = SceneAssets.DaoYu1EditorPath(category, d.PrefabName);
                GameObject prefab = null;
                yield return LoadPrefabOnce(location, editorPath, go => prefab = go);
                if (prefab == null)
                    continue;

                Transform parent = transform;
                if (category != "null" && _categoryParents.TryGetValue(category, out Transform cat))
                    parent = cat;

                GameObject instance = Instantiate(prefab);
                if (EnvStreamingConfig.IsTreeCategory(category))
                    EnvStreamingConfig.PushTreeBillboardLod(instance);
                EnvItemStreamerLite.AttachSpawned(instance, parent, i, false);
                Vector3 pos = d.Pos;
                EnvGroundFit ground = GameEntry.GroundFit;
                if (ground != null)
                    ground.TrySnapMapItemY(ref pos, category, d.PrefabName);
                if (EnvGroundFit.IsFlatGroundCard(d.PrefabName))
                    EnvGroundFit.DisableCastShadows(instance);

                instance.transform.position = pos;
                instance.transform.localEulerAngles = d.Rotation;
                instance.transform.localScale = d.Scale;
                instance.SetActive(d.IsActive);
                if (EnvStreamingConfig.IsBridgeCategory(category))
                    EnvBridgePillarFit.Ensure(instance);
                _colliderLod?.Register(instance, pos);
                _fullSpawnedCount++;

                if (sw.ElapsedMilliseconds >= EnvStreamingConfig.FinalizePerFrameBudgetMs)
                {
                    float t = total > 0 ? (float)(i + 1) / total : 1f;
                    report?.Invoke(Mathf.Lerp(progressMin, progressMax, t), "正在铺装环境...");
                    yield return null;
                    sw.Restart();
                }
            }

            report?.Invoke(progressMax, "正在铺装环境...");
        }

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
                }, trackLeave: true);
            }

#if UNITY_EDITOR
            if (prefab == null && !string.IsNullOrEmpty(editorPath))
                prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(editorPath);
#endif
            if (prefab != null && !string.IsNullOrEmpty(location))
                _fullPrefabCache[location] = prefab;
            onDone?.Invoke(prefab);
        }

        Transform ResolveFocus()
        {
            Camera cam = Camera.main;
            if (cam != null)
                return cam.transform;
            var go = new GameObject("MapItemFocus");
            go.transform.SetParent(transform, false);
            go.transform.position = IslandSpawnPose.DefaultPos;
            return go.transform;
        }
    }
}
