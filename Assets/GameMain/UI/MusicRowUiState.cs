using Game.BoluoApi;
using Game.BoluoApi.Music;

namespace Game
{
    /// <summary>
    /// 音乐列表行状态纯决议（PA-1186）：盘上优先、用户 Pending 才显示下载中。
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
                    case BankDownloadState.Failed:
                        return MusicItemUiState.Failed;
                    case BankDownloadState.Pending:
                        return userPending
                            ? MusicItemUiState.Downloading
                            : MusicItemUiState.NotDownloaded;
                }
            }

            if (userPending)
                return MusicItemUiState.Downloading;

            return MusicItemUiState.NotDownloaded;
        }
    }
}
