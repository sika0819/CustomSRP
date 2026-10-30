using System.Collections;
using System.IO;
using System.Reflection;
using UnityEngine;
using UnityGameFramework.Runtime;
using YooAsset;

namespace Game
{
    /// <summary>
    /// 岛屿 GPU 草地。服务和 OilTerrainGrassBinder 都在 Island 场景里，随场景销毁。
    /// 烘焙句柄在卸场景成功后放开。进程拆世界走 <see cref="Release"/>，场景还在时先拆绘制器。
    /// </summary>
    public sealed class IslandGrassService : MonoBehaviour
    {
        static readonly BindingFlags StaticPublic =
            BindingFlags.Public | BindingFlags.Static;

        AssetHandle _bakeHandle;
        object _binder;
        bool _loaded;
        bool _loading;

        public bool IsLoaded => _loaded;
        public static IslandGrassService Active { get; private set; }

        public static IslandGrassService Ensure()
        {
            IslandGrassService service = IslandServiceHost.Ensure(Active, "IslandGrassService");
            Active = service;
            GameEntry.Grass = service;
            return service;
        }

        void OnEnable()
        {
            Active = this;
            GameEntry.Grass = this;
        }

        void OnDisable()
        {
            if (Active == this)
                Active = null;
            if (GameEntry.Grass == this)
                GameEntry.Grass = null;
        }

        /// <summary>进岛后由 IslandWorldService 调用。</summary>
        public IEnumerator Load()
        {
            if (_loaded || _loading)
                yield break;

            _loading = true;
            try
            {
            byte[] bakeBytes = null;
            yield return LoadBakeBytes(bytes => bakeBytes = bytes);

            if (bakeBytes == null || bakeBytes.Length == 0)
            {
                Log.Error(
                    "[IslandGrass] bake missing — menu CustomSRP/Grass/Bake Island Sidecar " +
                    "(writes AssetRaw/Configs/Island_GrassBake.bytes)");
                yield break;
            }

            _binder = InvokeBootstrap(bakeBytes);
            if (_binder == null)
            {
                Log.Error("[IslandGrass] CustomSRP binder failed (shader/compute?)");
                yield break;
            }

            ApplyMobileBudget(_binder);
            _loaded = true;
            Log.Info($"[IslandGrass] ready bakeBytes={bakeBytes.Length}");
            }
            finally
            {
                _loading = false;
            }
        }

        public void Release()
        {
            _loaded = false;
            DestroyBinder();
            GameEntry.ReleaseLeaveHandle(_bakeHandle);
            _bakeHandle = null;
        }

        void OnDestroy()
        {
            // 场景卸载：句柄留在本轮 IslandLeaveHandles。进程拆世界已在 Release() 里 ReleaseNow。
            _loaded = false;
            _bakeHandle = null;
            if (Active == this)
                Active = null;
        }

        IEnumerator LoadBakeBytes(System.Action<byte[]> onDone)
        {
            TextAsset text = null;

            if (YooResourceModule.IsInitialized && YooResourceModule.DefaultPackage != null
                && YooResourceModule.DefaultPackage.CheckLocationValid(SceneAssets.IslandGrassBakeLocation))
            {
                yield return YooResourceModule.LoadAssetCoroutine<TextAsset>(
                    SceneAssets.IslandGrassBakeLocation,
                    (asset, handle) =>
                    {
                        text = asset;
                        _bakeHandle = handle;
                    },
                    trackLeave: true);
            }

#if UNITY_EDITOR
            if (text == null)
            {
                text = UnityEditor.AssetDatabase.LoadAssetAtPath<TextAsset>(
                    SceneAssets.IslandGrassBakeAssetPath);
            }
#endif

            if (text == null)
            {
                string streamingPath = Path.Combine(
                    Application.streamingAssetsPath, "Island_GrassBake.bytes");
#if UNITY_ANDROID && !UNITY_EDITOR
                using (var req = UnityEngine.Networking.UnityWebRequest.Get(streamingPath))
                {
                    req.timeout = 15;
                    yield return req.SendWebRequest();
                    if (req.result != UnityEngine.Networking.UnityWebRequest.Result.Success)
                    {
                        Log.Error("[IslandGrass] StreamingAssets bake read failed — " + req.error);
                    }
                    else if (req.downloadHandler?.data != null
                        && req.downloadHandler.data.Length > 0)
                    {
                        onDone?.Invoke(req.downloadHandler.data);
                        yield break;
                    }
                }
#else
                if (File.Exists(streamingPath))
                {
                    onDone?.Invoke(File.ReadAllBytes(streamingPath));
                    yield break;
                }
#endif
            }

            onDone?.Invoke(text != null ? text.bytes : null);
        }

        static object InvokeBootstrap(byte[] bakeBytes)
        {
            System.Type bootstrapType = System.Type.GetType(
                "CustomSRP.OilTerrainGrassBootstrap, Assembly-CSharp");
            if (bootstrapType == null)
            {
                Log.Error("[IslandGrass] OilTerrainGrassBootstrap type missing");
                return null;
            }

            MethodInfo ensure = bootstrapType.GetMethod(
                "EnsureConfigured",
                StaticPublic,
                null,
                new[] { typeof(byte[]) },
                null);
            if (ensure == null)
            {
                // Fallback: parameterless then TryLoadBake on binder.
                ensure = bootstrapType.GetMethod("EnsureConfigured", StaticPublic, null, System.Type.EmptyTypes, null);
                if (ensure == null)
                {
                    Log.Error("[IslandGrass] EnsureConfigured missing");
                    return null;
                }

                object binder = ensure.Invoke(null, null);
                TryLoadBakeOnBinder(binder, bakeBytes);
                return binder;
            }

            return ensure.Invoke(null, new object[] { bakeBytes });
        }

        static void TryLoadBakeOnBinder(object binder, byte[] bakeBytes)
        {
            if (binder == null || bakeBytes == null)
                return;
            MethodInfo tryLoad = binder.GetType().GetMethod(
                "TryLoadBake", BindingFlags.Public | BindingFlags.Instance);
            tryLoad?.Invoke(binder, new object[] { bakeBytes });
        }

        static void ApplyMobileBudget(object binder)
        {
            if (binder == null)
                return;
            MethodInfo apply = binder.GetType().GetMethod(
                "ApplyBudget", BindingFlags.Public | BindingFlags.Instance);
            if (apply == null)
                return;

#if UNITY_ANDROID || UNITY_IOS
            // 低档少镂空叶片。闺蜜机标准版再收（不改树）；4K 走下面的低档。
            if (IslandGraphicsTier.BandwidthConservative)
                apply.Invoke(binder, new object[] { 800, 12f, 1 });
            else if (IslandGraphicsTier.GpuTier == IslandGraphicsTier.Tier.Low)
                apply.Invoke(binder, new object[] { 8000, 48f, 2 });
            else
                apply.Invoke(binder, new object[] { 24000, 90f, 3 });
#else
            apply.Invoke(binder, new object[] { 48000, 180f, 4 });
#endif
        }

        static void DestroyBinder()
        {
            System.Type binderType = System.Type.GetType(
                "CustomSRP.OilTerrainGrassBinder, Assembly-CSharp");
            if (binderType == null)
                return;

            PropertyInfo prop = binderType.GetProperty("Active", StaticPublic);
            object active = prop?.GetValue(null);
            if (active is not Behaviour behaviour || behaviour == null)
                return;

            // 先还 GPU，再 Destroy。卸岛时 OnDestroy 里的延迟 Destroy 留不住 Mesh / ComputeBuffer。
            MethodInfo releaseGpu = binderType.GetMethod(
                "ReleaseGpu", BindingFlags.Public | BindingFlags.Instance);
            releaseGpu?.Invoke(active, null);

            if (Application.isPlaying)
                Object.Destroy(behaviour.gameObject);
            else
                Object.DestroyImmediate(behaviour.gameObject);
        }
    }
}
