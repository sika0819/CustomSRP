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
            NowPlayingService.CleanupAfterLeaveIsland();
            Log.Info("[BoluoLogout] session cleared, next island uses default spawn");
        }
    }
}
