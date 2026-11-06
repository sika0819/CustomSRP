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
        const string ReadyVersion = "1.0.0";
        const string ReadyMd5 = "zone-prog-md5";
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
            BankDownloader.EditorRememberIdentity(ReadyStem, ReadyVersion, ReadyMd5, _readyPath);

            PresetBankCatalog.TryApplyUpgrade(new BankUpgradeData
            {
                banks = new List<BankRemoteInfo>
                {
                    new BankRemoteInfo
                    {
                        bankName = ReadyStem,
                        url = "https://example.test/ready.wsbk",
                        // 2 KB；偏小文件会被体积否决。就绪还要对上 version + md5。
                        size = "2",
                        version = ReadyVersion,
                        md5 = ReadyMd5,
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
            BankDownloader.EditorForgetIdentity(ReadyStem);
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

        [Test]
        public void AreBanksTransferComplete_TrueWhenHttpProgressFullBeforeReady()
        {
            // HTTP 写完 Progress=1、尚未 MarkLocalReady / State=Ready（MD5 中）→ 应视为传输齐，可关进度窗。
            Assert.IsFalse(BankDownloader.IsLocalReady(ActiveStem));
            BankDownloader.EditorPutActiveItem(new BankDownloadItem
            {
                BankName = ActiveStem,
                SizeKb = "3072",
                Progress = 1f,
                State = BankDownloadState.Downloading,
                Url = "https://example.test/active.wsbk",
            });

            Assert.IsTrue(
                UserBankDownloadFlow.AreBanksTransferComplete(new[] { ReadyStem, ActiveStem }));
        }

        [Test]
        public void AreBanksTransferComplete_FalseWhileStillTransferring()
        {
            BankDownloader.EditorPutActiveItem(new BankDownloadItem
            {
                BankName = ActiveStem,
                SizeKb = "3072",
                Progress = 0.5f,
                State = BankDownloadState.Downloading,
                Url = "https://example.test/active.wsbk",
            });

            Assert.IsFalse(
                UserBankDownloadFlow.AreBanksTransferComplete(new[] { ReadyStem, ActiveStem }));
        }
    }
}
