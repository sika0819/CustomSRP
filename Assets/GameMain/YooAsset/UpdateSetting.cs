using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Game
{
    public enum UpdateStyle
    {
        Force = 1,
        Optional = 2,
    }

    public enum UpdateNotice
    {
        Notice = 1,
        NoNotice = 2,
    }

    public enum LoadResWayWebGL
    {
        Remote,
        StreamingAssets,
    }

    /// <summary>
    /// 热更 / 资源下载设置。对齐 BoluoUnity UpdateSetting（暂不含 HybridCLR 强制开关）。
    /// </summary>
    [CreateAssetMenu(menuName = "Game/UpdateSetting", fileName = "UpdateSetting")]
    public class UpdateSetting : ScriptableObject
    {
        [SerializeField]
        string projectName = "CustomSRP";

        [Header("Hot update assemblies (HybridCLR, optional)")]
        public List<string> HotUpdateAssemblies = new List<string>();

        [Header("AOT meta assemblies (HybridCLR, optional)")]
        public List<string> AOTMetaAssemblies = new List<string>
        {
            "mscorlib.dll",
            "System.dll",
            "System.Core.dll",
            "UniTask.dll",
            "YooAsset.dll",
            "UnityEngine.CoreModule.dll",
        };

        public string LogicMainDllName = "Game.dll";
        public string AssemblyTextAssetExtension = ".bytes";
        public string AssemblyTextAssetPath = "AssetRaw/DLL";

        [Header("更新设置")]
        public UpdateStyle UpdateStyle = UpdateStyle.Force;
        public UpdateNotice UpdateNotice = UpdateNotice.Notice;

        [SerializeField]
        string ResDownLoadPath = "http://127.0.0.1:8081";

        [SerializeField]
        string FallbackResDownLoadPath = "http://127.0.0.1:8082";

        [Header("WebGL设置")]
        [SerializeField]
        LoadResWayWebGL loadResWayWebGL = LoadResWayWebGL.Remote;

        [Header("构建资源设置")]
        [SerializeField]
        bool isAutoAssetCopyToBuildAddress;

        [SerializeField]
        string BuildAddress = "../../Builds/Unity_Data/StreamingAssets";

        [SerializeField, Tooltip("是否使用可寻址资源代替资源路径")]
        bool ReplaceAssetPathWithAddress;

        public string ProjectName => projectName;

        public bool IsAutoAssetCopyToBuildAddress() => isAutoAssetCopyToBuildAddress;

        public string GetBuildAddress() => BuildAddress;

        public bool GetReplaceAssetPathWithAddress() => ReplaceAssetPathWithAddress;

        public LoadResWayWebGL GetLoadResWayWebGL() => loadResWayWebGL;

        public string GetResDownLoadPath()
        {
            return Path.Combine(ResDownLoadPath, projectName, GetPlatformName()).Replace("\\", "/");
        }

        public string GetFallbackResDownLoadPath()
        {
            return Path.Combine(FallbackResDownLoadPath, projectName, GetPlatformName()).Replace("\\", "/");
        }

        public static string GetPlatformName()
        {
#if UNITY_ANDROID
            return "Android";
#elif UNITY_IOS
            return "IOS";
#elif UNITY_WEBGL
            return "WebGL";
#else
            switch (Application.platform)
            {
                case RuntimePlatform.WindowsEditor:
                case RuntimePlatform.WindowsPlayer:
                    return "Windows64";
                case RuntimePlatform.OSXEditor:
                case RuntimePlatform.OSXPlayer:
                    return "MacOS";
                case RuntimePlatform.IPhonePlayer:
                    return "IOS";
                case RuntimePlatform.Android:
                    return "Android";
                case RuntimePlatform.WebGLPlayer:
                    return "WebGL";
                default:
                    throw new NotSupportedException($"Platform '{Application.platform}' is not supported.");
            }
#endif
        }

        static UpdateSetting _cached;

        public static UpdateSetting LoadOrDefault()
        {
            if (_cached != null)
                return _cached;

            _cached = Resources.Load<UpdateSetting>("UpdateSetting");
            if (_cached == null)
                _cached = CreateInstance<UpdateSetting>();
            return _cached;
        }
    }
}
