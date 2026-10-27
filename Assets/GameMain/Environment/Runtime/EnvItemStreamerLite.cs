using System.Collections;
using System.Collections.Generic;
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
        readonly List<int> _spawnQueue = new List<int>(256);
        readonly List<int> _despawnQueue = new List<int>(256);
        readonly HashSet<int> _spawnQueued = new HashSet<int>();
        readonly HashSet<int> _despawnQueued = new HashSet<int>();

        Transform _focus;
        EnvMeshColliderLod _colliderLod;
        EnvNativeInstancing _native;
        bool _running;
        /// <summary>True while <see cref="CatchUpAroundFocus"/> runs — StreamLoop skips spawn/despawn to avoid races.</summary>
        bool _catchUpBusy;
        int _evalCursor;
        int _spawnedCount;

        public int ItemCount => _items.Count;
        public int SpawnedCount => _spawnedCount;
        public int PendingSpawnCount => _spawnQueue.Count;
        public bool IsCatchUpBusy => _catchUpBusy;

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
                _native.SetShadowCastMaxDist(EnvStreamingConfig.ShadowCastMaxDist);
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
                    LodTier = TierFull,
                    PendingTier = TierFull,
                    Go = null,
                    ProtoIndex = -1,
                    InNative = false,
                });
            }

            _running = true;
            // 勿在此 Start StreamLoop：与 PrefillAroundFocus 并行会抢同一 _items /
            // LoadPrefab / Native 合批，真机二次进岛可卡死在「正在铺装环境」。
            // Prefill 结束后由 BeginStreamingAfterPrefill 再开环。
            Log.Info(
                $"[EnvItemStreamerLite] init items={_items.Count} streaming=on " +
                $"native={(_native != null)} (StreamLoop deferred)");
        }

        /// <summary>Prefill 完成后再开常驻流式环。</summary>
        public void BeginStreamingAfterPrefill()
        {
            if (_released || !_running)
                return;
            StopCoroutine(nameof(StreamLoop));
            StartCoroutine(StreamLoop());
        }

        static int _prefillWarnDepth;
        static StackTraceLogType _prefillWarnStack = StackTraceLogType.ScriptOnly;

        /// <summary>
        /// 缺脚本 / NavMeshLink 的 Instantiate 会给每条 Warning 打完整托管栈。
        /// 华为 logcat 上这比生成本身还贵，近场铺装会被拖到一分多钟。铺装期间只留一行 Warning。
        /// </summary>
        static void SuppressPrefillWarningStacks()
        {
            if (_prefillWarnDepth++ != 0)
                return;
            _prefillWarnStack = Application.GetStackTraceLogType(LogType.Warning);
            Application.SetStackTraceLogType(LogType.Warning, StackTraceLogType.None);
        }

        static void RestorePrefillWarningStacks()
        {
            if (_prefillWarnDepth <= 0)
                return;
            _prefillWarnDepth--;
            if (_prefillWarnDepth == 0)
                Application.SetStackTraceLogType(LogType.Warning, _prefillWarnStack);
        }

        /// <param name="onNearReady">
        /// 近场段结束后回调（世界首装揭罩门闩）；null 时不做 Quiet Hold（CatchUp）。
        /// </param>
        public IEnumerator PrefillAroundFocus(
            float progressMin,
            float progressMax,
            System.Action<float, string> report,
            System.Action onNearReady = null)
        {
            if (_focus == null)
            {
                onNearReady?.Invoke();
                yield break;
            }

            SuppressPrefillWarningStacks();
            yield return PrefillAroundFocusBody(progressMin, progressMax, report, onNearReady);
            RestorePrefillWarningStacks();
        }

        IEnumerator PrefillAroundFocusBody(
            float progressMin,
            float progressMax,
            System.Action<float, string> report,
            System.Action onNearReady)
        {

            Vector3 p = _focus.position;
            var candidates = new List<int>(512);
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
                candidates.Add(i);
            }

            // 按有效距离升序：先近后远；当前焦点朝向的前/右扇区略提前（半径不变）。
            // ProcedureIsland 已在 Prefill 前 InitAndArm，用 focus 真实 yaw；无有效朝向再回退默认出生 yaw。
            // _prefabCache 会让重复 LoadPrefab 退化为 Dictionary 查找，无需再按 Location 分组。
            Vector3 lookFwd = _focus.forward;
            lookFwd.y = 0f;
            if (lookFwd.sqrMagnitude < 1e-4f)
                lookFwd = Quaternion.Euler(0f, IslandSpawnPose.DefaultYawDegrees, 0f) * Vector3.forward;
            else
                lookFwd.Normalize();
            Vector3 lookRight = new Vector3(lookFwd.z, 0f, -lookFwd.x);
            candidates.Sort((a, b) =>
            {
                float sa = PrefillPriorityDistSq(_items[a].Pos, p, lookFwd, lookRight);
                float sb = PrefillPriorityDistSq(_items[b].Pos, p, lookFwd, lookRight);
                return sa.CompareTo(sb);
            });

            int total = candidates.Count;
            int done = 0;
            float progressMid = Mathf.Lerp(progressMin, progressMax, 0.55f);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var nearClock = System.Diagnostics.Stopwatch.StartNew();
            float uncoverBudgetMs = EnvStreamingConfig.PrefillUncoverBudgetMs;
            bool uncoverCapped = false;

            // --- Near：脚下揭罩半径（手机）或 Ring0/MeshCollider 环（闺蜜机）→ IsNearReady ---
            // 墙钟到点就揭罩，剩下的交给下面的 Quiet 远场，避免华为把「正在铺装环境」钉死。
            for (int k = 0; k < candidates.Count; k++)
            {
                int idx = candidates[k];
                Item item = _items[idx];
                float distSq = DistSqXZ(item.Pos, p);
                float nearShow = EnvStreamingConfig.GetPrefillNearShowDistance(item.Category, item.ShowDist);
                if (distSq > nearShow * nearShow)
                    continue;

                if (onNearReady != null
                    && uncoverBudgetMs > 1f
                    && done > 0
                    && nearClock.ElapsedMilliseconds >= uncoverBudgetMs)
                {
                    uncoverCapped = true;
                    break;
                }

                yield return PrefillSpawnOne(idx, p, sw);
                done++;
                float tNear = total > 0 ? (float)done / total : 1f;
                report?.Invoke(Mathf.Lerp(progressMin, progressMid, tNear), "正在铺装环境...");
            }

            long nearMs = nearClock.ElapsedMilliseconds;
            onNearReady?.Invoke();
            if (onNearReady != null)
                EnvStreamingConfig.BeginPostUncoverQuiet();
            // 进岛揭罩路径：近场后不再刷 Loading 进度（远场后台铺，避免罩回来盖住岛）。
            // CatchUp（onNearReady==null）仍走完整进度条。
            bool reportFar = onNearReady == null;
            if (reportFar)
                report?.Invoke(progressMid, "近场就绪，继续铺装...");
            Log.Info(
                "[EnvItemStreamerLite] prefill near-ready spawned=" + _spawnedCount + "/" + _items.Count
                + " candidates=" + total
                + " nearDone=" + done
                + " nearMs=" + nearMs
                + " capped=" + uncoverCapped
                + " uncoverR=" + EnvStreamingConfig.PrefillUncoverRadius.ToString("0"));

            // --- Far：其余 PrefillShow 半径，Quiet 预算 ---
            for (int k = 0; k < candidates.Count; k++)
            {
                int idx = candidates[k];
                Item item = _items[idx];
                if (item.State == StateVisible && item.Go != null)
                    continue;

                yield return PrefillSpawnOne(idx, p, sw);
                done++;
                if (reportFar)
                {
                    float tFar = total > 0 ? (float)done / total : 1f;
                    report?.Invoke(Mathf.Lerp(progressMid, progressMax, tFar), "正在铺装环境...");
                }
            }

            if (reportFar)
                report?.Invoke(progressMax, "正在铺装环境...");
            Log.Info($"[EnvItemStreamerLite] prefill done spawned={_spawnedCount}/{_items.Count} candidates={total}");
        }

        IEnumerator PrefillSpawnOne(int idx, Vector3 focusPos, System.Diagnostics.Stopwatch sw)
        {
            Item item = _items[idx];
            if (item.State == StateVisible && item.Go != null)
                yield break;

            GameObject prefab = null;
            yield return LoadPrefab(item.Location, item.EditorPath, go => prefab = go);
            if (prefab == null)
            {
                Log.Warning(
                    "[EnvItemStreamerLite] prefab missing loc=" + item.Location +
                    " cat=" + item.Category + " name=" + item.PrefabName);
                yield break;
            }

            // Item 是 struct：Create 前再读一次，避免 LoadPrefab yield 后陈旧副本。
            item = _items[idx];
            byte tier = ResolveSpawnTier(item, DistSqXZ(item.Pos, focusPos));
            var spawned = CreateInstanceForTier(item, prefab, tier);
            if (spawned.Go == null)
                yield break;

            FinalizeSpawned(idx, spawned.Go, prefab, spawned.Tier);

            // Quiet（远场 Prefill / 揭罩后）：缓存命中时 LoadPrefab 不 yield，
            // 必须每实例让帧，否则二次进岛会把主线程打到个位数 FPS。
            if (EnvStreamingConfig.IsPostUncoverQuiet
                || sw.ElapsedMilliseconds >= EnvStreamingConfig.EffectiveFinalizePerFrameBudgetMs)
            {
                yield return null;
                sw.Restart();
            }
        }

        /// <summary>
        /// 切预设 / 传送后：按当前焦点把附近未生成的 MapItem 立刻铺齐（复用 Prefill 路径），
        /// 期间暂停 StreamLoop，避免与分帧队列双开同一条目。
        /// </summary>
        public IEnumerator CatchUpAroundFocus(System.Action<float, string> report = null)
        {
            if (!_running || _focus == null)
                yield break;

            _catchUpBusy = true;
            // 清空未处理队列：CatchUp 会全量重扫，避免 StreamLoop 恢复后立刻再排一遍旧焦点。
            _spawnQueue.Clear();
            _spawnQueued.Clear();
            _despawnQueue.Clear();
            _despawnQueued.Clear();
            try
            {
                yield return PrefillAroundFocus(0f, 1f, report);
            }
            finally
            {
                _catchUpBusy = false;
            }
        }

        IEnumerator StreamLoop()
        {
            while (_running)
            {
                if (_catchUpBusy)
                {
                    yield return null;
                    continue;
                }

                if (_focus == null)
                {
                    Camera cam = Camera.main;
                    if (cam != null)
                        _focus = cam.transform;
                    yield return null;
                    continue;
                }

                EvaluateQueues();
                int spawnBudget = EnvStreamingConfig.EffectiveSpawnPerFrameBudget;
                int despawnBudget = EnvStreamingConfig.DespawnPerFrameBudget;

                for (int i = 0; i < _despawnQueue.Count && despawnBudget > 0; i++)
                {
                    DespawnItem(_despawnQueue[i]);
                    despawnBudget--;
                }

                _despawnQueue.Clear();
                _despawnQueued.Clear();

                var spawnSw = System.Diagnostics.Stopwatch.StartNew();
                for (int i = 0; i < _spawnQueue.Count && spawnBudget > 0; i++)
                {
                    // 上一件已经把本帧吃满就留到下一帧，把 Update 还给摇杆。
                    if (i > 0 && spawnSw.ElapsedMilliseconds >= 6)
                        break;
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
            yield return LoadPrefab(item.Location, item.EditorPath, go => prefab = go);
            if (prefab == null)
                yield break;

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

            return new SpawnedInstance
            {
                Go = Object.Instantiate(prefab, item.Parent, false),
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
            if (EnvGroundFit.TrySnapMapItemY(ref pos, item.Category, item.PrefabName))
                item.Pos = pos;

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

            instance.SetActive(item.IsActive);

            if (isBillboard)
            {
                // Far impostors blur into the water and their contour verts
                // would be drawn again by the mirror camera while walking.
                EnvStreamerReflectionSkip.Apply(instance);
            }
            else
            {
                if (EnvStreamingConfig.IsBigTreeCategory(item.Category))
                    EnvTreeBillboardUtil.ExcludeLodBillboardFromReflection(instance);
                if (EnvStreamingConfig.IsRockCategory(item.Category))
                {
                    EnvRockColliders.Ensure(instance, item.PrefabName);
                    EnvRockShoreWet.ApplyIfNearShore(instance);
                }
                if (EnvStreamingConfig.IsBridgeCategory(item.Category))
                    EnvBridgePillarFit.Ensure(instance);
                if (EnvStreamingConfig.IsRockCategory(item.Category) &&
                    EnvStreamingConfig.IsSmallRockForFarCull(item.PrefabName))
                {
                    EnvStreamerReflectionSkip.Apply(instance);
                }
                if (item.FarField)
                    EnvLandmarkColliders.Ensure(instance, item.PrefabName);
                // Near full trees: LOD_Billboard must Cross-face when LOD switches.
                if (EnvStreamingConfig.IsBigTreeCategory(item.Category))
                    EnvTreeBillboardUtil.ApplyCrossBillboardToLodBillboardChildren(instance);

                _colliderLod?.Register(instance, pos);
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
                    proto = _native.AddBillboardPrototype(prefab, mainCameraOnly);
                    if (proto >= 0 && _native.TryAddInstance(instance, proto))
                        inNative = true;
                }
                else if (!item.SkipNative)
                {
                    bool mainCameraOnly = EnvStreamingConfig.ShouldBeMainCameraOnly(
                        item.Category, item.PrefabName);
                    bool canCastShadows = EnvStreamingConfig.CanItemCastShadows(
                        item.Category, item.PrefabName);
                    proto = _native.AddPrototype(prefab, mainCameraOnly, canCastShadows);
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

        /// <summary>
        /// Prefill 排序键：真实距离平方 × 扇区权重。背后变远、右前变近，环半径不变。
        /// </summary>
        static float PrefillPriorityDistSq(
            Vector3 itemPos, Vector3 focus, Vector3 lookFwd, Vector3 lookRight)
        {
            float dx = itemPos.x - focus.x;
            float dz = itemPos.z - focus.z;
            float distSq = dx * dx + dz * dz;
            if (distSq < 1e-4f)
                return 0f;

            float inv = 1f / Mathf.Sqrt(distSq);
            float fx = dx * inv;
            float fz = dz * inv;
            float facing = fx * lookFwd.x + fz * lookFwd.z;
            float rightness = fx * lookRight.x + fz * lookRight.z;
            float weight = 1f;
            if (facing < -0.15f)
                weight = 1.55f;
            else if (rightness > 0.25f && facing > -0.1f)
                weight = 0.72f;
            else if (facing > 0.25f)
                weight = 0.88f;
            return distSq * weight * weight;
        }

        bool _released;

        /// <summary>
        /// Release prefab AssetHandles with no visible (or spawn-queued) instances.
        /// Used by idle immersive soft unload; next Spawn reloads via YooAsset.
        /// </summary>
        public int TrimUnusedPrefabHandles()
        {
            if (_released || _prefabHandles.Count == 0)
                return 0;

            var keep = new HashSet<string>();
            for (int i = 0; i < _items.Count; i++)
            {
                Item item = _items[i];
                if (item.State == StateVisible && !string.IsNullOrEmpty(item.Location))
                    keep.Add(item.Location);
            }

            for (int i = 0; i < _spawnQueue.Count; i++)
            {
                int idx = _spawnQueue[i];
                if (idx < 0 || idx >= _items.Count)
                    continue;
                string loc = _items[idx].Location;
                if (!string.IsNullOrEmpty(loc))
                    keep.Add(loc);
            }

            var toRemove = new List<string>();
            foreach (var kv in _prefabHandles)
            {
                if (keep.Contains(kv.Key))
                    continue;
                kv.Value?.Release();
                toRemove.Add(kv.Key);
            }

            for (int i = 0; i < toRemove.Count; i++)
            {
                string key = toRemove[i];
                _prefabHandles.Remove(key);
                _prefabCache.Remove(key);
            }

            if (toRemove.Count > 0)
            {
                Log.Info(
                    "[EnvItemStreamerLite] trim unused prefab handles="
                    + toRemove.Count
                    + " kept="
                    + _prefabHandles.Count);
            }

            return toRemove.Count;
        }

        public void Release()
        {
            if (_released)
                return;
            _released = true;
            _running = false;
            StopAllCoroutines();
            RestorePrefillWarningStacks();
#if UNITY_ANDROID && !UNITY_EDITOR
            // 先清原生合批再 Destroy：否则 LateUpdate 仍扫幽灵实例，二次进岛 Prefill 卡死。
            EnvNativeInstancing.ClearAll();
            _native = null;
#endif
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
