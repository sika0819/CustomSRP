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
        readonly List<AssetHandle> _inlandChildHandles = new List<AssetHandle>(8);
        readonly List<MapItemData> _extrasCache = new List<MapItemData>(64);
        bool _loaded;

        public bool IsLoaded => _loaded;
        public EnvMapItemSpawner MapItemSpawner => _mapItemSpawner;
        public GameObject TerrainInstance => _terrainInstance;

        public static IslandWorldService Ensure()
        {
            IslandWorldService existing = FindAnyObjectByType<IslandWorldService>();
            if (existing != null)
                return existing;

            var go = new GameObject("IslandWorldService");
            DontDestroyOnLoad(go);
            return go.AddComponent<IslandWorldService>();
        }

        public IEnumerator Load(System.Action<float, string> reportProgress)
        {
            if (_loaded)
                yield break;

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

            reportProgress?.Invoke(0.86f, "正在铺装草地...");
            IslandGrassService grass = GameEntry.Grass ?? IslandGrassService.Ensure();
            yield return grass.Load();

            _loaded = true;
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
                Object.Destroy(_mapItemRoot);
                _mapItemRoot = null;
            }

            _mapItemHandle?.Release();
            _mapItemHandle = null;

            if (_inlandWaterInstance != null)
            {
                Object.Destroy(_inlandWaterInstance);
                _inlandWaterInstance = null;
            }

            if (_oceanInstance != null)
            {
                Object.Destroy(_oceanInstance);
                _oceanInstance = null;
            }

            if (_terrainInstance != null)
            {
                Object.Destroy(_terrainInstance);
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
            _loaded = false;
        }

        IEnumerator SpawnInlandWater()
        {
            if (_inlandWaterInstance != null)
            {
                Object.Destroy(_inlandWaterInstance);
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
                    _inlandWaterInstance = Object.Instantiate(prefab);
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
                        instance = Object.Instantiate(prefab);
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
                Object.Destroy(root);
                yield break;
            }

            _inlandWaterInstance = root;
            Log.Info("[IslandWorld] inland water pieces from MapItemExtras=" + placed);
        }

        IEnumerator SpawnMapItems(System.Action<float, string> reportProgress)
        {
            if (_mapItemRoot != null)
                Object.Destroy(_mapItemRoot);

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
                    _mapItemRoot = Object.Instantiate(prefab);
            }
#endif

            if (_mapItemRoot == null)
                _mapItemRoot = new GameObject("MapItem");
            _mapItemRoot.name = "MapItem";

            _mapItemSpawner = _mapItemRoot.GetComponent<EnvMapItemSpawner>()
                              ?? _mapItemRoot.AddComponent<EnvMapItemSpawner>();

            Transform focus = null;
            Camera main = Camera.main;
            if (main != null)
            {
                main.transform.position = IslandSpawnPose.DefaultPos;
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
