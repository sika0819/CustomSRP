using System;
using System.Collections;
using System.Collections.Generic;
using GameFramework;
using GameFramework.Download;
using GameFramework.FileSystem;
using GameFramework.ObjectPool;
using GameFramework.Resource;
using UnityEngine;
using YooAsset;
using LoadSceneMode = UnityEngine.SceneManagement.LoadSceneMode;
using LocalPhysicsMode = UnityEngine.SceneManagement.LocalPhysicsMode;
using UnityGameFramework.Runtime;
using Object = UnityEngine.Object;
using Log = UnityGameFramework.Runtime.Log;

#pragma warning disable 0067
namespace Game
{
    /// <summary>
    /// Bridges GameFramework UI/Entity LoadAsset to YooAsset DefaultPackage (device + editor).
    /// </summary>
    public sealed class YooAssetResourceManager : MonoBehaviour, IResourceManager
    {
        readonly Dictionary<object, AssetHandle> _handles = new Dictionary<object, AssetHandle>();
        readonly Dictionary<string, SceneHandle> _sceneHandles = new Dictionary<string, SceneHandle>();
        string _readOnlyPath;
        string _readWritePath;
        string _currentVariant;
        string _updatePrefixUri;
        int _generateReadWriteVersionListLength;
        int _updateRetryCount;
        float _assetAutoReleaseInterval = 60f;
        int _assetCapacity = 64;
        float _assetExpireTime = 60f;
        int _assetPriority;
        float _resourceAutoReleaseInterval = 60f;
        int _resourceCapacity = 16;
        float _resourceExpireTime = 60f;
        int _resourcePriority;

        public static YooAssetResourceManager Instance { get; private set; }

        public static YooAssetResourceManager Ensure(MonoBehaviour host)
        {
            if (host == null)
                return null;
            var existing = host.GetComponent<YooAssetResourceManager>();
            if (existing != null)
            {
                Instance = existing;
                return existing;
            }

            var created = host.gameObject.AddComponent<YooAssetResourceManager>();
            Instance = created;
            return created;
        }

        void Awake() => Instance = this;

        void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
        }

        public string ReadOnlyPath => _readOnlyPath;
        public string ReadWritePath => _readWritePath;
        public ResourceMode ResourceMode => ResourceMode.Package;
        public string CurrentVariant => _currentVariant;
        public PackageVersionListSerializer PackageVersionListSerializer => null;
        public UpdatableVersionListSerializer UpdatableVersionListSerializer => null;
        public ReadOnlyVersionListSerializer ReadOnlyVersionListSerializer => null;
        public ReadWriteVersionListSerializer ReadWriteVersionListSerializer => null;
        public ResourcePackVersionListSerializer ResourcePackVersionListSerializer => null;
        public string ApplicableGameVersion => Application.version;
        public int InternalResourceVersion => 0;
        public int AssetCount => 0;
        public int ResourceCount => 0;
        public int ResourceGroupCount => 0;
        public string UpdatePrefixUri { get => _updatePrefixUri; set => _updatePrefixUri = value; }
        public int GenerateReadWriteVersionListLength
        {
            get => _generateReadWriteVersionListLength;
            set => _generateReadWriteVersionListLength = value;
        }
        public string ApplyingResourcePackPath => null;
        public int ApplyWaitingCount => 0;
        public int UpdateRetryCount { get => _updateRetryCount; set => _updateRetryCount = value; }
        public IResourceGroup UpdatingResourceGroup => null;
        public int UpdateWaitingCount => 0;
        public int UpdateWaitingWhilePlayingCount => 0;
        public int UpdateCandidateCount => 0;
        public int LoadTotalAgentCount => 0;
        public int LoadFreeAgentCount => 0;
        public int LoadWorkingAgentCount => 0;
        public int LoadWaitingTaskCount => 0;
        public float AssetAutoReleaseInterval
        {
            get => _assetAutoReleaseInterval;
            set => _assetAutoReleaseInterval = value;
        }
        public int AssetCapacity { get => _assetCapacity; set => _assetCapacity = value; }
        public float AssetExpireTime { get => _assetExpireTime; set => _assetExpireTime = value; }
        public int AssetPriority { get => _assetPriority; set => _assetPriority = value; }
        public float ResourceAutoReleaseInterval
        {
            get => _resourceAutoReleaseInterval;
            set => _resourceAutoReleaseInterval = value;
        }
        public int ResourceCapacity { get => _resourceCapacity; set => _resourceCapacity = value; }
        public float ResourceExpireTime { get => _resourceExpireTime; set => _resourceExpireTime = value; }
        public int ResourcePriority { get => _resourcePriority; set => _resourcePriority = value; }

        public event EventHandler<GameFramework.Resource.ResourceVerifyStartEventArgs> ResourceVerifyStart;
        public event EventHandler<GameFramework.Resource.ResourceVerifySuccessEventArgs> ResourceVerifySuccess;
        public event EventHandler<GameFramework.Resource.ResourceVerifyFailureEventArgs> ResourceVerifyFailure;
        public event EventHandler<GameFramework.Resource.ResourceApplyStartEventArgs> ResourceApplyStart;
        public event EventHandler<GameFramework.Resource.ResourceApplySuccessEventArgs> ResourceApplySuccess;
        public event EventHandler<GameFramework.Resource.ResourceApplyFailureEventArgs> ResourceApplyFailure;
        public event EventHandler<GameFramework.Resource.ResourceUpdateStartEventArgs> ResourceUpdateStart;
        public event EventHandler<GameFramework.Resource.ResourceUpdateChangedEventArgs> ResourceUpdateChanged;
        public event EventHandler<GameFramework.Resource.ResourceUpdateSuccessEventArgs> ResourceUpdateSuccess;
        public event EventHandler<GameFramework.Resource.ResourceUpdateFailureEventArgs> ResourceUpdateFailure;
        public event EventHandler<GameFramework.Resource.ResourceUpdateAllCompleteEventArgs> ResourceUpdateAllComplete;

        public void SetReadOnlyPath(string readOnlyPath) => _readOnlyPath = readOnlyPath;
        public void SetReadWritePath(string readWritePath) => _readWritePath = readWritePath;
        public void SetResourceMode(ResourceMode resourceMode) { }
        public void SetCurrentVariant(string currentVariant) => _currentVariant = currentVariant;
        public void SetObjectPoolManager(IObjectPoolManager objectPoolManager) { }
        public void SetFileSystemManager(IFileSystemManager fileSystemManager) { }
        public void SetDownloadManager(IDownloadManager downloadManager) { }
        public void SetDecryptResourceCallback(DecryptResourceCallback decryptResourceCallback) { }
        public void SetResourceHelper(IResourceHelper resourceHelper) { }
        public void AddLoadResourceAgentHelper(ILoadResourceAgentHelper loadResourceAgentHelper) { }

        public void InitResources(InitResourcesCompleteCallback initResourcesCompleteCallback)
            => initResourcesCompleteCallback?.Invoke();

        public CheckVersionListResult CheckVersionList(int latestInternalResourceVersion)
            => CheckVersionListResult.Updated;

        public void UpdateVersionList(int versionListLength, int versionListHashCode, int versionListCompressedLength,
            int versionListCompressedHashCode, UpdateVersionListCallbacks updateVersionListCallbacks)
        {
        }

        public void VerifyResources(int verifyResourceLengthPerFrame,
            VerifyResourcesCompleteCallback verifyResourcesCompleteCallback)
            => verifyResourcesCompleteCallback?.Invoke(true);

        public void CheckResources(bool ignoreOtherVariant,
            CheckResourcesCompleteCallback checkResourcesCompleteCallback)
            => checkResourcesCompleteCallback?.Invoke(0, 0, 0, 0L, 0L);

        public void ApplyResources(string resourcePackPath,
            ApplyResourcesCompleteCallback applyResourcesCompleteCallback)
            => applyResourcesCompleteCallback?.Invoke(resourcePackPath, true);

        public void UpdateResources(UpdateResourcesCompleteCallback updateResourcesCompleteCallback)
            => updateResourcesCompleteCallback?.Invoke(null, true);

        public void UpdateResources(string resourceGroupName,
            UpdateResourcesCompleteCallback updateResourcesCompleteCallback)
            => updateResourcesCompleteCallback?.Invoke(null, true);

        public void StopUpdateResources() { }
        public bool VerifyResourcePack(string resourcePackPath) => false;

        public TaskInfo[] GetAllLoadAssetInfos() => Array.Empty<TaskInfo>();
        public void GetAllLoadAssetInfos(List<TaskInfo> results) => results?.Clear();

        public HasAssetResult HasAsset(string assetName)
        {
            if (!YooResourceModule.IsInitialized || YooResourceModule.DefaultPackage == null)
                return HasAssetResult.NotExist;
            return YooResourceModule.DefaultPackage.CheckLocationValid(assetName)
                ? HasAssetResult.AssetOnDisk
                : HasAssetResult.NotExist;
        }

        public void LoadAsset(string assetName, LoadAssetCallbacks loadAssetCallbacks)
            => LoadAsset(assetName, null, 0, loadAssetCallbacks, null);

        public void LoadAsset(string assetName, Type assetType, LoadAssetCallbacks loadAssetCallbacks)
            => LoadAsset(assetName, assetType, 0, loadAssetCallbacks, null);

        public void LoadAsset(string assetName, int priority, LoadAssetCallbacks loadAssetCallbacks)
            => LoadAsset(assetName, null, priority, loadAssetCallbacks, null);

        public void LoadAsset(string assetName, LoadAssetCallbacks loadAssetCallbacks, object userData)
            => LoadAsset(assetName, null, 0, loadAssetCallbacks, userData);

        public void LoadAsset(string assetName, Type assetType, int priority, LoadAssetCallbacks loadAssetCallbacks)
            => LoadAsset(assetName, assetType, priority, loadAssetCallbacks, null);

        public void LoadAsset(string assetName, Type assetType, LoadAssetCallbacks loadAssetCallbacks, object userData)
            => LoadAsset(assetName, assetType, 0, loadAssetCallbacks, userData);

        public void LoadAsset(string assetName, int priority, LoadAssetCallbacks loadAssetCallbacks, object userData)
            => LoadAsset(assetName, null, priority, loadAssetCallbacks, userData);

        public void LoadAsset(string assetName, Type assetType, int priority, LoadAssetCallbacks loadAssetCallbacks,
            object userData)
        {
            if (loadAssetCallbacks == null)
            {
                Log.Error("[YooGF] LoadAsset callbacks invalid: " + assetName);
                return;
            }

            if (string.IsNullOrEmpty(assetName))
            {
                loadAssetCallbacks.LoadAssetFailureCallback?.Invoke(
                    assetName, LoadResourceStatus.NotExist, "Asset name is invalid.", userData);
                return;
            }

            if (!YooResourceModule.IsInitialized || YooResourceModule.DefaultPackage == null)
            {
                Log.Error("[YooGF] Package not ready for: " + assetName);
                loadAssetCallbacks.LoadAssetFailureCallback?.Invoke(
                    assetName, LoadResourceStatus.NotReady, "YooAsset package not initialized.", userData);
                return;
            }

            ResourcePackage package = YooResourceModule.DefaultPackage;
            if (!package.CheckLocationValid(assetName))
            {
                Log.Error("[YooGF] Invalid location: " + assetName);
                loadAssetCallbacks.LoadAssetFailureCallback?.Invoke(
                    assetName, LoadResourceStatus.NotExist,
                    GameFramework.Utility.Text.Format("YooAsset location invalid: '{0}'.", assetName), userData);
                return;
            }

            Log.Info("[YooGF] LoadAsset begin: " + assetName);
            StartCoroutine(LoadAssetRoutine(assetName, assetType, loadAssetCallbacks, userData));
        }

        IEnumerator LoadAssetRoutine(
            string assetName,
            Type assetType,
            LoadAssetCallbacks loadAssetCallbacks,
            object userData)
        {
            float start = Time.realtimeSinceStartup;
            ResourcePackage package = YooResourceModule.DefaultPackage;
            AssetHandle handle;
            try
            {
                if (assetType != null)
                    handle = package.LoadAssetAsync(assetName, assetType);
                else if (assetName.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                    handle = package.LoadAssetAsync<GameObject>(assetName);
                else
                    handle = package.LoadAssetAsync(assetName);
            }
            catch (Exception e)
            {
                Log.Error("[YooGF] LoadAssetAsync threw: " + assetName + " — " + e.Message);
                loadAssetCallbacks.LoadAssetFailureCallback?.Invoke(
                    assetName, LoadResourceStatus.AssetError, e.Message, userData);
                yield break;
            }

            yield return YooResourceModule.WaitUntilDone(
                () => handle.IsDone, "UI LoadAsset " + assetName);
            if (!handle.IsDone)
            {
                string timeout = string.IsNullOrEmpty(YooResourceModule.LastError)
                    ? "timed out"
                    : YooResourceModule.LastError;
                Log.Error("[YooGF] LoadAsset timed out: " + assetName + " — " + timeout);
                loadAssetCallbacks.LoadAssetFailureCallback?.Invoke(
                    assetName, LoadResourceStatus.AssetError, timeout, userData);
                yield break;
            }

            float elapsed = Time.realtimeSinceStartup - start;
            if (handle.Status != EOperationStatus.Succeed || handle.AssetObject == null)
            {
                string err = string.IsNullOrEmpty(handle.LastError) ? "YooAsset load failed." : handle.LastError;
                Log.Error("[YooGF] LoadAsset fail: " + assetName + " — " + err);
                handle.Release();
                loadAssetCallbacks.LoadAssetFailureCallback?.Invoke(
                    assetName, LoadResourceStatus.AssetError, err, userData);
                yield break;
            }

            Object asset = handle.AssetObject;
            _handles[asset] = handle;
            Log.Info("[YooGF] LoadAsset ok: " + assetName + " type=" + asset.GetType().Name);
            try
            {
                loadAssetCallbacks.LoadAssetSuccessCallback?.Invoke(assetName, asset, elapsed, userData);
            }
            catch (Exception e)
            {
                Log.Error("[YooGF] Success callback threw: " + assetName + " — " + e);
            }
        }

        public void UnloadAsset(object asset)
        {
            if (asset == null)
                return;
            if (_handles.TryGetValue(asset, out AssetHandle handle))
            {
                _handles.Remove(asset);
                handle.Release();
            }
        }

        public void LoadScene(string sceneAssetName, LoadSceneCallbacks loadSceneCallbacks)
            => LoadScene(sceneAssetName, 0, loadSceneCallbacks, null);

        public void LoadScene(string sceneAssetName, int priority, LoadSceneCallbacks loadSceneCallbacks)
            => LoadScene(sceneAssetName, priority, loadSceneCallbacks, null);

        public void LoadScene(string sceneAssetName, LoadSceneCallbacks loadSceneCallbacks, object userData)
            => LoadScene(sceneAssetName, 0, loadSceneCallbacks, userData);

        public void LoadScene(string sceneAssetName, int priority, LoadSceneCallbacks loadSceneCallbacks,
            object userData)
        {
            StartCoroutine(LoadSceneCoroutine(sceneAssetName, priority, loadSceneCallbacks, userData));
        }

        IEnumerator LoadSceneCoroutine(string sceneAssetName, int priority, LoadSceneCallbacks callbacks, object userData)
        {
            float started = Time.realtimeSinceStartup;
            if (callbacks == null)
                yield break;

            ResourcePackage package = YooResourceModule.DefaultPackage;
            if (!YooResourceModule.IsInitialized || package == null || !package.CheckLocationValid(sceneAssetName))
            {
                callbacks.LoadSceneFailureCallback?.Invoke(
                    sceneAssetName,
                    LoadResourceStatus.NotExist,
                    "Yoo scene location missing: " + sceneAssetName,
                    userData);
                yield break;
            }

            uint yooPriority = priority > 0 ? (uint)priority : 0u;
            SceneHandle handle = package.LoadSceneAsync(
                sceneAssetName, LoadSceneMode.Additive, LocalPhysicsMode.None, false, yooPriority);
            while (!handle.IsDone)
            {
                callbacks.LoadSceneUpdateCallback?.Invoke(sceneAssetName, handle.Progress, userData);
                yield return null;
            }

            if (handle.Status != EOperationStatus.Succeed)
            {
                string err = string.IsNullOrEmpty(handle.LastError) ? "Yoo LoadScene failed." : handle.LastError;
                handle.Release();
                callbacks.LoadSceneFailureCallback?.Invoke(
                    sceneAssetName, LoadResourceStatus.AssetError, err, userData);
                yield break;
            }

            // 上一趟退登若没放开，这里再进会把引用计数叠上去，包就再也卸不掉。
            ReleaseScene(sceneAssetName);
            _sceneHandles[sceneAssetName] = handle;
            callbacks.LoadSceneSuccessCallback?.Invoke(
                sceneAssetName, Time.realtimeSinceStartup - started, userData);
        }

        /// <summary>
        /// 卸掉 Unity 场景之后必须 Release。本包的 UnloadAsync 只卸场景，不减引用计数。
        /// 句柄留在 provider 里的话，场景包和它依赖的 bundle 会一直占着。
        /// </summary>
        public void ReleaseScene(string sceneAssetName)
        {
            if (string.IsNullOrEmpty(sceneAssetName))
                return;
            if (!_sceneHandles.TryGetValue(sceneAssetName, out SceneHandle handle))
                return;
            _sceneHandles.Remove(sceneAssetName);
            if (handle != null && handle.IsValid)
                handle.Release();
        }

        public void UnloadScene(string sceneAssetName, UnloadSceneCallbacks unloadSceneCallbacks)
            => UnloadScene(sceneAssetName, unloadSceneCallbacks, null);

        public void UnloadScene(string sceneAssetName, UnloadSceneCallbacks unloadSceneCallbacks, object userData)
        {
            StartCoroutine(UnloadSceneCoroutine(sceneAssetName, unloadSceneCallbacks, userData));
        }

        IEnumerator UnloadSceneCoroutine(string sceneAssetName, UnloadSceneCallbacks callbacks, object userData)
        {
            if (callbacks == null)
                yield break;

            if (!_sceneHandles.TryGetValue(sceneAssetName, out SceneHandle handle) || handle == null || !handle.IsValid)
            {
                callbacks.UnloadSceneFailureCallback?.Invoke(sceneAssetName, userData);
                yield break;
            }

            // 先留在字典里。卸失败还能重试；卸成功由调用方 ReleaseScene，避免句柄先被摘掉就再也 Release 不到。
            UnloadSceneOperation operation = handle.UnloadAsync();
            while (!operation.IsDone)
                yield return null;

            if (operation.Status == EOperationStatus.Succeed)
                callbacks.UnloadSceneSuccessCallback?.Invoke(sceneAssetName, userData);
            else
                callbacks.UnloadSceneFailureCallback?.Invoke(sceneAssetName, userData);
        }

        public string GetBinaryPath(string binaryAssetName) => null;

        public bool GetBinaryPath(string binaryAssetName, out bool storageInReadOnly, out bool storageInFileSystem,
            out string relativePath, out string fileName)
        {
            storageInReadOnly = false;
            storageInFileSystem = false;
            relativePath = null;
            fileName = null;
            return false;
        }

        public int GetBinaryLength(string binaryAssetName) => -1;

        public void LoadBinary(string binaryAssetName, LoadBinaryCallbacks loadBinaryCallbacks)
            => LoadBinary(binaryAssetName, loadBinaryCallbacks, null);

        public void LoadBinary(string binaryAssetName, LoadBinaryCallbacks loadBinaryCallbacks, object userData)
        {
            loadBinaryCallbacks?.LoadBinaryFailureCallback?.Invoke(
                binaryAssetName, LoadResourceStatus.AssetError, "Binary load not used.", userData);
        }

        public byte[] LoadBinaryFromFileSystem(string binaryAssetName) => null;
        public int LoadBinaryFromFileSystem(string binaryAssetName, byte[] buffer) => 0;
        public int LoadBinaryFromFileSystem(string binaryAssetName, byte[] buffer, int startIndex) => 0;
        public int LoadBinaryFromFileSystem(string binaryAssetName, byte[] buffer, int startIndex, int length) => 0;
        public byte[] LoadBinarySegmentFromFileSystem(string binaryAssetName, int length) => null;
        public byte[] LoadBinarySegmentFromFileSystem(string binaryAssetName, int offset, int length) => null;
        public int LoadBinarySegmentFromFileSystem(string binaryAssetName, byte[] buffer) => 0;
        public int LoadBinarySegmentFromFileSystem(string binaryAssetName, byte[] buffer, int length) => 0;
        public int LoadBinarySegmentFromFileSystem(string binaryAssetName, byte[] buffer, int startIndex, int length)
            => 0;
        public int LoadBinarySegmentFromFileSystem(string binaryAssetName, int offset, byte[] buffer) => 0;
        public int LoadBinarySegmentFromFileSystem(string binaryAssetName, int offset, byte[] buffer, int length)
            => 0;
        public int LoadBinarySegmentFromFileSystem(string binaryAssetName, int offset, byte[] buffer, int startIndex,
            int length) => 0;

        public bool HasResourceGroup(string resourceGroupName) => false;
        public IResourceGroup GetResourceGroup() => null;
        public IResourceGroup GetResourceGroup(string resourceGroupName) => null;
        public IResourceGroup[] GetAllResourceGroups() => Array.Empty<IResourceGroup>();
        public void GetAllResourceGroups(List<IResourceGroup> results) => results?.Clear();
        public IResourceGroupCollection GetResourceGroupCollection(params string[] resourceGroupNames) => null;
        public IResourceGroupCollection GetResourceGroupCollection(List<string> resourceGroupNames) => null;
    }
}
#pragma warning restore 0067
