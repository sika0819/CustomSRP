using System;
using System.Collections.Generic;
using GameFramework;
using GameFramework.Download;
using GameFramework.FileSystem;
using GameFramework.ObjectPool;
using GameFramework.Resource;
using UnityEngine;

namespace Game
{
    /// <summary>
    /// Sound-only IResourceManager: synthesizes <see cref="WanosSoundAsset"/> from "Bank/Event" names.
    /// </summary>
    public sealed class WanosSoundResourceManager : IResourceManager
    {
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

#pragma warning disable 0067
        public event EventHandler<ResourceVerifyStartEventArgs> ResourceVerifyStart;
        public event EventHandler<ResourceVerifySuccessEventArgs> ResourceVerifySuccess;
        public event EventHandler<ResourceVerifyFailureEventArgs> ResourceVerifyFailure;
        public event EventHandler<ResourceApplyStartEventArgs> ResourceApplyStart;
        public event EventHandler<ResourceApplySuccessEventArgs> ResourceApplySuccess;
        public event EventHandler<ResourceApplyFailureEventArgs> ResourceApplyFailure;
        public event EventHandler<ResourceUpdateStartEventArgs> ResourceUpdateStart;
        public event EventHandler<ResourceUpdateChangedEventArgs> ResourceUpdateChanged;
        public event EventHandler<ResourceUpdateSuccessEventArgs> ResourceUpdateSuccess;
        public event EventHandler<ResourceUpdateFailureEventArgs> ResourceUpdateFailure;
        public event EventHandler<ResourceUpdateAllCompleteEventArgs> ResourceUpdateAllComplete;
#pragma warning restore 0067

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
            int versionListCompressedHashCode, UpdateVersionListCallbacks updateVersionListCallbacks) { }
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
            => WanosSoundAsset.TryParse(assetName, out _)
                ? HasAssetResult.AssetOnDisk
                : HasAssetResult.NotExist;

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
                return;

            if (WanosSoundAsset.TryParse(assetName, out WanosSoundAsset asset))
            {
                loadAssetCallbacks.LoadAssetSuccessCallback?.Invoke(assetName, asset, 0f, userData);
                return;
            }

            loadAssetCallbacks.LoadAssetFailureCallback?.Invoke(
                assetName, LoadResourceStatus.NotExist,
                "Expected Wanos asset name 'Bank/Event'.", userData);
        }

        public void UnloadAsset(object asset) { }

        public void LoadScene(string sceneAssetName, LoadSceneCallbacks loadSceneCallbacks)
            => FailScene(sceneAssetName, loadSceneCallbacks, null);
        public void LoadScene(string sceneAssetName, int priority, LoadSceneCallbacks loadSceneCallbacks)
            => FailScene(sceneAssetName, loadSceneCallbacks, null);
        public void LoadScene(string sceneAssetName, LoadSceneCallbacks loadSceneCallbacks, object userData)
            => FailScene(sceneAssetName, loadSceneCallbacks, userData);
        public void LoadScene(string sceneAssetName, int priority, LoadSceneCallbacks loadSceneCallbacks, object userData)
            => FailScene(sceneAssetName, loadSceneCallbacks, userData);

        static void FailScene(string sceneAssetName, LoadSceneCallbacks cbs, object userData)
        {
            cbs?.LoadSceneFailureCallback?.Invoke(
                sceneAssetName, LoadResourceStatus.AssetError, "Scene load not used for Sound.", userData);
        }

        public void UnloadScene(string sceneAssetName, UnloadSceneCallbacks unloadSceneCallbacks)
            => unloadSceneCallbacks?.UnloadSceneSuccessCallback?.Invoke(sceneAssetName, null);
        public void UnloadScene(string sceneAssetName, UnloadSceneCallbacks unloadSceneCallbacks, object userData)
            => unloadSceneCallbacks?.UnloadSceneSuccessCallback?.Invoke(sceneAssetName, userData);

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
