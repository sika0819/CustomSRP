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
            Assert.IsFalse(meter.HasPublishedEta);
            Assert.AreEqual(string.Empty, meter.ResolveEta(0f, 100f, completed: false));
        }

        [Test]
        public void ColdStart_PublishesAfterFirstShortSample()
        {
            var meter = new DownloadProgressDisplayMeter();
            meter.Tick(0f, 0f, completed: false);
            // ~0.15s 短窗有增量 → 立刻 commit 并公布 ETA
            Assert.IsTrue(meter.Tick(0.15f, 0.05f, completed: false));
            Assert.Greater(meter.DisplaySpeedMbps, 0f);
            string eta = meter.ResolveEta(0.05f, 100f, completed: false);
            Assert.IsTrue(meter.HasPublishedEta);
            Assert.IsTrue(eta.StartsWith("剩余"), eta);
            Assert.AreNotEqual("剩余1s", eta);
        }

        [Test]
        public void ColdStart_ZeroDeltaDoesNotCountAsSpeedSample()
        {
            var meter = new DownloadProgressDisplayMeter();
            meter.Tick(0f, 0f, completed: false);
            Assert.IsFalse(meter.Tick(0.15f, 0f, completed: false));
            Assert.IsTrue(meter.Tick(1.0f, 0f, completed: false));
            Assert.AreEqual(string.Empty, meter.ResolveEta(5f, 100f, completed: false));
            Assert.IsFalse(meter.HasPublishedEta);
        }

        [Test]
        public void SpeedBurst_DoesNotCrashEtaToOneSecond()
        {
            var meter = new DownloadProgressDisplayMeter();
            meter.Tick(0f, 0f, completed: false);
            Assert.IsTrue(meter.Tick(0.15f, 0.75f, completed: false));
            string first = meter.ResolveEta(0.75f, 100f, completed: false);
            Assert.IsTrue(meter.HasPublishedEta);
            Assert.IsTrue(first.StartsWith("剩余"), first);
            Assert.AreNotEqual("剩余1s", first);

            meter.Tick(0.65f, 50f, completed: false);
            Assert.IsTrue(meter.Tick(1.15f, 55f, completed: false));
            string afterBurst = meter.ResolveEta(55f, 100f, completed: false);
            Assert.AreNotEqual("剩余1s", afterBurst);
            Assert.IsTrue(afterBurst.StartsWith("剩余"), afterBurst);
        }

        [Test]
        public void Eta_CanDecreaseGraduallyWithFasterSpeed()
        {
            var meter = new DownloadProgressDisplayMeter();
            // 用秒级剩余起步：分钟桶（60–119s→「剩余2m」）里一次限幅校准看不出变化。
            meter.Tick(0f, 80f, completed: false);
            Assert.IsTrue(meter.Tick(0.15f, 80.15f, completed: false));
            string slow = meter.ResolveEta(80.15f, 100f, completed: false);
            Assert.IsTrue(meter.HasPublishedEta);
            Assert.AreEqual("剩余20s", slow);

            meter.Tick(0.65f, 88f, completed: false);
            Assert.IsTrue(meter.Tick(1.15f, 96f, completed: false));
            string faster = meter.ResolveEta(96f, 100f, completed: false);

            Assert.AreNotEqual(slow, faster);
            Assert.IsTrue(faster.StartsWith("剩余"), faster);
            Assert.AreNotEqual("剩余1s", faster);
        }

        [Test]
        public void NearDone_AllowsOneSecondEta()
        {
            var meter = new DownloadProgressDisplayMeter();
            meter.Tick(0f, 98f, completed: false);
            Assert.IsTrue(meter.Tick(0.15f, 99.2f, completed: false));
            string eta = meter.ResolveEta(99.2f, 100f, completed: false);
            Assert.IsTrue(meter.HasPublishedEta);
            Assert.IsTrue(eta == "剩余1s" || eta == "剩余2s", eta);
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
            meter.Tick(0.15f, 0.75f, completed: false);
            meter.ResolveEta(0.75f, 100f, completed: false);
            Assert.IsTrue(meter.HasPublishedEta);

            meter.Reset();
            Assert.Less(meter.DisplaySpeedMbps, 0f);
            Assert.IsFalse(meter.HasPublishedEta);
            Assert.AreEqual(string.Empty, meter.ResolveEta(5f, 100f, completed: false));
        }

        [Test]
        public void OpenMidDownload_UsesCurrentBytesAsBaseline()
        {
            var meter = new DownloadProgressDisplayMeter();
            // 打开时已经下了 40MB：这一笔只是锚点。
            meter.Tick(0f, 40f, completed: false);
            Assert.IsFalse(meter.HasPublishedEta);
            // 0.2s 后再 +0.4MB ≈ 2MB/s，剩余约 30s，不是「剩余2s」。
            Assert.IsTrue(meter.Tick(0.2f, 40.4f, completed: false));
            string eta = meter.ResolveEta(40.4f, 100f, completed: false);
            Assert.IsTrue(eta == "剩余29s" || eta == "剩余30s", eta);
        }

        [Test]
        public void ZeroThenImmediateExistingBytes_DoesNotCountAsSpeed()
        {
            var meter = new DownloadProgressDisplayMeter();
            meter.Tick(0f, 0f, completed: false);
            // 同一短窗里从 0 跳到已下载量：改锚点，不当成 0.01s 内下完 40MB。
            Assert.IsFalse(meter.Tick(0.01f, 40f, completed: false));
            Assert.IsFalse(meter.HasPublishedEta);
            Assert.AreEqual(string.Empty, meter.ResolveEta(40f, 100f, completed: false));

            Assert.IsTrue(meter.Tick(0.21f, 40.4f, completed: false));
            string eta = meter.ResolveEta(40.4f, 100f, completed: false);
            Assert.IsTrue(eta == "剩余29s" || eta == "剩余30s", eta);
        }
    }
}
