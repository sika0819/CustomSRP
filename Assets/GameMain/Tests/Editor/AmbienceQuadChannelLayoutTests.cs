using NUnit.Framework;
using UnityEngine;

namespace Game.Tests
{
    public sealed class AmbienceQuadChannelLayoutTests
    {
        [Test]
        public void Facing_UsesFeatureToListener_NotHeadYaw()
        {
            var feature = new Vector3(0f, 0f, 0f);
            var listener = new Vector3(10f, 0f, 0f);
            Vector3 fwd = AmbienceQuadChannelLayout.StableFacingFromFeatureToListener(
                feature, listener, Vector3.forward);
            Assert.AreEqual(1f, fwd.x, 1e-4f);
            Assert.AreEqual(0f, fwd.z, 1e-4f);

            // 同位置只转头：站位不变 → 朝向不变（与 listener.forward 无关）。
            Vector3 again = AmbienceQuadChannelLayout.StableFacingFromFeatureToListener(
                feature, listener, Vector3.left);
            Assert.AreEqual(fwd, again);
        }

        [Test]
        public void Corners_FrontRightIsEdge0()
        {
            AmbienceQuadChannelLayout.ComputeEdges(
                Vector3.zero, Vector3.forward, Vector3.right, 8f, 8f,
                out Vector3 e0, out Vector3 e1, out Vector3 e2, out Vector3 e3);
            Assert.AreEqual(new Vector3(8f, 0f, 8f), e0);
            Assert.AreEqual(new Vector3(-8f, 0f, 8f), e1);
            Assert.AreEqual(new Vector3(8f, 0f, -8f), e2);
            Assert.AreEqual(new Vector3(-8f, 0f, -8f), e3);
        }

        [Test]
        public void PlayEvent_MapsToBoluoCorners()
        {
            Assert.AreEqual(3, AmbienceQuadChannelLayout.CornerIndexForPlayEvent("Play_River_L"));
            Assert.AreEqual(1, AmbienceQuadChannelLayout.CornerIndexForPlayEvent("Play_River_Ls"));
            Assert.AreEqual(2, AmbienceQuadChannelLayout.CornerIndexForPlayEvent("Play_River_R"));
            Assert.AreEqual(0, AmbienceQuadChannelLayout.CornerIndexForPlayEvent("Play_River_Rs"));
        }

        [Test]
        public void WaterfallBig_DualOffsetsMatchBoluoPrefab()
        {
            AmbienceQuadChannelLayout.WaterfallDualWorldPoints(
                AmbienceQuadChannelLayout.WaterfallBigRoot,
                big: true,
                out Vector3 lsRs,
                out Vector3 lr);
            Assert.AreEqual(
                AmbienceQuadChannelLayout.WaterfallBigRoot + AmbienceQuadChannelLayout.WaterfallBig0Local,
                lsRs);
            Assert.AreEqual(
                AmbienceQuadChannelLayout.WaterfallBigRoot + AmbienceQuadChannelLayout.WaterfallBig1Local,
                lr);
        }

        [Test]
        public void RiverOcean_BoluoMoveThresholdAndHalf()
        {
            Assert.AreEqual(100f, AmbienceQuadChannelLayout.MoveThresholdMeters, 0.01f);
            Assert.AreEqual(0.3f, AmbienceQuadChannelLayout.CenterSmoothSeconds, 0.01f);
            Assert.AreEqual(8f, AmbienceQuadChannelLayout.HalfMetersFor(AmbienceEmitterKind.River), 0.01f);
            Assert.AreEqual(9f, AmbienceQuadChannelLayout.HalfMetersFor(AmbienceEmitterKind.Ocean), 0.01f);
        }
    }
}
