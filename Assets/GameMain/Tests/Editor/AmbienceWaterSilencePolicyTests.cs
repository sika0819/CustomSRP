using NUnit.Framework;

namespace Game.Tests
{
    /// <summary>PA-1190：水域钉正午 TOD + 贴瀑优先于湖。</summary>
    public sealed class AmbienceWaterSilencePolicyTests
    {
        [Test]
        public void PinDaytime_WaterAndCampfire()
        {
            Assert.IsTrue(AmbienceWaterSilencePolicy.ShouldPinDaytimeTimeOfDay(AmbienceEmitterKind.Ocean));
            Assert.IsTrue(AmbienceWaterSilencePolicy.ShouldPinDaytimeTimeOfDay(AmbienceEmitterKind.River));
            Assert.IsTrue(AmbienceWaterSilencePolicy.ShouldPinDaytimeTimeOfDay(AmbienceEmitterKind.Lake));
            Assert.IsTrue(AmbienceWaterSilencePolicy.ShouldPinDaytimeTimeOfDay(AmbienceEmitterKind.Waterfall));
            Assert.IsTrue(AmbienceWaterSilencePolicy.ShouldPinDaytimeTimeOfDay(AmbienceEmitterKind.Campfire));
            Assert.IsFalse(AmbienceWaterSilencePolicy.ShouldPinDaytimeTimeOfDay(AmbienceEmitterKind.Forest));
            Assert.IsFalse(AmbienceWaterSilencePolicy.ShouldPinDaytimeTimeOfDay(AmbienceEmitterKind.Grassland));
            Assert.IsFalse(AmbienceWaterSilencePolicy.ShouldPinDaytimeTimeOfDay(AmbienceEmitterKind.Plateau));
        }

        [Test]
        public void WaterDuck_NearCampfire_FullAtZero_NoneAtEdge()
        {
            Assert.AreEqual(-12f, AmbienceWaterSilencePolicy.WaterDuckDbNearCampfire(0f), 0.01f);
            Assert.AreEqual(0f, AmbienceWaterSilencePolicy.WaterDuckDbNearCampfire(30f), 0.01f);
            Assert.AreEqual(0f, AmbienceWaterSilencePolicy.WaterDuckDbNearCampfire(40f), 0.01f);
            Assert.AreEqual(-6f, AmbienceWaterSilencePolicy.WaterDuckDbNearCampfire(15f), 0.01f);
        }

        [Test]
        public void PreferFall_WhenWithinPreferRadius()
        {
            // 湖更近，但人已在贴瀑优先圈内 → 仍保留瀑。
            Assert.IsFalse(
                AmbienceWaterSilencePolicy.PreferLakeOverWaterfall(
                    bestLakeDist: 10f,
                    bestFallDist: 50f,
                    lakePlaying: true,
                    fallPlaying: false));
            Assert.IsFalse(
                AmbienceWaterSilencePolicy.PreferLakeOverWaterfall(
                    bestLakeDist: 10f,
                    bestFallDist: 20f,
                    lakePlaying: true,
                    fallPlaying: false));
        }

        [Test]
        public void PreferLake_WhenCloserAndOutsideFallRadius()
        {
            // 须在 PreferFallWithinMeters 外，否则贴瀑优先。
            Assert.IsTrue(
                AmbienceWaterSilencePolicy.PreferLakeOverWaterfall(
                    bestLakeDist: 15f,
                    bestFallDist: 120f,
                    lakePlaying: false,
                    fallPlaying: false));
        }

        [Test]
        public void Hysteresis_KeepsPlayingLake()
        {
            // 双方都在贴瀑优先圈外：瀑略近，但湖在播且在滞回内 → 继续湖。
            Assert.IsTrue(
                AmbienceWaterSilencePolicy.PreferLakeOverWaterfall(
                    bestLakeDist: 105f,
                    bestFallDist: 110f,
                    lakePlaying: true,
                    fallPlaying: false));

            Assert.IsTrue(
                AmbienceWaterSilencePolicy.PreferLakeOverWaterfall(
                    bestLakeDist: 110f,
                    bestFallDist: 105f,
                    lakePlaying: true,
                    fallPlaying: false));
        }

        [Test]
        public void Hysteresis_KeepsPlayingFall()
        {
            Assert.IsFalse(
                AmbienceWaterSilencePolicy.PreferLakeOverWaterfall(
                    bestLakeDist: 105f,
                    bestFallDist: 110f,
                    lakePlaying: false,
                    fallPlaying: true));
        }
    }
}
