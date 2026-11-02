using System;
using UnityEngine;
using UnityGameFramework.Runtime;

namespace Game.BoluoApi
{
    /// <summary>
    /// 读原生落盘的 AppLaunch 缓存（PlayerPrefs）。不二次 HTTP。
    /// <c>data.feature.cancelAccount</c>：线上下载弹窗开关（1 开 / 2 关），
    /// 与 HTTP <c>POST .../cancelAccount</c> 注销接口无关。
    /// </summary>
    public static class BoluoAppLaunchCache
    {
        public const string ResponseJsonKey = "BoluoHybrid_AppLaunchResponseJson";
        public const string CompleteKey = "BoluoNativeAppLaunchComplete";

        /// <summary>功能开。</summary>
        public const int FeatureOn = 1;

        /// <summary>功能关。</summary>
        public const int FeatureOff = 2;

#if UNITY_EDITOR
        /// <summary>Editor 单测：覆盖 <see cref="IsWifiDownloadConfirmEnabled"/>；null 读 Prefs。</summary>
        public static int? EditorCancelAccountOverride;
#endif

        [Serializable]
        public class AppLaunchRoot
        {
            public int code;
            public string msg;
            public AppLaunchData data;
        }

        [Serializable]
        public class AppLaunchData
        {
            public bool deviceLegal;
            public int storeReview;
            public AppLaunchFeature feature;
            public bool backgroundPlayDisable;
        }

        [Serializable]
        public class AppLaunchFeature
        {
            public int touristLogin;
            public int wyOneClickLogin;
            public int wechatLogin;
            /// <summary>线上下载弹窗（Wi‑Fi 确认）：1 开 / 2 关。</summary>
            public int cancelAccount;
        }

        /// <summary>
        /// 是否插入 Wi‑Fi 下载确认。<c>cancelAccount == 1</c> 为开；
        /// 缺缓存 / 解析失败 / 0 / 2 → 关（静默，对齐现网默认）。
        /// </summary>
        public static bool IsWifiDownloadConfirmEnabled()
        {
#if UNITY_EDITOR
            if (EditorCancelAccountOverride.HasValue)
                return EditorCancelAccountOverride.Value == FeatureOn;
#endif
            if (!TryLoad(out AppLaunchRoot root))
                return false;
            return root.data?.feature != null
                && root.data.feature.cancelAccount == FeatureOn;
        }

        /// <summary>读完整 AppLaunch 根对象；无缓存或解析失败返回 false。</summary>
        public static bool TryLoad(out AppLaunchRoot root)
        {
            root = null;
            string json = PlayerPrefs.GetString(ResponseJsonKey, string.Empty);
            if (string.IsNullOrEmpty(json))
                return false;

            try
            {
                root = JsonUtility.FromJson<AppLaunchRoot>(json);
            }
            catch (Exception e)
            {
                Log.Warning("[BoluoAppLaunchCache] parse failed: " + e.Message);
                root = null;
                return false;
            }

            return root?.data != null;
        }

        /// <summary>原生是否已写入完成闩（不保证 JSON 可解析）。</summary>
        public static bool IsComplete()
        {
            return PlayerPrefs.GetString(CompleteKey, string.Empty) == "1";
        }

#if UNITY_EDITOR
        public static void EditorClearOverride() => EditorCancelAccountOverride = null;
#endif
    }
}
