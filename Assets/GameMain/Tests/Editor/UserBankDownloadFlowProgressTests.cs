using System.Collections.Generic;
using System.IO;
using Game.BoluoApi;
using NUnit.Framework;

namespace Game.Tests
{
    /// <summary>
    /// 区域进度窗只统计本次 banks：完成项出 Entries 后仍计入；不把全量体积算进进度。
    /// </summary>
    public sealed class UserBankDownloadFlowProgressTests
    {
        const string ReadyStem = "ZoneProg_Ready";
        const string ActiveStem = "ZoneProg_Active";
        const string MissingStem = "ZoneProg_Missing";
        const string OtherStem = "ZoneProg_OtherSilent";

        string _readyPath;

        [SetUp]
        public void SetUp()
        {
            PresetBankCatalog.Clear();
            BankDownloader.ResetSession();
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
                        // 2 KB；SetUp 写 2048 字节以满足 FileMatchesRemoteSize。
                        size = "2",
                        md5 = "",
                    },
                    new BankRemoteInfo
                    {
                        bankName = ActiveStem,
                        url = "https://example.test/active.wsbk",
                        size = "3072",
                        md5 = "",
                    },
                    new BankRemoteInfo
                    {
                        bankName = MissingStem,
                        url = "https://example.test/missing.wsbk",
                        size = "2048",
                        md5 = "",
                    },
                    new BankRemoteInfo
                    {
                        bankName = OtherStem,
                        url = "https://example.test/other.wsbk",
                        size = "99999",
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
            BankDownloader.ResetSession();
            PresetBankCatalog.Clear();
            BankDownloader.InvalidateLocalReadyCache();
        }

        [Test]
        public void AggregateBanksProgress_CountsLocalReadyEvenWithoutActiveEntry()
        {
            Assert.IsTrue(BankDownloader.IsLocalReady(ReadyStem));
            Assert.IsFalse(BankDownloader.TryGetActiveItem(ReadyStem, out _));

            UserBankDownloadFlow.AggregateBanksProgress(
                new[] { ReadyStem, MissingStem },
                out float downloaded,
                out float total,
                out float percent);

            // 2+2048 KB；Ready 满额，Missing 未入队 0。
            float readyMb = 2f / 1024f;
            float missingMb = 2048f / 1024f;
            Assert.AreEqual(readyMb + missingMb, total, 0.01f);
            Assert.AreEqual(readyMb, downloaded, 0.01f);
            Assert.AreEqual(readyMb / (readyMb + missingMb) * 100f, percent, 0.1f);
        }

        [Test]
        public void AggregateBanksProgress_UsesActiveProgressAndIgnoresBanksOutsideList()
        {
            BankDownloader.EditorPutActiveItem(new BankDownloadItem
            {
                BankName = ActiveStem,
                SizeKb = "3072",
                Progress = 0.5f,
                State = BankDownloadState.Downloading,
                Url = "https://example.test/active.wsbk",
            });

            UserBankDownloadFlow.AggregateBanksProgress(
                new[] { ReadyStem, ActiveStem },
                out float downloaded,
                out float total,
                out float percent);

            // 本区：ready(2KB) + 3MB@50%；全量 OtherStem 不得计入。
            float readyMb = 2f / 1024f;
            float activeMb = 3072f / 1024f;
            Assert.AreEqual(readyMb + activeMb, total, 0.01f);
            Assert.AreEqual(readyMb + activeMb * 0.5f, downloaded, 0.01f);
            Assert.AreEqual(
                (readyMb + activeMb * 0.5f) / (readyMb + activeMb) * 100f,
                percent,
                0.1f);
        }

        [Test]
        public void AggregateBanksProgress_EmptyList_IsZero()
        {
            UserBankDownloadFlow.AggregateBanksProgress(
                System.Array.Empty<string>(),
                out float downloaded,
                out float total,
                out float percent);
            Assert.AreEqual(0f, downloaded);
            Assert.AreEqual(0f, total);
            Assert.AreEqual(0f, percent);
        }
    }
}
