using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityGameFramework.Runtime;

namespace Game.BoluoApi
{
    /// <summary>
    /// 本会话蜂窝下载策略。须与 <see cref="AllowMobileNetwork"/> 同开同关。
    /// AllowAlways 在恢复 Wi‑Fi 后会被撤销，再次进蜂窝需重确认。
    /// </summary>
    public enum CellularPolicy
    {
        WifiOnly = 0,
        AllowAlways = 1,
    }

    /// <summary>
    /// 岛内 Bank 下载的蜂窝闸。默认关闸 + WifiOnly；
    /// 「继续下载」→ AllowAlways；「切换 Wi‑Fi」→ WifiOnly。
    /// 回到 Wi‑Fi 后撤销 AllowAlways，再次进蜂窝需重确认。
    /// 系统「本 App 蜂窝数据」开关不进此判定。
    /// </summary>
    public static class DownloadCellularGate
    {
        static CellularPolicy _policy = CellularPolicy.WifiOnly;
        static bool _allowMobile;

        /// <summary>边沿日志：避免 Rest 巡检每秒刷屏。</summary>
        static NetworkReachability? _lastLoggedReachability;
        static bool? _lastLoggedWifiPresent;

        /// <summary>上次有效可达性；用于 Wi‑Fi 恢复时撤销本会话蜂窝开闸。</summary>
        static NetworkReachability? _prevEffective;

        /// <summary>原生 Wi‑Fi 探测短缓存（Rest 巡检 1s 一轮，避免每帧 JNI）。</summary>
        const float WifiProbeTtlSeconds = 1f;
        static float _wifiProbeAt = -999f;
        static bool _wifiProbeResult;

#if UNITY_EDITOR
        /// <summary>Editor 联调：假装真机（Android/iOS），否则 NeedsMobileDataPrompt 恒 false。</summary>
        static bool _editorSimulateMobilePlatform = true;

        /// <summary>Editor 联调：覆盖 Reachability；null 用真实 <see cref="Application.internetReachability"/>。</summary>
        static NetworkReachability? _editorReachabilityOverride;
#endif

        public static CellularPolicy Policy => _policy;

        /// <summary>与 Policy 同步的传输闸；未开闸时蜂窝上不得发请求。</summary>
        public static bool AllowMobileNetwork => _allowMobile;

        public static void ResetForNewSession()
        {
            _policy = CellularPolicy.WifiOnly;
            _allowMobile = false;
            _lastLoggedReachability = null;
            _lastLoggedWifiPresent = null;
            _prevEffective = null;
            _wifiProbeAt = -999f;
            Log.Info("[DownloadCellularGate] reset → WifiOnly / closed");
        }

        /// <summary>
        /// 流量窗「继续下载」：先由调用方 Priority 入队，再调本方法开闸。
        /// 内部只 EnsureRunner，禁止调用方再 Resume。
        /// </summary>
        public static void UserAllowCellularAlways()
        {
            _policy = CellularPolicy.AllowAlways;
            _allowMobile = true;
            Log.Info("[DownloadCellularGate] UserAllowCellularAlways");
            BankDownloader.NotifyTransmitAllowed();
        }

        /// <summary>
        /// 流量窗「切换 Wi‑Fi 后下载」：关闸；蜂窝上活跃槽 Abort 回队等待。
        /// </summary>
        public static void UserRequireWifiOnly()
        {
            _policy = CellularPolicy.WifiOnly;
            _allowMobile = false;
            Log.Info("[DownloadCellularGate] UserRequireWifiOnly");
            BankDownloader.HoldForCellularPolicy();
        }

        public static bool IsOffline() =>
            EffectiveReachability() == NetworkReachability.NotReachable;

        /// <summary>未开闸 ∧ 蜂窝（仅 Android / iOS；Editor 需开 SimulateMobilePlatform）。</summary>
        public static bool NeedsMobileDataPrompt()
        {
            if (!IsMobilePlatformForGate())
                return false;
            if (_allowMobile)
                return false;
            return EffectiveReachability()
                == NetworkReachability.ReachableViaCarrierDataNetwork;
        }

        /// <summary>非无网，且（非蜂窝或已开闸）—— BankDownloader 泵队列用。</summary>
        public static bool CanTransmitNow()
        {
            NetworkReachability r = EffectiveReachability();
            if (r == NetworkReachability.NotReachable)
                return false;
            if (IsMobilePlatformForGate()
                && r == NetworkReachability.ReachableViaCarrierDataNetwork
                && !_allowMobile)
                return false;
            return true;
        }

        static bool IsMobilePlatformForGate()
        {
#if UNITY_ANDROID || UNITY_IOS
            return true;
#elif UNITY_EDITOR
            return _editorSimulateMobilePlatform;
#else
            return false;
#endif
        }

        /// <summary>
        /// 有效可达性。OEM 双通道（Wi‑Fi+蜂窝同时开）时 Unity 常误报 Carrier；
        /// 本机仍有 Wi‑Fi/有线则按 LAN，Rest 静默下、不弹流量窗。
        /// Wi‑Fi 恢复时撤销 AllowAlways，使再次进蜂窝重新弹流量窗。
        /// </summary>
        static NetworkReachability EffectiveReachability()
        {
            NetworkReachability effective;
#if UNITY_EDITOR
            if (_editorReachabilityOverride.HasValue)
            {
                effective = _editorReachabilityOverride.Value;
                LogReachabilityEdge(effective, wifiPresent: null);
                ApplyWifiRestoreRevoke(effective);
                return effective;
            }
#endif
            NetworkReachability unity = Application.internetReachability;
            if (unity != NetworkReachability.ReachableViaCarrierDataNetwork)
            {
                effective = unity;
                LogReachabilityEdge(effective, wifiPresent: null);
                ApplyWifiRestoreRevoke(effective);
                return effective;
            }

            bool wifi = NativeHasWifiOrEthernet();
            effective = wifi
                ? NetworkReachability.ReachableViaLocalAreaNetwork
                : unity;
            LogReachabilityEdge(effective, wifi);
            ApplyWifiRestoreRevoke(effective);
            return effective;
        }

        /// <summary>
        /// 回到 Wi‑Fi/有线后清掉本会话「继续下载」开闸。
        /// LAN 上仍可传（不依赖 _allowMobile）；再切蜂窝时 NeedsMobileDataPrompt 重新为真。
        /// </summary>
        static void ApplyWifiRestoreRevoke(NetworkReachability effective)
        {
            NetworkReachability? prev = _prevEffective;
            _prevEffective = effective;

            if (effective != NetworkReachability.ReachableViaLocalAreaNetwork)
                return;
            if (!_allowMobile)
                return;
            // 已在 LAN 上则不必重复撤；仅从非 LAN → LAN 时撤一次。
            if (prev.HasValue
                && prev.Value == NetworkReachability.ReachableViaLocalAreaNetwork)
                return;

            _policy = CellularPolicy.WifiOnly;
            _allowMobile = false;
            Log.Info(
                "[DownloadCellularGate] Wi‑Fi restored → revoke AllowAlways " +
                $"(was {prev})");
        }

        static void LogReachabilityEdge(NetworkReachability effective, bool? wifiPresent)
        {
            if (_lastLoggedReachability == effective && _lastLoggedWifiPresent == wifiPresent)
                return;
            _lastLoggedReachability = effective;
            _lastLoggedWifiPresent = wifiPresent;
            string wifiTxt = wifiPresent.HasValue
                ? $" wifiOrEth={wifiPresent.Value}"
                : string.Empty;
            Log.Info(
                $"[DownloadCellularGate] reachability={effective} " +
                $"unity={Application.internetReachability}{wifiTxt}");
        }

        /// <summary>
        /// 本机是否挂着 Wi‑Fi / 有线（可不一定是默认路由）。
        /// 双通道下默认路由可能是蜂窝，但仍应静默走 Wi‑Fi 策略。
        /// </summary>
        static bool NativeHasWifiOrEthernet()
        {
            float now = Time.unscaledTime;
            if (now - _wifiProbeAt < WifiProbeTtlSeconds)
                return _wifiProbeResult;

#if UNITY_ANDROID && !UNITY_EDITOR
            _wifiProbeResult = AndroidHasWifiOrEthernetTransport();
#elif UNITY_IOS && !UNITY_EDITOR
            _wifiProbeResult = BoluoDownloadReachability_HasWifiOrEthernet() != 0;
#else
            // Editor / 其它平台：无原生探测，信 Unity。
            _wifiProbeResult = Application.internetReachability
                == NetworkReachability.ReachableViaLocalAreaNetwork;
#endif
            _wifiProbeAt = now;
            return _wifiProbeResult;
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        const int TransportWifi = 1;
        const int TransportEthernet = 3;

        static bool AndroidHasWifiOrEthernetTransport()
        {
            try
            {
                using (var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity"))
                {
                    if (activity == null)
                        return false;
                    using (var cm = activity.Call<AndroidJavaObject>("getSystemService", "connectivity"))
                    {
                        if (cm == null)
                            return false;
                        AndroidJavaObject[] networks = cm.Call<AndroidJavaObject[]>("getAllNetworks");
                        if (networks == null || networks.Length == 0)
                            return false;
                        for (int i = 0; i < networks.Length; i++)
                        {
                            AndroidJavaObject network = networks[i];
                            if (network == null)
                                continue;
                            using (network)
                            using (var caps = cm.Call<AndroidJavaObject>(
                                       "getNetworkCapabilities", network))
                            {
                                if (caps == null)
                                    continue;
                                if (caps.Call<bool>("hasTransport", TransportWifi)
                                    || caps.Call<bool>("hasTransport", TransportEthernet))
                                    return true;
                            }
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Log.Warning(
                    "[DownloadCellularGate] Android Wi‑Fi probe failed: " + e.Message);
            }

            return false;
        }
#endif

#if UNITY_IOS && !UNITY_EDITOR
        [DllImport("__Internal")]
        static extern int BoluoDownloadReachability_HasWifiOrEthernet();
#endif

#if UNITY_EDITOR
        public static bool EditorSimulateMobilePlatform
        {
            get => _editorSimulateMobilePlatform;
            set => _editorSimulateMobilePlatform = value;
        }

        public static NetworkReachability? EditorReachabilityOverride
        {
            get => _editorReachabilityOverride;
            set => _editorReachabilityOverride = value;
        }

        public static NetworkReachability EditorEffectiveReachability => EffectiveReachability();

        public static void EditorClearReachabilityOverride() =>
            _editorReachabilityOverride = null;
#endif
    }
}
