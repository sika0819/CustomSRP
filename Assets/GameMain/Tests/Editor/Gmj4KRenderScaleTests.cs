using NUnit.Framework;

namespace Game.Tests
{
    /// <summary>闺蜜机 4K 只按当前 Screen 把中间 RT 长边收到 1920，不按物理面板再乘一次。</summary>
    public sealed class Gmj4KRenderScaleTests
    {
        [Test]
        public void Full4KPanel_StandingLongEdgeIs1920()
        {
            float scale = IslandCameraSrpSetup.ScaleToLongEdge(3840, 2160, IslandCameraSrpSetup.Gmj4KColorLongEdge);
            Assert.AreEqual(0.5f, scale, 0.0001f);
            Assert.AreEqual(1920, (int)(3840 * scale));
        }

        [Test]
        public void Full4KPanel_WalkAndOverlayFollow1920Buffer()
        {
            float stand = IslandCameraSrpSetup.ScaleToLongEdge(3840, 2160, IslandCameraSrpSetup.Gmj4KColorLongEdge);
            float walk = stand * IslandCameraSrpSetup.Gmj4KWalkFactor;
            float overlay = stand * IslandCameraSrpSetup.Gmj4KOverlayFactor;
            Assert.AreEqual(1440, (int)(3840 * walk));
            Assert.AreEqual(960, (int)(3840 * overlay));
        }

        [Test]
        public void AlreadyScaledBackbuffer_DoesNotClampAgainstPhysicalPanel()
        {
            // 0.7 × 3840。系数相对这块缓冲，长边仍是 1920，而不是 0.5×2688。
            const int width = 2688;
            float scale = IslandCameraSrpSetup.ScaleToLongEdge(width, 1512, IslandCameraSrpSetup.Gmj4KColorLongEdge);
            Assert.AreEqual(1920f / width, scale, 0.0001f);
            Assert.Greater(scale, 0.5f);
        }

        [Test]
        public void ShortPanel_StaysAtOne()
        {
            Assert.AreEqual(1f, IslandCameraSrpSetup.ScaleToLongEdge(1920, 1080, IslandCameraSrpSetup.Gmj4KColorLongEdge));
            Assert.AreEqual(1f, IslandCameraSrpSetup.ScaleToLongEdge(0, 0, IslandCameraSrpSetup.Gmj4KColorLongEdge));
        }
    }

    /// <summary>闺蜜机标准版：站立画满，走路 0.75，界面 0.5，沉浸长边 1280。</summary>
    public sealed class GmjStandardRenderScaleTests
    {
        [Test]
        public void Full1080p_ImmersiveLongEdgeIs1280()
        {
            float scale = IslandCameraSrpSetup.ScaleToLongEdge(
                1920, 1080, IslandCameraSrpSetup.GmjStandardImmersiveLongEdge);
            Assert.AreEqual(1280f / 1920f, scale, 0.0001f);
            Assert.AreEqual(1280, (int)(1920 * scale));
        }

        [Test]
        public void Full1080p_StandWalkAndUiAreAbsolute()
        {
            Assert.AreEqual(1f, IslandCameraSrpSetup.GmjStandardStandScale);
            Assert.AreEqual(1440, (int)(1920 * IslandCameraSrpSetup.GmjStandardWalkScale));
            Assert.AreEqual(960, (int)(1920 * IslandCameraSrpSetup.GmjStandardUiScale));
        }

        [Test]
        public void BufferAlreadyUnder1280_ImmersiveStaysAtOne()
        {
            Assert.AreEqual(
                1f,
                IslandCameraSrpSetup.ScaleToLongEdge(
                    1280, 720, IslandCameraSrpSetup.GmjStandardImmersiveLongEdge));
        }
    }
}
