using System;
using System.Collections;
using UnityEngine;
using YooAsset;

using UnityGameFramework.Runtime;
namespace Game
{
    /// <summary>
    /// 轻量 YooAsset 封装。对齐 BoluoUnity ResourceModule 的 Initialize / InitPackage，
    /// 并提供启动下载与运行时按需加载。
    /// </summary>
    public static class YooResourceModule
    {
        public const string DefaultPackageName = "DefaultPackage";

        public static ResourcePackage DefaultPackage { get; private set; }
        public static bool IsInitialized { get; private set; }
        public static EPlayMode ActivePlayMode { get; private set; }
        public static string PackageVersion { get; private set; }
        public static string LastError { get; private set; }

        /// <summary>单次 Yoo 操作超过此时长仍未完成，视为卡死并放弃等待。</summary>
        public const float OperationWaitCapSeconds = 45f;

        /// <summary>
        /// Editor re-enter without Domain Reload leaves YooAssets static state alive.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        public static void ResetSession()
        {
            DefaultPackage = null;
            IsInitialized = false;
            PackageVersion = null;
            LastError = null;
            try
            {
                if (YooAssets.Initialized)
                    YooAssets.Destroy();
            }
            catch (System.Exception e)
            {
                Log.Warning($"[YooAsset] ResetSession: {e.Message}");
            }
        }

        public static void InitializeSystem()
        {
            if (YooAssets.Initialized)
                return;

            YooAssets.Initialize();
            YooAssets.SetOperationSystemMaxTimeSlice(30);
        }

        public static IEnumerator InitDefaultPackageCoroutine(System.Action<bool, string> onDone = null)
        {
            LastError = null;
            if (IsInitialized && DefaultPackage != null)
            {
                onDone?.Invoke(true, null);
                yield break;
            }

            InitializeSystem();

            UpdateSetting updateSetting = UpdateSetting.LoadOrDefault();
            EPlayMode playMode;
#if UNITY_EDITOR
            playMode = YooAssetPlayModePrefs.GetEditorPlayMode();
            Log.Warning($"[YooAsset] Editor Module Used : {playMode}");
#else
            playMode = EPlayMode.OfflinePlayMode;
#endif
            ActivePlayMode = playMode;

            ResourcePackage package = YooAssets.TryGetPackage(DefaultPackageName);
            if (package == null)
                package = YooAssets.CreatePackage(DefaultPackageName);
            YooAssets.SetDefaultPackage(package);
            DefaultPackage = package;

            InitializationOperation initOp = null;
            if (playMode == EPlayMode.EditorSimulateMode)
            {
#if UNITY_EDITOR
                PackageInvokeBuildResult buildResult;
                try
                {
                    buildResult = EditorSimulateModeHelper.SimulateBuild(DefaultPackageName);
                }
                catch (System.Exception e)
                {
                    LastError = e.InnerException?.Message ?? e.Message;
                    Log.Error($"[YooAsset] SimulateBuild failed: {LastError}");
                    IsInitialized = false;
                    onDone?.Invoke(false, LastError);
                    yield break;
                }

                var parameters = new EditorSimulateModeParameters
                {
                    EditorFileSystemParameters =
                        FileSystemParameters.CreateDefaultEditorFileSystemParameters(buildResult.PackageRootDirectory),
                };
                initOp = package.InitializeAsync(parameters);
#else
                LastError = "EditorSimulateMode is editor-only";
                onDone?.Invoke(false, LastError);
                yield break;
#endif
            }
            else if (playMode == EPlayMode.OfflinePlayMode)
            {
                var parameters = new OfflinePlayModeParameters
                {
                    BuildinFileSystemParameters = FileSystemParameters.CreateDefaultBuildinFileSystemParameters(),
                };
                initOp = package.InitializeAsync(parameters);
            }
            else if (playMode == EPlayMode.HostPlayMode)
            {
                string host = updateSetting.GetResDownLoadPath();
                string fallback = updateSetting.GetFallbackResDownLoadPath();
                IRemoteServices remote = new YooRemoteServices(host, fallback);
                var parameters = new HostPlayModeParameters
                {
                    BuildinFileSystemParameters = FileSystemParameters.CreateDefaultBuildinFileSystemParameters(),
                    CacheFileSystemParameters = FileSystemParameters.CreateDefaultCacheFileSystemParameters(remote),
                };
                initOp = package.InitializeAsync(parameters);
            }
            else if (playMode == EPlayMode.WebPlayMode)
            {
                string host = updateSetting.GetResDownLoadPath();
                string fallback = updateSetting.GetFallbackResDownLoadPath();
                IRemoteServices remote = new YooRemoteServices(host, fallback);
                var parameters = new WebPlayModeParameters
                {
                    WebServerFileSystemParameters = FileSystemParameters.CreateDefaultWebServerFileSystemParameters(),
                };
                if (updateSetting.GetLoadResWayWebGL() == LoadResWayWebGL.Remote)
                {
                    parameters.WebRemoteFileSystemParameters =
                        FileSystemParameters.CreateDefaultWebRemoteFileSystemParameters(remote);
                }

                initOp = package.InitializeAsync(parameters);
            }
            else
            {
                LastError = $"Unsupported play mode: {playMode}";
                onDone?.Invoke(false, LastError);
                yield break;
            }

            if (initOp != null)
                yield return WaitUntilDone(() => initOp.IsDone, "InitializeAsync");

            if (initOp == null || initOp.Status != EOperationStatus.Succeed)
            {
                LastError = initOp?.Error ?? "InitializeAsync failed";
                Log.Error($"[YooAsset] Init package failed: {LastError}");
                IsInitialized = false;
                onDone?.Invoke(false, LastError);
                yield break;
            }

            // YooAsset 2.x+: InitializeAsync no longer loads the manifest for any play mode
            // (including EditorSimulate / Offline). Always RequestPackageVersion + UpdatePackageManifest.
            {
                var versionOp = package.RequestPackageVersionAsync();
                yield return WaitUntilDone(() => versionOp.IsDone, "RequestPackageVersion");

                if (versionOp.Status != EOperationStatus.Succeed)
                {
                    LastError = versionOp.Error;
                    Log.Error($"[YooAsset] RequestPackageVersion failed: {LastError}");
                    IsInitialized = false;
                    onDone?.Invoke(false, LastError);
                    yield break;
                }

                PackageVersion = versionOp.PackageVersion;
                var manifestOp = package.UpdatePackageManifestAsync(PackageVersion);
                yield return WaitUntilDone(() => manifestOp.IsDone, "UpdatePackageManifest");

                if (manifestOp.Status != EOperationStatus.Succeed)
                {
                    LastError = manifestOp.Error;
                    Log.Error($"[YooAsset] UpdatePackageManifest failed: {LastError}");
                    IsInitialized = false;
                    onDone?.Invoke(false, LastError);
                    yield break;
                }
            }

            if (!package.PackageValid)
            {
                LastError = "Package has no active manifest (collector empty or StreamingAssets missing).";
                Log.Error($"[YooAsset] {LastError}");
                IsInitialized = false;
                onDone?.Invoke(false, LastError);
                yield break;
            }

            IsInitialized = true;
            Log.Info($"[YooAsset] Init ok package={DefaultPackageName} mode={playMode} version={PackageVersion}");
            onDone?.Invoke(true, null);
        }

        /// <summary>
        /// Host/Web only: download pending remote bundles. EditorSimulate / Offline skip (builtin).
        /// </summary>
        public static IEnumerator DownloadDefaultPackageCoroutine(
            System.Action<float, string> onProgress = null,
            System.Action<bool, string> onDone = null)
        {
            LastError = null;
            if (!IsInitialized || DefaultPackage == null)
            {
                LastError = "Package not initialized";
                onDone?.Invoke(false, LastError);
                yield break;
            }

            // No remote catalog in these modes — CreateResourceDownloader throws without manifest.
            if (ActivePlayMode == EPlayMode.EditorSimulateMode
                || ActivePlayMode == EPlayMode.OfflinePlayMode)
            {
                onProgress?.Invoke(1f, "资源已就绪");
                onDone?.Invoke(true, null);
                yield break;
            }

            if (!DefaultPackage.PackageValid)
            {
                LastError = "Can not found active package manifest";
                Log.Warning($"[YooAsset] Skip download: {LastError}");
                onDone?.Invoke(false, LastError);
                yield break;
            }

            ResourceDownloaderOperation downloader = DefaultPackage.CreateResourceDownloader(10, 3);
            if (downloader == null || downloader.TotalDownloadCount == 0)
            {
                onProgress?.Invoke(1f, "资源已就绪");
                onDone?.Invoke(true, null);
                yield break;
            }

            onProgress?.Invoke(0f,
                $"下载资源 0/{downloader.TotalDownloadCount}");

            downloader.DownloadUpdateCallback = data =>
            {
                float p = data.TotalDownloadBytes > 0
                    ? (float)data.CurrentDownloadBytes / data.TotalDownloadBytes
                    : 0f;
                onProgress?.Invoke(
                    Mathf.Clamp01(p),
                    $"下载资源 {data.CurrentDownloadCount}/{data.TotalDownloadCount}");
            };

            downloader.BeginDownload();
            float stallAt = Time.realtimeSinceStartup;
            long lastBytes = downloader.CurrentDownloadBytes;
            const float stallCapSeconds = 60f;
            while (!downloader.IsDone)
            {
                long bytes = downloader.CurrentDownloadBytes;
                if (bytes != lastBytes)
                {
                    lastBytes = bytes;
                    stallAt = Time.realtimeSinceStartup;
                }
                else if (Time.realtimeSinceStartup - stallAt >= stallCapSeconds)
                {
                    LastError = "download stalled " + stallCapSeconds.ToString("0")
                        + "s bytes=" + lastBytes;
                    Log.Error("[YooAsset] " + LastError + " — stop waiting so boot can continue");
                    break;
                }

                yield return null;
            }

            if (downloader.Status != EOperationStatus.Succeed)
            {
                LastError = downloader.Error;
                Log.Error($"[YooAsset] Download failed: {LastError}");
                onDone?.Invoke(false, LastError);
                yield break;
            }

            onProgress?.Invoke(1f, "下载完成");
            onDone?.Invoke(true, null);
        }

        /// <summary>
        /// Release unused AB providers (ref-count 0). Fire-and-forget; safe mid-session.
        /// Distinct from GF <c>Resources.UnloadUnusedAssets</c>.
        /// </summary>
        public static void UnloadUnusedAssets(int loopCount = 10)
        {
            if (!IsInitialized || DefaultPackage == null)
                return;
            DefaultPackage.UnloadUnusedAssetsAsync(loopCount);
        }

        /// <summary>
        /// 等 Yoo 卸完 ref=0 的 provider。退登后 / Prefill 前调用；勿与二次 Prefill 叠帧。
        /// </summary>
        public static IEnumerator UnloadUnusedAssetsCoroutine(int loopCount = 10)
        {
            if (!IsInitialized || DefaultPackage == null)
                yield break;
            UnloadUnusedAssetsOperation op = DefaultPackage.UnloadUnusedAssetsAsync(loopCount);
            if (op == null)
                yield break;
            yield return op;
            Log.Info(
                $"[YooAsset] UnloadUnused status={op.Status} destroyedLoaders={op.DestroyedLoaderCount}");
        }

        /// <summary>
        /// Load an addressed asset. Caller owns the handle until Release.
        /// <paramref name="trackLeave"/> 在 LoadAssetAsync 返回后立刻登记，协程被停掉也能在卸场景成功后放开。
        /// </summary>
        public static IEnumerator LoadAssetCoroutine<T>(
            string location,
            System.Action<T, AssetHandle> onDone,
            bool trackLeave = false) where T : UnityEngine.Object
        {
            LastError = null;
            if (!IsInitialized || DefaultPackage == null)
            {
                LastError = "Package not initialized";
                onDone?.Invoke(null, null);
                yield break;
            }

            if (!DefaultPackage.CheckLocationValid(location))
            {
                LastError = $"Invalid location: {location}";
                Log.Error($"[YooAsset] {LastError}");
                onDone?.Invoke(null, null);
                yield break;
            }

            Log.Info($"[YooAsset] handle enter loc={location}");
            AssetHandle handle = DefaultPackage.LoadAssetAsync<T>(location);
            if (trackLeave)
                GameEntry.TrackLeaveHandle(handle);
            Log.Info($"[YooAsset] handle created loc={location} done={handle.IsDone}");
            // 不要 yield return handle。Handle.Current 是 Provider（也是 IEnumerator），
            // Unity 会嵌套抽干它；Provider 没完成时 MoveNext 一直为 true，主线程不再渲染。
            yield return WaitUntilDone(() => handle.IsDone, "LoadAsset " + location);
            Log.Info($"[YooAsset] handle resume loc={location} status={handle.Status} done={handle.IsDone}");

            if (!handle.IsDone || handle.Status != EOperationStatus.Succeed)
            {
                if (handle.IsDone)
                    LastError = handle.LastError;
                else if (string.IsNullOrEmpty(LastError))
                    LastError = "LoadAsset timed out";
                Log.Error($"[YooAsset] Load failed location={location}: {LastError}");
                onDone?.Invoke(null, handle);
                yield break;
            }

            onDone?.Invoke(handle.AssetObject as T, handle);
        }

        /// <summary>
        /// 轮询到完成或墙钟上限。超时打 Error 并返回，调用方按失败继续，避免进岛协程永远停在 yield。
        /// </summary>
        public static IEnumerator WaitUntilDone(Func<bool> isDone, string what, float capSeconds = OperationWaitCapSeconds)
        {
            if (isDone == null || isDone())
                yield break;

            float start = Time.realtimeSinceStartup;
            float cap = capSeconds > 0f ? capSeconds : OperationWaitCapSeconds;
            while (!isDone())
            {
                if (Time.realtimeSinceStartup - start >= cap)
                {
                    LastError = what + " timed out after " + cap.ToString("0") + "s";
                    Log.Error("[YooAsset] " + LastError + " — stop waiting so island entry can continue");
                    yield break;
                }

                yield return null;
            }
        }

        /// <summary>Load + InstantiateSync; caller owns the handle until the instance is destroyed.</summary>
        public static IEnumerator InstantiateCoroutine(
            string location,
            System.Action<GameObject, AssetHandle> onDone,
            bool trackLeave = false)
        {
            GameObject prefab = null;
            AssetHandle handle = null;
            yield return LoadAssetCoroutine<GameObject>(location, (asset, h) =>
            {
                prefab = asset;
                handle = h;
            }, trackLeave);

            if (prefab == null)
            {
                onDone?.Invoke(null, handle);
                yield break;
            }

            GameObject instance = handle.InstantiateSync();
            if (instance != null)
                instance.name = prefab.name;
            onDone?.Invoke(instance, handle);
        }
    }
}
