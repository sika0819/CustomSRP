using NUnit.Framework;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game.Tests
{
    /// <summary>PA-1186：下载进度环单调不回退（对齐 Preset）。</summary>
    public sealed class ArcProgressRingTests
    {
        [Test]
        public void FillAmount_ClampsAndNeverDecreasesUntilReset()
        {
            var ring = new ArcProgressRing();
            ring.FillAmount = 0.4f;
            Assert.AreEqual(0.4f, ring.FillAmount, 1e-4f);

            ring.FillAmount = 0.2f; // 回退被峰值挡住
            Assert.AreEqual(0.4f, ring.FillAmount, 1e-4f);

            ring.FillAmount = 0.85f;
            Assert.AreEqual(0.85f, ring.FillAmount, 1e-4f);

            ring.FillAmount = 2f;
            Assert.AreEqual(1f, ring.FillAmount, 1e-4f);

            ring.ResetProgress();
            Assert.AreEqual(0f, ring.FillAmount, 1e-4f);
            ring.FillAmount = 0.1f;
            Assert.AreEqual(0.1f, ring.FillAmount, 1e-4f);
        }

        [Test]
        public void DefaultColors_AreOpaqueFore()
        {
            var ring = new ArcProgressRing();
            // 构造后可赋值；浅色卡用深 track + 绿 fore（MusicForm 接线侧）。
            ring.ForeColor = new Color(0f, 0.639f, 0.459f, 1f);
            ring.TrackColor = new Color(24f / 255f, 24f / 255f, 25f / 255f, 0.28f);
            Assert.AreEqual(1f, ring.ForeColor.a, 1e-4f);
            Assert.Greater(ring.TrackColor.a, 0f);
        }
    }
}
