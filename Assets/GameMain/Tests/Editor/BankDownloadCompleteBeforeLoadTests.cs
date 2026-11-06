using System.Collections.Generic;
using System.IO;
using Game.BoluoApi;
using NUnit.Framework;

namespace Game.Tests
{
    /// <summary>
    /// 契约：落盘 Ready / OnBankLocalReady 不得依赖「安全灌引擎」门控。
    /// 否则 Prefill / 加载罩期间 HTTP 已 100% 仍不 FireDone，重启才走 CompleteAlreadyReady。
    /// </summary>
    public sealed class BankDownloadCompleteBeforeLoadTests
    {
        const string Stem = "CompleteBeforeLoad_Bank";
        const string Version = "1.0.0";
        const string Md5 = "complete-before-load-md5";

        string _path;

        [SetUp]
        public void SetUp()
        {
            PresetBankCatalog.Clear();
            BankDownloader.ResetSession();
            BankDownloader.InvalidateLocalReadyCache();
            AudioEnginePaths.EnsureWritableDirs();
            _path = Path.Combine(
                AudioEnginePaths.PersistentBankPath,
                Stem + AudioEnginePaths.BankExtension);
            File.WriteAllBytes(_path, new byte[2048]);
            BankDownloader.EditorRememberIdentity(Stem, Version, Md5, _path);
            PresetBankCatalog.TryApplyUpgrade(new BankUpgradeData
            {
                banks = new List<BankRemoteInfo>
                {
                    new BankRemoteInfo
                    {
                        bankName = Stem,
                        url = "https://example.test/complete-before-load.wsbk",
                        size = "2",
                        version = Version,
                        md5 = Md5,
                    },
                },
            });
            BankDownloader.InvalidateLocalReadyCache();
        }

        [TearDown]
        public void TearDown()
        {
            if (!string.IsNullOrEmpty(_path) && File.Exists(_path))
                File.Delete(_path);
            BankDownloader.EditorForgetIdentity(Stem);
            BankDownloader.ResetSession();
            PresetBankCatalog.Clear();
            BankDownloader.InvalidateLocalReadyCache();
        }

        [Test]
        public void LocalReady_IsTrue_BeforeAnyEngineLoad()
        {
            Assert.IsTrue(
                BankDownloader.IsLocalReady(Stem),
                "盘上齐即 Ready；不得等 WaitUntilSafeToLoadBank / TryLoadBank");
        }

        [Test]
        public void FilterPending_DropsDiskReady_WithoutEngineLoad()
        {
            string[] pending = UserBankDownloadFlow.FilterPending(new[] { Stem });
            Assert.AreEqual(0, pending.Length);
        }

        [Test]
        public void NotifyBankReady_MarksLocalReady_SurvivesFileDelete()
        {
            // 完成通知写正向缓存：Entries.Remove / 探针失败后 UI 仍立刻 IsLocalReady。
            string fired = null;
            void OnReady(string stem) => fired = stem;
            BankDownloader.OnBankLocalReady += OnReady;
            try
            {
                BankDownloader.InvalidateLocalReadyCache(Stem);
                BankDownloader.EditorNotifyBankReady(Stem);
                Assert.AreEqual(Stem, fired);
                Assert.IsTrue(BankDownloader.IsLocalReady(Stem));

                File.Delete(_path);
                _path = null;
                Assert.IsTrue(
                    BankDownloader.IsLocalReady(Stem),
                    "MarkLocalReady 正向缓存不得因盘文件已删而立刻变 false");
            }
            finally
            {
                BankDownloader.OnBankLocalReady -= OnReady;
            }
        }
    }
}
