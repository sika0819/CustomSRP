using UnityGameFramework.Runtime;

namespace Game
{
    /// <summary>退出登录：清会话并强制落盘，下次进岛默认出生点。</summary>
    public static class BoluoLogout
    {
        public static void Execute()
        {
            IslandPlayerPosSave.UseDefaultPlayerPosNextLoad = true;
            IslandPlayerPosSave.ResetSession();
            BoluoGameSave.ResetSession();
            DownloadedUnplayedManager.ResetSession();
            GuestSession.Clear();
            // 退登后 bank 回调仍可能命中旧 Pending → RequestTransition，二次进岛 iris 钉死行走。
            PendingDownloadIntent.Clear();
            GameEntry.Presets?.AbortTransition();
            PresetWeatherHold.Abort();
            IslandJoystickCamera.Current?.ForceUnlockLocomotion();
            NowPlayingService.CleanupAfterLeaveIsland();
            Log.Info("[BoluoLogout] session cleared, next island uses default spawn");
        }
    }
}
