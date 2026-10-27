using System.Collections;
using System.IO;
using System.Reflection;
using UnityEngine;
using UnityGameFramework.Runtime;
using YooAsset;

namespace Game
{
    /// <summary>
    /// 岛屿 GPU 草地生命周期（GameFramework Service）。
    /// 渲染实现在 CustomSRP.OilTerrainGrassBinder；本服务只负责 bake 加载 / 预算 / 释放。
    /// </summary>
    public sealed class IslandGrassService : MonoBehaviour
    {
        static readonly BindingFlags StaticPublic =
            BindingFlags.Public | BindingFlags.Static;

        AssetHandle _bakeHandle;
        object _binder;
        bool _loaded;

        public bool IsLoaded => _loaded;
        public static IslandGrassService Active { get; private set; }

        public static IslandGrassService Ensure()
        {
            if (Active != null)
                return Active;

            IslandGrassService existing = FindAnyObjectByType<IslandGrassService>();
            if (existing != null)
            {
                Active = existing;
                return existing;
            }

            var go = new GameObject("IslandGrassService");
            if (Application.isPlaying)
                DontDestroyOnLoad(go);
            Active = go.AddComponent<IslandGrassService>();
            return Active;
        }

        void OnEnable() => Active = this;

        void OnDisable()
        {
            if (Active == this)
                Active = null;
        }

        /// <summary>进岛后由 IslandWorldService 调用。</summary>
        public IEnumerator Load()
        {
            if (_loaded)
                yield break;

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

        public void Release()
        {
            _loaded = false;
            DestroyBinder();
            if (_bakeHandle != null)
            {
                _bakeHandle.Release();
                _bakeHandle = null;
            }
        }

        void OnDestroy()
        {
            Release();
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
                    });
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
            if (active is Behaviour behaviour && behaviour != null)
            {
                if (Application.isPlaying)
                    Object.Destroy(behaviour.gameObject);
                else
                    Object.DestroyImmediate(behaviour.gameObject);
            }
        }
    }
}
