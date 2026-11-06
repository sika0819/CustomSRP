using System.Collections.Generic;
using System.IO;
using Game.BoluoApi;
using NUnit.Framework;

namespace Game.Tests
{
    /// <summary>
    /// catalog 无条目 / url 空 → 不可下；有 url 缺盘 → 可下。
    /// </summary>
    public sealed class PresetBankCatalogGapsTests
    {
        const string MissingStem = "CatalogGap_MissingBank";
        const string EmptyUrlStem = "CatalogGap_EmptyUrlBank";
        const string PendingStem = "CatalogGap_PendingBank";
        const string ReadyStem = "CatalogGap_ReadyBank";
        const string ReadyVersion = "1.0.0";
        const string ReadyMd5 = "gap-ready-md5";

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
                        bankName = EmptyUrlStem,
                        url = "",
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
                    new BankRemoteInfo
                    {
                        bankName = ReadyStem,
                        url = "https://example.test/ready.wsbk",
                        size = "2",
                        version = ReadyVersion,
                        md5 = ReadyMd5,
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
        public void IsUndownloadable_NoCatalogEntry_True()
        {
            Assert.IsFalse(PresetBankCatalog.TryGet(MissingStem, out _));
            Assert.IsTrue(PresetBankCatalogGaps.IsUndownloadable(MissingStem));
        }

        [Test]
        public void IsUndownloadable_EmptyUrl_True()
        {
            Assert.IsTrue(PresetBankCatalogGaps.IsUndownloadable(EmptyUrlStem));
        }

        [Test]
        public void IsUndownloadable_HasUrlMissingDisk_False()
        {
            Assert.IsFalse(BankDownloader.IsLocalReady(PendingStem));
            Assert.IsFalse(PresetBankCatalogGaps.IsUndownloadable(PendingStem));
        }

        [Test]
        public void IsUndownloadable_LocalReady_False()
        {
            Assert.IsTrue(BankDownloader.IsLocalReady(ReadyStem));
            Assert.IsFalse(PresetBankCatalogGaps.IsUndownloadable(ReadyStem));
        }

        [Test]
        public void IsLocalReady_UrlEmptySkipped_NotTreatedAsReady()
        {
            BankDownloader.EditorPutActiveItem(new BankDownloadItem
            {
                BankName = EmptyUrlStem,
                Url = "",
                State = BankDownloadState.Skipped,
                Error = BankDownloadItem.UrlEmptyError,
            });

            Assert.IsFalse(
                BankDownloader.IsLocalReady(EmptyUrlStem),
                "空 url 且没有本地文件时不得当 Ready");
        }

        [Test]
        public void IsLocalReady_EmptyUrlWithLocalFile_UsesLocal()
        {
            string path = Path.Combine(
                AudioEnginePaths.PersistentBankPath,
                EmptyUrlStem + AudioEnginePaths.BankExtension);
            File.WriteAllBytes(path, new byte[2048]);
            try
            {
                BankDownloader.InvalidateLocalReadyCache(EmptyUrlStem);
                Assert.IsTrue(BankDownloader.IsLocalReady(EmptyUrlStem));
            }
            finally
            {
                if (File.Exists(path))
                    File.Delete(path);
                BankDownloader.InvalidateLocalReadyCache(EmptyUrlStem);
            }
        }

        [Test]
        public void IsLocalReady_EmptyMd5WithLocalFile_UsesLocal()
        {
            string path = Path.Combine(
                AudioEnginePaths.PersistentBankPath,
                PendingStem + AudioEnginePaths.BankExtension);
            File.WriteAllBytes(path, new byte[1024 * 1024]);
            try
            {
                BankDownloader.InvalidateLocalReadyCache(PendingStem);
                Assert.IsTrue(BankDownloader.IsLocalReady(PendingStem));
                Assert.IsFalse(BankUpgradeBootstrap.EditorCriticalNeedsHttpUpdate(PendingStem));
            }
            finally
            {
                if (File.Exists(path))
                    File.Delete(path);
                BankDownloader.InvalidateLocalReadyCache(PendingStem);
            }
        }

        [Test]
        public void ArmUserRetry_ResetsFailedEntryPastRequeueCap()
        {
            BankDownloader.EditorPutActiveItem(new BankDownloadItem
            {
                BankName = PendingStem,
                Url = "https://example.test/pending.wsbk",
                State = BankDownloadState.Failed,
                Error = "no network",
            });
            BankDownloader.EditorSetRequeueCount(PendingStem, 2);
            BankDownloader.EditorNoteTerminalFailure(PendingStem);

            Assert.IsTrue(BankDownloader.HasTerminalFailure(PendingStem));
            Assert.IsTrue(BankDownloader.EditorShouldFinishWithoutRestart(
                BankDownloadState.Failed, "no network", 2, userRetry: false));
            Assert.IsFalse(BankDownloader.EditorShouldFinishWithoutRestart(
                BankDownloadState.Failed, "no network", 2, userRetry: true));

            BankDownloader.ArmUserRetry(new[] { PendingStem });

            Assert.IsFalse(BankDownloader.HasTerminalFailure(PendingStem));
            Assert.IsTrue(BankDownloader.TryGetActiveItem(PendingStem, out BankDownloadItem item));
            Assert.AreEqual(BankDownloadState.Pending, item.State);
            Assert.IsNull(item.Error);
            Assert.AreEqual(0, BankDownloader.EditorGetRequeueCount(PendingStem));
        }

        [Test]
        public void UserRetry_DoesNotTreatUrlEmptySkipAsFinished()
        {
            Assert.IsFalse(BankDownloader.EditorShouldFinishWithoutRestart(
                BankDownloadState.Skipped,
                BankDownloadItem.UrlEmptyError,
                0,
                userRetry: true));
            Assert.IsTrue(BankDownloader.EditorShouldFinishWithoutRestart(
                BankDownloadState.Skipped,
                BankDownloadItem.UrlEmptyError,
                0,
                userRetry: false));
        }

        [Test]
        public void IsLocalReady_NonUrlEmptySkipped_StillReady()
        {
            BankDownloader.EditorPutActiveItem(new BankDownloadItem
            {
                BankName = ReadyStem,
                Url = "https://example.test/ready.wsbk",
                State = BankDownloadState.Skipped,
                Error = null,
                SkipReason = "streaming version match 1.0.0",
            });

            Assert.IsTrue(
                BankDownloader.IsLocalReady(ReadyStem),
                "streaming version match 等 Skipped 仍算就绪");
        }
    }
}
