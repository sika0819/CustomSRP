using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityGameFramework.Runtime;
using YooAsset;

namespace Game
{
    /// <summary>
    /// 岛屿世界生命周期：地形 / 海 / 内河 / MapItem。Procedure 只编排阶段。
    /// </summary>
    public sealed class IslandWorldService : MonoBehaviour
    {
        GameObject _terrainInstance;
        GameObject _oceanInstance;
        GameObject _inlandWaterInstance;
        GameObject _mapItemRoot;
        EnvMapItemSpawner _mapItemSpawner;
        AssetHandle _terrainHandle;
        AssetHandle _oceanHandle;
        AssetHandle _inlandWaterHandle;
        AssetHandle _mapItemHandle;
        AssetHandle _spawnPoseHandle;
        readonly List<AssetHandle> _inlandChildHandles = new List<AssetHandle>(8);
        readonly List<MapItemData> _extrasCache = new List<MapItemData>(64);
        readonly WaterSurfaceProbe _waterProbe = new WaterSurfaceProbe();
        IslandSpawnPoseAsset _spawnPoseAsset;
        bool _loaded;

        /// <summary>Fires once after inland water is loaded (or confirmed missing). Safe to poll for null.</summary>
        public static event Action<bool> OnInlandWaterReady;

        /// <summary>
        /// 共享标记：InlandWaterInstance 是否已完成加载（含两种路径：资源实例化 或 MapItemExtras 拼装）。
        /// 用于 AmbienceZoneService 在事件错过时也能确认 water 已就绪。
        /// </summary>
        public static bool InlandWaterReady { get; private set; }

        public static IslandWorldService Instance { get; private set; }

        public WaterSurfaceProbe WaterProbe => _waterProbe;

        /// <summary>出生点配置。静态 <see cref="IslandSpawnPose"/> 只保留回退坐标。</summary>
        public IslandSpawnPoseAsset SpawnPoseAsset => _spawnPoseAsset;

        public bool IsLoaded => _loaded;

        /// <summary>
        /// 近场（地形 / 海 / 地图物件）已铺完。草和后续流送仍可在 <see cref="Load"/> 里继续。
        /// </summary>
        public bool IsNearReady { get; private set; }

        /// <summary>本轮 Load 已结束且近场没铺上。Procedure 用来停止空等。</summary>
        public bool LoadFailed { get; private set; }
        public EnvMapItemSpawner MapItemSpawner => _mapItemSpawner;
        public GameObject TerrainInstance => _terrainInstance;
        public GameObject OceanInstance => _oceanInstance;
        public GameObject InlandWaterInstance => _inlandWaterInstance;

        /// <summary>
        /// 退登后世界留在内存里，但脚本和渲染先停掉。
        /// 加载罩期间 LateUpdate 仍会扫实例矩阵；父节点若已坏，localToWorldMatrix 不再返回。
        /// </summary>
        public void SetSimulationActive(bool active)
        {
            SetActive(_terrainInstance, active);
            SetActive(_oceanInstance, active);
            SetActive(_inlandWaterInstance, active);
            // 地图物件重新 SetActive 后，下一帧合批/脚本会去走 Transform，主线程不再返回。
            // 地形、海、草地和相机可以恢复。树和建筑靠退登前的合批矩阵画，不激活节点。
            if (!active)
            {
                SetActive(_mapItemRoot, false);
                if (_mapItemSpawner != null)
                    _mapItemSpawner.enabled = false;
                EnvNativeInstancing.SetRunning(false);
            }
            if (IslandJoystickCamera.Current != null)
                IslandJoystickCamera.Current.enabled = active;

            if (active)
            {
                // 椰树、码头不进合批，渲染器挂在地图根上。根关掉就看不见。
                // 先关掉子节点上的脚本再激活，避免脚本 LateUpdate 去走 Transform。
                ResumeMapRenderers();
                EnvNativeInstancing.SetRunning(true, freezeTransforms: true);
            }
            Log.Info("[IslandWorld] simulation active=" + active
                + " map=" + (_mapItemRoot != null && _mapItemRoot.activeSelf));
        }

        static void SetActive(GameObject go, bool active)
        {
            if (go != null && go.activeSelf != active)
                go.SetActive(active);
        }

        void ResumeMapRenderers()
        {
            if (_mapItemRoot == null || _mapItemRoot.activeSelf)
                return;
            MonoBehaviour[] behaviours = _mapItemRoot.GetComponentsInChildren<MonoBehaviour>(true);
            for (int i = 0; i < behaviours.Length; i++)
            {
                if (behaviours[i] != null)
                    behaviours[i].enabled = false;
            }

            Log.Info("[IslandWorld] map behaviours off n=" + behaviours.Length);
            _mapItemRoot.SetActive(true);
        }

        public static IslandWorldService Ensure()
        {
            IslandWorldService service = IslandServiceHost.Ensure(Instance, "IslandWorldService");
            Instance = service;
            return service;
        }

        void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;
            GameEntry.World = this;
        }

        public IEnumerator Load(System.Action<float, string> reportProgress)
        {
            LoadFailed = false;
            if (_loaded)
            {
                if (!IsNearReady && _terrainInstance != null)
                    IsNearReady = true;
                if (!IsNearReady)
                {
                    Log.Error(
                        "[IslandWorld] marked loaded but terrain/ocean missing — release and retry");
                    Release();
                    LoadFailed = true;
                }

                yield break;
            }

            // 出生点配置最先加载：SpawnMapItems / ApplySpawnPose 都要读它。
            yield return LoadSpawnPose();

            bool probe = IslandLeakProbe.Enabled;
            if (probe)
                Log.Info("[LeakProbe] 进岛只加载已经打开的模块");

            if (IslandLeakProbe.Allow(IslandLeakProbe.Module.Terrain))
            {
                reportProgress?.Invoke(0.72f, "正在加载地形...");
                yield return LoadTerrain(fatal: !probe);
                if (LoadFailed)
                    yield break;
            }
            else
            {
                Log.Info("[LeakProbe] terrain OFF");
            }

            if (IslandLeakProbe.Allow(IslandLeakProbe.Module.Ocean))
            {
                reportProgress?.Invoke(0.78f, "正在加载海洋...");
                yield return LoadOcean(fatal: !probe);
                if (LoadFailed)
                    yield break;
            }
            else
            {
                Log.Info("[LeakProbe] ocean OFF");
            }

            if (IslandLeakProbe.Allow(IslandLeakProbe.Module.Inland))
            {
                reportProgress?.Invoke(0.79f, "正在加载河流瀑布...");
                yield return SpawnInlandWater();
            }
            else
            {
                Log.Info("[LeakProbe] inland OFF");
            }

            reportProgress?.Invoke(0.80f, "正在铺装环境...");
            yield return SpawnMapItems(reportProgress);
            IsNearReady = probe || _terrainInstance != null;

            IslandGrassService grass = GameEntry.Grass ?? IslandGrassService.Ensure();
            if (IslandLeakProbe.Allow(IslandLeakProbe.Module.Grass))
            {
                reportProgress?.Invoke(0.86f, "正在铺装草地...");
                yield return grass.Load();
            }
            else
            {
                Log.Info("[LeakProbe] grass OFF");
            }

            // 水体最近点改成运行期直接从场景采样（见 WaterSurfaceProbe），
            // 不再加载任何烘焙的水域资源。

            InlandWaterReady = _inlandWaterInstance != null;
            _loaded = true;
            OnInlandWaterReady?.Invoke(_inlandWaterInstance != null);
            int mapCount = _mapItemSpawner != null ? _mapItemSpawner.MapItemCount : 0;
            int spawned = _mapItemSpawner != null ? _mapItemSpawner.SpawnedCount : 0;
            int extras = _mapItemSpawner != null ? _mapItemSpawner.ExtrasSpawnedCount : 0;

            Log.Info(
                "[IslandWorld] loaded terrain=" + (_terrainInstance != null) +
                " ocean=" + (_oceanInstance != null) +
                " inland=" + (_inlandWaterInstance != null) +
                " mapItems=" + mapCount + " spawned=" + spawned + " extras=" + extras);

            reportProgress?.Invoke(BootLoadingService.TerrainPrepareMax, "正在准备场景...");

            if (GameEntry.Event != null)
                GameEntry.Event.Fire(this, IslandWorldReadyEventArgs.Create(mapCount, spawned, extras));
        }

        bool _released;
        int _terrainGate;
        int _oceanGate;

        public void Release() => Release(destroyObjects: true);

        void Release(bool destroyObjects)
        {
            if (_released)
                return;
            _released = true;

            if (destroyObjects)
                (GameEntry.Grass ?? IslandGrassService.Active)?.Release();

            if (destroyObjects && _mapItemSpawner != null)
            {
                _mapItemSpawner.Release();
                _mapItemSpawner = null;
            }

            // 坐标表不挂在地图根上。拆根之前清掉；进程拆世界时场景还在，不能等服务 OnDestroy。
            if (GameEntry.MapItemExtras != null)
                GameEntry.MapItemExtras.Clear();

            if (destroyObjects && _mapItemRoot != null)
            {
                DestroyUnlinked(_mapItemRoot);
                _mapItemRoot = null;
            }

            DropHandle(ref _mapItemHandle, destroyObjects);

            _waterProbe.Clear();

            DropHandle(ref _spawnPoseHandle, destroyObjects);
            _spawnPoseAsset = null;

            if (destroyObjects && _inlandWaterInstance != null)
            {
                DestroyUnlinked(_inlandWaterInstance);
                _inlandWaterInstance = null;
            }

            if (destroyObjects && _oceanInstance != null)
            {
                DestroyUnlinked(_oceanInstance);
                _oceanInstance = null;
            }

            GameEntry.UnbindGroundFit();
            if (_terrainInstance != null)
            {
                if (destroyObjects)
                    DestroyUnlinked(_terrainInstance);
                _terrainInstance = null;
            }

            DropHandle(ref _inlandWaterHandle, destroyObjects);
            DropChildHandles(destroyObjects);
            DropHandle(ref _oceanHandle, destroyObjects);
            DropHandle(ref _terrainHandle, destroyObjects);
            _extrasCache.Clear();

            AtmospherePropService props = GameEntry.AtmosphereProps;
            props?.ClearRuntime();
            FireController.ReleaseSharedFlameMaterial();

            // 离岛时必须把静态标志复位：否则下一次进岛时，音频侧会误判“水体已就绪”，
            // 直接沿用上一次场景的过期探针。
            if (InlandWaterReady)
            {
                InlandWaterReady = false;
                OnInlandWaterReady?.Invoke(false);
            }

            _loaded = false;
            IsNearReady = false;
        }

        /// <summary>
        /// destroyObjects 为真：实例已经拆掉，当场 Release 并从登记摘掉。
        /// 场景卸载：只丢掉字段，句柄留在 <see cref="IslandLeaveHandles"/>，等卸场景成功。
        /// </summary>
        static void DropHandle(ref AssetHandle handle, bool releaseNow)
        {
            if (releaseNow)
                GameEntry.ReleaseLeaveHandle(handle);
            handle = null;
        }

        void DropChildHandles(bool releaseNow)
        {
            if (releaseNow)
            {
                for (int i = 0; i < _inlandChildHandles.Count; i++)
                    GameEntry.ReleaseLeaveHandle(_inlandChildHandles[i]);
            }

            _inlandChildHandles.Clear();
        }

        /// <summary>本服务拆世界时用：先摘掉父节点再立刻销毁。场景卸载不进这里。</summary>
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

        /// <summary>泄漏对照在进岛之后补开地形、海、河或草。</summary>
        public IEnumerator ActivateModule(IslandLeakProbe.Module module)
        {
            switch (module)
            {
                case IslandLeakProbe.Module.Terrain:
                    yield return LoadTerrain(fatal: false);
                    break;
                case IslandLeakProbe.Module.Ocean:
                    yield return LoadOcean(fatal: false);
                    break;
                case IslandLeakProbe.Module.Inland:
                    yield return SpawnInlandWater();
                    InlandWaterReady = _inlandWaterInstance != null;
                    OnInlandWaterReady?.Invoke(_inlandWaterInstance != null);
                    break;
                case IslandLeakProbe.Module.Grass:
                    IslandGrassService grass = GameEntry.Grass ?? IslandGrassService.Ensure();
                    yield return grass.Load();
                    break;
                default:
                    yield break;
            }

            IslandLeakProbe.LogMemory(IslandLeakProbe.Display(module) + " loaded");
        }

        IEnumerator LoadTerrain(bool fatal)
        {
            if (_terrainInstance != null)
                yield break;

            while (System.Threading.Interlocked.CompareExchange(ref _terrainGate, 1, 0) != 0)
            {
                if (_terrainInstance != null)
                    yield break;
                yield return null;
            }

            try
            {
                if (_terrainInstance != null)
                    yield break;

                yield return YooResourceModule.InstantiateCoroutine(
                    SceneAssets.IslandTerrainLocation,
                    (go, handle) =>
                    {
                        _terrainInstance = go;
                        _terrainHandle = handle;
                    },
                    trackLeave: true);

                if (_terrainInstance == null)
                {
                    Log.Error(
                        "YooAsset missing location '{0}' ({1}). Island entry will not wait.",
                        SceneAssets.IslandTerrainLocation,
                        SceneAssets.IslandTerrainPrefabPath);
                    if (fatal)
                    {
                        Release();
                        LoadFailed = true;
                    }

                    yield break;
                }

                GameEntry.BindGroundFit(_terrainInstance);
                IslandMobileTune.ApplyAfterTerrainSpawn(_terrainInstance);
            }
            finally
            {
                System.Threading.Interlocked.Exchange(ref _terrainGate, 0);
            }
        }

        IEnumerator LoadOcean(bool fatal)
        {
            if (_oceanInstance != null)
                yield break;

            while (System.Threading.Interlocked.CompareExchange(ref _oceanGate, 1, 0) != 0)
            {
                if (_oceanInstance != null)
                    yield break;
                yield return null;
            }

            try
            {
                if (_oceanInstance != null)
                    yield break;

                yield return YooResourceModule.InstantiateCoroutine(
                    SceneAssets.IslandOceanLocation,
                    (go, handle) =>
                    {
                        _oceanInstance = go;
                        _oceanHandle = handle;
                    },
                    trackLeave: true);

                if (_oceanInstance == null)
                {
                    Log.Error(
                        "YooAsset missing location '{0}' ({1}). Island entry will not wait.",
                        SceneAssets.IslandOceanLocation,
                        SceneAssets.IslandOceanPrefabPath);
                    if (fatal)
                    {
                        Release();
                        LoadFailed = true;
                    }
                }
            }
            finally
            {
                System.Threading.Interlocked.Exchange(ref _oceanGate, 0);
            }
        }

        /// <summary>
        /// Loads <see cref="IslandSpawnPoseAsset"/> so <see cref="IslandSpawnPose"/> serves
        /// configured values instead of built-in defaults. Missing asset is not fatal:
        /// <see cref="IslandSpawnPose"/> falls back to its hardcoded pose.
        /// </summary>
        IEnumerator LoadSpawnPose()
        {
            DropHandle(ref _spawnPoseHandle, releaseNow: true);

            IslandSpawnPoseAsset asset = null;
            if (YooResourceModule.DefaultPackage != null
                && YooResourceModule.DefaultPackage.CheckLocationValid(
                    SceneAssets.IslandSpawnPoseLocation))
            {
                yield return YooResourceModule.LoadAssetCoroutine<IslandSpawnPoseAsset>(
                    SceneAssets.IslandSpawnPoseLocation,
                    (loaded, handle) =>
                    {
                        asset = loaded;
                        _spawnPoseHandle = handle;
                    },
                    trackLeave: true);
            }

#if UNITY_EDITOR
            if (asset == null)
            {
                asset = UnityEditor.AssetDatabase.LoadAssetAtPath<IslandSpawnPoseAsset>(
                    SceneAssets.IslandSpawnPoseAssetPath);
            }
#endif

            _spawnPoseAsset = asset;
            if (asset == null)
            {
                Log.Warning(
                    "[IslandWorld] IslandSpawnPose config missing — using built-in default pose");
            }
            (Vector3 pos, float yaw) = IslandSpawnPose.Resolve(asset);
            Log.Info(
                "[IslandSpawnPose] configured=" + (asset != null) +
                " derived=" + (asset != null && asset.DeriveFromPreset) +
                " pos=" + pos + " yaw=" + yaw.ToString("F2"));
        }

        IEnumerator SpawnInlandWater()
        {
            if (_inlandWaterInstance != null)
            {
                UnityEngine.Object.Destroy(_inlandWaterInstance);
                _inlandWaterInstance = null;
            }

            DropHandle(ref _inlandWaterHandle, releaseNow: true);
            DropChildHandles(releaseNow: true);

            if (YooResourceModule.DefaultPackage != null
                && YooResourceModule.DefaultPackage.CheckLocationValid(SceneAssets.IslandInlandWaterLocation))
            {
                yield return YooResourceModule.InstantiateCoroutine(
                    SceneAssets.IslandInlandWaterLocation,
                    (go, handle) =>
                    {
                        _inlandWaterInstance = go;
                        _inlandWaterHandle = handle;
                    },
                    trackLeave: true);
            }

#if UNITY_EDITOR
            if (_inlandWaterInstance == null)
            {
                var prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(
                    SceneAssets.IslandInlandWaterPrefabPath);
                if (prefab != null)
                    _inlandWaterInstance = UnityEngine.Object.Instantiate(prefab);
            }
#endif

            if (_inlandWaterInstance == null)
                yield return SpawnInlandWaterFromExtras();

            if (_inlandWaterInstance == null)
            {
                Log.Warning(
                    "IslandInlandWater missing. Build Island Inland Water Prefab or MapItemExtras Common/texiao.");
            }
            else
            {
                _inlandWaterInstance.name = "IslandInlandWater";
            }
        }

        IEnumerator SpawnInlandWaterFromExtras()
        {
            if (GameEntry.MapItemExtras != null)
                yield return GameEntry.MapItemExtras.LoadDataCoroutine(_extrasCache);
            var root = new GameObject("IslandInlandWater");
            int placed = 0;

            for (int i = 0; i < _extrasCache.Count; i++)
            {
                MapItemData d = _extrasCache[i];
                if (d.Parent != "Common" && d.Parent != "texiao")
                    continue;

                string location = SceneAssets.DaoYu1Location(d.Parent, d.PrefabName);
                GameObject instance = null;

                if (YooResourceModule.DefaultPackage != null
                    && YooResourceModule.DefaultPackage.CheckLocationValid(location))
                {
                    yield return YooResourceModule.InstantiateCoroutine(
                        location,
                        (go, handle) =>
                        {
                            instance = go;
                            if (handle != null)
                                _inlandChildHandles.Add(handle);
                        },
                        trackLeave: true);
                }

#if UNITY_EDITOR
                if (instance == null)
                {
                    string path = SceneAssets.DaoYu1EditorPath(d.Parent, d.PrefabName);
                    var prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(path);
                    if (prefab != null)
                        instance = UnityEngine.Object.Instantiate(prefab);
                }
#endif

                if (instance == null)
                {
                    Log.Warning("InlandWater piece missing: " + location);
                    continue;
                }

                instance.transform.SetParent(root.transform, false);
                instance.transform.localPosition = d.Pos;
                instance.transform.localEulerAngles = d.Rotation;
                instance.transform.localScale = d.Scale;
                instance.name = d.PrefabName;
                placed++;
            }

            if (placed == 0)
            {
                UnityEngine.Object.Destroy(root);
                yield break;
            }

            _inlandWaterInstance = root;
            Log.Info("[IslandWorld] inland water pieces from MapItemExtras=" + placed);
        }

        IEnumerator SpawnMapItems(System.Action<float, string> reportProgress)
        {
            if (_mapItemRoot != null)
                UnityEngine.Object.Destroy(_mapItemRoot);

            _mapItemRoot = null;
            DropHandle(ref _mapItemHandle, releaseNow: true);

            if (YooResourceModule.DefaultPackage != null
                && YooResourceModule.DefaultPackage.CheckLocationValid(SceneAssets.MapItemLocation))
            {
                yield return YooResourceModule.InstantiateCoroutine(
                    SceneAssets.MapItemLocation,
                    (go, handle) =>
                    {
                        _mapItemRoot = go;
                        _mapItemHandle = handle;
                    },
                    trackLeave: true);
            }

#if UNITY_EDITOR
            if (_mapItemRoot == null)
            {
                var prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(
                    SceneAssets.MapItemPrefabPath);
                if (prefab != null)
                    _mapItemRoot = UnityEngine.Object.Instantiate(prefab);
            }
#endif

            if (_mapItemRoot == null)
                _mapItemRoot = new GameObject("MapItem");
            _mapItemRoot.name = "MapItem";

            _mapItemSpawner = _mapItemRoot.GetComponent<EnvMapItemSpawner>()
                              ?? _mapItemRoot.AddComponent<EnvMapItemSpawner>();

            // 相机已由进岛设位摆好。这里只拿它当流送焦点，不能改 position：
            // 写成 DefaultPos 会把眼睛按回注视点高度，揭罩时看起来像掉下去。
            Transform focus;
            Camera main = Camera.main;
            if (main != null)
            {
                focus = main.transform;
            }
            else
            {
                var focusGo = new GameObject("MapItemFocus");
                focusGo.transform.SetParent(_mapItemRoot.transform, false);
                focusGo.transform.position = IslandSpawnPose.DefaultPos;
                focus = focusGo.transform;
            }

            yield return _mapItemSpawner.Run(focus, reportProgress);
        }

        void OnDestroy()
        {
            bool owner = Instance == this || GameEntry.World == this;
            if (Instance == this)
                Instance = null;
            if (GameEntry.World == this)
                GameEntry.World = null;
            if (!owner)
                return;

            // 流程离岛已在场景还在时 Release(true)。这里只兜没走 BeginLeave 的销毁：不拆实例，句柄留到卸场景成功。
            Release(destroyObjects: false);
            OnInlandWaterReady = null;
        }
    }
}
