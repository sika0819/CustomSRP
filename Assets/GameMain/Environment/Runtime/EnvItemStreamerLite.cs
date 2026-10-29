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
    /// 地图物件流送，挂在 Island 场景的 MapItem 上，由 <see cref="EnvMapItemSpawner"/> 启动。
    /// 进岛先 <see cref="Initialize"/> 登记全部点，<see cref="PrefillAroundFocus"/> 铺焦点附近，再按距离生成和回收。
    /// 场景卸载走 <see cref="OnDestroy"/>：停协程、摘合批，句柄留到卸场景成功再 Release。不立刻销毁实例。
    /// 单面公告板网格由 <see cref="EnvTreeBillboardService"/> 跟着场景销毁。
    /// 进程拆世界走 <see cref="Release()"/>，才把实例从父节点摘掉并立刻销毁。
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
        readonly List<AssetHandle> _inflightHandles = new List<AssetHandle>(4);
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
        bool _prefillActive;
        readonly PrefillFrameGate _prefillGate = new PrefillFrameGate();
        bool _released;
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
#if (UNITY_ANDROID || UNITY_IOS) && !UNITY_EDITOR
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
                // 区域门由 SceneBankGateSpawner 生成，不加载缺失的 BanksChecker 预制体。
                if (category == SceneBankGateCatalog.Category)
                    continue;
                string prefabName = d.PrefabName;
                float hide = EnvStreamingConfig.GetHideDistance(category, prefabName, out float show);
                Transform parent = mapItemRoot;
                if (category != "null" && categoryParents != null
                    && categoryParents.TryGetValue(category, out Transform cat))
                    parent = cat;

                Vector3 pos = d.Pos;
                EnvGroundFit ground = GameEntry.GroundFit;
                if (ground != null)
                    ground.TrySnapMapItemY(ref pos, category, prefabName);

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
            // StartCoroutine 会立刻跑到第一次 yield。先闩住，避免这一拍发出 LoadAssetAsync，
            // 再和紧接着的铺装 LoadAssetSync 叠在同一个包上。离岛后再进时，下一帧读
            // Handle.IsDone 不再返回，宿主就等不到 Notify(2)。PrefillAroundFocus 结束时松闩。
            _prefillActive = true;
            StartCoroutine(StreamLoop());
            Log.Info(
                $"[EnvItemStreamerLite] init items={_items.Count} streaming=on " +
                $"native={(_native != null)} leakProbe={IslandLeakProbe.Enabled}");
        }

        /// <summary>铺装时跳过世界几何。摄像机仍画加载界面，避免 Game 视图报没有摄像机。</summary>
        public static bool PrefillSuppressRender { get; private set; }

        /// <summary>退登、场景还在时立刻拆掉 (YBillboard)，贴图引用才能在卸包前归零。</summary>
        public static void ReleaseBillboardsImmediate()
        {
            if (Current == null || Current._released)
                return;
            Current.DestroyBillboardsNow();
        }

        public static void ReleasePrefillHold()
        {
            PrefillSuppressRender = false;
            if (Current != null)
            {
                Current._prefillActive = false;
                Current._prefillGate.End();
            }
        }

        public IEnumerator PrefillAroundFocus(float progressMin, float progressMax, System.Action<float, string> report)
        {
            if (_focus == null)
            {
                _prefillActive = false;
                yield break;
            }

            // 铺装期间关掉流送。Initialize 已经闩上；这里再写一次，避免中途被松开。
            // 两条协程同时 LoadAsset，离岛后再进会叠在未完成的 LoadFromFile 上，
            // 主线程卡死，加载罩不撤。
            _prefillActive = true;
            // 摄像机保持开启：关掉它时 Game 视图会写 “Display 1 No cameras rendering”，
            // 加载条也画不出来。世界几何由 CameraRenderer 在此标志下跳过。
            PrefillSuppressRender = true;
            _prefillGate.Begin();
            try
            {
                PrefillBody(progressMin, progressMax, report);
            }
            finally
            {
                _prefillActive = false;
                PrefillSuppressRender = false;
                _prefillGate.End();
            }

            Log.Info("[EnvItemStreamerLite] prefill tail end");
            yield break;
        }

        void PrefillBody(float progressMin, float progressMax, System.Action<float, string> report)
        {
            Vector3 p = _focus.position;
            var near = new List<int>(512);
            for (int i = 0; i < _items.Count; i++)
            {
                Item item = _items[i];
                if (!IslandLeakProbe.AllowCategory(item.Category))
                    continue;
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

                _prefillGate.Mark(2, done);
                GameObject prefab = LoadPrefabNow(item.Location, item.EditorPath, "prefill");
                _prefillGate.Mark(3, done);
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
                _prefillGate.Mark(3, done);
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
            // 二次 Instantiate 会把新节点插进一条还指着旧兄弟的链表。
            // 本帧一旦 yield，下一帧 UnitySendMessage 的 FindActiveTransformWithPath 顺着环走不出去，
            // 主线程不再回来，宿主就等不到 IslandReady。先摘再挂，必须在 yield 之前做完。
            Log.Info("[EnvItemStreamerLite] prefill tail begin");
            _prefillGate.Mark(4, done);
            RelinkSpawned();
            SealSpawnedNames();
        }

        /// <summary>
        /// 本实例拆世界时用：先摘掉父节点再立刻销毁。场景卸载走 OnDestroy，不进这里。
        /// </summary>
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

        /// <summary>
        /// 把已生成实例的名字再写一遍。托管字符串会拷成以 0 结尾的原生缓冲，
        /// 避免下一帧 UnitySendMessage 的 FindActiveTransformWithPath 在未终止名字上 strlen 不返回。
        /// </summary>
        void SealSpawnedNames()
        {
            int sealedCount = 0;
            for (int i = 0; i < _items.Count; i++)
            {
                GameObject go = _items[i].Go;
                if (go == null)
                    continue;
                // 不读旧名字。旧缓冲若没结尾，getter 里的 strlen 不会返回。
                go.name = "env" + i;
                sealedCount++;
            }

            Log.Info($"[EnvItemStreamerLite] prefill seal names={sealedCount}");
        }

        void RelinkSpawned()
        {
            Log.Info("[EnvItemStreamerLite] prefill relink begin n=" + _items.Count);
            int relinked = 0;
            for (int i = 0; i < _items.Count; i++)
            {
                GameObject go = _items[i].Go;
                if (go == null)
                    continue;
                Transform t = go.transform;
                Transform parent = _items[i].Parent;
                t.SetParent(null, false);
                if (parent != null)
                    t.SetParent(parent, false);
                relinked++;
            }

            Log.Info($"[EnvItemStreamerLite] prefill relink={relinked}");
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
            if (!IslandLeakProbe.AllowCategory(item.Category))
                return false;
            if (item.State == StateVisible && item.Go != null)
            {
                // Full↔Billboard seamless swap: allow spawn while the old GO is still up.
                if (item.PendingTier == item.LodTier
                    || (item.PendingTier != TierFull && item.PendingTier != TierBillboard))
                    return false;
            }

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
            if (!IslandLeakProbe.AllowCategory(item.Category))
                return item.Go != null || item.State == StateVisible;
            if (item.Go == null)
                return item.State == StateVisible;

            float distSq = DistSqToFocus(item.Pos);
            bool neverUnload = item.HideDist >= 1e20f;
            bool thinFar = EnvStreamingConfig.ShouldThinFarTree(item.Category, distSq, index);
            if (!neverUnload && (thinFar || distSq > item.HideDist * item.HideDist))
                return true;

            // Tier swap is spawn-before-despawn (see TryQueueTreeTierSwap) — never
            // treat a pending Full↔Billboard handoff as a plain unload.
            if (item.PendingTier != item.LodTier
                && (item.PendingTier == TierFull || item.PendingTier == TierBillboard))
                return false;

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
                if (!IslandLeakProbe.AllowCategory(item.Category))
                {
                    if (item.State != StateHidden && _despawnQueued.Add(i))
                        _despawnQueue.Add(i);
                    continue;
                }

                float dx = item.Pos.x - p.x;
                float dz = item.Pos.z - p.z;
                float distSq = dx * dx + dz * dz;

                bool neverUnload = item.HideDist >= 1e20f;
                bool thinFar = EnvStreamingConfig.ShouldThinFarTree(item.Category, distSq, i);
                if (item.State == StateHidden)
                {
                    if (!thinFar && (neverUnload || distSq <= item.ShowDist * item.ShowDist))
                    {
                        // Keep a pending swap target if set; otherwise resolve from distance.
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
            bool tierSwap = item.State == StateVisible
                && item.Go != null
                && item.PendingTier != item.LodTier
                && (item.PendingTier == TierFull || item.PendingTier == TierBillboard);
            if (item.State == StateVisible && item.Go != null && !tierSwap)
                yield break;

            GameObject prefab = null;
            yield return LoadPrefab(item.Location, item.EditorPath, go => prefab = go, "stream");
            if (prefab == null)
                yield break;

            // Focus / thin may have changed while the prefab loaded — drop a stale swap.
            item = _items[index];
            if (tierSwap)
            {
                if (item.Go == null || item.State != StateVisible
                    || item.PendingTier == item.LodTier
                    || (item.PendingTier != TierFull && item.PendingTier != TierBillboard))
                    yield break;
                float distSqNow = DistSqToFocus(item.Pos);
                if (EnvStreamingConfig.ShouldThinFarTree(item.Category, distSqNow, index)
                    || (item.HideDist < 1e20f && distSqNow > item.HideDist * item.HideDist))
                {
                    item.PendingTier = item.LodTier;
                    _items[index] = item;
                    yield break;
                }
            }

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
        /// Full↔Billboard swap: queue spawn of the next tier while the current GO stays
        /// visible. SpawnItem destroys the old GO only after the new one is ready, so
        /// the handoff has no blank frame (old path: despawn → Hidden → later spawn).
        /// </summary>
        bool TryQueueTreeTierSwap(ref Item item, float distSq, int index)
        {
            if (!EnvStreamingConfig.IsBigTreeCategory(item.Category) || item.Go == null)
                return false;

            if (item.LodTier == TierFull
                && EnvStreamingConfig.ShouldDowngradeTreeToBillboard(item.Category, distSq))
            {
                item.PendingTier = TierBillboard;
                if (_spawnQueued.Add(index))
                    _spawnQueue.Add(index);
                return true;
            }

            if (item.LodTier == TierBillboard
                && EnvStreamingConfig.ShouldUpgradeTreeToFull(item.Category, distSq))
            {
                item.PendingTier = TierFull;
                if (_spawnQueued.Add(index))
                    _spawnQueue.Add(index);
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
                EnvGroundFit ground = GameEntry.GroundFit;
                if (ground != null)
                    ground.TrySnapMapItemY(ref pos, item.Category, item.PrefabName);
                LogStepBegin("billboard-proxy", item.PrefabName);
                GameObject proxy = GameEntry.Billboards?.BuildBillboardProxy(
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
            bool tierSwap = item.State == StateVisible
                && item.Go != null
                && item.PendingTier != item.LodTier
                && (item.PendingTier == TierFull || item.PendingTier == TierBillboard)
                && (tier == item.PendingTier);
            if (item.State == StateVisible && item.Go != null && !tierSwap)
            {
                Destroy(instance);
                return;
            }

            // New GO is ready — tear down the previous tier in the same frame so
            // Full↔Billboard never shows a blank hole.
            if (tierSwap)
                DestroyCurrentGo(ref item, destroyImmediate: false);

            bool isBillboard = tier == TierBillboard
                && EnvStreamingConfig.IsBigTreeCategory(item.Category);

            instance.name = isBillboard
                ? index + " " + prefab.name + "#bb"
                : index + " " + prefab.name;
            instance.transform.SetParent(item.Parent, false);

            Vector3 pos = item.Pos;
            LogStepBegin("ground", item.PrefabName);
            EnvGroundFit ground = GameEntry.GroundFit;
            if (ground != null && ground.TrySnapMapItemY(ref pos, item.Category, item.PrefabName))
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
            }

            int proto = -1;
            bool inNative = false;
#if (UNITY_ANDROID || UNITY_IOS) && !UNITY_EDITOR
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

        /// <summary>
        /// Drop the live GO (native batch + runtime mats) without touching PendingTier.
        /// Seamless tier swap calls this once the next GO exists; unload then marks Hidden.
        /// </summary>
        void DestroyCurrentGo(ref Item item, bool destroyImmediate)
        {
            if (item.Go == null)
                return;

#if (UNITY_ANDROID || UNITY_IOS) && !UNITY_EDITOR
            if (item.InNative && _native != null)
                _native.RemoveInstance(item.Go, item.ProtoIndex);
#endif
            DestroyYBillboardMaterials(item.Go, destroyImmediate);
            RemoveLocationUser(item.Location);
            if (destroyImmediate)
                DestroyImmediate(item.Go);
            else
                Destroy(item.Go);
            item.Go = null;
            item.InNative = false;
            item.ProtoIndex = -1;
            item.ShadowCasters = null;
            item.CastingShadows = false;
            _spawnedCount = Mathf.Max(0, _spawnedCount - 1);
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

            DestroyCurrentGo(ref item, destroyImmediate: false);
            item.State = StateHidden;
            // Keep PendingTier so a rare Hidden→spawn path can still honor a swap target.
            _items[index] = item;
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
                    if (handle != null)
                        GameEntry.TrackLeaveHandle(handle);
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
                        GameEntry.ReleaseLeaveHandle(handle);
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

            // 句柄立刻登记。退登 StopCoroutine 后字段会清掉，卸场景成功再 Release。
            AssetHandle handle = null;
            if (locationValid)
            {
                handle = YooResourceModule.DefaultPackage.LoadAssetAsync<GameObject>(location);
                if (handle != null)
                {
                    GameEntry.TrackLeaveHandle(handle);
                    _inflightHandles.Add(handle);
                }
                yield return YooResourceModule.WaitUntilDone(
                    () => _released || handle == null || handle.IsDone,
                    "EnvItemStreamerLite " + location);
            }

            if (trackLoad)
                _loadingLocations.Remove(location);

            if (_released)
                yield break;

            if (handle != null)
            {
                _inflightHandles.Remove(handle);
                if (handle.Status == EOperationStatus.Succeed)
                {
                    prefab = handle.AssetObject as GameObject;
                    if (!string.IsNullOrEmpty(location))
                        _prefabHandles[location] = handle;
                    else
                        GameEntry.ReleaseLeaveHandle(handle);
                }
                else
                {
                    Log.Warning(
                        "[EnvItemStreamerLite] load fail loc=" + location +
                        " status=" + handle.Status +
                        " err=" + handle.LastError);
                    GameEntry.ReleaseLeaveHandle(handle);
                }
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

        public void Release() => Release(destroyObjects: true);

        void Release(bool destroyObjects)
        {
            if (_released)
                return;
            _released = true;
            _prefillActive = false;
            PrefillSuppressRender = false;
            _prefillGate.End();
            _running = false;
            StopAllCoroutines();
            int destroyed = 0;
            for (int i = 0; i < _items.Count; i++)
            {
                Item item = _items[i];
#if (UNITY_ANDROID || UNITY_IOS) && !UNITY_EDITOR
                if (item.InNative && _native != null && item.Go != null)
                    _native.RemoveInstance(item.Go, item.ProtoIndex);
#endif
                if (item.Go != null)
                {
                    DestroyYBillboardMaterials(item.Go, destroyObjects);
                    if (destroyObjects)
                    {
                        DestroyUnlinked(item.Go);
                        destroyed++;
                    }
                }
            }

#if (UNITY_ANDROID || UNITY_IOS) && !UNITY_EDITOR
            // 句柄稍后才 Release；先丢掉合批对 Mesh/Material 的握持，UnloadUnused 才能回收。
            // 场景卸载时物体跟着场景走，不再 DestroyImmediate 合批宿主。
            if (destroyObjects)
                EnvNativeInstancing.DestroySingleton();
            _native = null;
#endif
            int inflight = _inflightHandles.Count;
            int handles = _prefabHandles.Count;
            Log.Info(
                $"[EnvItemStreamerLite] release gos={destroyed} handles={handles} inflight={inflight} " +
                "releaseNow=" + destroyObjects);
            _items.Clear();
            _spawnedCount = 0;
            _spawnBacklog = false;
            _locationUsers.Clear();
            _loadingLocations.Clear();
            if (Current == this)
                Current = null;

            // 场景卸载不 Release。句柄已在加载时登记，等卸场景成功再放。
            if (destroyObjects)
            {
                for (int i = 0; i < _inflightHandles.Count; i++)
                    GameEntry.ReleaseLeaveHandle(_inflightHandles[i]);
                foreach (var kv in _prefabHandles)
                    GameEntry.ReleaseLeaveHandle(kv.Value);
            }

            _inflightHandles.Clear();
            _prefabHandles.Clear();
            _prefabCache.Clear();
        }

        void OnDestroy()
        {
            Release(destroyObjects: false);
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
            GameEntry.ReleaseLeaveHandle(handle);
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

        void DestroyBillboardsNow()
        {
            for (int i = 0; i < _items.Count; i++)
            {
                GameObject go = _items[i].Go;
                if (go != null)
                    DestroyYBillboardMaterials(go, true);
            }
        }

        /// <summary>
        /// (YBillboard) 是运行时克隆，Destroy(GO) 不会带走它，贴图引用会挡住卸包。
        /// </summary>
        void DestroyYBillboardMaterials(GameObject go, bool immediate)
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
                    if (mat == null || !mat.name.EndsWith("(YBillboard)"))
                        continue;
                    if (immediate)
                        DestroyImmediate(mat);
                    else
                        Destroy(mat);
                }
            }
        }
    }
}
