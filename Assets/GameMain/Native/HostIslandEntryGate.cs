using System;
using System.Threading;
using UnityEngine;
using UnityGameFramework.Runtime;

namespace Game.Native
{
    /// <summary>
    /// 宿主交权进岛门闩：早挂后 ProcedureGuestLogin 停在 LoadingForm，
    /// 等 N→U Push <see cref="MsgType.HostArmIslandEntry"/>（enterHybridGame）再进 ProcedureIsland。
    /// 退登 Push <see cref="MsgType.HostLeaveIsland"/> 复位门闩并退回 GuestLogin，否则二次登录无法再交权。
    /// Editor 由 <see cref="EditorNativeHost"/> 应答握手，因此同样要等 106，避免 Play Mode 绕过宿主时序。
    /// </summary>
    public static class HostIslandEntryGate
    {
        static int _armed;
        static int _generation;

        public static bool IsArmed => Volatile.Read(ref _armed) != 0;

        /// <summary>
        /// Leave，以及 Arm 从 0 变成 1，各加一代。已经是 1 时再来的 106 不加代。
        /// 进行中的换票记下这一代，代际变了就丢掉手里的票。
        /// </summary>
        public static int Generation => Volatile.Read(ref _generation);

        /// <summary>早于 Procedure / NativeBridgeFacade.Init，避免 enterGame Push 先到丢弃。</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Bootstrap()
        {
            _armed = 0;
            _generation = 0;
            MessageRouter.Register(MsgType.HostArmIslandEntry, (_, __, ___) => Arm());
            MessageRouter.Register(MsgType.HostLeaveIsland, (a, __, ___) => Leave(a));
        }

        public static bool ShouldWaitForHost()
        {
            return NativeSession.IsHybridAvailable();
        }

        public static void Arm()
        {
            // 本轮已经接到 106。宿主重发不得再进岛，等 107 拆闩。
            if (Interlocked.Exchange(ref _armed, 1) != 0)
            {
                Log.Info("[IMP] HostArmIslandEntry ignored — already armed");
                IslandEntryLog.Skip("gate", "alreadyArmed", "gen=" + Generation);
                return;
            }

            Interlocked.Increment(ref _generation);
            Log.Info($"[IMP] HostIslandEntryGate armed gen={Generation}");
            IslandEntryLog.Ok("gate", "armed", "gen=" + Generation);
        }

        public static void Reset()
        {
            Interlocked.Exchange(ref _armed, 0);
        }

        /// <summary>
        /// 宿主退登/注销：Disarm + 清 IslandReady 一次性 + 松 overlay 触控 + 清会话镜像，
        /// 并请求 ProcedureIsland 退回 GuestLogin（下次 Arm 才能再进岛 / 再发 Notify(2)）。
        /// </summary>
        /// <param name="leaveReason">
        /// <see cref="MsgType.HostLeaveReasonLogout"/> 保留分桶进度；
        /// <see cref="MsgType.HostLeaveReasonAccountDeleted"/> 清空当前 userId 桶。
        /// </param>
        public static void Leave(int leaveReason = MsgType.HostLeaveReasonLogout)
        {
            Interlocked.Increment(ref _generation);
            Reset();
            NativeMessages.ResetIslandReadyFlag();
            NativeOverlayGate.Apply(present: 0, touchEnabled: 1, bottomHidden: 0);
            // 清会话会抛。先记下退回，下一帧 ProcedureIsland 才能 BeginLeave。
            // 抛在请求之后被吃掉，也不能把流程留在岛上。
            Game.ProcedureIsland.RequestReturnToGuestLogin();
            // Leave 刚拆掉门闩。人已经在 GuestLogin 时立刻回报 a=0，宿主才会重发 106。
            if (Game.ProcedureGuestLogin.IsCurrent)
                NativeMessages.ReturnedToGuestLogin(0);
            try
            {
#if UNITY_EDITOR
                EditorNativeHost.RevokeUserTicket();
#endif
                if (leaveReason == MsgType.HostLeaveReasonAccountDeleted)
                    Game.BoluoLogout.ExecuteAccountDeleted();
                else
                    Game.BoluoLogout.ExecuteLeave();
                Game.BoluoApi.BankUpgradeBootstrap.InvalidateAfterLeave();
            }
            catch (Exception e)
            {
                Log.Error("[IMP] HostLeaveIsland cleanup failed: " + e);
                string err = e.Message ?? "throw";
                IslandEntryLog.Fail("gate", "leaveCleanup", "err=" + err.Replace(' ', '_').Replace('\n', '_'));
            }

            Log.Info(
                $"[IMP] HostLeaveIsland reason={leaveReason} gen={Generation} — gates reset, return to GuestLogin requested");
            IslandEntryLog.Ok("gate", "leave", "leaveReason=" + leaveReason + " gen=" + Generation);
        }
    }
}
