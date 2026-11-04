using Game.BoluoApi;
using Game.BoluoApi.Music;

namespace Game
{
    /// <summary>
    /// 音乐列表行状态纯决议：盘上优先；仅真实 Downloading（或用户点过的 Pending）显示进度环。
    /// 静默全量入队的 Pending 仍显示未下载，做到一个一个翻牌。
    /// </summary>
    public static class MusicRowUiState
    {
        public static MusicItemUiState Resolve(
            bool trackValid,
            bool isPlaying,
            bool localReady,
            bool showRedDot,
            bool userPending,
            bool hasActiveItem,
            BankDownloadState activeState)
        {
            if (!trackValid)
                return MusicItemUiState.Failed;

            if (isPlaying)
                return MusicItemUiState.Playing;

            if (localReady)
                return showRedDot ? MusicItemUiState.Succeeded : MusicItemUiState.Ready;

            if (hasActiveItem)
            {
                switch (activeState)
                {
                    case BankDownloadState.Downloading:
                        return MusicItemUiState.Downloading;
                    case BankDownloadState.Pending:
                        // 用户点过才对排队项转圈；静默 Kick 的 Pending 保持未下载。
                        return userPending
                            ? MusicItemUiState.Downloading
                            : MusicItemUiState.NotDownloaded;
                    case BankDownloadState.Failed:
                        return MusicItemUiState.Failed;
                }
            }

            if (userPending)
                return MusicItemUiState.Downloading;

            return MusicItemUiState.NotDownloaded;
        }
    }
}
