using NUnit.Framework;

namespace Game.Tests
{
    /// <summary>负一手动本专辑环绕 / 列表曲终末→首纯函数。</summary>
    public sealed class MusicAlbumNavTests
    {
        [Test]
        public void Adjacent_LastToFirst()
        {
            Assert.AreEqual(0, MusicAlbumNav.ResolveAdjacentIndex(5, 4, +1));
        }

        [Test]
        public void Adjacent_FirstToLast()
        {
            Assert.AreEqual(4, MusicAlbumNav.ResolveAdjacentIndex(5, 0, -1));
        }

        [Test]
        public void Adjacent_SingleTrack_Restarts()
        {
            Assert.AreEqual(0, MusicAlbumNav.ResolveAdjacentIndex(1, 0, +1));
            Assert.AreEqual(0, MusicAlbumNav.ResolveAdjacentIndex(1, 0, -1));
        }

        [Test]
        public void Adjacent_Empty_ReturnsInvalid()
        {
            Assert.AreEqual(-1, MusicAlbumNav.ResolveAdjacentIndex(0, 0, +1));
        }

        [Test]
        public void Adjacent_MidAlbum()
        {
            Assert.AreEqual(3, MusicAlbumNav.ResolveAdjacentIndex(5, 2, +1));
            Assert.AreEqual(1, MusicAlbumNav.ResolveAdjacentIndex(5, 2, -1));
        }

        [Test]
        public void ManualSkip_AdjacentNotReady_ReplaysCurrent()
        {
            // 0 ready, 1 not, 2 ready — next from 0 → replay 0
            Assert.AreEqual(0, MusicAlbumNav.ResolveManualSkipIndex(
                3, 0, +1, i => i != 1));
            Assert.AreEqual(2, MusicAlbumNav.ResolveManualSkipIndex(
                3, 2, -1, i => i != 1));
        }

        [Test]
        public void ManualSkip_AdjacentReady_Switches()
        {
            Assert.AreEqual(1, MusicAlbumNav.ResolveManualSkipIndex(
                3, 0, +1, _ => true));
            Assert.AreEqual(0, MusicAlbumNav.ResolveManualSkipIndex(
                3, 1, -1, _ => true));
        }

        [Test]
        public void ManualSkip_NullReadyPredicate_BehavesLikeAdjacent()
        {
            Assert.AreEqual(1, MusicAlbumNav.ResolveManualSkipIndex(3, 0, +1, null));
        }

        [Test]
        public void ListAdvance_StillOnlyLoopAndShuffle()
        {
            Assert.IsFalse(MusicPlayMode.IsListAdvanceMode(MusicPlayMode.Single));
            Assert.IsTrue(MusicPlayMode.IsListAdvanceMode(MusicPlayMode.Loop));
            Assert.IsTrue(MusicPlayMode.IsListAdvanceMode(MusicPlayMode.Shuffle));
        }
    }
}
