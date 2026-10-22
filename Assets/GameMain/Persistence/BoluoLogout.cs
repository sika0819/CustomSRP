using UnityGameFramework.Runtime;

namespace Game
{
    /// <summary>退出登录 / 切账号：清会话、清账号绑定本地配置，下次进岛默认出生点与默认天气。</summary>
    public static class BoluoLogout
    {
        public static void Execute()
        {
            ClearAccountLocalProgress("logout");
            GuestSession.Clear();
            // 退登后 bank 回调仍可能命中旧 Pending → RequestTransition，二次进岛 iris 钉死行走。
            PendingDownloadIntent.Clear();
            GameEntry.Presets?.AbortTransition();
            PresetWeatherHold.Abort();
            IslandJoystickCamera.Current?.ForceUnlockLocomotion();
            NowPlayingService.CleanupAfterLeaveIsland();
            Log.Info("[BoluoLogout] session + account progress cleared");
        }

        /// <summary>
        /// 清账号绑定进度（磁盘 + 内存），不拆登录票。
        /// 退登与 <see cref="GuestSession.ApplyUserSession"/> 检测到 userId 变化时共用。
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
    }
}
