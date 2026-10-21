using System.Collections;
using UnityEngine;
using UnityEngine.AI;
using UnityGameFramework.Runtime;
using YooAsset;

namespace Game
{
    /// <summary>
    /// 加载烘焙的 <see cref="NavMeshData"/> 并 <see cref="NavMesh.AddNavMeshData"/>。
    /// 缺资源不阻断进岛；<see cref="IslandJoystickCamera"/> 无数据时自动跳过钳位。
    /// </summary>
    public sealed class IslandNavMeshService : MonoBehaviour
    {
        AssetHandle _handle;
        NavMeshDataInstance _instance;
        NavMeshData _data;
        bool _loaded;

        public static IslandNavMeshService Instance { get; private set; }

        public bool IsReady => _loaded && _instance.valid;
        public NavMeshData Data => _data;

        public static IslandNavMeshService Ensure()
        {
            IslandNavMeshService existing = FindAnyObjectByType<IslandNavMeshService>();
            if (existing != null)
            {
                Instance = existing;
                return existing;
            }

            var go = new GameObject("IslandNavMeshService");
            DontDestroyOnLoad(go);
            return go.AddComponent<IslandNavMeshService>();
        }

        void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;
        }

        public IEnumerator Load()
        {
            if (_loaded)
                yield break;

            ReleaseInstanceOnly();

            NavMeshData data = null;
            if (YooResourceModule.DefaultPackage != null
                && YooResourceModule.DefaultPackage.CheckLocationValid(SceneAssets.IslandNavMeshLocation))
            {
                yield return YooResourceModule.LoadAssetCoroutine<NavMeshData>(
                    SceneAssets.IslandNavMeshLocation,
                    (asset, handle) =>
                    {
                        data = asset;
                        _handle = handle;
                    });
            }

#if UNITY_EDITOR
            if (data == null)
            {
                data = UnityEditor.AssetDatabase.LoadAssetAtPath<NavMeshData>(
                    SceneAssets.IslandNavMeshAssetPath);
                if (data != null)
                    Log.Info("[IslandNavMesh] Editor fallback load " + SceneAssets.IslandNavMeshAssetPath);
            }
#endif

            if (data == null)
            {
                Log.Error(
                    "[IslandNavMesh] Missing '{0}' ({1}) — camera clamp disabled.",
                    SceneAssets.IslandNavMeshLocation,
                    SceneAssets.IslandNavMeshAssetPath);
                yield break;
            }

            _data = data;
            _instance = NavMesh.AddNavMeshData(data);
            _loaded = _instance.valid;
            if (!_loaded)
            {
                Log.Error("[IslandNavMesh] AddNavMeshData failed");
                yield break;
            }

            Log.Info("[IslandNavMesh] ready");
        }

        public void Release()
        {
            ReleaseInstanceOnly();
            if (_handle != null)
            {
                _handle.Release();
                _handle = null;
            }

            _data = null;
            _loaded = false;
        }

        void ReleaseInstanceOnly()
        {
            if (_instance.valid)
                _instance.Remove();
            _instance = default;
        }

        void OnDestroy()
        {
            Release();
            if (Instance == this)
                Instance = null;
        }

        /// <summary>
        /// 将世界脚底 XZ 钳到最近 NavMesh。成功返回 true 并写出钳后位置（Y 保持传入值）。
        /// </summary>
        public static bool TryClampFootXZ(Vector3 footWorld, float maxDistance, out Vector3 clampedFoot)
        {
            clampedFoot = footWorld;
            IslandNavMeshService svc = Instance;
            if (svc == null || !svc.IsReady)
                return false;

            if (!NavMesh.SamplePosition(footWorld, out NavMeshHit hit, maxDistance, NavMesh.AllAreas))
                return false;

            clampedFoot = new Vector3(hit.position.x, footWorld.y, hit.position.z);
            return true;
        }
    }
}
