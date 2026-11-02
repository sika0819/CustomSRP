using NUnit.Framework;
using UnityEngine;

namespace Game.Tests
{
    public sealed class AmbienceOceanPosePolicyTests
    {
        [Test]
        public void FirstPose_AlwaysAllowed()
        {
            Assert.IsTrue(
                AmbienceOceanPosePolicy.ShouldUpdateChannelPose(
                    Vector3.zero, new Vector3(1f, 0f, 0f), hasCurrentPose: false));
        }

        [Test]
        public void NearChannel_HoldsPose()
        {
            var listener = new Vector3(0f, 0f, 0f);
            var channel = new Vector3(3f, AmbienceZoneCatalog.OceanSurfaceY, 0f);
            Assert.IsFalse(
                AmbienceOceanPosePolicy.ShouldUpdateChannelPose(
                    listener, channel, hasCurrentPose: true));
        }

        [Test]
        public void FarChannel_AllowsUpdate()
        {
            var listener = new Vector3(0f, 0f, 0f);
            var channel = new Vector3(AmbienceOceanPosePolicy.PoseUpdateMinMeters, 0f, 0f);
            Assert.IsTrue(
                AmbienceOceanPosePolicy.ShouldUpdateChannelPose(
                    listener, channel, hasCurrentPose: true));
        }

        [Test]
        public void Resolve_HoldsWhenNear_SnapsWhenFar()
        {
            var listener = new Vector3(0f, 0f, 0f);
            var current = new Vector3(2f, AmbienceZoneCatalog.OceanSurfaceY, 0f);
            var target = new Vector3(10f, AmbienceZoneCatalog.OceanSurfaceY, 5f);

            Vector3 held = AmbienceOceanPosePolicy.ResolveChannelPose(
                listener, current, target, hasCurrentPose: true);
            Assert.AreEqual(current, held);

            var farListener = new Vector3(20f, 0f, 0f);
            Vector3 snapped = AmbienceOceanPosePolicy.ResolveChannelPose(
                farListener, current, target, hasCurrentPose: true);
            Assert.AreEqual(target, snapped);
        }

        [Test]
        public void LeftAndRight_GateIndependently()
        {
            // 听者贴着右声道：右冻结、左（已够远）可更新。
            var listener = new Vector3(0f, 0f, 0f);
            var right = new Vector3(2f, 0f, 0f);
            var left = new Vector3(-AmbienceOceanPosePolicy.PoseUpdateMinMeters, 0f, 0f);

            Assert.IsFalse(
                AmbienceOceanPosePolicy.ShouldUpdateChannelPose(
                    listener, right, hasCurrentPose: true));
            Assert.IsTrue(
                AmbienceOceanPosePolicy.ShouldUpdateChannelPose(
                    listener, left, hasCurrentPose: true));
        }
    }
}
