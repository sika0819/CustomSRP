using System.Collections.Generic;
using System.IO;
using Game.BoluoApi;
using NUnit.Framework;
using UnityEngine;

namespace Game.Tests
{
    /// <summary>
    /// 跨入口跳过：FilterPending / PendingKilobytes 不含已就绪 bank。
    /// </summary>
    public sealed class UserBankDownloadFlowSkipTests
    {
        const string ReadyStem = "FlowSkip_ReadyBank";
        const string PendingStem = "FlowSkip_PendingBank";
        const string ReadyVersion = "1.0.0";
        const string ReadyMd5 = "flow-skip-md5";

        string _readyPath;

        [SetUp]
        public void SetUp()
        {
            PresetBankCatalog.Clear();
            BankDownloader.InvalidateLocalReadyCache();
            AudioEnginePaths.EnsureWritableDirs();

            _readyPath = Path.Combine(
                AudioEnginePaths.PersistentBankPath,
                ReadyStem + AudioEnginePaths.BankExtension);
            File.WriteAllBytes(_readyPath, new byte[2048]);
            BankDownloader.EditorRememberIdentity(ReadyStem, ReadyVersion, ReadyMd5, _readyPath);

            PresetBankCatalog.TryApplyUpgrade(new BankUpgradeData
            {
                banks = new List<BankRemoteInfo>
                {
                    new BankRemoteInfo
                    {
                        bankName = ReadyStem,
                        url = "https://example.test/ready.wsbk",
                        size = "2",
                        version = ReadyVersion,
                        md5 = ReadyMd5,
                    },
                    new BankRemoteInfo
                    {
                        bankName = PendingStem,
                        url = "https://example.test/pending.wsbk",
                        size = "1024",
                        md5 = "",
                    },
                },
            });
            BankDownloader.InvalidateLocalReadyCache();
        }

        [TearDown]
        public void TearDown()
        {
            if (!string.IsNullOrEmpty(_readyPath) && File.Exists(_readyPath))
                File.Delete(_readyPath);
            BankDownloader.EditorForgetIdentity(ReadyStem);
            PresetBankCatalog.Clear();
            BankDownloader.InvalidateLocalReadyCache();
        }

        [Test]
        public void FilterPending_DropsLocalReady_KeepsMissing()
        {
            Assert.IsTrue(BankDownloader.IsLocalReady(ReadyStem));
            Assert.IsFalse(BankDownloader.IsLocalReady(PendingStem));

            string[] pending = UserBankDownloadFlow.FilterPending(
                new[] { ReadyStem, PendingStem, ReadyStem });

            Assert.AreEqual(1, pending.Length);
            Assert.AreEqual(PendingStem, pending[0]);
        }

        [Test]
        public void FilterPending_AllReady_ReturnsEmpty()
        {
            string[] pending = UserBankDownloadFlow.FilterPending(new[] { ReadyStem });
            Assert.AreEqual(0, pending.Length);
        }

        [Test]
        public void PendingKilobytes_SkipsLocalReady()
        {
            long kb = DownloadNetworkGate.PendingKilobytes(
                new List<string> { ReadyStem, PendingStem });
            Assert.AreEqual(1024L, kb);
        }

        [Test]
        public void Start_AllReady_InvokesCallbacksWithoutBusy()
        {
            int starting = 0;
            int banksDone = 0;
            int complete = 0;

            UserBankDownloadFlow.Start(
                new[] { ReadyStem },
                onComplete: () => complete++,
                onBanksDone: _ => banksDone++,
                onDownloadStarting: () => starting++);

            Assert.AreEqual(1, starting);
            Assert.AreEqual(1, banksDone);
            Assert.AreEqual(1, complete);
            Assert.IsFalse(UserBankDownloadFlow.IsBusy);
        }

        [Test]
        public void BusyToastText_IsProductCopy()
        {
            Assert.AreEqual("已有资源包在下载中", UserBankDownloadFlow.BusyToastText);
        }

        [Test]
        public void SoftBusy_AllowsPreempt_HardBusyRejects()
        {
            UserBankDownloadFlow.EditorForceBusy(busy: false);
            Assert.IsFalse(UserBankDownloadFlow.ShouldRejectNewBatch(preemptBusyBatch: true));
            Assert.IsFalse(UserBankDownloadFlow.ShouldRejectNewBatch(preemptBusyBatch: false));

            // 软占用（无进度票）：列表抢占放行，非抢占仍拒。
            UserBankDownloadFlow.EditorForceBusy(busy: true, activeTicket: 0);
            Assert.IsFalse(UserBankDownloadFlow.ShouldRejectNewBatch(preemptBusyBatch: true));
            Assert.IsTrue(UserBankDownloadFlow.ShouldRejectNewBatch(preemptBusyBatch: false));

            // 区域进度窗硬占用：一律拒。
            UserBankDownloadFlow.EditorForceBusy(busy: true, activeTicket: 42);
            Assert.IsTrue(UserBankDownloadFlow.ShouldRejectNewBatch(preemptBusyBatch: true));
            Assert.IsTrue(UserBankDownloadFlow.ShouldRejectNewBatch(preemptBusyBatch: false));

            UserBankDownloadFlow.EditorForceBusy(busy: false);
        }
    }
}
