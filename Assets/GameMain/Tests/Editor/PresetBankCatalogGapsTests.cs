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
                "空 url Skipped 不得当 Ready，否则预设会假进场/假翻牌");
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
