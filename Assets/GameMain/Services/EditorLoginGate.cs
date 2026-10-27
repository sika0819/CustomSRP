using System.Threading;
using UnityEngine;

namespace Game
{
    /// <summary>
    /// Editor Play 登录门闩：等「编辑器登录」窗口放行（手机正式账号或游客按钮）。
    /// 仅 <c>UNITY_EDITOR</c> 有意义；真机 hybrid 走宿主 Arm。
    /// 已落盘的正式 / 游客会话均可复用，无需再点登录（主动清空 / 退登除外）。
    /// 注销确认期：会话已落盘但 <see cref="CancelPendingLogin"/> 为真时暂不进岛。
    /// </summary>
    public static class EditorLoginGate
    {
        static int _guestProceed;
        static int _cancelPending;

        public static bool HasFormalSession
        {
            get
            {
                GuestSession.LoadFromPrefs();
                return GuestSession.IsLoggedIn && !GuestSession.IsGuest;
            }
        }

        /// <summary>Prefs 中已有游客 userToken（上次游客登录未主动退出）。</summary>
        public static bool HasGuestSession
        {
            get
            {
                GuestSession.LoadFromPrefs();
                return GuestSession.IsLoggedIn && GuestSession.IsGuest;
            }
        }

        public static bool HasAnySession => HasFormalSession || HasGuestSession;

        public static bool GuestProceedRequested => Volatile.Read(ref _guestProceed) != 0;

        /// <summary>
        /// 注销确认期登录成功后置位：ProcedureGuestLogin 等待弹窗 resolve
        /// （终止注销成功清位进岛；退出则清会话）。
        /// </summary>
        public static bool CancelPendingLogin => Volatile.Read(ref _cancelPending) != 0;

        public static void RequestGuestProceed()
        {
            Interlocked.Exchange(ref _guestProceed, 1);
        }

        public static void SetCancelPendingLogin(bool pending)
        {
            Interlocked.Exchange(ref _cancelPending, pending ? 1 : 0);
        }

        public static void ResetSessionFlags()
        {
            Interlocked.Exchange(ref _guestProceed, 0);
            Interlocked.Exchange(ref _cancelPending, 0);
        }

#if UNITY_EDITOR
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Bootstrap()
        {
            ResetSessionFlags();
        }
#endif
    }
}
