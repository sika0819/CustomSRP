using NUnit.Framework;

namespace Game.Tests
{
    /// <summary>PA-1191：桥上重启 / 命名甲板贴地策略。</summary>
    public sealed class NamedDeckSpawnPolicyTests
    {
        [Test]
        public void MatchesObjectName_DeckKeywords()
        {
            Assert.IsTrue(NamedDeckSpawnPolicy.MatchesObjectName("Bridge_01"));
            Assert.IsTrue(NamedDeckSpawnPolicy.MatchesObjectName("wood_qiao_A"));
            Assert.IsTrue(NamedDeckSpawnPolicy.MatchesObjectName("PierRoot"));
            Assert.IsTrue(NamedDeckSpawnPolicy.MatchesObjectName("Dock"));
            Assert.IsTrue(NamedDeckSpawnPolicy.MatchesObjectName("UpperDeck"));
            Assert.IsTrue(NamedDeckSpawnPolicy.MatchesObjectName("SM_Env_Dock_02_LOD1"));
            Assert.IsTrue(NamedDeckSpawnPolicy.MatchesObjectName("Prefab_Docks"));
            Assert.IsFalse(NamedDeckSpawnPolicy.MatchesObjectName("Rock_Large"));
            Assert.IsFalse(NamedDeckSpawnPolicy.MatchesObjectName("env42"));
            Assert.IsFalse(NamedDeckSpawnPolicy.MatchesObjectName("JianZhu"));
            Assert.IsFalse(NamedDeckSpawnPolicy.MatchesObjectName(null));
            Assert.IsFalse(NamedDeckSpawnPolicy.MatchesObjectName(""));
        }

        [Test]
        public void MatchesHierarchy_StreamerRenamedDockRoot_UsesChildName()
        {
            // SealSpawnedNames 把根改成 envN；海边码头碰撞在根上，Dock 只在子节点。
            Assert.IsTrue(
                NamedDeckSpawnPolicy.MatchesHierarchy(
                    selfName: "env42",
                    parentNames: new[] { "JianZhu" },
                    childNames: new[] { "SM_Env_Dock_02_LOD1" }));
            Assert.IsTrue(
                NamedDeckSpawnPolicy.MatchesHierarchy(
                    selfName: "env7",
                    parentNames: new[] { "Qiao" },
                    childNames: new[] { "Prefab_Docks" }));
            Assert.IsFalse(
                NamedDeckSpawnPolicy.MatchesHierarchy(
                    selfName: "env99",
                    parentNames: new[] { "JianZhu" },
                    childNames: new[] { "SM_Env_Rock_01_LOD1" }));
        }

        [Test]
        public void PreferDeck_WhenHintOnBridgeAndTerrainIsRiverbed()
        {
            const float eye = 1.7f;
            float terrainY = 2f;   // 河床
            float deckY = 8f;      // 桥面
            float hintY = deckY + eye; // 存档眼位
            Assert.IsTrue(
                NamedDeckSpawnPolicy.ShouldPreferDeckOverWater(
                    hintY: hintY,
                    terrainY: terrainY,
                    deckY: deckY,
                    eyeHeight: eye));
        }

        [Test]
        public void PreferDeck_WhenBridgeCrossesWalkableGround()
        {
            // 桥下是可走地面，不算禁水，也要直接贴甲板，不能先落到桥底再被顶上来。
            const float eye = 1.7f;
            float terrainY = 4f;
            float deckY = 9f;
            Assert.IsTrue(
                NamedDeckSpawnPolicy.ShouldPreferDeckOverWater(
                    hintY: deckY + eye,
                    terrainY: terrainY,
                    deckY: deckY,
                    eyeHeight: eye));
        }

        [Test]
        public void PreferDeck_RejectsWhenHintAlreadyUnderBridge()
        {
            // 人本来就在桥下河床 → 不得拽上桥。
            Assert.IsFalse(
                NamedDeckSpawnPolicy.ShouldPreferDeckOverWater(
                    hintY: 2.2f,
                    terrainY: 2f,
                    deckY: 8f,
                    eyeHeight: 1.7f));
        }

        [Test]
        public void PreferDeck_RejectsWhenDeckBarelyAboveTerrain()
        {
            // 命名碰撞只比高度图高半米：不当桥面，避免贴地石阶把人抬走。
            Assert.IsFalse(
                NamedDeckSpawnPolicy.ShouldPreferDeckOverWater(
                    hintY: 10f,
                    terrainY: 8f,
                    deckY: 8.5f,
                    eyeHeight: 1.7f));
        }

        [Test]
        public void PreferDeck_RejectsFlatDeckAlmostAtTerrain()
        {
            Assert.IsFalse(
                NamedDeckSpawnPolicy.ShouldPreferDeckOverWater(
                    hintY: 3f,
                    terrainY: 2f,
                    deckY: 2.2f, // < terrain + 1
                    eyeHeight: 1.7f));
        }

        [Test]
        public void OceanDeck_ShoreBelowSeasideBridgeIsNotOpenWater()
        {
            // 海面 5.7，海边码头桥面约 6.8，人还站在贴水岸 5.9。这一步要能抬上去。
            const float oceanY = 5.7f;
            const float deckY = 6.81f;
            const float shoreFootY = 5.9f;
            Assert.IsTrue(NamedDeckSpawnPolicy.IsOceanDeckSurface(deckY, oceanY));
            Assert.IsTrue(NamedDeckSpawnPolicy.IsFootNearOceanDeck(shoreFootY, deckY, oceanY));
            Assert.IsTrue(
                NamedDeckSpawnPolicy.ShouldLiftOntoOceanDeck(
                    shoreFootY, deckY, oceanY, targetsDeck: true));
        }

        [Test]
        public void OceanDeck_StandingOnBridgeIsWalkable()
        {
            Assert.IsTrue(NamedDeckSpawnPolicy.IsFootNearOceanDeck(6.81f, 6.81f, 5.7f));
            Assert.IsFalse(
                NamedDeckSpawnPolicy.ShouldLiftOntoOceanDeck(6.81f, 6.83f, 5.7f, targetsDeck: true));
        }

        [Test]
        public void OceanDeck_RejectsUnderwaterColliderAndHighBridgeUnderside()
        {
            // 顶面在水下：柱身，不是桥面。
            Assert.IsFalse(NamedDeckSpawnPolicy.IsOceanDeckSurface(4.5f, 5.7f));
            Assert.IsFalse(NamedDeckSpawnPolicy.IsFootNearOceanDeck(4.2f, 4.5f, 5.7f));
            // 人在高桥底下，高差超过上台阶：不要吸上桥。
            Assert.IsFalse(
                NamedDeckSpawnPolicy.ShouldLiftOntoOceanDeck(8f, 16.7f, 5.7f, targetsDeck: true));
            Assert.IsFalse(
                NamedDeckSpawnPolicy.ShouldLiftOntoOceanDeck(5.9f, 6.81f, 5.7f, targetsDeck: false));
        }

        [Test]
        public void KeepSavedEye_WhenTeleportDroppedToRiverbed()
        {
            Assert.IsTrue(NamedDeckSpawnPolicy.ShouldKeepSavedEye(savedEyeY: 10f, afterTeleportY: 3f));
            Assert.IsFalse(NamedDeckSpawnPolicy.ShouldKeepSavedEye(savedEyeY: 10f, afterTeleportY: 9f));
            Assert.IsFalse(
                NamedDeckSpawnPolicy.ShouldKeepSavedEye(
                    savedEyeY: 10f,
                    afterTeleportY: 10f - NamedDeckSpawnPolicy.KeepSavedEyeDropMeters));
        }
    }
}
