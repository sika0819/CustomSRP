using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using UnityEngine.Rendering;
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

        /// <summary>Full Prefab + LODGroup.</summary>
        const byte TierFull = 0;
        /// <summary>Lightweight single-card billboard (optionally native-batched).</summary>
        const byte TierBillboard = 1;

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
            public byte LodTier;
            /// <summary>Desired tier for next spawn after a forced despawn (tier swap).</summary>
            public byte PendingTier;
            public GameObject Go;
            public int ProtoIndex;
            public bool InNative;
            public Renderer[] ShadowCasters;
            public bool CastingShadows;
        }

        readonly List<Item> _items = new List<Item>(2560);
        readonly Dictionary<string, GameObject> _prefabCache = new Dictionary<string, GameObject>(192);
        readonly Dictionary<string, AssetHandle> _prefabHandles = new Dictionary<string, AssetHandle>(192);
        readonly Dictionary<string, int> _locationUsers = new Dictionary<string, int>(192);
        readonly HashSet<string> _loadingLocations = new HashSet<string>();
        readonly List<int> _spawnQueue = new List<int>(256);
        readonly List<int> _despawnQueue = new List<int>(256);
        readonly HashSet<int> _spawnQueued = new HashSet<int>();
        readonly HashSet<int> _despawnQueued = new HashSet<int>();

        Transform _focus;
        EnvNativeInstancing _native;
        readonly Stopwatch _sliceSw = new Stopwatch();
        int _sliceFrame = -1;
        bool _running;
        bool _spawnBacklog;
        int _evalCursor;
        int _spawnedCount;

        public static EnvItemStreamerLite Current { get; private set; }

        public int ItemCount => _items.Count;
        public int SpawnedCount => _spawnedCount;

        /// <summary>
        /// 本帧生成预算没排完，或有预制体还在加载。卸载要让开 Instantiate。
        /// </summary>
        public bool HasPendingSpawn => _spawnBacklog || _loadingLocations.Count > 0;

        public void Initialize(
            List<MapItemData> data,
            Dictionary<string, Transform> categoryParents,
            Transform mapItemRoot,
            Transform focus)
        {
            _focus = focus;
            EnvStreamingConfig.ApplyMobileTierDefaults();
#if UNITY_ANDROID && !UNITY_EDITOR
            if (EnvStreamingConfig.NativeInstancingEnabled)
            {
                _native = EnvNativeInstancing.Ensure();
                _native.SetMaxDist(EnvStreamingConfig.NativeInstancingMaxDist);
                _native.SetShadowCastMaxDist(EnvStreamingConfig.ShadowCastMaxDist);
            }
#endif

            _items.Clear();
            _spawnQueue.Clear();
            _despawnQueue.Clear();
            _spawnQueued.Clear();
            _despawnQueued.Clear();
            _locationUsers.Clear();
            _loadingLocations.Clear();
            _spawnBacklog = false;
            _evalCursor = 0;
            Current = this;
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
                    LodTier = TierFull,
                    PendingTier = TierFull,
                    Go = null,
                    ProtoIndex = -1,
                    InNative = false,
                });
            }

            _running = true;
            _prefillActive = false;
            StartCoroutine(StreamLoop());
            Log.Info(
                $"[EnvItemStreamerLite] init items={_items.Count} streaming=on " +
                $"native={(_native != null)}");
        }

        bool _prefillActive;
        /// <summary>铺装时跳过世界几何。摄像机仍画加载界面，避免 Game 视图报没有摄像机。</summary>
        public static bool PrefillSuppressRender { get; private set; }

        /// <summary>进岛加载被放弃时松开铺装闩，避免流送协程一直让在 Prefill。</summary>
        public static void ReleasePrefillHold()
        {
            PrefillSuppressRender = false;
            if (Current != null)
                Current._prefillActive = false;
        }

        public IEnumerator PrefillAroundFocus(float progressMin, float progressMax, System.Action<float, string> report)
        {
            if (_focus == null)
                yield break;

            // 铺装期间关掉流送。两条协程同时 LoadAssetAsync，离岛后再进会叠在未完成的
            // LoadFromFile 上，主线程卡死在「正在铺装环境」。
            _prefillActive = true;
            // 摄像机保持开启：关掉它时 Game 视图会写 “Display 1 No cameras rendering”，
            // 加载条也画不出来。世界几何由 CameraRenderer 在此标志下跳过。
            PrefillSuppressRender = true;
            PrefillFrameGate.Begin();

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
            int skippedVisible = 0;
            Log.Info($"[EnvItemStreamerLite] prefill begin near={total} items={_items.Count}");
            report?.Invoke(progressMin, "正在铺装环境...");
            // 二次进岛时 yield return null 交出去的那一帧不再回来。铺装在这一次调用里做完，
            // 同步 LoadAssetSync，中间不把协程交回玩家循环。
            for (int k = 0; k < near.Count; k++)
            {
                int idx = near[k];
                Item item = _items[idx];
                if (item.State == StateVisible && item.Go != null)
                {
                    skippedVisible++;
                    done++;
                    continue;
                }

                PrefillFrameGate.Phase = 2;
                PrefillFrameGate.Done = done;
                GameObject prefab = LoadPrefabNow(item.Location, item.EditorPath, "prefill");
                PrefillFrameGate.Phase = 3;
                PrefillFrameGate.Done = done;
                if (prefab == null)
                {
                    Log.Warning(
                        "[EnvItemStreamerLite] prefab missing loc=" + item.Location +
                        " cat=" + item.Category + " name=" + item.PrefabName);
                    done++;
                    continue;
                }

                byte tier = ResolveSpawnTier(item, DistSqXZ(item.Pos, p));
                var instSw = System.Diagnostics.Stopwatch.StartNew();
                var spawned = CreateInstanceForTier(item, prefab, tier);
                long instMs = instSw.ElapsedMilliseconds;
                if (spawned.Go == null)
                {
                    done++;
                    continue;
                }

                FinalizeSpawned(idx, spawned.Go, prefab, spawned.Tier);
                done++;
                PrefillFrameGate.Done = done;
                if (instMs >= 8 || (done % 20) == 0)
                {
                    Log.Info(
                        $"[EnvItemStreamerLite] prefill sync {done}/{total} " +
                        $"instMs={instMs} name={item.PrefabName}");
                }
            }

            report?.Invoke(progressMax, "正在铺装环境...");
            Log.Info(
                $"[EnvItemStreamerLite] prefill spawned={_spawnedCount}/{_items.Count} " +
                $"near={total} skipVisible={skippedVisible}");
            _prefillActive = false;
            PrefillSuppressRender = false;
            PrefillFrameGate.End();
            yield break;
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

                if (_prefillActive || EnvStreamingConfig.IsPostUncoverQuiet)
                {
                    yield return null;
                    continue;
                }

                EvaluateQueues();
                int spawnBudget = EnvStreamingConfig.SpawnPerFrameBudget;
                int despawnBudget = EnvStreamingConfig.DespawnPerFrameBudget;
                float budgetMs = EnvStreamingConfig.FinalizePerFrameBudgetMs;
                _sliceSw.Restart();
                _sliceFrame = Time.frameCount;

                int despawned = DrainDespawns(despawnBudget, budgetMs);
                if (despawned > 0)
                    _despawnQueue.RemoveRange(0, despawned);

                int spawnCount = _spawnQueue.Count;
                int consumed = 0;
                for (int i = 0; i < spawnCount && spawnBudget > 0; i++)
                {
                    if (SliceBudgetSpent(budgetMs))
                        break;

                    int index = _spawnQueue[i];
                    _spawnQueued.Remove(index);
                    consumed++;
                    if (!ShouldStillSpawn(index))
                        continue;

                    yield return SpawnItem(index);
                    spawnBudget--;
                }

                if (consumed > 0)
                    _spawnQueue.RemoveRange(0, consumed);
                // 预算没用完的下标留在队列里，下一帧接着排，不再整表丢掉。
                _spawnBacklog = _spawnQueue.Count > 0;
                yield return null;
            }
        }

        int DrainDespawns(int despawnBudget, float budgetMs)
        {
            int despawnCount = _despawnQueue.Count;
            int despawned = 0;
            for (int i = 0; i < despawnCount && despawnBudget > 0; i++)
            {
                if (SliceBudgetSpent(budgetMs))
                    break;

                int index = _despawnQueue[i];
                _despawnQueued.Remove(index);
                despawned++;
                if (ShouldStillDespawn(index))
                {
                    DespawnItem(index);
                    despawnBudget--;
                }
            }

            return despawned;
        }

        /// <summary>
        /// 加载协程让出多帧后，在 Instantiate 之前把计时拨回当前帧，避免把等待算进预算。
        /// </summary>
        void RestartSliceIfNewFrame()
        {
            if (Time.frameCount == _sliceFrame)
                return;
            _sliceFrame = Time.frameCount;
            _sliceSw.Restart();
        }

        bool SliceBudgetSpent(float budgetMs)
        {
            RestartSliceIfNewFrame();
            return _sliceSw.ElapsedMilliseconds >= budgetMs;
        }

        bool ShouldStillSpawn(int index)
        {
            if (index < 0 || index >= _items.Count)
                return false;

            Item item = _items[index];
            if (item.State == StateVisible && item.Go != null)
                return false;

            float distSq = DistSqToFocus(item.Pos);
            bool neverUnload = item.HideDist >= 1e20f;
            bool thinFar = EnvStreamingConfig.ShouldThinFarTree(item.Category, distSq, index);
            if (thinFar || (!neverUnload && distSq > item.ShowDist * item.ShowDist))
                return false;

            // 换档卸完后 LodTier != PendingTier，保留目标档；否则按当前距离重判。
            if (item.LodTier == item.PendingTier)
            {
                item.PendingTier = ResolveSpawnTier(item, distSq);
                _items[index] = item;
            }

            return true;
        }

        bool ShouldStillDespawn(int index)
        {
            if (index < 0 || index >= _items.Count)
                return false;

            Item item = _items[index];
            if (item.Go == null)
                return item.State == StateVisible;

            float distSq = DistSqToFocus(item.Pos);
            bool neverUnload = item.HideDist >= 1e20f;
            bool thinFar = EnvStreamingConfig.ShouldThinFarTree(item.Category, distSq, index);
            if (!neverUnload && (thinFar || distSq > item.HideDist * item.HideDist))
                return true;

            if (item.LodTier == TierFull
                && EnvStreamingConfig.ShouldDowngradeTreeToBillboard(item.Category, distSq))
            {
                item.PendingTier = TierBillboard;
                _items[index] = item;
                return true;
            }

            if (item.LodTier == TierBillboard
                && EnvStreamingConfig.ShouldUpgradeTreeToFull(item.Category, distSq))
            {
                item.PendingTier = TierFull;
                _items[index] = item;
                return true;
            }

            if (item.PendingTier != item.LodTier)
            {
                item.PendingTier = item.LodTier;
                _items[index] = item;
            }

            return false;
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
                        // After Full↔Billboard swap: LodTier (pre-despawn) != PendingTier
                        // → keep PendingTier. Otherwise re-resolve from distance.
                        if (item.LodTier == item.PendingTier)
                            item.PendingTier = ResolveSpawnTier(item, distSq);
                        _items[i] = item;
                        if (_spawnQueued.Add(i))
                            _spawnQueue.Add(i);
                    }
                }
                else if (!neverUnload && (thinFar || distSq > item.HideDist * item.HideDist))
                {
                    // 超 keep 卸，或远树隔株。
                    if (_despawnQueued.Add(i))
                        _despawnQueue.Add(i);
                }
                else if (TryQueueTreeTierSwap(ref item, distSq, i))
                {
                    _items[i] = item;
                }
                else if (item.ShadowCasters != null && UpdateShadowRange(ref item, distSq))
                {
                    _items[i] = item;
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

            // B7: insertion sort, no Sort(Comparison) closure allocation each frame.
            for (int i = 1; i < count; i++)
            {
                int key = _spawnQueue[i];
                float keyDist = DistSqXZ(_items[key].Pos, p);
                int j = i - 1;
                while (j >= 0)
                {
                    float d = DistSqXZ(_items[_spawnQueue[j]].Pos, p);
                    if (d <= keyDist)
                        break;
                    _spawnQueue[j + 1] = _spawnQueue[j];
                    j--;
                }

                _spawnQueue[j + 1] = key;
            }
        }

        static float DistSqXZ(Vector3 a, Vector3 p)
        {
            float dx = a.x - p.x;
            float dz = a.z - p.z;
            return dx * dx + dz * dz;
        }

        IEnumerator SpawnItem(int index)
        {
            if (index < 0 || index >= _items.Count)
                yield break;
            Item item = _items[index];
            if (item.State == StateVisible && item.Go != null)
                yield break;

            GameObject prefab = null;
            yield return LoadPrefab(item.Location, item.EditorPath, go => prefab = go, "stream");
            if (prefab == null)
                yield break;

            RestartSliceIfNewFrame();
            byte tier = item.PendingTier;
            if (tier != TierFull && tier != TierBillboard)
                tier = ResolveSpawnTier(item, DistSqToFocus(item.Pos));

            var spawned = CreateInstanceForTier(item, prefab, tier);
            if (spawned.Go == null)
                yield break;

            FinalizeSpawned(index, spawned.Go, prefab, spawned.Tier);
        }

        static byte ResolveSpawnTier(Item item, float distSq)
        {
            if (EnvStreamingConfig.ShouldSpawnAsTreeBillboard(item.Category, distSq))
                return TierBillboard;
            return TierFull;
        }

        /// <summary>
        /// Full↔Billboard swap: despawn now; spawn is re-queued next eval once Hidden
        /// (avoids same-frame Visible spawn skip). PendingTier selects the next form.
        /// </summary>
        bool TryQueueTreeTierSwap(ref Item item, float distSq, int index)
        {
            if (!EnvStreamingConfig.IsBigTreeCategory(item.Category) || item.Go == null)
                return false;

            if (item.LodTier == TierFull
                && EnvStreamingConfig.ShouldDowngradeTreeToBillboard(item.Category, distSq))
            {
                item.PendingTier = TierBillboard;
                if (_despawnQueued.Add(index))
                    _despawnQueue.Add(index);
                return true;
            }

            if (item.LodTier == TierBillboard
                && EnvStreamingConfig.ShouldUpgradeTreeToFull(item.Category, distSq))
            {
                item.PendingTier = TierFull;
                if (_despawnQueued.Add(index))
                    _despawnQueue.Add(index);
                return true;
            }

            return false;
        }

        struct SpawnedInstance
        {
            public GameObject Go;
            public byte Tier;
        }

        /// <summary>
        /// Builds Full Prefab or Billboard proxy. Tier may fall back to Full when
        /// the prefab has no extractable LOD_Billboard. Returns a struct so
        /// iterator callers avoid ref/out locals.
        /// </summary>
        SpawnedInstance CreateInstanceForTier(Item item, GameObject prefab, byte requestedTier)
        {
            byte actualTier = requestedTier;
            if (requestedTier == TierBillboard
                && EnvStreamingConfig.IsBigTreeCategory(item.Category))
            {
                Vector3 pos = item.Pos;
                EnvGroundFit.TrySnapMapItemY(ref pos, item.Category, item.PrefabName);
                LogStepBegin("billboard-proxy", item.PrefabName);
                GameObject proxy = EnvTreeBillboardUtil.BuildBillboardProxy(
                    prefab,
                    item.Parent,
                    (prefab != null ? prefab.name : item.PrefabName) + "#bb",
                    pos,
                    item.Rotation,
                    item.Scale);
                if (proxy != null)
                    return new SpawnedInstance { Go = proxy, Tier = TierBillboard };
                // No LOD_Billboard on prefab — fall back to full mesh.
                actualTier = TierFull;
            }

            LogStepBegin("instantiate", item.PrefabName);
            GameObject go = Object.Instantiate(prefab, item.Parent, false);
            LogStepEnd("instantiate", item.PrefabName);
            return new SpawnedInstance
            {
                Go = go,
                Tier = actualTier,
            };
        }

        void FinalizeSpawned(int index, GameObject instance, GameObject prefab, byte tier)
        {
            if (instance == null || index < 0 || index >= _items.Count)
                return;

            Item item = _items[index];
            if (item.State == StateVisible && item.Go != null)
            {
                Destroy(instance);
                return;
            }

            bool isBillboard = tier == TierBillboard
                && EnvStreamingConfig.IsBigTreeCategory(item.Category);

            instance.name = isBillboard
                ? index + " " + prefab.name + "#bb"
                : index + " " + prefab.name;
            instance.transform.SetParent(item.Parent, false);

            Vector3 pos = item.Pos;
            LogStepBegin("ground", item.PrefabName);
            if (EnvGroundFit.TrySnapMapItemY(ref pos, item.Category, item.PrefabName))
                item.Pos = pos;
            LogStepEnd("ground", item.PrefabName);

            if (EnvGroundFit.IsFlatGroundCard(item.PrefabName) || item.FarField)
                EnvGroundFit.DisableCastShadows(instance);

            instance.transform.position = pos;
            instance.transform.localEulerAngles = item.Rotation;
            instance.transform.localScale = item.Scale;

            // 阴影 atlas 留给大树：灌木/草/杂物只显示不投射。
            if (EnvStreamingConfig.IsSmallTreeCategory(item.Category)
                || EnvStreamingConfig.IsSmallVegCategory(item.Category)
                || EnvStreamingConfig.IsClutterCategory(item.Category)
                || isBillboard)
                EnvGroundFit.DisableCastShadows(instance);

            LogStepBegin("set-active", item.PrefabName);
            instance.SetActive(item.IsActive);
            LogStepEnd("set-active", item.PrefabName);

            if (!isBillboard)
            {
                if (EnvStreamingConfig.IsRockCategory(item.Category))
                {
                    LogStepBegin("rock-collider", item.PrefabName);
                    EnvRockColliders.Ensure(instance, item.PrefabName);
                    LogStepEnd("rock-collider", item.PrefabName);
                }
                if (EnvStreamingConfig.IsBridgeCategory(item.Category))
                {
                    LogStepBegin("bridge", item.PrefabName);
                    EnvBridgePillarFit.Ensure(instance);
                    LogStepEnd("bridge", item.PrefabName);
                }
                if (EnvStreamingConfig.IsRockCategory(item.Category) &&
                    EnvStreamingConfig.IsSmallRockForFarCull(item.PrefabName))
                {
                    EnvStreamerReflectionSkip.Apply(instance);
                }
                // Near full trees: LOD_Billboard must also Y-face when LOD switches.
                if (EnvStreamingConfig.IsBigTreeCategory(item.Category))
                {
                    LogStepBegin("y-billboard", item.PrefabName);
                    EnvTreeBillboardUtil.ApplyYBillboardToLodBillboardChildren(instance);
                    LogStepEnd("y-billboard", item.PrefabName);
                }
            }

            int proto = -1;
            bool inNative = false;
#if UNITY_ANDROID && !UNITY_EDITOR
            if (_native != null)
            {
                if (isBillboard)
                {
                    bool mainCameraOnly = EnvStreamingConfig.ShouldBeMainCameraOnly(
                        item.Category, item.PrefabName);
                    LogStepBegin("native-bb", item.PrefabName);
                    proto = _native.AddBillboardPrototype(prefab, mainCameraOnly);
                    LogStepEnd("native-bb", item.PrefabName);
                    if (proto >= 0 && _native.TryAddInstance(instance, proto))
                        inNative = true;
                }
                else if (!item.SkipNative)
                {
                    bool mainCameraOnly = EnvStreamingConfig.ShouldBeMainCameraOnly(
                        item.Category, item.PrefabName);
                    bool canCastShadows = EnvStreamingConfig.CanItemCastShadows(
                        item.Category, item.PrefabName);
                    LogStepBegin("native-proto", item.PrefabName);
                    proto = _native.AddPrototype(prefab, mainCameraOnly, canCastShadows);
                    LogStepEnd("native-proto", item.PrefabName);
                    if (proto >= 0 && _native.TryAddInstance(instance, proto))
                        inNative = true;
                }
            }
#endif

            item.Go = instance;
            item.State = StateVisible;
            item.LodTier = isBillboard ? TierBillboard : TierFull;
            item.PendingTier = item.LodTier;
            item.ProtoIndex = proto;
            item.InNative = inNative;
            item.ShadowCasters = isBillboard ? null : CaptureShadowCasters(instance, item);
            if (item.ShadowCasters != null)
            {
                bool cast = DistSqToFocus(item.Pos) <=
                    EnvStreamingConfig.ShadowCastMaxDist * EnvStreamingConfig.ShadowCastMaxDist;
                SetShadowCasting(item.ShadowCasters, cast);
                item.CastingShadows = cast;
            }
            else
            {
                item.CastingShadows = false;
            }

            _items[index] = item;
            _spawnedCount++;
            AddLocationUser(item.Location);
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
            DestroyYBillboardMaterials(item.Go);
            RemoveLocationUser(item.Location);
            Destroy(item.Go);
            item.Go = null;
            item.State = StateHidden;
            item.InNative = false;
            item.ProtoIndex = -1;
            item.ShadowCasters = null;
            item.CastingShadows = false;
            // Keep PendingTier so a tier-swap re-spawn uses the desired tier.
            _items[index] = item;
            _spawnedCount = Mathf.Max(0, _spawnedCount - 1);
        }

        static bool NeverCastsShadows(Item item)
        {
            return EnvGroundFit.IsFlatGroundCard(item.PrefabName)
                || item.FarField
                || EnvStreamingConfig.IsSmallTreeCategory(item.Category)
                || EnvStreamingConfig.IsSmallVegCategory(item.Category)
                || EnvStreamingConfig.IsClutterCategory(item.Category);
        }

        static Renderer[] CaptureShadowCasters(GameObject instance, Item item)
        {
            if (instance == null || NeverCastsShadows(item))
                return null;

            Renderer[] all = instance.GetComponentsInChildren<Renderer>(true);
            int count = 0;
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] != null && all[i].shadowCastingMode != ShadowCastingMode.Off)
                    count++;
            }

            if (count == 0)
                return null;

            var casters = new Renderer[count];
            int write = 0;
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] != null && all[i].shadowCastingMode != ShadowCastingMode.Off)
                    casters[write++] = all[i];
            }

            return casters;
        }

        float DistSqToFocus(Vector3 pos)
        {
            Vector3 focus = _focus != null ? _focus.position : pos;
            float dx = pos.x - focus.x;
            float dz = pos.z - focus.z;
            return dx * dx + dz * dz;
        }

        static void SetShadowCasting(Renderer[] casters, bool on)
        {
            ShadowCastingMode mode = on ? ShadowCastingMode.On : ShadowCastingMode.Off;
            for (int i = 0; i < casters.Length; i++)
            {
                Renderer renderer = casters[i];
                if (renderer != null && renderer.shadowCastingMode != mode)
                    renderer.shadowCastingMode = mode;
            }
        }

        /// <summary>大于 26 米关，小于 22 米开；中间保持当前状态。</summary>
        static bool UpdateShadowRange(ref Item item, float distSq)
        {
            float max = EnvStreamingConfig.ShadowCastMaxDist;
            float restore = EnvStreamingConfig.ShadowCastRestoreDist;
            if (item.CastingShadows && distSq > max * max)
            {
                SetShadowCasting(item.ShadowCasters, false);
                item.CastingShadows = false;
                return true;
            }

            if (!item.CastingShadows && distSq < restore * restore)
            {
                SetShadowCasting(item.ShadowCasters, true);
                item.CastingShadows = true;
                return true;
            }

            return false;
        }

        static void LogStepBegin(string step, string name)
        {
            _ = step;
            _ = name;
        }

        static void LogStepEnd(string step, string name)
        {
            _ = step;
            _ = name;
        }

        GameObject LoadPrefabNow(string location, string editorPath, string phase)
        {
            if (!string.IsNullOrEmpty(location)
                && _prefabCache.TryGetValue(location, out GameObject cached)
                && cached != null)
                return cached;

            GameObject prefab = null;
            bool trackLoad = !string.IsNullOrEmpty(location);
            if (trackLoad)
                _loadingLocations.Add(location);
            var loadSw = System.Diagnostics.Stopwatch.StartNew();
            Log.Info($"[EnvItemStreamerLite] load sync begin phase={phase} loc={location}");
            try
            {
                bool locationValid = YooResourceModule.IsInitialized
                    && YooResourceModule.DefaultPackage != null
                    && !string.IsNullOrEmpty(location)
                    && YooResourceModule.DefaultPackage.CheckLocationValid(location);
                if (locationValid)
                {
                    AssetHandle handle = YooResourceModule.DefaultPackage.LoadAssetSync<GameObject>(location);
                    if (handle != null && handle.Status == EOperationStatus.Succeed)
                    {
                        prefab = handle.AssetObject as GameObject;
                        _prefabHandles[location] = handle;
                    }
                    else
                    {
                        Log.Warning(
                            "[EnvItemStreamerLite] load sync fail loc=" + location +
                            " status=" + (handle != null ? handle.Status.ToString() : "null") +
                            " err=" + handle?.LastError);
                        handle?.Release();
                    }
                }
            }
            finally
            {
                if (trackLoad)
                    _loadingLocations.Remove(location);
            }

            Log.Info(
                $"[EnvItemStreamerLite] load sync end phase={phase} loc={location} " +
                $"ms={loadSw.ElapsedMilliseconds} ok={prefab != null}");

#if UNITY_EDITOR
            if (prefab == null && !string.IsNullOrEmpty(editorPath))
                prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(editorPath);
#endif
            if (prefab != null && !string.IsNullOrEmpty(location))
                _prefabCache[location] = prefab;
            return prefab;
        }

        IEnumerator LoadPrefab(string location, string editorPath, System.Action<GameObject> onDone, string phase)
        {
            if (!string.IsNullOrEmpty(location)
                && _prefabCache.TryGetValue(location, out GameObject cached)
                && cached != null)
            {
                onDone?.Invoke(cached);
                yield break;
            }

            GameObject prefab = null;
            bool trackLoad = !string.IsNullOrEmpty(location);
            if (trackLoad)
                _loadingLocations.Add(location);
            var loadSw = System.Diagnostics.Stopwatch.StartNew();
            Log.Info($"[EnvItemStreamerLite] load begin phase={phase} loc={location}");
            bool locationValid = YooResourceModule.IsInitialized && YooResourceModule.DefaultPackage != null
                && !string.IsNullOrEmpty(location)
                && YooResourceModule.DefaultPackage.CheckLocationValid(location);
            Log.Info($"[EnvItemStreamerLite] load valid phase={phase} loc={location} valid={locationValid}");
            try
            {
                if (locationValid)
                {
                    yield return YooResourceModule.LoadAssetCoroutine<GameObject>(location, (asset, handle) =>
                    {
                        prefab = asset;
                        if (handle != null && !string.IsNullOrEmpty(location))
                            _prefabHandles[location] = handle;
                    });
                }
            }
            finally
            {
                if (trackLoad)
                    _loadingLocations.Remove(location);
            }

            Log.Info(
                $"[EnvItemStreamerLite] load end phase={phase} loc={location} " +
                $"ms={loadSw.ElapsedMilliseconds} ok={prefab != null}");

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
            _prefillActive = false;
            PrefillSuppressRender = false;
            PrefillFrameGate.End();
            _running = false;
            StopAllCoroutines();
            for (int i = 0; i < _items.Count; i++)
            {
                Item item = _items[i];
#if UNITY_ANDROID && !UNITY_EDITOR
                if (item.InNative && _native != null && item.Go != null)
                    _native.RemoveInstance(item.Go, item.ProtoIndex);
#endif
                if (item.Go != null)
                {
                    DestroyYBillboardMaterials(item.Go);
                    Destroy(item.Go);
                }
            }

#if UNITY_ANDROID && !UNITY_EDITOR
            // 句柄稍后才 Release；先丢掉合批对 Mesh/Material 的握持，UnloadUnused 才能回收。
            _native?.Clear();
#endif
            _items.Clear();
            _spawnedCount = 0;
            _spawnBacklog = false;
            _locationUsers.Clear();
            _loadingLocations.Clear();
            if (Current == this)
                Current = null;

            foreach (var kv in _prefabHandles)
                kv.Value?.Release();
            _prefabHandles.Clear();
            _prefabCache.Clear();
        }

        /// <summary>
        /// 释放一个已经没有可见实例、也没在加载的预制体句柄。没有可释放的返回 false。
        /// </summary>
        public bool ReleaseOneUnusedPrefab()
        {
            if (_released)
                return false;

            string location = null;
            foreach (var kv in _prefabHandles)
            {
                if (string.IsNullOrEmpty(kv.Key))
                    continue;
                if (_loadingLocations.Contains(kv.Key))
                    continue;
                if (_locationUsers.TryGetValue(kv.Key, out int users) && users > 0)
                    continue;
                location = kv.Key;
                break;
            }

            if (location == null)
                return false;

            AssetHandle handle = _prefabHandles[location];
            _prefabHandles.Remove(location);
            _prefabCache.Remove(location);
            handle?.Release();
            return true;
        }

        void AddLocationUser(string location)
        {
            if (string.IsNullOrEmpty(location))
                return;
            _locationUsers.TryGetValue(location, out int count);
            _locationUsers[location] = count + 1;
        }

        void RemoveLocationUser(string location)
        {
            if (string.IsNullOrEmpty(location))
                return;
            if (!_locationUsers.TryGetValue(location, out int count))
                return;
            count--;
            if (count <= 0)
                _locationUsers.Remove(location);
            else
                _locationUsers[location] = count;
        }

        void OnDestroy()
        {
            Release();
        }

        /// <summary>
        /// (YBillboard) 是运行时克隆，Destroy(GO) 不会带走它，贴图引用会挡住卸包。
        /// </summary>
        static void DestroyYBillboardMaterials(GameObject go)
        {
            Renderer[] renderers = go.GetComponentsInChildren<Renderer>(true);
            for (int r = 0; r < renderers.Length; r++)
            {
                Renderer renderer = renderers[r];
                if (renderer == null)
                    continue;
                Material[] mats = renderer.sharedMaterials;
                if (mats == null)
                    continue;
                for (int m = 0; m < mats.Length; m++)
                {
                    Material mat = mats[m];
                    if (mat != null && mat.name.EndsWith("(YBillboard)"))
                        Destroy(mat);
                }
            }
        }
    }
}
