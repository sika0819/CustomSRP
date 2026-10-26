using UnityGameFramework.Runtime;

namespace Game
{
    /// <summary>
    /// 退登 / 注销。普通退登只清会话票，保留按 userId 分桶的本地进度；
    /// 注销成功才清当前账号进度桶。
    /// </summary>
    public static class BoluoLogout
    {
        /// <summary>兼容旧调用：等同普通退登（保留分桶进度）。</summary>
        public static void Execute() => ExecuteLeave();

        /// <summary>普通退登：清会话与运行时态，不删 PlayerPrefs 进度桶。</summary>
        public static void ExecuteLeave()
        {
            // 必须在清会话票之前落盘：否则 AllowsAccountProgressDisk=false，位置写被门控丢掉。
            IslandPlayerPosSave.TrySaveCurrentAndFlush();
            ClearRuntimeAfterLeave("logout");
            GuestSession.Clear();
            Log.Info("[BoluoLogout] session cleared (progress buckets kept)");
        }

        /// <summary>注销成功：清当前 userId 进度桶 + 会话。</summary>
        public static void ExecuteAccountDeleted()
        {
            string userId = GuestSession.UserId ?? "";
            // 注销故意不先存位置——桶会被 ClearAccountLocalProgress 删掉。
            ClearAccountLocalProgress("account deleted");
            ClearRuntimeAfterLeave("account deleted");
            GuestSession.Clear();
            Log.Info($"[BoluoLogout] account deleted — bucket cleared userId={userId}");
        }

        static void ClearRuntimeAfterLeave(string reason)
        {
            // 退登后 bank 回调仍可能命中旧 Pending → RequestTransition，二次进岛 iris 钉死行走。
            PendingDownloadIntent.Clear();
            GameEntry.Presets?.AbortTransition();
            PresetWeatherHold.Abort();
            IslandJoystickCamera.Current?.ForceUnlockLocomotion();
            NowPlayingService.CleanupAfterLeaveIsland();
            IslandPlayerPosSave.ResetSession();
            BoluoGameSave.ResetSession();
            DownloadedUnplayedManager.ResetSession();
            GameEntry.Weather?.ClearForAccountSwitch();
            Log.Info($"[BoluoLogout] runtime cleared ({reason})");
        }

        /// <summary>
        /// 清账号绑定进度（磁盘 + 内存），不拆登录票。
        /// 注销与显式清档调用；热切账号改走 <see cref="ResetInMemoryForAccountSwitch"/>。
        /// </summary>
        public static void ClearAccountLocalProgress(string reason)
        {
            IslandPlayerPosSave.UseDefaultPlayerPosNextLoad = true;
            IslandPlayerPosSave.ResetSession();
            BoluoGamePersistence.ClearAccountBoundProgress();
            BoluoGameSave.ResetSession();
            DownloadedUnplayedManager.ResetSession();
            GameEntry.Weather?.ClearForAccountSwitch();
            Log.Info($"[BoluoLogout] account local progress cleared ({reason})");
        }

        /// <summary>
        /// 热切到另一 userId：只重置内存态，磁盘上旧账号桶保留；下次进岛按新桶 Load。
        /// </summary>
        public static void ResetInMemoryForAccountSwitch(string reason)
        {
            IslandPlayerPosSave.UseDefaultPlayerPosNextLoad = false;
            IslandPlayerPosSave.ResetSession();
            BoluoGameSave.ResetSession();
            DownloadedUnplayedManager.ResetSession();
            GameEntry.Weather?.ClearForAccountSwitch();
            Log.Info($"[BoluoLogout] in-memory reset for account switch ({reason})");
        }
    }
}
