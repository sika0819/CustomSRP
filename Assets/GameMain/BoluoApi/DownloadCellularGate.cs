using UnityEngine;
using UnityGameFramework.Runtime;

namespace Game.BoluoApi
{
    /// <summary>
    /// 本会话蜂窝下载策略。须与 <see cref="AllowMobileNetwork"/> 同开同关。
    /// </summary>
    public enum CellularPolicy
    {
        WifiOnly = 0,
        AllowAlways = 1,
    }

    /// <summary>
    /// 岛内 Bank 下载的蜂窝闸。默认关闸 + WifiOnly；
    /// 「继续下载」→ AllowAlways；「切换 Wi‑Fi」→ WifiOnly。
    /// 系统「本 App 蜂窝数据」开关不进此判定。
    /// </summary>
    public static class DownloadCellularGate
    {
        static CellularPolicy _policy = CellularPolicy.WifiOnly;
        static bool _allowMobile;

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

        static NetworkReachability EffectiveReachability()
        {
#if UNITY_EDITOR
            if (_editorReachabilityOverride.HasValue)
                return _editorReachabilityOverride.Value;
#endif
            return Application.internetReachability;
        }

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
