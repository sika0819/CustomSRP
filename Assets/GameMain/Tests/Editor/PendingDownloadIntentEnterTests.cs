using NUnit.Framework;

namespace Game.Tests
{
    /// <summary>切预设自动进场：仅用户点 / 健康推荐；静默下载不得进场。</summary>
    public sealed class PendingDownloadIntentEnterTests
    {
        [TearDown]
        public void TearDown() => PendingDownloadIntent.Clear();

        [Test]
        public void UserClick_ShouldAutoEnter()
        {
            PendingDownloadIntent.SetPreset(4, PendingDownloadIntent.EnterSource.UserClick);
            Assert.IsTrue(PendingDownloadIntent.ShouldAutoEnterPreset(4));
            Assert.IsFalse(PendingDownloadIntent.ShouldAutoEnterPreset(5));
        }

        [Test]
        public void HealthRecommend_ShouldAutoEnter()
        {
            PendingDownloadIntent.SetPreset(3, PendingDownloadIntent.EnterSource.HealthRecommend);
            Assert.IsTrue(PendingDownloadIntent.ShouldAutoEnterPreset(3));
        }

        [Test]
        public void SilentOrNone_ShouldNotAutoEnter()
        {
            PendingDownloadIntent.SetPreset(2, PendingDownloadIntent.EnterSource.None);
            Assert.IsFalse(PendingDownloadIntent.ShouldAutoEnterPreset(2));
            PendingDownloadIntent.Clear();
            Assert.IsFalse(PendingDownloadIntent.ShouldAutoEnterPreset(2));
        }

        [Test]
        public void MusicIntent_ShouldNotAutoEnterPreset()
        {
            PendingDownloadIntent.SetMusic(0, 1);
            Assert.IsFalse(PendingDownloadIntent.ShouldAutoEnterPreset(1));
        }
    }
}
