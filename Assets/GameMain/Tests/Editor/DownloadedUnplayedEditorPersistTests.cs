using Game.BoluoApi;
using NUnit.Framework;
using UnityEngine;

namespace Game.Tests
{
    /// <summary>
    /// Editor 游客 saveLocal=false 时账号进度盘隔离；已听须走独立键，
    /// 否则退登 ResetSession 后重进红点回潮。
    /// </summary>
    public sealed class DownloadedUnplayedEditorPersistTests
    {
        [SetUp]
        public void SetUp()
        {
            DownloadedUnplayedManager.EditorClearHeardPrefsForTests();
            GuestSession.Clear();
            GuestSession.ApplyLocalEditorGuest();
            Assert.IsFalse(
                BoluoGamePersistence.AllowsAccountProgressDisk,
                "本测要求 Editor 游客隔离进度盘");
        }

        [TearDown]
        public void TearDown()
        {
            DownloadedUnplayedManager.EditorClearHeardPrefsForTests();
            GuestSession.Clear();
        }

        [Test]
        public void MarkPlayed_SurvivesResetSession_WhenEditorGuest()
        {
            string key = DownloadedUnplayedManager.PresetKey("清晨起床");
            Assert.IsTrue(DownloadedUnplayedManager.ShouldShowRedDot(key), "首次应亮红点");

            DownloadedUnplayedManager.MarkPlayed(key);
            Assert.IsFalse(DownloadedUnplayedManager.ShouldShowRedDot(key), "已听后应灭");

            // 模拟退登 / 离岛清内存。
            DownloadedUnplayedManager.ResetSession();
            Assert.IsFalse(
                DownloadedUnplayedManager.ShouldShowRedDot(key),
                "Editor 已听落盘后，ResetSession 再 EnsureLoaded 仍应灭红点");
        }

        [Test]
        public void JsonRoundTrip_KeepsPlayedKeys()
        {
            string key = DownloadedUnplayedManager.PresetKey("午后放松");
            DownloadedUnplayedManager.MarkPlayed(key);
            DownloadedUnplayedManager.ResetSession();
            DownloadedUnplayedManager.EnsureLoaded();
            Assert.IsFalse(DownloadedUnplayedManager.ShouldShowRedDot(key));
            Assert.IsTrue(PlayerPrefs.HasKey("Boluo.Editor.DownloadedUnplayed"));
        }
    }
}
