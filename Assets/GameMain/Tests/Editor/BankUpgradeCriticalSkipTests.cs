using System;
using System.Collections.Generic;
using System.IO;
using Game.BoluoApi;
using NUnit.Framework;

namespace Game.Tests
{
    /// <summary>
    /// Critical 申请窗：盘上文件须同时对上服务端 version 与 md5 才跳过 HTTP。
    /// </summary>
    public sealed class BankUpgradeCriticalSkipTests
    {
        const string Stem = "CriticalSkip_ReadyBank";
        const string MatchVersion = "1.0.0";
        const string MatchMd5 = "abc123";

        string _path;

        [SetUp]
        public void SetUp()
        {
            PresetBankCatalog.Clear();
            BankDownloader.InvalidateLocalReadyCache();
            BankDownloader.EditorForgetIdentity(Stem);
            StreamingBankCatalog.EditorRemove(Stem);
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
            BankDownloader.EditorForgetIdentity(Stem);
            StreamingBankCatalog.EditorRemove(Stem);
            PresetBankCatalog.Clear();
            BankDownloader.InvalidateLocalReadyCache();
        }

        [Test]
        public void CriticalNeedsHttp_FileWithoutIdentity_True()
        {
            Apply("9.9.9", "server-md5");
            File.WriteAllBytes(_path, new byte[2048]);
            BankDownloader.InvalidateLocalReadyCache(Stem);

            Assert.IsFalse(BankDownloader.IsLocalReady(Stem));
            Assert.IsTrue(
                BankUpgradeBootstrap.EditorCriticalNeedsHttpUpdate(Stem),
                "没有已接受的 version/md5 时，盘上旧文件不得跳过 Critical HTTP");
        }

        [Test]
        public void CriticalNeedsHttp_VersionAndMd5Match_False()
        {
            Apply(MatchVersion, MatchMd5);
            File.WriteAllBytes(_path, new byte[2048]);
            BankDownloader.EditorRememberIdentity(Stem, MatchVersion, MatchMd5, _path);
            BankDownloader.InvalidateLocalReadyCache(Stem);

            Assert.IsTrue(BankDownloader.IsLocalReady(Stem));
            Assert.IsFalse(BankUpgradeBootstrap.EditorCriticalNeedsHttpUpdate(Stem));
        }

        [Test]
        public void CriticalNeedsHttp_VersionChangedMd5Same_UpdatesCacheWithoutHttp()
        {
            Apply("9.9.9", MatchMd5);
            File.WriteAllBytes(_path, new byte[2048]);
            BankDownloader.EditorRememberIdentity(Stem, MatchVersion, MatchMd5, _path);
            BankDownloader.InvalidateLocalReadyCache(Stem);

            Assert.IsTrue(BankDownloader.IsLocalReady(Stem));
            Assert.IsTrue(BankDownloader.EditorTryGetIdentity(Stem, out string version, out string md5));
            Assert.AreEqual("9.9.9", version);
            Assert.AreEqual(MatchMd5, md5);
            Assert.IsFalse(BankUpgradeBootstrap.EditorCriticalNeedsHttpUpdate(Stem));
        }

        [Test]
        public void CriticalNeedsHttp_MtimeDrift_SameSizeAndMd5_StillReady()
        {
            Apply(MatchVersion, MatchMd5);
            File.WriteAllBytes(_path, new byte[2048]);
            BankDownloader.EditorRememberIdentity(Stem, MatchVersion, MatchMd5, _path);
            File.SetLastWriteTimeUtc(_path, DateTime.UtcNow.AddHours(-5));
            BankDownloader.InvalidateLocalReadyCache(Stem);

            Assert.IsTrue(BankDownloader.IsLocalReady(Stem));
            Assert.IsTrue(BankDownloader.EditorTryGetIdentity(Stem, out string version, out string md5));
            Assert.AreEqual(MatchVersion, version);
            Assert.AreEqual(MatchMd5, md5);
            Assert.IsFalse(BankUpgradeBootstrap.EditorCriticalNeedsHttpUpdate(Stem));
        }

        [Test]
        public void CriticalNeedsHttp_MtimeDrift_Md5Differs_StillNeedsHttp()
        {
            Apply(MatchVersion, "new-md5");
            File.WriteAllBytes(_path, new byte[2048]);
            BankDownloader.EditorRememberIdentity(Stem, MatchVersion, "old-md5", _path);
            File.SetLastWriteTimeUtc(_path, DateTime.UtcNow.AddHours(-5));
            BankDownloader.InvalidateLocalReadyCache(Stem);

            Assert.IsFalse(BankDownloader.IsLocalReady(Stem));
            Assert.IsTrue(BankUpgradeBootstrap.EditorCriticalNeedsHttpUpdate(Stem));
        }

        [Test]
        public void CriticalNeedsHttp_EmptyVersion_Md5Match_StillReady()
        {
            Apply("", MatchMd5);
            File.WriteAllBytes(_path, new byte[2048]);
            BankDownloader.EditorRememberIdentity(Stem, "", MatchMd5, _path);
            BankDownloader.InvalidateLocalReadyCache(Stem);

            Assert.IsTrue(BankDownloader.IsLocalReady(Stem));
            Assert.IsFalse(BankUpgradeBootstrap.EditorCriticalNeedsHttpUpdate(Stem));
        }

        [Test]
        public void CriticalNeedsHttp_Md5Mismatch_True()
        {
            Apply(MatchVersion, "new-md5");
            File.WriteAllBytes(_path, new byte[2048]);
            BankDownloader.EditorRememberIdentity(Stem, MatchVersion, "old-md5", _path);
            BankDownloader.InvalidateLocalReadyCache(Stem);

            Assert.IsFalse(BankDownloader.IsLocalReady(Stem));
            Assert.IsTrue(BankDownloader.EditorTryGetIdentity(Stem, out string version, out string md5));
            Assert.AreEqual(MatchVersion, version);
            Assert.AreEqual("old-md5", md5);
            Assert.IsTrue(BankUpgradeBootstrap.EditorCriticalNeedsHttpUpdate(Stem));
        }

        [Test]
        public void CriticalNeedsHttp_StreamingVersionSameMd5Differs_True()
        {
            Apply(MatchVersion, "server-md5");
            File.WriteAllBytes(_path, new byte[2048]);
            StreamingBankCatalog.EditorSet(Stem, MatchVersion, "packaged-md5");
            BankDownloader.InvalidateLocalReadyCache(Stem);

            Assert.IsFalse(BankDownloader.IsLocalReady(Stem));
            Assert.IsTrue(
                BankUpgradeBootstrap.EditorCriticalNeedsHttpUpdate(Stem),
                "只对上首包 version、md5 不同，不得跳过 HTTP");
        }

        [Test]
        public void CriticalNeedsHttp_StreamingVersionAndMd5Match_False()
        {
            Apply(MatchVersion, "packaged-md5");
            File.WriteAllBytes(_path, new byte[2048]);
            StreamingBankCatalog.EditorSet(Stem, MatchVersion, "packaged-md5");
            BankDownloader.InvalidateLocalReadyCache(Stem);

            Assert.IsTrue(BankDownloader.IsLocalReady(Stem));
            Assert.IsFalse(BankUpgradeBootstrap.EditorCriticalNeedsHttpUpdate(Stem));
        }

        [Test]
        public void CriticalNeedsHttp_MissingLocal_True()
        {
            Apply("9.9.9", "server-md5");
            BankDownloader.InvalidateLocalReadyCache(Stem);
            Assert.IsFalse(BankDownloader.IsLocalReady(Stem));
            Assert.IsTrue(BankUpgradeBootstrap.EditorCriticalNeedsHttpUpdate(Stem));
        }

        [Test]
        public void CriticalNeedsHttp_MissingLocal_IgnoresStreamingVersionFieldAlone()
        {
            Apply(MatchVersion, "packaged-md5");
            StreamingBankCatalog.EditorSet(Stem, MatchVersion, "packaged-md5");
            BankDownloader.InvalidateLocalReadyCache(Stem);

            Assert.IsFalse(BankDownloader.IsLocalReady(Stem));
            Assert.IsTrue(
                BankUpgradeBootstrap.EditorCriticalNeedsHttpUpdate(Stem),
                "无落盘不得因首包 version+md5 跳过 Critical HTTP");
        }

        void Apply(string version, string md5)
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
                        version = version,
                        md5 = md5,
                    },
                },
            });
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
