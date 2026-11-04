using System.Collections.Generic;
using System.IO;
using Game.BoluoApi;
using NUnit.Framework;

namespace Game.Tests
{
    /// <summary>
    /// Critical 申请窗：盘上已满足 catalog 时不得因首包版本不等再弹。
    /// </summary>
    public sealed class BankUpgradeCriticalSkipTests
    {
        const string Stem = "CriticalSkip_ReadyBank";

        string _path;

        [SetUp]
        public void SetUp()
        {
            PresetBankCatalog.Clear();
            BankDownloader.InvalidateLocalReadyCache();
            AudioEnginePaths.EnsureWritableDirs();
            _path = Path.Combine(
                AudioEnginePaths.PersistentBankPath,
                Stem + AudioEnginePaths.BankExtension);
            TryDelete(_path);
        }

        [TearDown]
        public void TearDown()
        {
            TryDelete(_path);
            PresetBankCatalog.Clear();
            BankDownloader.InvalidateLocalReadyCache();
        }

        [Test]
        public void CriticalNeedsHttp_LocalReady_False_EvenWithRemoteVersion()
        {
            PresetBankCatalog.TryApplyUpgrade(new BankUpgradeData
            {
                banks = new List<BankRemoteInfo>
                {
                    new BankRemoteInfo
                    {
                        bankName = Stem,
                        url = "https://example.test/critical.wsbk",
                        size = "2",
                        version = "9.9.9",
                        md5 = "",
                    },
                },
            });
            File.WriteAllBytes(_path, new byte[2048]);
            BankDownloader.InvalidateLocalReadyCache(Stem);
            Assert.IsTrue(BankDownloader.IsLocalReady(Stem));

            Assert.IsFalse(
                BankUpgradeBootstrap.EditorCriticalNeedsHttpUpdate(Stem),
                "已下完的 Critical 不得因版本字段再申请下载");
        }

        [Test]
        public void CriticalNeedsHttp_MissingLocal_True()
        {
            PresetBankCatalog.TryApplyUpgrade(new BankUpgradeData
            {
                banks = new List<BankRemoteInfo>
                {
                    new BankRemoteInfo
                    {
                        bankName = Stem,
                        url = "https://example.test/critical.wsbk",
                        size = "2",
                        version = "9.9.9",
                        md5 = "",
                    },
                },
            });
            BankDownloader.InvalidateLocalReadyCache(Stem);
            Assert.IsFalse(BankDownloader.IsLocalReady(Stem));

            Assert.IsTrue(BankUpgradeBootstrap.EditorCriticalNeedsHttpUpdate(Stem));
        }

        [Test]
        public void CriticalNeedsHttp_MissingLocal_IgnoresStreamingVersionFieldAlone()
        {
            // 回归：仅 ProjectMetadata 版本一致、盘上无文件时仍须 HTTP，
            // 否则 Critical 被跳过 + Rest 排除 Critical 名 → 预设永不下。
            PresetBankCatalog.TryApplyUpgrade(new BankUpgradeData
            {
                banks = new List<BankRemoteInfo>
                {
                    new BankRemoteInfo
                    {
                        bankName = Stem,
                        url = "https://example.test/critical.wsbk",
                        size = "2",
                        version = "1.0.0",
                        md5 = "",
                    },
                },
            });
            BankDownloader.InvalidateLocalReadyCache(Stem);
            Assert.IsFalse(BankDownloader.IsLocalReady(Stem));
            Assert.IsTrue(
                BankUpgradeBootstrap.EditorCriticalNeedsHttpUpdate(Stem),
                "无落盘不得因 version 字段跳过 Critical HTTP");
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
