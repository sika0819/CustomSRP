using UnityEngine;

namespace Game.BoluoApi
{
    /// <summary>
    /// Boluo 网关环境：dev / test / prerelease / product。
    /// Editor 可用 EditorPrefs 覆盖；真机后续由原生 SSOT 写入。
    /// </summary>
    public static class BoluoApiEnvironment
    {
        public const string PrefsKey = "BoluoApiEnvironmentType";

#if UNITY_EDITOR
        /// <summary>Editor 测试覆盖 key（仅 EditorPrefs，不进工程/安装包）。</summary>
        public const string EditorEnvOverrideKey = "Boluo.EditorTest.ApiEnvOverride";
#endif

        static string _cached;

        public static void SetCached(string environmentType)
        {
            if (string.IsNullOrEmpty(environmentType))
                return;
            _cached = environmentType.Trim().ToLowerInvariant();
            PlayerPrefs.SetString(PrefsKey, _cached);
        }

        public static void ClearCached() => _cached = null;

#if UNITY_EDITOR
        public static void SetEditorOverride(string environmentType)
        {
            if (string.IsNullOrEmpty(environmentType))
                return;
            UnityEditor.EditorPrefs.SetString(
                EditorEnvOverrideKey, environmentType.Trim().ToLowerInvariant());
            _cached = null;
        }

        public static void ClearEditorOverride()
        {
            UnityEditor.EditorPrefs.DeleteKey(EditorEnvOverrideKey);
            _cached = null;
        }

        public static string GetEditorOverride() =>
            UnityEditor.EditorPrefs.GetString(EditorEnvOverrideKey, "");
#endif

        public static string GetEnvironmentType()
        {
            if (!string.IsNullOrEmpty(_cached))
                return _cached;

#if UNITY_EDITOR
            var editorOverride = UnityEditor.EditorPrefs.GetString(EditorEnvOverrideKey, "");
            if (!string.IsNullOrEmpty(editorOverride))
            {
                _cached = editorOverride.Trim().ToLowerInvariant();
                return _cached;
            }
#endif

#if !UNITY_EDITOR
            if (Game.Native.NativeSession.TryGetHostApiEnvironmentType(out var hostEnv)
                && !string.IsNullOrEmpty(hostEnv))
            {
                _cached = hostEnv.Trim().ToLowerInvariant();
                return _cached;
            }
#endif

            string prefs = PlayerPrefs.GetString(PrefsKey, "");
            if (!string.IsNullOrEmpty(prefs))
            {
                _cached = prefs.Trim().ToLowerInvariant();
                return _cached;
            }

            return CompileTimeEnvironmentType();
        }

        public static string ResolveBoluoBaseUrl()
        {
            return GetEnvironmentType() switch
            {
                "product" => "https://boluo.boluoapp.com",
                "prerelease" => "https://boluo-pre.boluoapp.com",
                "test" => "https://boluo-test.wanos.vip",
                _ => "https://boluo-dev.wanos.vip",
            };
        }

        /// <summary>公共用户域（boluoRsa / boluoDeviceLogin）。</summary>
        public static string ResolveUserApiBase()
        {
            return GetEnvironmentType() switch
            {
                "product" => "https://wanos-common.wavworks.com/wanosuser",
                "prerelease" => "https://wanos-common-pre.wavworks.com/wanosuser",
                "test" => "https://wanos-common-test3.wanos.vip/wanosuser",
                _ => "https://wanos-common-dev.wanos.vip/wanosuser",
            };
        }

        static string CompileTimeEnvironmentType()
        {
#if BOLUO_PRODUCT
            return "product";
#elif BOLUO_PRERELEASE
            return "prerelease";
#elif BOLUO_TEST
            return "test";
#else
            return "dev";
#endif
        }
    }
}
