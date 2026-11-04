using Game.BoluoApi;
using Game.BoluoApi.Music;
using NUnit.Framework;

namespace Game.Tests
{
    /// <summary>PA-1186：未下载 / 队列 Pending / 用户触发下载中态区分。</summary>
    public sealed class MusicRowUiStateTests
    {
        [Test]
        public void InvalidTrack_IsFailed()
        {
            Assert.AreEqual(
                MusicItemUiState.Failed,
                MusicRowUiState.Resolve(
                    trackValid: false,
                    isPlaying: false,
                    localReady: false,
                    showRedDot: false,
                    userPending: false,
                    hasActiveItem: false,
                    activeState: BankDownloadState.Pending));
        }

        [Test]
        public void Playing_WinsOverLocalReady()
        {
            Assert.AreEqual(
                MusicItemUiState.Playing,
                MusicRowUiState.Resolve(
                    trackValid: true,
                    isPlaying: true,
                    localReady: true,
                    showRedDot: true,
                    userPending: false,
                    hasActiveItem: false,
                    activeState: BankDownloadState.Pending));
        }

        [Test]
        public void LocalReady_ShowsReadyOrRedDot()
        {
            Assert.AreEqual(
                MusicItemUiState.Ready,
                MusicRowUiState.Resolve(
                    trackValid: true,
                    isPlaying: false,
                    localReady: true,
                    showRedDot: false,
                    userPending: true, // 不得因残留 pending 显示下载中
                    hasActiveItem: true,
                    activeState: BankDownloadState.Pending));

            Assert.AreEqual(
                MusicItemUiState.Succeeded,
                MusicRowUiState.Resolve(
                    trackValid: true,
                    isPlaying: false,
                    localReady: true,
                    showRedDot: true,
                    userPending: false,
                    hasActiveItem: false,
                    activeState: BankDownloadState.Pending));
        }

        [Test]
        public void QueuePending_WithoutUserIntent_IsNotDownloaded()
        {
            // 静默全量：排队 Pending 保持未下载，仅真实 Downloading 转圈。
            Assert.AreEqual(
                MusicItemUiState.NotDownloaded,
                MusicRowUiState.Resolve(
                    trackValid: true,
                    isPlaying: false,
                    localReady: false,
                    showRedDot: false,
                    userPending: false,
                    hasActiveItem: true,
                    activeState: BankDownloadState.Pending));
        }

        [Test]
        public void QueuePending_WithUserIntent_IsDownloading()
        {
            Assert.AreEqual(
                MusicItemUiState.Downloading,
                MusicRowUiState.Resolve(
                    trackValid: true,
                    isPlaying: false,
                    localReady: false,
                    showRedDot: false,
                    userPending: true,
                    hasActiveItem: true,
                    activeState: BankDownloadState.Pending));
        }

        [Test]
        public void ActiveDownloading_AndFailed()
        {
            Assert.AreEqual(
                MusicItemUiState.Downloading,
                MusicRowUiState.Resolve(
                    trackValid: true,
                    isPlaying: false,
                    localReady: false,
                    showRedDot: false,
                    userPending: false,
                    hasActiveItem: true,
                    activeState: BankDownloadState.Downloading));

            Assert.AreEqual(
                MusicItemUiState.Failed,
                MusicRowUiState.Resolve(
                    trackValid: true,
                    isPlaying: false,
                    localReady: false,
                    showRedDot: false,
                    userPending: false,
                    hasActiveItem: true,
                    activeState: BankDownloadState.Failed));
        }

        [Test]
        public void UserPending_WithoutActiveItem_IsDownloading()
        {
            Assert.AreEqual(
                MusicItemUiState.Downloading,
                MusicRowUiState.Resolve(
                    trackValid: true,
                    isPlaying: false,
                    localReady: false,
                    showRedDot: false,
                    userPending: true,
                    hasActiveItem: false,
                    activeState: BankDownloadState.Pending));
        }

        [Test]
        public void Default_IsNotDownloaded()
        {
            Assert.AreEqual(
                MusicItemUiState.NotDownloaded,
                MusicRowUiState.Resolve(
                    trackValid: true,
                    isPlaying: false,
                    localReady: false,
                    showRedDot: false,
                    userPending: false,
                    hasActiveItem: false,
                    activeState: BankDownloadState.Pending));
        }
    }
}
