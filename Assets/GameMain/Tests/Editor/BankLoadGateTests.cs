using System.IO;
using System.Text;
using Game.BoluoApi;
using NUnit.Framework;

namespace Game.Tests
{
    /// <summary>
    /// 未确认的 persistent bank 不能进引擎；坏文件改名为 .bad。
    /// </summary>
    public sealed class BankLoadGateTests
    {
        const string MarkerStem = "GateMarker_UiTest";
        const string PlainStem = "GatePlain_UiTest";
        const string ShortStem = "GateShort_UiTest";
        const string BadStem = "GateBad_UiTest";

        [SetUp]
        public void SetUp()
        {
            BankLoadGate.ResetSession();
            AudioEnginePaths.EnsureWritableDirs();
        }

        [TearDown]
        public void TearDown()
        {
            Cleanup(MarkerStem);
            Cleanup(PlainStem);
            Cleanup(ShortStem);
            Cleanup(BadStem);
            BankDownloader.EditorRemoveMd5Cache(MarkerStem);
            BankDownloader.EditorRemoveMd5Cache(PlainStem);
            BankDownloader.EditorRemoveMd5Cache(ShortStem);
            BankDownloader.EditorRemoveMd5Cache(BadStem);
            BankLoadGate.ResetSession();
        }

        [Test]
        public void MarkerMatch_ResolvesPersistent()
        {
            string path = WriteBank(MarkerStem, 256);
            var fi = new FileInfo(path);
            File.WriteAllText(path + ".loadok", fi.Length + "\t" + fi.LastWriteTimeUtc.Ticks);

            Assert.IsTrue(BankLoadGate.TryResolveForEngine(MarkerStem, out string resolved));
            Assert.AreEqual(path, resolved);
        }

        [Test]
        public void Unconfirmed_DoesNotResolvePersistent()
        {
            string path = WriteBank(PlainStem, 256);

            Assert.IsFalse(BankLoadGate.TryResolveForEngine(PlainStem, out _));
            Assert.IsTrue(File.Exists(path));
            Assert.IsFalse(File.Exists(path + ".bad"));
            Assert.AreEqual("no-package-copy", BankLoadGate.LastBlockReason);
            Assert.IsTrue(BankLoadGate.CanEnterWithoutOfficialFile(PlainStem));
        }

        [Test]
        public void ShorterThanCache_Quarantines()
        {
            string path = WriteBank(ShortStem, 256);
            var fi = new FileInfo(path);
            BankDownloader.EditorSetMd5Cache(
                ShortStem, fi.Length + 4096, fi.LastWriteTimeUtc.Ticks, "gate-md5", "1.0.0");

            Assert.IsFalse(BankLoadGate.TryResolveForEngine(ShortStem, out _));
            Assert.IsFalse(File.Exists(path));
            Assert.IsTrue(File.Exists(path + ".bad"));
            Assert.IsTrue(BankLoadGate.CanEnterWithoutOfficialFile(ShortStem));
        }

        [Test]
        public void InvalidHeader_Quarantines()
        {
            string path = Path.Combine(
                AudioEnginePaths.PersistentBankPath,
                BadStem + AudioEnginePaths.BankExtension);
            File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4 });

            Assert.IsFalse(BankLoadGate.TryResolveForEngine(BadStem, out _));
            Assert.IsFalse(File.Exists(path));
            Assert.IsTrue(File.Exists(path + ".bad"));
        }

        [Test]
        public void CacheSameSize_MtimeDrift_KeepsFileAndStamps()
        {
            string path = WriteBank(MarkerStem, 256);
            var fi = new FileInfo(path);
            BankDownloader.EditorSetMd5Cache(MarkerStem, fi.Length, 1L, "gate-md5", "1.0.0");

            Assert.IsTrue(BankLoadGate.TryResolveForEngine(MarkerStem, out string resolved));
            Assert.AreEqual(path, resolved);
            Assert.IsTrue(File.Exists(path));
            Assert.IsFalse(File.Exists(path + ".bad"));
            Assert.IsTrue(File.Exists(path + ".loadok"));
        }

        [Test]
        public void MarkerSizeMatch_MtimeDrift_KeepsFile()
        {
            string path = WriteBank(PlainStem, 256);
            var fi = new FileInfo(path);
            File.WriteAllText(path + ".loadok", fi.Length + "\t1");

            Assert.IsTrue(BankLoadGate.TryResolveForEngine(PlainStem, out string resolved));
            Assert.AreEqual(path, resolved);
            Assert.IsTrue(File.Exists(path));
            Assert.IsFalse(File.Exists(path + ".bad"));
        }

        [Test]
        public void CacheMatch_StampsMarker()
        {
            string path = WriteBank(MarkerStem, 256);
            var fi = new FileInfo(path);
            BankDownloader.EditorSetMd5Cache(
                MarkerStem, fi.Length, fi.LastWriteTimeUtc.Ticks, "gate-md5", "1.0.0");

            Assert.IsTrue(BankLoadGate.TryResolveForEngine(MarkerStem, out string resolved));
            Assert.AreEqual(path, resolved);
            Assert.IsTrue(File.Exists(path + ".loadok"));
        }

        static string WriteBank(string stem, int size)
        {
            var bytes = new byte[size];
            byte[] marker = Encoding.ASCII.GetBytes("BankHeader");
            System.Buffer.BlockCopy(marker, 0, bytes, 0, marker.Length);
            string path = Path.Combine(
                AudioEnginePaths.PersistentBankPath,
                stem + AudioEnginePaths.BankExtension);
            File.WriteAllBytes(path, bytes);
            return path;
        }

        static void Cleanup(string stem)
        {
            string path = Path.Combine(
                AudioEnginePaths.PersistentBankPath,
                stem + AudioEnginePaths.BankExtension);
            TryDelete(path);
            TryDelete(path + ".loadok");
            TryDelete(path + ".bad");
            TryDelete(path + ".pkg");
        }

        static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
                // 测试清理失败不掩盖断言。
            }
        }
    }
}
