using System.Threading;
using UnityEngine;

namespace Game
{
    /// <summary>
    /// Editor Play 登录门闩：等「编辑器登录」窗口放行（手机正式账号或游客按钮）。
    /// 仅 Editor 有意义。Play Mode 仍走宿主时序：登录窗写票，<c>EditorNativeHost</c> 下一帧 Push 106，
    /// <c>ProcedureGuestLogin</c> Query 之后才进岛。注销确认期会话已落盘但暂不交权。
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

        /// <summary>本进程内存里的游客票。游客不落盘，下次 Play 不会从 Prefs 恢复。</summary>
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
