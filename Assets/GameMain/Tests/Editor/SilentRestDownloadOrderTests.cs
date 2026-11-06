using System.Collections.Generic;
using Game.BoluoApi;
using NUnit.Framework;

namespace Game.Tests
{
    /// <summary>
    /// 静默 Rest：Music* 优先于预设；用户点击可打断其它 User；Startup 默认≤3。
    /// </summary>
    public sealed class SilentRestDownloadOrderTests
    {
        [SetUp]
        public void SetUp()
        {
            PresetBankCatalog.Clear();
            BankDownloader.ResetSession();
        }

        [TearDown]
        public void TearDown()
        {
            PresetBankCatalog.Clear();
            BankDownloader.ResetSession();
        }

        [Test]
        public void CompareRest_MusicBeforePreset()
        {
            var music = new BankRemoteInfo { bankName = "Music2_3" };
            var preset = new BankRemoteInfo { bankName = "Campfire_Extra" };
            Assert.Less(
                BankUpgradeBootstrap.EditorCompareRestRemoteMusicFirst(music, preset),
                0);
            Assert.Greater(
                BankUpgradeBootstrap.EditorCompareRestRemoteMusicFirst(preset, music),
                0);
            Assert.AreEqual(
                0,
                BankUpgradeBootstrap.EditorCompareRestRemoteMusicFirst(music, music));
        }

        [Test]
        public void CollectRest_OrdersMusicBeforeNonMusic_ExcludesCritical()
        {
            PresetBankCatalog.TryApplyUpgrade(new BankUpgradeData
            {
                banks = new List<BankRemoteInfo>
                {
                    new BankRemoteInfo { bankName = "Z_PresetBank", url = "https://x/a" },
                    new BankRemoteInfo { bankName = "Music9_9", url = "https://x/b" },
                    new BankRemoteInfo { bankName = "Music1_1", url = "https://x/c" },
                    new BankRemoteInfo { bankName = "A_Ambience", url = "https://x/d" },
                },
            });

            List<BankRemoteInfo> rest = BankUpgradeBootstrap.EditorCollectRestRemotes(
                new[] { "Music1_1" });

            Assert.AreEqual(3, rest.Count);
            // Critical Music1_1 excluded; sole remaining music is first; presets after.
            Assert.AreEqual("Music9_9", rest[0].bankName);
            Assert.IsFalse(
                rest[1].bankName.StartsWith("Music", System.StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(
                rest[2].bankName.StartsWith("Music", System.StringComparison.OrdinalIgnoreCase));
        }

        [Test]
        public void AbortOtherUser_True_StillAbortsOtherUserSlot()
        {
            // API 仍支持打断；预设/音乐入口传 abortOtherUser=true，未下载点击优先当前项。
            BankDownloader.EditorPutActiveSlot("Campfire", BankQueueKind.User);
            BankDownloader.EditorAbortConflictingActives(
                keepBankName: "Music2_1",
                abortOtherUserDownloads: true);
            Assert.IsTrue(
                BankDownloader.EditorIsActiveAbort("Campfire"),
                "abortOtherUserDownloads=true still aborts other User slots");
            Assert.IsTrue(
                BankDownloader.EditorIsReleaseWaitersOnAbort("Campfire"),
                "preempt abort must release old DownloadBanks waiters");
        }

        [Test]
        public void AbortOtherUser_False_KeepsOtherUserSlot()
        {
            BankDownloader.EditorPutActiveSlot("Campfire", BankQueueKind.User);
            BankDownloader.EditorAbortConflictingActives(
                keepBankName: "Music2_1",
                abortOtherUserDownloads: false);
            Assert.IsFalse(
                BankDownloader.EditorIsActiveAbort("Campfire"),
                "without flag, other User slots stay");
        }

        [Test]
        public void AbortOtherUser_StillAbortsStartup()
        {
            BankDownloader.EditorPutActiveSlot("Ocean_Calm_Quad", BankQueueKind.Startup);
            BankDownloader.EditorAbortConflictingActives(
                keepBankName: "Music2_1",
                abortOtherUserDownloads: false);
            Assert.IsTrue(BankDownloader.EditorIsActiveAbort("Ocean_Calm_Quad"));
            Assert.IsTrue(BankDownloader.EditorIsReleaseWaitersOnAbort("Ocean_Calm_Quad"));
        }

        [Test]
        public void AbortOtherUser_SkipsKeepBatch()
        {
            // 同批多 bank：b1 Active 时入队 b2，keepBatch 含二者 → b1 不 Abort。
            BankDownloader.EditorPutActiveSlot("Preset_B1", BankQueueKind.User);
            BankDownloader.EditorAbortConflictingActives(
                keepBankName: "Preset_B2",
                abortOtherUserDownloads: true,
                keepBatch: new[] { "Preset_B1", "Preset_B2" });
            Assert.IsFalse(
                BankDownloader.EditorIsActiveAbort("Preset_B1"),
                "same-batch Active must not self-abort");
        }

        [Test]
        public void AbortOtherUser_FiresDoneSoAwaitCompletes()
        {
            var pending = new BankDownloadItem
            {
                BankName = "Campfire",
                Url = "https://example.test/c.wsbk",
                State = BankDownloadState.Downloading,
            };
            BankDownloader.EditorPutActiveItem(pending);
            BankDownloader.EditorPutActiveSlot("Campfire", BankQueueKind.User);

            int done = 0;
            BankDownloader.EditorAttachDoneCallback("Campfire", _ => done++);
            BankDownloader.EditorAbortConflictingActives(
                keepBankName: "Music2_1",
                abortOtherUserDownloads: true);
            Assert.IsTrue(BankDownloader.EditorSimulatePreemptAbortFireDone("Campfire"));
            Assert.AreEqual(1, done, "preempt FireDone must complete old waiter immediately");
        }

        [Test]
        public void StartupParallelSlots_DefaultIsThree()
        {
            BankDownloader.ResetSession();
            Assert.AreEqual(3, BankDownloader.EditorStartupParallelSlots);
        }

        [Test]
        public void DesiredMaxSlots_BackgroundAllowsThree_WhenIdleOfUserAndStartup()
        {
            // Normal/Rest 并发上限与 Startup 对齐为 3（Editor 只读常量行为：无 User/Startup 时泵得开多槽）。
            BankDownloader.ResetSession();
            Assert.AreEqual(3, BankDownloader.EditorStartupParallelSlots);
            Assert.AreEqual(3, BankDownloader.EditorMaxBackgroundSlots);
        }
    }
}
