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
        bool _loaded;

        /// <summary>Fires once after inland water is loaded (or confirmed missing). Safe to poll for null.</summary>
        public static event Action<bool> OnInlandWaterReady;

        /// <summary>
        /// 共享标记：InlandWaterInstance 是否已完成加载（含两种路径：资源实例化 或 MapItemExtras 拼装）。
        /// 用于 AmbienceZoneService 在事件错过时也能确认 water 已就绪。
        /// </summary>
        public static bool InlandWaterReady { get; private set; }

        /// <summary>单例入口，供 <see cref="WaterSurfaceProbe"/> 等 static 类访问。</summary>
        public static IslandWorldService Instance { get; private set; }

        public bool IsLoaded => _loaded;

        /// <summary>
        /// 近场（地形 / 海 / 地图物件）已铺完。草和后续流送仍可在 <see cref="Load"/> 里继续。
        /// </summary>
        public bool IsNearReady { get; private set; }
        public EnvMapItemSpawner MapItemSpawner => _mapItemSpawner;
        public GameObject TerrainInstance => _terrainInstance;
        public GameObject OceanInstance => _oceanInstance;
        public GameObject InlandWaterInstance => _inlandWaterInstance;

        public static IslandWorldService Ensure()
        {
            IslandWorldService existing = FindAnyObjectByType<IslandWorldService>();
            if (existing != null)
            {
                Instance = existing;
                return existing;
            }

            var go = new GameObject("IslandWorldService");
            DontDestroyOnLoad(go);
            return go.AddComponent<IslandWorldService>();
        }

        void Awake()
        {
            if (Instance != null && Instance != this)
                Destroy(gameObject);
            Instance = this;
        }

        public IEnumerator Load(System.Action<float, string> reportProgress)
        {
            if (_loaded)
                yield break;

            // 出生点配置最先加载：SpawnMapItems / ApplySpawnPose 都要读它。
            yield return LoadSpawnPose();

            reportProgress?.Invoke(0.72f, "正在加载地形...");
            yield return YooResourceModule.InstantiateCoroutine(
                SceneAssets.IslandTerrainLocation,
                (go, handle) =>
                {
                    _terrainInstance = go;
                    _terrainHandle = handle;
                });

            if (_terrainInstance == null)
            {
                Log.Error(
                    "YooAsset missing location '{0}' ({1}).",
                    SceneAssets.IslandTerrainLocation,
                    SceneAssets.IslandTerrainPrefabPath);
                Release();
                yield break;
            }

            reportProgress?.Invoke(0.78f, "正在加载海洋...");
            yield return YooResourceModule.InstantiateCoroutine(
                SceneAssets.IslandOceanLocation,
                (go, handle) =>
                {
                    _oceanInstance = go;
                    _oceanHandle = handle;
                });

            if (_oceanInstance == null)
            {
                Log.Error(
                    "YooAsset missing location '{0}' ({1}).",
                    SceneAssets.IslandOceanLocation,
                    SceneAssets.IslandOceanPrefabPath);
                Release();
                yield break;
            }

            IslandMobileTune.ApplyAfterTerrainSpawn(_terrainInstance);

            reportProgress?.Invoke(0.79f, "正在加载河流瀑布...");
            yield return SpawnInlandWater();

            reportProgress?.Invoke(0.80f, "正在铺装环境...");
            yield return SpawnMapItems(reportProgress);
            IsNearReady = _terrainInstance != null;

            reportProgress?.Invoke(0.86f, "正在铺装草地...");
            IslandGrassService grass = GameEntry.Grass ?? IslandGrassService.Ensure();
            yield return grass.Load();

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

        public void Release()
        {
            (GameEntry.Grass ?? IslandGrassService.Active)?.Release();

            if (_mapItemSpawner != null)
            {
                _mapItemSpawner.Release();
                _mapItemSpawner = null;
            }

            if (_mapItemRoot != null)
            {
                UnityEngine.Object.Destroy(_mapItemRoot);
                _mapItemRoot = null;
            }

            _mapItemHandle?.Release();
            _mapItemHandle = null;

            // 水体最近点改成运行期采样，场景卸载时清掉缓存。
            WaterSurfaceProbe.Clear();

            if (_spawnPoseHandle != null)
            {
                _spawnPoseHandle.Release();
                _spawnPoseHandle = null;
            }
            IslandSpawnPose.Unload();

            if (_inlandWaterInstance != null)
            {
                UnityEngine.Object.Destroy(_inlandWaterInstance);
                _inlandWaterInstance = null;
            }

            if (_oceanInstance != null)
            {
                UnityEngine.Object.Destroy(_oceanInstance);
                _oceanInstance = null;
            }

            if (_terrainInstance != null)
            {
                DestroyMobileShadowMaterials(_terrainInstance);
                UnityEngine.Object.Destroy(_terrainInstance);
                _terrainInstance = null;
            }

            _inlandWaterHandle?.Release();
            _inlandWaterHandle = null;
            for (int i = 0; i < _inlandChildHandles.Count; i++)
                _inlandChildHandles[i]?.Release();
            _inlandChildHandles.Clear();
            _oceanHandle?.Release();
            _oceanHandle = null;
            _terrainHandle?.Release();
            _terrainHandle = null;
            _extrasCache.Clear();

            AtmospherePropRegistry.Clear();
            CampfireWeatherGate.ClearVisualIgnited();
            CampfireWeatherGate.SetSuppressIgnite(true);

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
        /// (MobileShadow) 是进岛时克隆的地形材质，Destroy(地形) 不会销毁它。
        /// </summary>
        static void DestroyMobileShadowMaterials(GameObject terrainRoot)
        {
            Terrain[] terrains = terrainRoot.GetComponentsInChildren<Terrain>(true);
            for (int i = 0; i < terrains.Length; i++)
            {
                Terrain terrain = terrains[i];
                if (terrain == null)
                    continue;
                Material mat = terrain.materialTemplate;
                if (mat == null || mat.name.IndexOf("(MobileShadow)", System.StringComparison.Ordinal) < 0)
                    continue;
                terrain.materialTemplate = null;
                UnityEngine.Object.Destroy(mat);
            }
        }

        /// <summary>
        /// Loads <see cref="IslandSpawnPoseAsset"/> so <see cref="IslandSpawnPose"/> serves
        /// configured values instead of built-in defaults. Missing asset is not fatal:
        /// <see cref="IslandSpawnPose"/> falls back to its hardcoded pose.
        /// </summary>
        IEnumerator LoadSpawnPose()
        {
            _spawnPoseHandle?.Release();
            _spawnPoseHandle = null;

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
                    });
            }

#if UNITY_EDITOR
            if (asset == null)
            {
                asset = UnityEditor.AssetDatabase.LoadAssetAtPath<IslandSpawnPoseAsset>(
                    SceneAssets.IslandSpawnPoseAssetPath);
            }
#endif

            if (asset != null)
            {
                IslandSpawnPose.Load(asset);
            }
            else
            {
                IslandSpawnPose.Load(null);
                Log.Warning(
                    "[IslandWorld] IslandSpawnPose config missing — using built-in default pose");
            }
        }

        IEnumerator SpawnInlandWater()
        {
            if (_inlandWaterInstance != null)
            {
                UnityEngine.Object.Destroy(_inlandWaterInstance);
                _inlandWaterInstance = null;
            }

            _inlandWaterHandle?.Release();
            _inlandWaterHandle = null;
            for (int i = 0; i < _inlandChildHandles.Count; i++)
                _inlandChildHandles[i]?.Release();
            _inlandChildHandles.Clear();

            if (YooResourceModule.DefaultPackage != null
                && YooResourceModule.DefaultPackage.CheckLocationValid(SceneAssets.IslandInlandWaterLocation))
            {
                yield return YooResourceModule.InstantiateCoroutine(
                    SceneAssets.IslandInlandWaterLocation,
                    (go, handle) =>
                    {
                        _inlandWaterInstance = go;
                        _inlandWaterHandle = handle;
                    });
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
            yield return EnvMapItemExtras.LoadDataCoroutine(_extrasCache);
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
                        });
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
            _mapItemHandle?.Release();
            _mapItemHandle = null;

            if (YooResourceModule.DefaultPackage != null
                && YooResourceModule.DefaultPackage.CheckLocationValid(SceneAssets.MapItemLocation))
            {
                yield return YooResourceModule.InstantiateCoroutine(
                    SceneAssets.MapItemLocation,
                    (go, handle) =>
                    {
                        _mapItemRoot = go;
                        _mapItemHandle = handle;
                    });
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
                focusGo.transform.position = IslandSpawnPose.DefaultPos;
                focus = focusGo.transform;
            }

            yield return _mapItemSpawner.Run(focus, reportProgress);
        }

        void OnDestroy()
        {
            Release();
        }
    }
}
