using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Game
{
    /// <summary>
    /// Android Native DrawMeshInstanced 合批（无 GPUI）。Editor/iOS 为 no-op。
    /// </summary>
    public sealed class EnvNativeInstancing : MonoBehaviour
    {
        const int BatchSize = 1023;

        static EnvNativeInstancing _instance;

        readonly List<Prototype> _prototypes = new List<Prototype>(64);
        readonly Dictionary<EntityId, int> _assetIdToIndex = new Dictionary<EntityId, int>(64);
        readonly Dictionary<string, int> _nameToIndex = new Dictionary<string, int>(64);
        readonly List<Bucket> _buckets = new List<Bucket>(64);

        readonly Plane[] _frustumPlanes = new Plane[6];
        readonly Matrix4x4[] _batch = new Matrix4x4[BatchSize];
        float _maxDist = EnvStreamingConfig.NativeInstancingMaxDist;
        Camera _cachedCam;
        int _cachedCamFrame = -1;

        sealed class Prototype
        {
            public string Name;
            public Mesh Mesh;
            public Material Material;
            public Bounds LocalBounds;
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

            public void SyncMatrices()
            {
                for (int i = Instances.Count - 1; i >= 0; i--)
                {
                    GameObject go = Instances[i];
                    if (go == null)
                    {
                        RemoveAt(i);
                        continue;
                    }

                    Matrices[i] = go.transform.localToWorldMatrix;
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

        public void SetMaxDist(float maxDist)
        {
            if (maxDist > 0f)
                _maxDist = maxDist;
        }

        public int AddPrototype(GameObject prefabAsset)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
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
#if UNITY_ANDROID && !UNITY_EDITOR
            if (go != null && protoIndex >= 0 && protoIndex < _buckets.Count)
                _buckets[protoIndex].Remove(go);
            if (go != null)
                SetRenderersEnabled(go, true);
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

            Vector3 camPos = cam.transform.position;
            GeometryUtility.CalculateFrustumPlanes(cam, _frustumPlanes);
            float maxDistSq = _maxDist * _maxDist;

            for (int p = 0; p < _prototypes.Count; p++)
            {
                Prototype proto = _prototypes[p];
                Bucket bucket = _buckets[p];
                if (proto.Mesh == null || proto.Material == null || bucket.Count == 0)
                    continue;

                bucket.SyncMatrices();
                int batchCount = 0;
                for (int i = 0; i < bucket.Matrices.Count; i++)
                {
                    Matrix4x4 m = bucket.Matrices[i];
                    Vector3 pos = m.GetColumn(3);
                    float dx = pos.x - camPos.x;
                    float dy = pos.y - camPos.y;
                    float dz = pos.z - camPos.z;
                    if (dx * dx + dy * dy + dz * dz > maxDistSq)
                        continue;

                    Bounds worldBounds = TransformBounds(proto.LocalBounds, m);
                    if (!GeometryUtility.TestPlanesAABB(_frustumPlanes, worldBounds))
                        continue;

                    _batch[batchCount++] = m;
                    if (batchCount >= BatchSize)
                    {
                        Graphics.DrawMeshInstanced(
                            proto.Mesh, 0, proto.Material, _batch, batchCount, null,
                            ShadowCastingMode.Off, true);
                        batchCount = 0;
                    }
                }

                if (batchCount > 0)
                {
                    Graphics.DrawMeshInstanced(
                        proto.Mesh, 0, proto.Material, _batch, batchCount, null,
                        ShadowCastingMode.Off, true);
                }
            }
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
                            mr = candidate;
                            break;
                        }
                    }
                }
            }

            if (mr == null)
                mr = prefab.GetComponentInChildren<MeshRenderer>(true);
            if (mr == null)
                return false;

            var filter = mr.GetComponent<MeshFilter>();
            if (filter == null || filter.sharedMesh == null || mr.sharedMaterial == null)
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
