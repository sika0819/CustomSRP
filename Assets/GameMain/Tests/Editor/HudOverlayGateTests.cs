using NUnit.Framework;

namespace Game.Tests
{
    /// <summary>区域软下载窗不冻走路；其它 Dialog 仍冻。</summary>
    public sealed class HudOverlayGateTests
    {
        [Test]
        public void SoftDownloadOnly_DoesNotBlockLocomotion()
        {
            Assert.IsFalse(HudOverlayGate.DialogsBlockLocomotion(1, softDownloadDialogCount: 1));
            Assert.IsFalse(HudOverlayGate.DialogsBlockLocomotion(2, softDownloadDialogCount: 2));
        }

        [Test]
        public void HardDialog_BlocksLocomotion()
        {
            Assert.IsTrue(HudOverlayGate.DialogsBlockLocomotion(1, softDownloadDialogCount: 0));
            Assert.IsTrue(HudOverlayGate.DialogsBlockLocomotion(2, softDownloadDialogCount: 1));
        }

        [Test]
        public void EmptyDialogGroup_DoesNotBlock()
        {
            Assert.IsFalse(HudOverlayGate.DialogsBlockLocomotion(0, softDownloadDialogCount: 0));
        }
    }
}
