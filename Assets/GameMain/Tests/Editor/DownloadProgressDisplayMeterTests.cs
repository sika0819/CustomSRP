using NUnit.Framework;

namespace Game.Tests
{
    public sealed class DownloadProgressDisplayMeterTests
    {
        [Test]
        public void FirstTick_CommitsUnknownSpeed()
        {
            var meter = new DownloadProgressDisplayMeter();
            Assert.IsTrue(meter.Tick(0f, 0f, completed: false));
            Assert.Less(meter.DisplaySpeedMbps, 0f);
            Assert.AreEqual(string.Empty, meter.ResolveEta(0f, 100f, completed: false));
        }

        [Test]
        public void SpeedAndEta_CommitAboutOncePerSecond()
        {
            var meter = new DownloadProgressDisplayMeter();
            Assert.IsTrue(meter.Tick(0f, 0f, completed: false));

            // 采样窗内不提交
            Assert.IsFalse(meter.Tick(0.2f, 1f, completed: false));
            Assert.IsFalse(meter.Tick(0.4f, 2f, completed: false));

            // 0.5s 起有样本，但仍未到 1s 提交点
            Assert.IsFalse(meter.Tick(0.6f, 3f, completed: false));
            Assert.IsFalse(meter.Tick(0.9f, 4.5f, completed: false));

            Assert.IsTrue(meter.Tick(1.0f, 5f, completed: false));
            Assert.Greater(meter.DisplaySpeedMbps, 0f);

            string eta = meter.ResolveEta(5f, 100f, completed: false);
            Assert.IsTrue(eta.StartsWith("剩余"), eta);
        }

        [Test]
        public void Eta_DoesNotJumpUpOnSmallBlip()
        {
            var meter = new DownloadProgressDisplayMeter();
            // t=0 起步；匀速约 5MB/s → 1s 提交后剩余 ~19s（100-5）
            meter.Tick(0f, 0f, completed: false);
            meter.Tick(0.5f, 2.5f, completed: false);
            Assert.IsTrue(meter.Tick(1.0f, 5f, completed: false));
            string first = meter.ResolveEta(5f, 100f, completed: false);
            Assert.AreEqual("剩余19s", first);

            // 轻微减速（~4MB/s）：瞬时剩余只上飘一两秒，低于上跳阈值，保持原文案
            meter.Tick(1.5f, 7f, completed: false);
            Assert.IsTrue(meter.Tick(2.0f, 9f, completed: false));
            string sticky = meter.ResolveEta(9f, 100f, completed: false);
            Assert.AreEqual(first, sticky);
        }

        [Test]
        public void Eta_CanDecreaseWithFasterSpeed()
        {
            var meter = new DownloadProgressDisplayMeter();
            meter.Tick(0f, 0f, completed: false);
            meter.Tick(0.5f, 0.5f, completed: false);
            Assert.IsTrue(meter.Tick(1.0f, 1f, completed: false));
            string slow = meter.ResolveEta(1f, 100f, completed: false);

            meter.Tick(1.5f, 6f, completed: false);
            Assert.IsTrue(meter.Tick(2.0f, 11f, completed: false));
            string faster = meter.ResolveEta(11f, 100f, completed: false);

            Assert.AreNotEqual(slow, faster);
            Assert.IsTrue(faster.StartsWith("剩余"), faster);
        }

        [Test]
        public void Completed_ForcesCommitAndDoneLabel()
        {
            var meter = new DownloadProgressDisplayMeter();
            meter.Tick(0f, 0f, completed: false);
            Assert.IsTrue(meter.Tick(0.2f, 10f, completed: true));
            Assert.AreEqual("完成", meter.ResolveEta(100f, 100f, completed: true));
        }

        [Test]
        public void Reset_ClearsStickyState()
        {
            var meter = new DownloadProgressDisplayMeter();
            meter.Tick(0f, 0f, completed: false);
            meter.Tick(0.5f, 2.5f, completed: false);
            meter.Tick(1.0f, 5f, completed: false);
            meter.ResolveEta(5f, 100f, completed: false);

            meter.Reset();
            Assert.Less(meter.DisplaySpeedMbps, 0f);
            Assert.AreEqual(string.Empty, meter.ResolveEta(5f, 100f, completed: false));
        }
    }
}
