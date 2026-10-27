using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Game
{
    /// <summary>
    /// Android Native RenderMeshInstanced 合批（无 GPUI）。Editor/iOS 为 no-op。
    /// 进合批的物体会退出 SRP（LODGroup + Renderer 关掉），并用私有材质绘制。
    /// 共享材质仍留给 MeshRenderer，避免 SRP Batcher 和 DrawMeshInstanced 画同一份 Material。
    /// </summary>
    public sealed class EnvNativeInstancing : MonoBehaviour
    {
        const int BatchSize = 1023;

        static EnvNativeInstancing _instance;

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
            public bool OwnsMaterial;
            /// <summary>Prefab 子节点网格相对根的矩阵。实例矩阵 = 根 localToWorld × 此值。</summary>
            public Matrix4x4 MeshLocalToRoot = Matrix4x4.identity;
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

            public bool Add(GameObject go, Matrix4x4 meshLocalToRoot)
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
                Matrices.Add(go.transform.localToWorldMatrix * meshLocalToRoot);
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

            public void ClearSlots()
            {
                Matrices.Clear();
                Instances.Clear();
                _goIdToSlot.Clear();
                _removedAtFrame.Clear();
            }

            /// <summary>
            /// Dirty sync: only rewrite matrices when transform.hasChanged, or force
            /// every <paramref name="forceFull"/> call (periodic sweep for destroyed GOs).
            /// Far tree billboards are static in world space (yaw is GPU).
            /// </summary>
            public void SyncMatrices(bool forceFull, Matrix4x4 meshLocalToRoot)
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
                    Matrices[i] = t.localToWorldMatrix * meshLocalToRoot;
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
            DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideInHierarchy;
            _instance = go.AddComponent<EnvNativeInstancing>();
            return _instance;
        }

        /// <summary>
        /// 退岛 / World.Release：清空合批。否则二次 Prefill 叠在失效 Mesh/Material
        /// 与幽灵实例上，LateUpdate DrawAll 会把主线程打到卡死（「正在铺装环境」）。
        /// </summary>
        public static void ClearAll()
        {
            if (_instance == null)
                return;
            _instance.Clear();
        }

        void Clear()
        {
            for (int i = 0; i < _buckets.Count; i++)
            {
                Bucket bucket = _buckets[i];
                // 实例由 streamer Destroy；此处只拆槽位，避免再 SetRenderersEnabled。
                bucket.Matrices.Clear();
                bucket.Instances.Clear();
                bucket.ClearSlots();
            }

            for (int i = 0; i < _prototypes.Count; i++)
            {
                Prototype proto = _prototypes[i];
                if (proto.OwnsMaterial && proto.Material != null)
                    Destroy(proto.Material);
            }

            _buckets.Clear();
            _prototypes.Clear();
            _assetIdToIndex.Clear();
            _bbAssetIdToIndex.Clear();
            _bbNameToIndex.Clear();
            _nameToIndex.Clear();
            _syncFrame = 0;
            _cachedCam = null;
            _cachedCamFrame = -1;
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
#if UNITY_ANDROID && !UNITY_EDITOR
            if (prefabAsset == null)
                return -1;
            EntityId assetId = prefabAsset.GetEntityId();
            if (_assetIdToIndex.TryGetValue(assetId, out int existing))
                return existing;

            if (!TryExtractLod0(
                    prefabAsset, out Mesh mesh, out Material shared, out Bounds localBounds,
                    out Matrix4x4 meshLocalToRoot))
                return -1;

            // 私有副本：不要改共享材质的 enableInstancing。
            // 共享材质还挂在大树 / 未进合批的 MeshRenderer 上，SRP Batcher 和
            // RenderMeshInstanced 不能共用同一份 Material，否则真机会叠一层或整批丢。
            Material material = CreateInstancedMaterial(shared);
            if (material == null)
                return -1;

            int index = _prototypes.Count;
            var proto = new Prototype
            {
                Name = prefabAsset.name,
                Mesh = mesh,
                Material = material,
                OwnsMaterial = true,
                MeshLocalToRoot = meshLocalToRoot,
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
        /// Far-tree cross-billboard prototype (front/side snap + yaw material instance).
        /// Separate key space from <see cref="AddPrototype"/> so LOD0 veg and tree bb coexist.
        /// </summary>
        public int AddBillboardPrototype(GameObject prefabAsset, bool mainCameraOnly)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (prefabAsset == null)
                return -1;
            EntityId assetId = prefabAsset.GetEntityId();
            if (_bbAssetIdToIndex.TryGetValue(assetId, out int existing))
                return existing;

            if (!EnvTreeBillboardUtil.TryExtractBillboard(
                    prefabAsset, out Mesh mesh, out Material sharedMat, out Bounds localBounds))
                return -1;

            // 近树 LOD_Billboard 的 MeshRenderer 用缓存那份（SRP）。合批再拷一份。
            Material cross = EnvTreeBillboardUtil.CreateCrossBillboardMaterial(sharedMat);
            Material material = CreateInstancedMaterial(cross);
            if (mesh == null || material == null)
                return -1;

            int index = _prototypes.Count;
            string protoName = prefabAsset.name + "#bb";
            var proto = new Prototype
            {
                Name = protoName,
                Mesh = mesh,
                Material = material,
                OwnsMaterial = true,
                // 代理把广告牌网格放在根上，不要再乘 prefab 子节点偏移。
                MeshLocalToRoot = Matrix4x4.identity,
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
#if UNITY_ANDROID && !UNITY_EDITOR
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
#if UNITY_ANDROID && !UNITY_EDITOR
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
#if UNITY_ANDROID && !UNITY_EDITOR
            if (go == null || protoIndex < 0 || protoIndex >= _buckets.Count)
                return false;
            if (_prototypes[protoIndex].Mesh == null || _prototypes[protoIndex].Material == null)
                return false;
            if (go.GetComponentInChildren<MeshRenderer>(true) == null)
                return false;

            if (!_buckets[protoIndex].Add(go, _prototypes[protoIndex].MeshLocalToRoot))
                return false;

            // 进了 DrawMeshInstanced 就退出 SRP。LODGroup 每帧会把 Renderer 打开，必须一起关掉。
            SetSrpSuppressed(go, true);
            return true;
#else
            return false;
#endif
        }

        public void RemoveInstance(GameObject go, int protoIndex)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (go != null && protoIndex >= 0 && protoIndex < _buckets.Count)
                _buckets[protoIndex].Remove(go);
            if (go != null)
                SetSrpSuppressed(go, false);
#endif
        }

        void LateUpdate()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            DrawAll();
#endif
        }

        void DrawAll()
        {
            Camera cam = ResolveCamera();
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
                bool forceSync = (_syncFrame % 30) == 0;
                bucket.SyncMatrices(forceSync, proto.MeshLocalToRoot);
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

        /// <summary>
        /// SRP Batcher 画 MeshRenderer；本类画 RenderMeshInstanced。同一物体只能走一条。
        /// 关掉 LODGroup 后再压 Renderer，避免 LOD 在裁剪阶段把它们交回 SRP。
        /// </summary>
        static void SetSrpSuppressed(GameObject go, bool suppressed)
        {
            var lodGroups = go.GetComponentsInChildren<LODGroup>(true);
            var renderers = go.GetComponentsInChildren<Renderer>(true);

            // 退出合批时先打开 Renderer，再交还 LODGroup，避免把所有 LOD 同时画出来。
            // 进入合批时先关 LODGroup（它会把当前 LOD 打开），再把 Renderer 压掉。
            if (!suppressed)
                ApplyRendererSuppression(renderers, false);

            for (int i = 0; i < lodGroups.Length; i++)
            {
                if (lodGroups[i] != null)
                    lodGroups[i].enabled = !suppressed;
            }

            if (suppressed)
                ApplyRendererSuppression(renderers, true);
        }

        static void ApplyRendererSuppression(Renderer[] renderers, bool suppressed)
        {
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null)
                    continue;
                renderer.forceRenderingOff = suppressed;
                renderer.enabled = !suppressed;
            }
        }

        /// <summary>
        /// 合批专用材质。不改共享材质：共享材质仍给没进合批的 MeshRenderer，走 SRP Batcher。
        /// Unity 6 的 INSTANCING_ON 是 local keyword，不能 EnableKeyword。
        /// </summary>
        static Material CreateInstancedMaterial(Material shared)
        {
            if (shared == null)
                return null;
            var material = new Material(shared)
            {
                name = shared.name + " (NativeBatch)",
                enableInstancing = true
            };
            return material;
        }

        static Matrix4x4 MeshLocalToRootOf(Transform root, Transform meshTransform)
        {
            if (root == null || meshTransform == null || meshTransform == root)
                return Matrix4x4.identity;
            return root.worldToLocalMatrix * meshTransform.localToWorldMatrix;
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

        static bool TryExtractLod0(
            GameObject prefab, out Mesh mesh, out Material material, out Bounds localBounds,
            out Matrix4x4 meshLocalToRoot)
        {
            mesh = null;
            material = null;
            localBounds = new Bounds(Vector3.zero, Vector3.one);
            meshLocalToRoot = Matrix4x4.identity;

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
            else if (lod0MeshRendererCount != 1 || HasMeshRendererOutsideLod(prefab, lodGroup))
            {
                // 树干+树叶、或 LOD 名单外的网格：合批只画一块就会把其余关掉。
                // 整棵留在 MeshRenderer，让 SRP 一次创建全部部件。
                return false;
            }

            var filter = mr.GetComponent<MeshFilter>();
            if (filter == null || filter.sharedMesh == null || mr.sharedMaterial == null)
                return false;

            // 多材质只画 submesh 0，其余槽位在关掉 Renderer 后会消失。
            Material[] mats = mr.sharedMaterials;
            if (mats != null && mats.Length > 1)
                return false;

            mesh = filter.sharedMesh;
            material = mr.sharedMaterial;
            localBounds = mesh.bounds;
            meshLocalToRoot = MeshLocalToRootOf(prefab.transform, mr.transform);
            return true;
        }

        /// <summary>LOD 名单外还有网格时，合批不能只带走 LOD0。</summary>
        static bool HasMeshRendererOutsideLod(GameObject prefab, LODGroup lodGroup)
        {
            if (prefab == null || lodGroup == null)
                return false;
            LOD[] lods = lodGroup.GetLODs();
            MeshRenderer[] all = prefab.GetComponentsInChildren<MeshRenderer>(true);
            for (int i = 0; i < all.Length; i++)
            {
                MeshRenderer renderer = all[i];
                if (renderer == null)
                    continue;
                bool listed = false;
                if (lods != null)
                {
                    for (int l = 0; l < lods.Length && !listed; l++)
                    {
                        Renderer[] renderers = lods[l].renderers;
                        if (renderers == null)
                            continue;
                        for (int r = 0; r < renderers.Length; r++)
                        {
                            if (renderers[r] == renderer)
                            {
                                listed = true;
                                break;
                            }
                        }
                    }
                }

                if (!listed)
                    return true;
            }

            return false;
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
