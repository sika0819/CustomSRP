using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Game
{
    /// <summary>
    /// 手机 Native DrawMeshInstanced 合批（无 GPUI）。Android / iOS 真机；编辑器为 no-op。
    /// </summary>
    public sealed class EnvNativeInstancing : MonoBehaviour
    {
        const int BatchSize = 1023;

        static EnvNativeInstancing _instance;
        bool _freezeTransforms;
        bool _loggedFrozenDraw;

        readonly List<Prototype> _prototypes = new List<Prototype>(64);
        readonly Dictionary<EntityId, int> _assetIdToIndex = new Dictionary<EntityId, int>(64);
        /// <summary>Billboard prototype keys (separate from LOD0 so veg + tree bb don't collide).</summary>
        readonly Dictionary<EntityId, int> _bbAssetIdToIndex = new Dictionary<EntityId, int>(64);
        readonly Dictionary<string, int> _bbNameToIndex = new Dictionary<string, int>(64);
        readonly Dictionary<string, int> _nameToIndex = new Dictionary<string, int>(64);
        readonly List<Bucket> _buckets = new List<Bucket>(64);
        int _syncFrame;

        readonly Plane[] _frustumPlanes = new Plane[6];
        readonly Matrix4x4[] _batch = new Matrix4x4[BatchSize];
        // Instances inside ShadowCastMaxDist flush into their own batch so the only
        // shadow-casting batch is the handful of trees the player is standing next
        // to; everything further out keeps ShadowCastingMode.Off.
        readonly Matrix4x4[] _batchNear = new Matrix4x4[BatchSize];
        float _maxDist = EnvStreamingConfig.NativeInstancingMaxDist;
        float _shadowCastMaxDist = EnvStreamingConfig.ShadowCastMaxDist;
        Camera _cachedCam;
        int _cachedCamFrame = -1;

        sealed class Prototype
        {
            public string Name;
            public Mesh Mesh;
            public Material Material;
            public Bounds LocalBounds;
            public bool CanCastShadows;
            public bool MainCameraOnly;
        }

        sealed class Bucket
        {
            public readonly List<Matrix4x4> Matrices = new List<Matrix4x4>(64);
            public readonly List<GameObject> Instances = new List<GameObject>(64);
            readonly Dictionary<EntityId, int> _goIdToSlot = new Dictionary<EntityId, int>(64);
            readonly Dictionary<EntityId, int> _removedAtFrame = new Dictionary<EntityId, int>(16);

            public int Count => Matrices.Count;

            public bool Add(GameObject go)
            {
                if (go == null)
                    return false;
                EntityId id = go.GetEntityId();
                if (_goIdToSlot.ContainsKey(id))
                    return true;
                if (_removedAtFrame.TryGetValue(id, out int frame) && frame >= Time.frameCount)
                    return false;

                _removedAtFrame.Remove(id);
                _goIdToSlot[id] = Matrices.Count;
                Matrices.Add(go.transform.localToWorldMatrix);
                Instances.Add(go);
                return true;
            }

            public void Remove(GameObject go)
            {
                if (go == null)
                    return;
                EntityId id = go.GetEntityId();
                if (!_goIdToSlot.TryGetValue(id, out int slot))
                    return;

                int last = Matrices.Count - 1;
                if (slot != last)
                {
                    Matrices[slot] = Matrices[last];
                    Instances[slot] = Instances[last];
                    if (Instances[slot] != null)
                        _goIdToSlot[Instances[slot].GetEntityId()] = slot;
                }

                Matrices.RemoveAt(last);
                Instances.RemoveAt(last);
                _goIdToSlot.Remove(id);
                _removedAtFrame[id] = Time.frameCount;
            }

            /// <summary>
            /// Dirty sync: only rewrite matrices when transform.hasChanged, or force
            /// every <paramref name="forceFull"/> call (periodic sweep for destroyed GOs).
            /// Far tree billboards are static in world space (yaw is GPU).
            /// </summary>
            public void SyncMatrices(bool forceFull)
            {
                for (int i = Instances.Count - 1; i >= 0; i--)
                {
                    GameObject go = Instances[i];
                    if (go == null)
                    {
                        RemoveAt(i);
                        continue;
                    }

                    Transform t = go.transform;
                    if (!forceFull && !t.hasChanged)
                        continue;
                    Matrices[i] = t.localToWorldMatrix;
                    t.hasChanged = false;
                }
            }

            void RemoveAt(int slot)
            {
                GameObject go = Instances[slot];
                EntityId id = go != null ? go.GetEntityId() : default;
                int last = Matrices.Count - 1;
                if (slot != last)
                {
                    Matrices[slot] = Matrices[last];
                    Instances[slot] = Instances[last];
                    if (Instances[slot] != null)
                        _goIdToSlot[Instances[slot].GetEntityId()] = slot;
                }

                Matrices.RemoveAt(last);
                Instances.RemoveAt(last);
                if (id != default)
                {
                    _goIdToSlot.Remove(id);
                    _removedAtFrame[id] = Time.frameCount;
                }
            }
        }

        public static EnvNativeInstancing Ensure()
        {
            if (_instance != null)
                return _instance;
            var go = new GameObject("[EnvNativeInstancing]");
            // 挂在当前岛屿场景里，随场景卸载。不要 DontDestroyOnLoad，也不要 HideInHierarchy。
            _instance = go.AddComponent<EnvNativeInstancing>();
            return _instance;
        }

        /// <summary>暂停或恢复合批绘制。freezeTransforms 时沿用已有矩阵，不再读 Transform。</summary>
        public static void SetRunning(bool running, bool freezeTransforms = false)
        {
            if (_instance == null)
                return;
            _instance._freezeTransforms = freezeTransforms;
            _instance._loggedFrozenDraw = false;
            _instance.enabled = running;
            if (running && freezeTransforms)
                UnityGameFramework.Runtime.Log.Info("[EnvNativeInstancing] resume frozen");
        }

        /// <summary>退登时立刻拆掉公告板运行时材质，不等场景 OnDestroy。</summary>
        public static void ReleaseForLeave()
        {
            if (_instance == null)
                return;
            _instance.Clear(immediate: true);
        }

        /// <summary>
        /// 进程拆世界时拆掉合批宿主。场景卸载不要走这里，物体随 Island 场景销毁。
        /// </summary>
        public static void DestroySingleton()
        {
            if (_instance == null)
                return;
            EnvNativeInstancing inst = _instance;
            _instance = null;
            inst.Clear(immediate: true);
            inst.DestroyUnlinked();
        }

        /// <summary>拆掉自己这个合批宿主。只从 <see cref="DestroySingleton"/> 进来。</summary>
        void DestroyUnlinked()
        {
            GameObject go = gameObject;
            if (go == null)
                return;
            go.hideFlags = HideFlags.None;
            Transform t = transform;
            if (t != null && t.parent != null)
                t.SetParent(null, false);
            DestroyImmediate(go);
        }

        void OnDestroy()
        {
            if (_instance == this)
                _instance = null;
            Clear(immediate: false);
        }

        /// <summary>
        /// 松开 Mesh/Material。公告板材质是运行时克隆，不摘掉卸包看不到 ref=0。
        /// 灌木和杂物握的是资源上的共享材质，不能销毁。
        /// </summary>
        public void Clear(bool immediate)
        {
            for (int i = 0; i < _prototypes.Count; i++)
            {
                Prototype proto = _prototypes[i];
                Material material = proto.Material;
                proto.Mesh = null;
                proto.Material = null;
                if (material == null || !material.name.EndsWith("(YBillboard)"))
                    continue;
                if (immediate)
                    DestroyImmediate(material);
                else
                    Destroy(material);
            }

            _prototypes.Clear();
            _buckets.Clear();
            _assetIdToIndex.Clear();
            _bbAssetIdToIndex.Clear();
            _bbNameToIndex.Clear();
            _nameToIndex.Clear();
            _syncFrame = 0;
        }

        /// <summary>
        /// 丢掉一个已经没有实例的原型，放开 Mesh/Material。不压缩列表，避免活着的 ProtoIndex 错位。
        /// </summary>
        public static bool TryDropOneEmptyPrototype()
        {
#if (UNITY_ANDROID || UNITY_IOS) && !UNITY_EDITOR
            return _instance != null && _instance.DropOneEmptyPrototype();
#else
            return false;
#endif
        }

        public void SetMaxDist(float maxDist)
        {
            if (maxDist > 0f)
                _maxDist = maxDist;
        }

        /// <summary>Near-batch shadow radius. Matches the streamer's
        /// <see cref="EnvStreamingConfig.ShadowCastMaxDist"/> hysteresis band so the
        /// instanced batch casts from exactly the same set of trees the MeshRenderer
        /// path would.</summary>
        public void SetShadowCastMaxDist(float dist)
        {
            if (dist > 0f)
                _shadowCastMaxDist = dist;
        }

        public int AddPrototype(GameObject prefabAsset, bool mainCameraOnly, bool canCastShadows)
        {
#if (UNITY_ANDROID || UNITY_IOS) && !UNITY_EDITOR
            if (prefabAsset == null)
                return -1;
            EntityId assetId = prefabAsset.GetEntityId();
            if (_assetIdToIndex.TryGetValue(assetId, out int existing))
                return existing;

            if (!TryExtractLod0(prefabAsset, out Mesh mesh, out Material material, out Bounds localBounds))
                return -1;

            // Unity 6：INSTANCING_ON 为 local keyword，EnableKeyword 会报错并跳过；只设 enableInstancing。
            if (material != null)
                material.enableInstancing = true;

            int index = _prototypes.Count;
            var proto = new Prototype
            {
                Name = prefabAsset.name,
                Mesh = mesh,
                Material = material,
                LocalBounds = localBounds,
                CanCastShadows = canCastShadows,
                MainCameraOnly = mainCameraOnly,
            };
            _prototypes.Add(proto);
            _buckets.Add(new Bucket());
            _assetIdToIndex[assetId] = index;
            if (!_nameToIndex.ContainsKey(proto.Name))
                _nameToIndex[proto.Name] = index;
            return index;
#else
            return -1;
#endif
        }

        /// <summary>
        /// Far-tree single-card billboard prototype. Uses the baked Y-facing material.
        /// Separate key space from <see cref="AddPrototype"/> so LOD0 veg and tree bb coexist.
        /// </summary>
        public int AddBillboardPrototype(GameObject prefabAsset, bool mainCameraOnly)
        {
#if (UNITY_ANDROID || UNITY_IOS) && !UNITY_EDITOR
            if (prefabAsset == null)
                return -1;
            EntityId assetId = prefabAsset.GetEntityId();
            if (_bbAssetIdToIndex.TryGetValue(assetId, out int existing))
                return existing;

            EnvTreeBillboardService billboards = GameEntry.Billboards;
            if (billboards == null || !billboards.TryExtractBillboard(
                    prefabAsset, out Mesh mesh, out Material material, out Bounds localBounds))
                return -1;

            if (mesh == null || material == null)
                return -1;

            int index = _prototypes.Count;
            string protoName = prefabAsset.name + "#bb";
            var proto = new Prototype
            {
                Name = protoName,
                Mesh = mesh,
                Material = material,
                LocalBounds = localBounds,
                CanCastShadows = false,
                MainCameraOnly = mainCameraOnly,
            };
            _prototypes.Add(proto);
            _buckets.Add(new Bucket());
            _bbAssetIdToIndex[assetId] = index;
            if (!_bbNameToIndex.ContainsKey(prefabAsset.name))
                _bbNameToIndex[prefabAsset.name] = index;
            return index;
#else
            return -1;
#endif
        }

        public int GetBillboardPrototypeIndex(GameObject instanceOrPrefab)
        {
#if (UNITY_ANDROID || UNITY_IOS) && !UNITY_EDITOR
            if (instanceOrPrefab == null)
                return -1;
            if (_bbAssetIdToIndex.TryGetValue(instanceOrPrefab.GetEntityId(), out int byAsset))
                return byAsset;
            string name = StripSpawnIndexPrefix(instanceOrPrefab.name);
            if (!string.IsNullOrEmpty(name))
            {
                // Proxy names: "12 Grand Fir 1" or "12 Grand Fir 1#bb"
                if (name.EndsWith("#bb"))
                    name = name.Substring(0, name.Length - 3);
                if (_bbNameToIndex.TryGetValue(name, out int byName))
                    return byName;
            }
#endif
            return -1;
        }

        public int GetPrototypeIndex(GameObject instanceOrPrefab)
        {
#if (UNITY_ANDROID || UNITY_IOS) && !UNITY_EDITOR
            if (instanceOrPrefab == null)
                return -1;
            if (_assetIdToIndex.TryGetValue(instanceOrPrefab.GetEntityId(), out int byAsset))
                return byAsset;
            string name = StripSpawnIndexPrefix(instanceOrPrefab.name);
            if (!string.IsNullOrEmpty(name) && _nameToIndex.TryGetValue(name, out int byName))
                return byName;
#endif
            return -1;
        }

        public bool TryAddInstance(GameObject go, int protoIndex)
        {
#if (UNITY_ANDROID || UNITY_IOS) && !UNITY_EDITOR
            if (go == null || protoIndex < 0 || protoIndex >= _buckets.Count)
                return false;
            if (_prototypes[protoIndex].Mesh == null || _prototypes[protoIndex].Material == null)
                return false;
            if (go.GetComponentInChildren<MeshRenderer>(true) == null)
                return false;

            if (!_buckets[protoIndex].Add(go))
                return false;

            SetRenderersEnabled(go, false);
            return true;
#else
            return false;
#endif
        }

        public void RemoveInstance(GameObject go, int protoIndex)
        {
#if (UNITY_ANDROID || UNITY_IOS) && !UNITY_EDITOR
            if (go != null && protoIndex >= 0 && protoIndex < _buckets.Count)
                _buckets[protoIndex].Remove(go);
            if (go != null)
                SetRenderersEnabled(go, true);
#endif
        }

#if (UNITY_ANDROID || UNITY_IOS) && !UNITY_EDITOR
        bool DropOneEmptyPrototype()
        {
            for (int i = 0; i < _prototypes.Count; i++)
            {
                if (i >= _buckets.Count || _buckets[i].Count > 0)
                    continue;
                Prototype proto = _prototypes[i];
                if (proto.Mesh == null && proto.Material == null)
                    continue;

                Material material = proto.Material;
                proto.Mesh = null;
                proto.Material = null;
                if (material != null && material.name.EndsWith("(YBillboard)"))
                    Destroy(material);
                UnbindPrototypeIndex(i);
                return true;
            }

            return false;
        }

        void UnbindPrototypeIndex(int index)
        {
            Unbind(_assetIdToIndex, index);
            Unbind(_bbAssetIdToIndex, index);
            Unbind(_nameToIndex, index);
            Unbind(_bbNameToIndex, index);
        }

        static void Unbind<TKey>(Dictionary<TKey, int> map, int index)
        {
            bool removed;
            do
            {
                removed = false;
                TKey key = default;
                foreach (var kv in map)
                {
                    if (kv.Value != index)
                        continue;
                    key = kv.Key;
                    removed = true;
                    break;
                }

                if (removed)
                    map.Remove(key);
            }
            while (removed);
        }
#endif

        void LateUpdate()
        {
#if (UNITY_ANDROID || UNITY_IOS) && !UNITY_EDITOR
            DrawAll();
#endif
        }

        void DrawAll()
        {
            if (EnvItemStreamerLite.PrefillSuppressRender)
                return;
            Camera cam = _freezeTransforms ? FrozenCamera() : ResolveCamera();
            if (cam == null)
                return;

            _syncFrame++;
            Vector3 camPos = cam.transform.position;
            GeometryUtility.CalculateFrustumPlanes(cam, _frustumPlanes);
            float maxDistSq = _maxDist * _maxDist;
            float shadowDistSq = _shadowCastMaxDist * _shadowCastMaxDist;

            for (int p = 0; p < _prototypes.Count; p++)
            {
                Prototype proto = _prototypes[p];
                Bucket bucket = _buckets[p];
                if (proto.Mesh == null || proto.Material == null || bucket.Count == 0)
                    continue;

                // Full sweep every 30 frames to drop destroyed GOs; otherwise dirty-only.
                // 退登后再读 Transform.localToWorldMatrix 时主线程不返回，沿用进岛时的矩阵。
                bool forceSync = (_syncFrame % 30) == 0;
                if (!_freezeTransforms)
                    bucket.SyncMatrices(forceSync);
                // Shadow-capable prototypes (big trees / buildings) split into a near
                // casting batch and a far non-casting batch, mirroring the streamer's
                // per-distance ShadowCastMaxDist toggle. Prototypes that never cast
                // (shrubs / small veg / clutter / far tree bb) stay single-batch Off.
                bool splitShadows = proto.CanCastShadows;
                int batchCount = 0;
                int nearCount = 0;
                for (int i = 0; i < bucket.Matrices.Count; i++)
                {
                    Matrix4x4 m = bucket.Matrices[i];
                    Vector3 pos = m.GetColumn(3);
                    float dx = pos.x - camPos.x;
                    float dy = pos.y - camPos.y;
                    float dz = pos.z - camPos.z;
                    float distSq = dx * dx + dy * dy + dz * dz;
                    if (distSq > maxDistSq)
                        continue;

                    Bounds worldBounds = TransformBounds(proto.LocalBounds, m);
                    if (!GeometryUtility.TestPlanesAABB(_frustumPlanes, worldBounds))
                        continue;

                    if (splitShadows && distSq <= shadowDistSq)
                    {
                        _batchNear[nearCount++] = m;
                        if (nearCount >= BatchSize)
                        {
                            FlushBatch(cam, proto, _batchNear, nearCount,
                                ShadowCastingMode.On);
                            nearCount = 0;
                        }
                        continue;
                    }

                    _batch[batchCount++] = m;
                    if (batchCount >= BatchSize)
                    {
                        FlushBatch(cam, proto, _batch, batchCount,
                            ShadowCastingMode.Off);
                        batchCount = 0;
                    }
                }

                if (nearCount > 0)
                {
                    FlushBatch(cam, proto, _batchNear, nearCount, ShadowCastingMode.On);
                }

                if (batchCount > 0)
                {
                    FlushBatch(cam, proto, _batch, batchCount, ShadowCastingMode.Off);
                }
            }

            if (_freezeTransforms && !_loggedFrozenDraw)
            {
                _loggedFrozenDraw = true;
                UnityGameFramework.Runtime.Log.Info("[EnvNativeInstancing] frozen draw done");
            }
        }

        static Camera FrozenCamera()
        {
            // 地图根保持未激活时 Camera.main 能返回。不要去激活地图节点再找相机。
            return Camera.main;
        }

        static void FlushBatch(
            Camera cam, Prototype proto, Matrix4x4[] batch, int count,
            ShadowCastingMode shadowCastingMode)
        {
            if (count <= 0)
                return;

            // Bind to the main camera so the planar mirror probe never picks up
            // these instances. Graphics.DrawMeshInstanced (legacy overload) draws
            // for every camera rendering that frame including reflection probes,
            // which is exactly what we don't want for grass / clutter / small veg.
            // RenderMeshInstanced takes a RenderParams.camera that scopes the draw.
            //
            // worldBounds MUST be the union of instance world AABBs. Passing
            // proto.LocalBounds (mesh-space, near origin) makes Unity cull the
            // whole batch whenever the camera isn't looking at the origin —
            // MeshRenderers are already disabled, so trees vanish on device.
            Bounds worldBounds = TransformBounds(proto.LocalBounds, batch[0]);
            for (int i = 1; i < count; i++)
                worldBounds.Encapsulate(TransformBounds(proto.LocalBounds, batch[i]));

            var rp = new RenderParams(proto.Material)
            {
                camera = cam,
                worldBounds = worldBounds,
                shadowCastingMode = shadowCastingMode,
                receiveShadows = false,
                renderingLayerMask = 1,
                rendererPriority = 0,
                layer = 0
            };

            Graphics.RenderMeshInstanced(rp, proto.Mesh, 0, batch, count);
        }

        Camera ResolveCamera()
        {
            int frame = Time.frameCount;
            if (_cachedCam != null && _cachedCamFrame == frame)
                return _cachedCam;
            _cachedCam = Camera.main;
            _cachedCamFrame = frame;
            return _cachedCam;
        }

        static void SetRenderersEnabled(GameObject go, bool enabled)
        {
            var renderers = go.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                if (renderers[i] != null)
                    renderers[i].enabled = enabled;
            }
        }

        static string StripSpawnIndexPrefix(string name)
        {
            if (string.IsNullOrEmpty(name))
                return name;
            int space = name.IndexOf(' ');
            if (space <= 0)
                return name;
            for (int i = 0; i < space; i++)
            {
                if (!char.IsDigit(name[i]))
                    return name;
            }

            return name.Substring(space + 1);
        }

        static bool TryExtractLod0(GameObject prefab, out Mesh mesh, out Material material, out Bounds localBounds)
        {
            mesh = null;
            material = null;
            localBounds = new Bounds(Vector3.zero, Vector3.one);

            MeshRenderer mr = null;
            int lod0MeshRendererCount = 0;
            var lodGroup = prefab.GetComponentInChildren<LODGroup>(true);
            if (lodGroup != null)
            {
                LOD[] lods = lodGroup.GetLODs();
                if (lods != null && lods.Length > 0 && lods[0].renderers != null)
                {
                    for (int i = 0; i < lods[0].renderers.Length; i++)
                    {
                        if (lods[0].renderers[i] is MeshRenderer candidate && candidate != null)
                        {
                            lod0MeshRendererCount++;
                            if (mr == null)
                                mr = candidate;
                        }
                    }
                }
            }

            if (mr == null)
            {
                var all = prefab.GetComponentsInChildren<MeshRenderer>(true);
                if (all == null || all.Length == 0)
                    return false;
                // Single-draw path cannot replace multi-MR prefabs (bark+leaf trees).
                if (all.Length != 1)
                    return false;
                mr = all[0];
                lod0MeshRendererCount = 1;
            }
            else if (lod0MeshRendererCount != 1)
            {
                return false;
            }

            var filter = mr.GetComponent<MeshFilter>();
            if (filter == null || filter.sharedMesh == null || mr.sharedMaterial == null)
                return false;

            // Submesh loop not implemented — multi-mat would leave later slots undrawn
            // after SetRenderersEnabled(false).
            Material[] mats = mr.sharedMaterials;
            if (mats != null && mats.Length > 1)
                return false;

            mesh = filter.sharedMesh;
            material = mr.sharedMaterial;
            localBounds = mesh.bounds;
            return true;
        }

        static Bounds TransformBounds(Bounds local, Matrix4x4 localToWorld)
        {
            Vector3 center = localToWorld.MultiplyPoint3x4(local.center);
            Vector3 ext = local.extents;
            Vector3 axisX = localToWorld.MultiplyVector(new Vector3(ext.x, 0f, 0f));
            Vector3 axisY = localToWorld.MultiplyVector(new Vector3(0f, ext.y, 0f));
            Vector3 axisZ = localToWorld.MultiplyVector(new Vector3(0f, 0f, ext.z));
            Vector3 worldExt = new Vector3(
                Mathf.Abs(axisX.x) + Mathf.Abs(axisY.x) + Mathf.Abs(axisZ.x),
                Mathf.Abs(axisX.y) + Mathf.Abs(axisY.y) + Mathf.Abs(axisZ.y),
                Mathf.Abs(axisX.z) + Mathf.Abs(axisY.z) + Mathf.Abs(axisZ.z));
            return new Bounds(center, worldExt * 2f);
        }
    }
}
