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

            PresetBankCatalog.TryApplyUpgrade(new BankUpgradeData
            {
                banks = new List<BankRemoteInfo>
                {
                    new BankRemoteInfo
                    {
                        bankName = ReadyStem,
                        url = "https://example.test/ready.wsbk",
                        size = "2",
                        md5 = "",
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
    }
}
