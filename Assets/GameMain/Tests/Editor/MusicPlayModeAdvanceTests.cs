using NUnit.Framework;

namespace Game.Tests
{
    /// <summary>PA-1189：锁屏曲终自动下一首仅列表/随机；单曲不推进。</summary>
    public sealed class MusicPlayModeAdvanceTests
    {
        [Test]
        public void ListAdvance_OnlyLoopAndShuffle()
        {
            Assert.IsFalse(MusicPlayMode.IsListAdvanceMode(MusicPlayMode.Single));
            Assert.IsTrue(MusicPlayMode.IsListAdvanceMode(MusicPlayMode.Loop));
            Assert.IsTrue(MusicPlayMode.IsListAdvanceMode(MusicPlayMode.Shuffle));
            Assert.IsFalse(MusicPlayMode.IsListAdvanceMode(0));
            Assert.IsFalse(MusicPlayMode.IsListAdvanceMode(99));
        }

        [Test]
        public void Next_CyclesSingleLoopShuffle()
        {
            Assert.AreEqual(MusicPlayMode.Loop, MusicPlayMode.Next(MusicPlayMode.Single));
            Assert.AreEqual(MusicPlayMode.Shuffle, MusicPlayMode.Next(MusicPlayMode.Loop));
            Assert.AreEqual(MusicPlayMode.Single, MusicPlayMode.Next(MusicPlayMode.Shuffle));
        }
    }
}
