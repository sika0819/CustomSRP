using System.Collections.Generic;
using System.IO;
using Game.BoluoApi;
using NUnit.Framework;

namespace Game.Tests
{
    /// <summary>
    /// IsLocalReady / Probe：残缺 persistent .wsbk 回退其它路径；catalog size 对齐。
    /// </summary>
    public sealed class BankLocalReadyProbeTests
    {
        const string Stem = "ProbeReady_UiTest";

        string _partialWsbk;
        string _goodWb;

        [SetUp]
        public void SetUp()
        {
            PresetBankCatalog.Clear();
            BankDownloader.InvalidateLocalReadyCache();
            AudioEnginePaths.EnsureWritableDirs();

            _partialWsbk = Path.Combine(
                AudioEnginePaths.PersistentBankPath,
                Stem + AudioEnginePaths.BankExtension);
            _goodWb = Path.Combine(
                AudioEnginePaths.PersistentBankPath,
                Stem + AudioEnginePaths.LegacyBankExtension);

            // Catalog：期望约 2KB；半截 .wsbk 远小于 90%。
            PresetBankCatalog.TryApplyUpgrade(new BankUpgradeData
            {
                banks = new List<BankRemoteInfo>
                {
                    new BankRemoteInfo
                    {
                        bankName = Stem,
                        url = "https://example.test/probe.wsbk",
                        size = "2",
                        md5 = "",
                    },
                },
            });
        }

        [TearDown]
        public void TearDown()
        {
            TryDelete(_partialWsbk);
            TryDelete(_goodWb);
            PresetBankCatalog.Clear();
            BankDownloader.InvalidateLocalReadyCache();
        }

        [Test]
        public void Probe_PartialWsbk_FallsBackToGoodWb()
        {
            File.WriteAllBytes(_partialWsbk, new byte[64]);
            File.WriteAllBytes(_goodWb, new byte[2048]);
            BankDownloader.InvalidateLocalReadyCache(Stem);

            Assert.IsTrue(
                BankDownloader.ProbeLocalSatisfiesCatalog(Stem),
                "残缺 .wsbk 应回退到同目录完整 .wb");
            Assert.IsTrue(BankDownloader.IsLocalReady(Stem));
        }

        [Test]
        public void Probe_PartialWsbkAlone_NotReady()
        {
            File.WriteAllBytes(_partialWsbk, new byte[64]);
            BankDownloader.InvalidateLocalReadyCache(Stem);

            Assert.IsFalse(BankDownloader.ProbeLocalSatisfiesCatalog(Stem));
            Assert.IsFalse(BankDownloader.IsLocalReady(Stem));
        }

        [Test]
        public void Probe_FullWsbk_Ready()
        {
            File.WriteAllBytes(_partialWsbk, new byte[2048]);
            BankDownloader.InvalidateLocalReadyCache(Stem);

            Assert.IsTrue(BankDownloader.ProbeLocalSatisfiesCatalog(Stem));
            Assert.IsTrue(BankDownloader.IsLocalReady(Stem));
        }

        [Test]
        public void IsLocalReady_NoRemote_FileExists_Ready()
        {
            PresetBankCatalog.Clear();
            File.WriteAllBytes(_partialWsbk, new byte[] { 1, 2, 3 });
            BankDownloader.InvalidateLocalReadyCache(Stem);

            Assert.IsTrue(BankDownloader.IsLocalReady(Stem));
        }

        static void TryDelete(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return;
            try
            {
                File.Delete(path);
            }
            catch
            {
                // ignore
            }
        }
    }
}
