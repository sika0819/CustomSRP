using Game.Native;
using UnityEngine;

namespace Game.BoluoApi
{
    /// <summary>
    /// 写入签名 Header / boluoRsa 的设备与渠道字段。
    /// Editor 按 Build Target + 编译宏模拟；可用 EditorPrefs 覆盖渠道/版本/originEnd（不进安装包）。
    /// originEnd ≠ channel：前者小写端编码（如 boluocwgmj），后者大写业务渠道（如 BOLUOCWGMJ）。
    /// </summary>
    public static class BoluoDeviceInfo
    {
        /// <summary>App / Editor 默认用户端编码（与 iOS / 非 hybrid 一致）。</summary>
        public const string DefaultOriginEndApp = "boluoapp";

        /// <summary>闺蜜机用户端编码（与 Android WanosDeviceUtils.DEFAULT_ORIGIN_END 对齐）。</summary>
        public const string DefaultOriginEndGmj = "boluocwgmj";

        /// <summary>闺蜜机渠道（创维 Neo / Ultra 等真机包）。</summary>
        public const string DefaultChannelGmj = "BOLUOCWGMJ";

        /// <summary>应用宝（服务端正写；旧串少一个 P 见 <see cref="NormalizeChannel"/>）。</summary>
        public const string ChannelAndroidAppStore = "BOLUOANDROIDAPPSTORE";

        /// <summary>历史拼写（少一个 P），读到时归一为正写。</summary>
        public const string ChannelAndroidAppStoreLegacyTypo = "BOLUOANDROIDAPPSOTRE";

        /// <summary>
        /// 服务端「应用渠道号」全表（对齐 AndroidProject/Docs/渠道.md §3）。
        /// Editor 环境窗口预设与此同源；勿自造占位值。
        /// </summary>
        public static readonly string[] KnownAppChannels =
        {
            DefaultChannelGmj,           // 创维闺蜜机 / 一树（当前）
            ChannelAndroidAppStore,      // 应用宝
            "BOLUOBAIDU",
            "BOLUO360",
            "BOLUOXIAOMI",
            "BOLUOOPPO",
            "BOLUOVIVO",
            "BOLUOHONOR",
            "BOLUOGALLERY",              // 三星（编码是 GALLERY，不是 SAMSUNG）
            "BOLUOMEIZU",
            "BOLUOIOSAPPSTORE",
            "BOLUOAPPHARMONY",
            "BOLUOTESTANDROID",
            "BOLUOTESTIOS",
            "BOLUOTESTHARMONY",
        };

        /// <summary>
        /// 创维闺蜜机：channel=BOLUOCWGMJ 或 originEnd=boluocwgmj（Editor 可经环境窗口覆盖）。
        /// </summary>
        public static bool IsSkyworthGmj()
        {
            return string.Equals(GetChannel(), DefaultChannelGmj, System.StringComparison.OrdinalIgnoreCase)
                || string.Equals(GetOriginEnd(), DefaultOriginEndGmj, System.StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>把历史错拼归一成服务端正写；其它原样返回。</summary>
        public static string NormalizeChannel(string channel)
        {
            if (string.IsNullOrEmpty(channel))
                return channel;
            if (string.Equals(channel, ChannelAndroidAppStoreLegacyTypo, System.StringComparison.OrdinalIgnoreCase))
                return ChannelAndroidAppStore;
            return channel;
        }

        /// <summary>按 channel 建议 originEnd（闺蜜 → boluocwgmj，其余 → boluoapp）。</summary>
        public static string SuggestOriginEndForChannel(string channel)
        {
            if (string.Equals(NormalizeChannel(channel), DefaultChannelGmj, System.StringComparison.OrdinalIgnoreCase))
                return DefaultOriginEndGmj;
            return DefaultOriginEndApp;
        }

#if UNITY_EDITOR
        /// <summary>Editor 测试覆盖 key（环境·渠道·版本窗口写入；仅 EditorPrefs）。</summary>
        public const string EditorChannelOverrideKey = "Boluo.EditorTest.ChannelOverride";
        public const string EditorVersionOverrideKey = "Boluo.EditorTest.VersionOverride";
        public const string EditorOriginEndOverrideKey = "Boluo.EditorTest.OriginEndOverride";
#endif

        // 渠道 / appId 映射（与原生对齐）：
        //   1101 BOLUOANDROIDAPPSTORE · 1107 BOLUOIOSAPPSTORE
        //   1001 BOLUOTESTANDROID · 1002 BOLUOTESTIOS · 1003 BOLUOTESTHARMONY

        public static string GetIdentifier()
        {
#if UNITY_EDITOR
            return SystemInfo.deviceUniqueIdentifier ?? "";
#elif UNITY_IOS || UNITY_ANDROID
            if (NativeSession.TryGetHostDeviceId(out var native) && !string.IsNullOrEmpty(native))
                return native;
            return SystemInfo.deviceUniqueIdentifier ?? "";
#else
            return SystemInfo.deviceUniqueIdentifier ?? "";
#endif
        }

        /// <summary>
        /// 渠道号。Editor：覆盖优先，否则按 Build Target + release-like 模拟。
        /// </summary>
        public static string GetChannel()
        {
#if UNITY_EDITOR
            var channelOverride = UnityEditor.EditorPrefs.GetString(EditorChannelOverrideKey, "");
            if (!string.IsNullOrEmpty(channelOverride))
                return NormalizeChannel(channelOverride);
            return EditorSimulatedChannel();
#else
#if UNITY_IOS || UNITY_ANDROID
            if (NativeSession.TryGetHostChannel(out var native) && !string.IsNullOrEmpty(native))
                return NormalizeChannel(native);
#endif
#if UNITY_IOS
            return IsReleaseLike() ? "BOLUOIOSAPPSTORE" : "BOLUOTESTIOS";
#elif UNITY_ANDROID
            // 独立宿主 APK 真机默认闺蜜渠道（对齐 SkyworthPlatformImpl）
            return DefaultChannelGmj;
#else
            return IsReleaseLike() ? ChannelAndroidAppStore : "BOLUOTESTANDROID";
#endif
#endif
        }

        /// <summary>
        /// appId。hybrid 优先 Query(21)；否则按 Build Target 模拟（勿写死 Android 1001）。
        /// iOS：test=1002 / 正式=1107；Android：闺蜜=1101 / test=1001。
        /// </summary>
        public static int GetAppId()
        {
#if UNITY_EDITOR
            return EditorSimulatedAppId();
#elif UNITY_IOS || UNITY_ANDROID
            if (NativeSession.TryGetHostAppId(out var hostAppId) && hostAppId > 0)
                return hostAppId;
#if UNITY_IOS
            return IsReleaseLike() ? 1107 : 1002;
#else
            string channel = NativeSession.TryGetHostChannel(out var native) && !string.IsNullOrEmpty(native)
                ? native
                : DefaultChannelGmj;
            if (string.Equals(channel, DefaultChannelGmj, System.StringComparison.OrdinalIgnoreCase))
                return 1101;
            return IsReleaseLike() ? 1101 : 1001;
#endif
#else
            return IsReleaseLike() ? 1101 : 1001;
#endif
        }

        /// <summary>App 默认版本号，与 ProjectSettings.bundleVersion 对齐；用于 EditorPrefs 空覆盖时的兜底。</summary>
        public const string DefaultVersion = "1.1.70";

        public static string GetVersion()
        {
#if UNITY_EDITOR
            var versionOverride = UnityEditor.EditorPrefs.GetString(EditorVersionOverrideKey, DefaultVersion);
            if (!string.IsNullOrEmpty(versionOverride))
                return versionOverride;
#endif
            var v = Application.version;
            return string.IsNullOrEmpty(v) ? DefaultVersion : v;
        }

        /// <summary>系统类型：1 android · 2 ios · 3 harmony。</summary>
        public static int GetOsType()
        {
#if UNITY_ANDROID
            return 1;
#elif UNITY_IOS
            return 2;
#elif UNITY_OPENHARMONY
            return 3;
#else
            return 1;
#endif
        }

        public static string GetOsVersion()
        {
#if UNITY_EDITOR
#if UNITY_IOS
            return "18.0";
#else
            return "14";
#endif
#else
            return SystemInfo.operatingSystem ?? "";
#endif
        }

        public static string GetBrand()
        {
#if UNITY_EDITOR
#if UNITY_IOS
            return "Apple";
#else
            return "xiaomi";
#endif
#elif UNITY_IOS
            return "Apple";
#else
            return SystemInfo.deviceName ?? "";
#endif
        }

        public static string GetModel()
        {
#if UNITY_EDITOR
#if UNITY_IOS
            return "iPhone";
#else
            return "P12";
#endif
#else
            return SystemInfo.deviceModel ?? "";
#endif
        }

        public static string GetDeviceType() => "phone";

        public static string GetCompanyName()
        {
#if UNITY_IOS
            return "Apple";
#else
            return "xiaomi";
#endif
        }

        public static float GetLatitude() => 39f;
        public static float GetLongitude() => 116f;
        public static float GetAccuracy() => 1f;

        /// <summary>
        /// 健康渠道号（≠ 应用渠道）。对齐渠道.md §3.1；无对应则空串。
        /// </summary>
        public static string GetHealthChannel()
        {
            string channel = GetChannel();
            if (string.Equals(channel, "BOLUOIOSAPPSTORE", System.StringComparison.OrdinalIgnoreCase)
                || string.Equals(channel, "BOLUOTESTIOS", System.StringComparison.OrdinalIgnoreCase))
                return "APPLE_HEALTH";
            if (string.Equals(channel, "BOLUOXIAOMI", System.StringComparison.OrdinalIgnoreCase))
                return "XIAOMI_HEALTH";
            if (string.Equals(channel, "BOLUOOPPO", System.StringComparison.OrdinalIgnoreCase))
                return "OPPO_HEALTH";
            if (string.Equals(channel, "BOLUOVIVO", System.StringComparison.OrdinalIgnoreCase))
                return "VIVO_HEALTH";
            if (string.Equals(channel, "BOLUOHONOR", System.StringComparison.OrdinalIgnoreCase))
                return "HONOR_HEALTH";
#if UNITY_IOS
            return "APPLE_HEALTH";
#else
            return "";
#endif
        }

        public static string GetThirdPartyId() => "";

        /// <summary>
        /// 用户端编码（HTTP Header / boluoRsa 的 originEnd；与 channel 独立）。
        /// Editor / iOS：默认 <see cref="DefaultOriginEndApp"/>；Editor 可用 EditorPrefs 覆盖。
        /// Android / iOS 真机 hybrid：经 <see cref="NativeSession"/> 读 Query(21) originEnd；
        /// 禁止在 Android 路径写死 boluoapp（错档易导致 AppLaunch / VersionCheck / 换票 400）。
        /// </summary>
        public static string GetOriginEnd()
        {
#if UNITY_EDITOR
            var originOverride = UnityEditor.EditorPrefs.GetString(EditorOriginEndOverrideKey, "");
            if (!string.IsNullOrEmpty(originOverride))
                return originOverride;
            return DefaultOriginEndApp;
#elif UNITY_ANDROID || UNITY_IOS
            if (NativeSession.TryGetHostOriginEnd(out var native) && !string.IsNullOrEmpty(native))
                return native;
#if UNITY_ANDROID
            // 独立宿主 APK 真机默认闺蜜 originEnd；hybrid 正常路径读 Query(21)。
            return DefaultOriginEndGmj;
#else
            return DefaultOriginEndApp;
#endif
#else
            return DefaultOriginEndApp;
#endif
        }

        static bool IsReleaseLike()
        {
#if BOLUO_PRODUCT || BOLUO_PRERELEASE
            return true;
#else
            return false;
#endif
        }

#if UNITY_EDITOR
        static string EditorSimulatedChannel()
        {
#if UNITY_IOS
            return IsReleaseLike() ? "BOLUOIOSAPPSTORE" : "BOLUOTESTIOS";
#elif UNITY_ANDROID
            // Editor 兜底用测试渠道；完整壳真机默认是闺蜜（见非 Editor Android 分支）。
            return IsReleaseLike() ? ChannelAndroidAppStore : "BOLUOTESTANDROID";
#else
            return IsReleaseLike() ? ChannelAndroidAppStore : "BOLUOTESTANDROID";
#endif
        }

        static int EditorSimulatedAppId()
        {
#if UNITY_IOS
            return IsReleaseLike() ? 1107 : 1002;
#elif UNITY_ANDROID
            return IsReleaseLike() ? 1101 : 1001;
#else
            return IsReleaseLike() ? 1101 : 1001;
#endif
        }
#endif
    }
}
