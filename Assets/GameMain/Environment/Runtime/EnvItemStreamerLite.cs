using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using YooAsset;

using UnityGameFramework.Runtime;
namespace Game
{
    /// <summary>
    /// 距离流式 Lite：按相机/焦点位置驻留 MapItem，分帧生成与回收；可选 Native 合批。
    /// </summary>
    public sealed class EnvItemStreamerLite : MonoBehaviour
    {
        const byte StateHidden = 0;
        const byte StateVisible = 1;

        struct Item
        {
            public Vector3 Pos;
            public Vector3 Rotation;
            public Vector3 Scale;
            public bool IsActive;
            public string Category;
            public string PrefabName;
            public string Location;
            public string EditorPath;
            public Transform Parent;
            public float HideDist;
            public float ShowDist;
            public bool SkipNative;
            public bool FarField;
            public byte State;
            public GameObject Go;
            public int ProtoIndex;
            public bool InNative;
        }

        readonly List<Item> _items = new List<Item>(2560);
        readonly Dictionary<string, GameObject> _prefabCache = new Dictionary<string, GameObject>(192);
        readonly Dictionary<string, AssetHandle> _prefabHandles = new Dictionary<string, AssetHandle>(192);
        readonly List<int> _spawnQueue = new List<int>(256);
        readonly List<int> _despawnQueue = new List<int>(256);
        readonly HashSet<int> _spawnQueued = new HashSet<int>();
        readonly HashSet<int> _despawnQueued = new HashSet<int>();

        Transform _focus;
        EnvMeshColliderLod _colliderLod;
        EnvNativeInstancing _native;
        bool _running;
        int _evalCursor;
        int _spawnedCount;

        public int ItemCount => _items.Count;
        public int SpawnedCount => _spawnedCount;

        public void Initialize(
            List<MapItemData> data,
            Dictionary<string, Transform> categoryParents,
            Transform mapItemRoot,
            Transform focus,
            EnvMeshColliderLod colliderLod)
        {
            _focus = focus;
            _colliderLod = colliderLod;
            EnvStreamingConfig.ApplyMobileTierDefaults();
#if UNITY_ANDROID && !UNITY_EDITOR
            if (EnvStreamingConfig.NativeInstancingEnabled)
            {
                _native = EnvNativeInstancing.Ensure();
                _native.SetMaxDist(EnvStreamingConfig.NativeInstancingMaxDist);
            }
#endif

            _items.Clear();
            for (int i = 0; i < data.Count; i++)
            {
                MapItemData d = data[i];
                string category = d.Parent ?? "null";
                string prefabName = d.PrefabName;
                float hide = EnvStreamingConfig.GetHideDistance(category, prefabName, out float show);
                Transform parent = mapItemRoot;
                if (category != "null" && categoryParents != null
                    && categoryParents.TryGetValue(category, out Transform cat))
                    parent = cat;

                Vector3 pos = d.Pos;
                EnvGroundFit.TrySnapMapItemY(ref pos, category, prefabName);

                _items.Add(new Item
                {
                    Pos = pos,
                    Rotation = d.Rotation,
                    Scale = d.Scale,
                    IsActive = d.IsActive,
                    Category = category,
                    PrefabName = prefabName,
                    Location = SceneAssets.DaoYu1Location(category, prefabName),
                    EditorPath = SceneAssets.DaoYu1EditorPath(category, prefabName),
                    Parent = parent,
                    HideDist = hide,
                    ShowDist = show,
                    SkipNative = EnvStreamingConfig.ShouldForceSkipNativeInstancing(category, prefabName)
                                 || EnvStreamingConfig.IsFarFieldLandmark(prefabName),
                    FarField = EnvStreamingConfig.IsFarFieldLandmark(prefabName),
                    State = StateHidden,
                    Go = null,
                    ProtoIndex = -1,
                    InNative = false,
                });
            }

            _running = true;
            StartCoroutine(StreamLoop());
            Log.Info(
                $"[EnvItemStreamerLite] init items={_items.Count} streaming=on " +
                $"native={(_native != null)}");
        }

        public IEnumerator PrefillAroundFocus(float progressMin, float progressMax, System.Action<float, string> report)
        {
            if (_focus == null)
                yield break;

            Vector3 p = _focus.position;
            var near = new List<int>(512);
            for (int i = 0; i < _items.Count; i++)
            {
                Item item = _items[i];
                float dx = item.Pos.x - p.x;
                float dz = item.Pos.z - p.z;
                float distSq = dx * dx + dz * dz;
                // 首装：大树用 PrefillBoost（标准版=keep 180）；松/绿超 SilhouetteMin 隔株。
                float show = EnvStreamingConfig.GetPrefillShowDistance(item.Category, item.ShowDist);
                if (show < 1e20f && distSq > show * show)
                    continue;
                if (EnvStreamingConfig.ShouldThinFarTree(item.Category, distSq, i))
                    continue;
                near.Add(i);
            }

            // 严格按距离升序：先近后远。
            // _prefabCache 会让重复 LoadPrefab 退化为 Dictionary 查找，无需再按 Location 分组，
            // 否则一个 prefab 的最远实例会排在另一 prefab 的最近实例之前。
            near.Sort((a, b) =>
            {
                float dax = _items[a].Pos.x - p.x;
                float daz = _items[a].Pos.z - p.z;
                float dbx = _items[b].Pos.x - p.x;
                float dbz = _items[b].Pos.z - p.z;
                return (dax * dax + daz * daz).CompareTo(dbx * dbx + dbz * dbz);
            });

            int total = near.Count;
            int done = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int k = 0; k < near.Count; k++)
            {
                int idx = near[k];
                Item item = _items[idx];
                if (item.State == StateVisible && item.Go != null)
                {
                    done++;
                    continue;
                }

                GameObject prefab = null;
                yield return LoadPrefab(item.Location, item.EditorPath, go => prefab = go);
                if (prefab == null)
                {
                    Log.Warning(
                        "[EnvItemStreamerLite] prefab missing loc=" + item.Location +
                        " cat=" + item.Category + " name=" + item.PrefabName);
                    done++;
                    continue;
                }

#if UNITY_ANDROID && !UNITY_EDITOR
                if (_native != null && !item.SkipNative)
                    _native.AddPrototype(prefab);
#endif

                GameObject instance = Instantiate(prefab, item.Parent, false);
                FinalizeSpawned(idx, instance, prefab);
                done++;

                if (sw.ElapsedMilliseconds >= EnvStreamingConfig.FinalizePerFrameBudgetMs)
                {
                    float t = total > 0 ? (float)done / total : 1f;
                    report?.Invoke(Mathf.Lerp(progressMin, progressMax, t), "正在铺装环境...");
                    yield return null;
                    sw.Restart();
                }
            }

            report?.Invoke(progressMax, "正在铺装环境...");
            Log.Info($"[EnvItemStreamerLite] prefill spawned={_spawnedCount}/{_items.Count} near={total}");
        }

        IEnumerator StreamLoop()
        {
            while (_running)
            {
                if (_focus == null)
                {
                    Camera cam = Camera.main;
                    if (cam != null)
                        _focus = cam.transform;
                    yield return null;
                    continue;
                }

                EvaluateQueues();
                int spawnBudget = EnvStreamingConfig.SpawnPerFrameBudget;
                int despawnBudget = EnvStreamingConfig.DespawnPerFrameBudget;

                for (int i = 0; i < _despawnQueue.Count && despawnBudget > 0; i++)
                {
                    DespawnItem(_despawnQueue[i]);
                    despawnBudget--;
                }

                _despawnQueue.Clear();
                _despawnQueued.Clear();

                for (int i = 0; i < _spawnQueue.Count && spawnBudget > 0; i++)
                {
                    yield return SpawnItem(_spawnQueue[i]);
                    spawnBudget--;
                }

                // 未处理完的由下一帧 EvaluateQueues 重新入队。
                _spawnQueue.Clear();
                _spawnQueued.Clear();
                yield return null;
            }
        }

        void EvaluateQueues()
        {
            Vector3 p = _focus.position;
            int n = _items.Count;
            int steps = Mathf.Min(256, n);
            for (int s = 0; s < steps; s++)
            {
                if (_evalCursor >= n)
                    _evalCursor = 0;
                int i = _evalCursor++;
                Item item = _items[i];
                float dx = item.Pos.x - p.x;
                float dz = item.Pos.z - p.z;
                float distSq = dx * dx + dz * dz;

                bool neverUnload = item.HideDist >= 1e20f;
                bool thinFar = EnvStreamingConfig.ShouldThinFarTree(item.Category, distSq, i);
                if (item.State == StateHidden)
                {
                    if (!thinFar && (neverUnload || distSq <= item.ShowDist * item.ShowDist))
                    {
                        if (_spawnQueued.Add(i))
                            _spawnQueue.Add(i);
                    }
                }
                else if (!neverUnload && (thinFar || distSq > item.HideDist * item.HideDist))
                {
                    // 超 keep 卸，或标准版 120m 外非留株隔掉。
                    if (_despawnQueued.Add(i))
                        _despawnQueue.Add(i);
                }
            }

            // _evalCursor 轮询入队顺序与距离无关；按距离升序，保证 StreamLoop 排出时"近先远后"。
            SortSpawnQueueByDistance(p);
        }

        void SortSpawnQueueByDistance(Vector3 p)
        {
            int count = _spawnQueue.Count;
            if (count < 2)
                return;

            // 按距离升序：近→远。每帧只对当前帧入队的若干项排序，开销可忽略。
            _spawnQueue.Sort((a, b) =>
            {
                Item ia = _items[a];
                Item ib = _items[b];
                float dax = ia.Pos.x - p.x;
                float daz = ia.Pos.z - p.z;
                float dbx = ib.Pos.x - p.x;
                float dbz = ib.Pos.z - p.z;
                return (dax * dax + daz * daz).CompareTo(dbx * dbx + dbz * dbz);
            });
        }

        IEnumerator SpawnItem(int index)
        {
            if (index < 0 || index >= _items.Count)
                yield break;
            Item item = _items[index];
            if (item.State == StateVisible && item.Go != null)
                yield break;

            GameObject prefab = null;
            yield return LoadPrefab(item.Location, item.EditorPath, go => prefab = go);
            if (prefab == null)
                yield break;

            GameObject instance = Instantiate(prefab, item.Parent, false);
            FinalizeSpawned(index, instance, prefab);
        }

        void FinalizeSpawned(int index, GameObject instance, GameObject prefab)
        {
            if (instance == null || index < 0 || index >= _items.Count)
                return;

            Item item = _items[index];
            if (item.State == StateVisible && item.Go != null)
            {
                Destroy(instance);
                return;
            }

            instance.name = index + " " + prefab.name;
            instance.transform.SetParent(item.Parent, false);

            Vector3 pos = item.Pos;
            if (EnvGroundFit.TrySnapMapItemY(ref pos, item.Category, item.PrefabName))
                item.Pos = pos;

            if (EnvGroundFit.IsFlatGroundCard(item.PrefabName) || item.FarField)
                EnvGroundFit.DisableCastShadows(instance);

            instance.transform.position = pos;
            instance.transform.localEulerAngles = item.Rotation;
            instance.transform.localScale = item.Scale;
            // Foliage material patches (clipping/shadows/keyword) live in
            // MapItemEditorMenu.EnsureFoliageClipAndShadows (Editor menu).
            // Prefab-driven LOD0+LOD1 + Billboard disable used to live in
            // EnvLodRepair; deleted along with the Outline pass.

            // 阴影 atlas 留给大树：灌木/草/杂物只显示不投射，地面接触影更稳、更省。
            if (EnvStreamingConfig.IsSmallTreeCategory(item.Category)
                || EnvStreamingConfig.IsSmallVegCategory(item.Category)
                || EnvStreamingConfig.IsClutterCategory(item.Category))
                EnvGroundFit.DisableCastShadows(instance);

            instance.SetActive(item.IsActive);
            if (EnvStreamingConfig.IsRockCategory(item.Category))
                EnvRockColliders.Ensure(instance, item.PrefabName);
            if (EnvStreamingConfig.IsBridgeCategory(item.Category))
                EnvBridgePillarFit.Ensure(instance);
            if (item.FarField)
                EnvLandmarkColliders.Ensure(instance, item.PrefabName);
            _colliderLod?.Register(instance, pos);

            int proto = -1;
            bool inNative = false;
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!item.SkipNative && _native != null)
            {
                proto = _native.AddPrototype(prefab);
                if (proto >= 0 && _native.TryAddInstance(instance, proto))
                    inNative = true;
            }
#endif

            item.Go = instance;
            item.State = StateVisible;
            item.ProtoIndex = proto;
            item.InNative = inNative;
            _items[index] = item;
            _spawnedCount++;
        }

        void DespawnItem(int index)
        {
            if (index < 0 || index >= _items.Count)
                return;
            Item item = _items[index];
            if (item.Go == null)
            {
                item.State = StateHidden;
                _items[index] = item;
                return;
            }

#if UNITY_ANDROID && !UNITY_EDITOR
            if (item.InNative && _native != null)
                _native.RemoveInstance(item.Go, item.ProtoIndex);
#endif
            _colliderLod?.Unregister(item.Go);
            Destroy(item.Go);
            item.Go = null;
            item.State = StateHidden;
            item.InNative = false;
            item.ProtoIndex = -1;
            _items[index] = item;
            _spawnedCount = Mathf.Max(0, _spawnedCount - 1);
        }

        IEnumerator LoadPrefab(string location, string editorPath, System.Action<GameObject> onDone)
        {
            if (!string.IsNullOrEmpty(location) && _prefabCache.TryGetValue(location, out GameObject cached)
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
                    if (handle != null && !string.IsNullOrEmpty(location))
                        _prefabHandles[location] = handle;
                });
            }

#if UNITY_EDITOR
            if (prefab == null && !string.IsNullOrEmpty(editorPath))
                prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(editorPath);
#endif

            if (prefab != null && !string.IsNullOrEmpty(location))
                _prefabCache[location] = prefab;
            onDone?.Invoke(prefab);
        }

        bool _released;

        public void Release()
        {
            if (_released)
                return;
            _released = true;
            _running = false;
            StopAllCoroutines();
            for (int i = 0; i < _items.Count; i++)
            {
                if (_items[i].Go != null)
                    Destroy(_items[i].Go);
            }

            _items.Clear();
            _spawnedCount = 0;

            foreach (var kv in _prefabHandles)
                kv.Value?.Release();
            _prefabHandles.Clear();
            _prefabCache.Clear();
        }

        void OnDestroy()
        {
            Release();
        }
    }
}
