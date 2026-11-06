using NUnit.Framework;
using UnityEngine;

namespace Game.Tests
{
    /// <summary>PA-1190：Mist 不当瀑；River_04 / Waterfall* 才进瀑分类。Boluo Big_0/1 双落点。</summary>
    public sealed class AmbienceWaterfallCatalogTests
    {
        [Test]
        public void Mist_IsNotWaterfall()
        {
            Assert.IsFalse(AmbienceZoneCatalog.IsWaterfallPrefab("WaterfallMist"));
            Assert.IsFalse(AmbienceZoneCatalog.IsWaterfallPrefab("FX_WaterfallMist_01"));
        }

        [Test]
        public void SplashAndNamedFalls_AreWaterfall()
        {
            Assert.IsTrue(AmbienceZoneCatalog.IsWaterfallPrefab("WaterfallSplash"));
            Assert.IsTrue(AmbienceZoneCatalog.IsWaterfallPrefab("Waterfall_Big"));
            Assert.IsTrue(AmbienceZoneCatalog.IsWaterfallPrefab("River_04"));
        }

        [Test]
        public void OrdinaryRiver_IsNotWaterfall()
        {
            Assert.IsFalse(AmbienceZoneCatalog.IsWaterfallPrefab("River_01"));
            Assert.IsFalse(AmbienceZoneCatalog.IsWaterfallPrefab("River_Quad"));
            Assert.IsFalse(AmbienceZoneCatalog.IsWaterfallPrefab(null));
            Assert.IsFalse(AmbienceZoneCatalog.IsWaterfallPrefab(""));
        }

        [Test]
        public void BoluoRadii_MatchSpatialAmbienceRange()
        {
            // 开播=CurveForGain；停播=SphereCollider；卸载=unloadwaittime。
            Assert.AreEqual(40f, AmbienceZoneCatalog.RiverAttenuationMeters, 0.01f);
            Assert.AreEqual(60f, AmbienceZoneCatalog.RiverExitMeters, 0.01f);
            Assert.AreEqual(0f, AmbienceZoneCatalog.RiverUnloadDelaySeconds, 0.01f);
            Assert.AreEqual(48f, AmbienceZoneCatalog.LakeAttenuationMeters, 0.01f);
            Assert.AreEqual(62.21f, AmbienceZoneCatalog.LakeExitMeters, 0.01f);
            Assert.AreEqual(2f, AmbienceZoneCatalog.LakeUnloadDelaySeconds, 0.01f);
            Assert.AreEqual(125f, AmbienceZoneCatalog.OceanAttenuationMeters, 0.01f);
            Assert.AreEqual(166.97f, AmbienceZoneCatalog.OceanExitMeters, 0.01f);
            Assert.AreEqual(0f, AmbienceZoneCatalog.OceanUnloadDelaySeconds, 0.01f);
            Assert.AreEqual(30f, AmbienceZoneCatalog.CampfireAttenuationMeters, 0.01f);
            Assert.AreEqual(50f, AmbienceZoneCatalog.CampfireExitMeters, 0.01f);
            Assert.AreEqual(5f, AmbienceZoneCatalog.CampfireUnloadDelaySeconds, 0.01f);
            Assert.AreEqual(80f, AmbienceZoneCatalog.WaterfallBigEnterMeters, 0.01f);
            Assert.AreEqual(100f, AmbienceZoneCatalog.WaterfallBigExitMeters, 0.01f);
            Assert.Greater(
                AmbienceZoneCatalog.WaterfallBigEnterMeters,
                AmbienceZoneCatalog.WaterfallSmallEnterMeters);
            Assert.Greater(
                AmbienceZoneCatalog.WaterfallBigExitMeters,
                AmbienceZoneCatalog.WaterfallSmallExitMeters);
            Assert.AreEqual(2f, AmbienceZoneCatalog.WaterfallBigUnloadDelaySeconds, 0.01f);
            Assert.AreEqual(50f, AmbienceZoneCatalog.WaterfallSmallEnterMeters, 0.01f);
            Assert.AreEqual(72f, AmbienceZoneCatalog.WaterfallSmallExitMeters, 0.01f);
            Assert.AreEqual(0f, AmbienceZoneCatalog.WaterfallSmallUnloadDelaySeconds, 0.01f);
        }

        [Test]
        public void IsWaterfallBig_UsesBankNotExitRadius()
        {
            var big = new AmbienceEmitterSpec
            {
                Kind = AmbienceEmitterKind.Waterfall,
                Bank = "Waterfall_Big_Quad",
                PlayEvent = "Play_Waterfall_Big_LR",
                EnterRadius = 80f,
                ExitRadius = 117.8f,
            };
            var small = new AmbienceEmitterSpec
            {
                Kind = AmbienceEmitterKind.Waterfall,
                Bank = "Waterfall_Small_Quad",
                PlayEvent = "Play_Waterfall_Small_LR",
                EnterRadius = 50f,
                ExitRadius = 50f,
            };
            Assert.IsTrue(AmbienceQuadChannelLayout.IsWaterfallBig(big));
            Assert.IsFalse(AmbienceQuadChannelLayout.IsWaterfallBig(small));
        }

        [Test]
        public void BigFeature_OverlookWithinEnter_FarBeyondExit()
        {
            // Preset7 观景点 ≈ (504.67, 38.82, 463.85)；到 Big_1 约 33m，在开播圈内。
            var overlook = new Vector3(504.6723f, 38.82f, 463.8487f);
            float d = AmbienceQuadChannelLayout.DistanceToWaterfallBigFeature(
                overlook, out _);
            Assert.Less(d, AmbienceZoneCatalog.WaterfallBigEnterMeters);

            // 远离大瀑：应大于停播。
            var far = new Vector3(600f, 40f, 600f);
            float dFar = AmbienceQuadChannelLayout.DistanceToWaterfallBigFeature(far, out _);
            Assert.Greater(dFar, AmbienceZoneCatalog.WaterfallBigExitMeters);
        }

        [Test]
        public void RiverBesideFall_DoesNotExtendBigAudibleRange()
        {
            // 贴着瀑底河、但离 Loader/Big_0/Big_1 已超出停播：不得再把距离打成 0。
            var listener = new Vector3(435.4f, 22f, 330f);
            float d = AmbienceQuadChannelLayout.DistanceToWaterfallBigFeature(listener, out _);
            Assert.Greater(d, AmbienceZoneCatalog.WaterfallBigExitMeters);

            // 小瀑音源附近也不在大瀑停播圈里，两条瀑的满音量区不重叠。
            var nearSmall = AmbienceQuadChannelLayout.WaterfallSmallRoot;
            float dSmall = AmbienceQuadChannelLayout.DistanceToWaterfallBigFeature(nearSmall, out _);
            Assert.Greater(dSmall, AmbienceZoneCatalog.WaterfallBigExitMeters);
        }

        [Test]
        public void WaterfallPlayEvent_MapsToDualChannels()
        {
            Assert.AreEqual(
                0, AmbienceQuadChannelLayout.ChannelIndexForWaterfallPlayEvent("Play_Waterfall_Big_LsRs"));
            Assert.AreEqual(
                1, AmbienceQuadChannelLayout.ChannelIndexForWaterfallPlayEvent("Play_Waterfall_Big_LR"));
            Assert.AreEqual(
                0, AmbienceQuadChannelLayout.ChannelIndexForWaterfallPlayEvent("Play_Waterfall_Small_LsRs"));
            Assert.AreEqual(
                1, AmbienceQuadChannelLayout.ChannelIndexForWaterfallPlayEvent("Play_Waterfall_Small_LR"));
        }

        [Test]
        public void NoScenicFixedWorldAnchor_UsesBoluoDualInstead()
        {
            for (int i = 0; i < AmbienceZoneCatalog.StaticEmitters.Length; i++)
            {
                var spec = AmbienceZoneCatalog.StaticEmitters[i];
                if (spec == null)
                    continue;
                Assert.IsFalse(
                    spec.FixedWorldAnchor && spec.Kind == AmbienceEmitterKind.Waterfall,
                    "观景代理已撤；大瀑靠 Big_0/1");
            }
        }
    }
}
