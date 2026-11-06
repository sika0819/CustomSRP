using Game.BoluoApi;
using Game.BoluoApi.Music;

namespace Game
{
    /// <summary>
    /// 音乐列表行状态纯决议：盘上优先；真正 Downloading 或用户刚点下才出进度环。
    /// 传输失败（含自动重试还没轮到）显示重试。静默 Kick 的首次 Pending 仍是未下载。
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
            BankDownloadState activeState,
            bool failedAwaitingRetry = false)
        {
            if (!trackValid)
                return MusicItemUiState.Failed;

            if (isPlaying)
                return MusicItemUiState.Playing;

            if (localReady)
                return showRedDot ? MusicItemUiState.Succeeded : MusicItemUiState.Ready;

            // 用户刚点下、这轮还没出结果：进度环。失败收掉意图后才回到重试。
            if (userPending)
                return MusicItemUiState.Downloading;

            if (hasActiveItem && activeState == BankDownloadState.Downloading)
                return MusicItemUiState.Downloading;

            if (failedAwaitingRetry
                || (hasActiveItem && activeState == BankDownloadState.Failed))
                return MusicItemUiState.Failed;

            if (hasActiveItem && activeState == BankDownloadState.Pending)
                return MusicItemUiState.NotDownloaded;

            return MusicItemUiState.NotDownloaded;
        }
    }
}
