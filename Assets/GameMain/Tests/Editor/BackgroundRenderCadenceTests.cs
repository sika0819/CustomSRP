using NUnit.Framework;

namespace Game.Tests.Editor
{
    public sealed class BackgroundRenderCadenceTests
    {
        [Test]
        public void Background_DoesNotAnimateVectorUi()
        {
            Assert.IsFalse(BackgroundVectorUi.ShouldAnimateVectorUi(inBackground: true));
        }

        [Test]
        public void Foreground_AnimatesVectorUi()
        {
            Assert.IsTrue(BackgroundVectorUi.ShouldAnimateVectorUi(inBackground: false));
        }
    }
}
