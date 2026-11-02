using NUnit.Framework;

namespace Game.Tests
{
    /// <summary>PA-1190：水域钉正午 TOD + 贴瀑优先于湖。</summary>
    public sealed class AmbienceWaterSilencePolicyTests
    {
        [Test]
        public void PinDaytime_WaterKinds_Only()
        {
            Assert.IsTrue(AmbienceWaterSilencePolicy.ShouldPinDaytimeTimeOfDay(AmbienceEmitterKind.Ocean));
            Assert.IsTrue(AmbienceWaterSilencePolicy.ShouldPinDaytimeTimeOfDay(AmbienceEmitterKind.River));
            Assert.IsTrue(AmbienceWaterSilencePolicy.ShouldPinDaytimeTimeOfDay(AmbienceEmitterKind.Lake));
            Assert.IsTrue(AmbienceWaterSilencePolicy.ShouldPinDaytimeTimeOfDay(AmbienceEmitterKind.Waterfall));
            Assert.IsFalse(AmbienceWaterSilencePolicy.ShouldPinDaytimeTimeOfDay(AmbienceEmitterKind.Forest));
            Assert.IsFalse(AmbienceWaterSilencePolicy.ShouldPinDaytimeTimeOfDay(AmbienceEmitterKind.Campfire));
            Assert.IsFalse(AmbienceWaterSilencePolicy.ShouldPinDaytimeTimeOfDay(AmbienceEmitterKind.Grassland));
            Assert.IsFalse(AmbienceWaterSilencePolicy.ShouldPinDaytimeTimeOfDay(AmbienceEmitterKind.Plateau));
        }

        [Test]
        public void PreferFall_WhenWithinPreferRadius()
        {
            // 湖更近，但人已在 20m 瀑圈内 → 仍保留瀑。
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
            Assert.IsTrue(
                AmbienceWaterSilencePolicy.PreferLakeOverWaterfall(
                    bestLakeDist: 15f,
                    bestFallDist: 40f,
                    lakePlaying: false,
                    fallPlaying: false));
        }

        [Test]
        public void Hysteresis_KeepsPlayingLake()
        {
            // 瀑略近，但湖在播且在滞回内 → 继续湖。
            float fall = 40f;
            float lake = fall - 5f; // 湖更近
            Assert.IsTrue(
                AmbienceWaterSilencePolicy.PreferLakeOverWaterfall(
                    bestLakeDist: lake,
                    bestFallDist: fall,
                    lakePlaying: true,
                    fallPlaying: false));

            // 瀑更近一点但仍在湖 sticky 内
            Assert.IsTrue(
                AmbienceWaterSilencePolicy.PreferLakeOverWaterfall(
                    bestLakeDist: 40f,
                    bestFallDist: 35f,
                    lakePlaying: true,
                    fallPlaying: false));
        }

        [Test]
        public void Hysteresis_KeepsPlayingFall()
        {
            Assert.IsFalse(
                AmbienceWaterSilencePolicy.PreferLakeOverWaterfall(
                    bestLakeDist: 35f,
                    bestFallDist: 40f,
                    lakePlaying: false,
                    fallPlaying: true));
        }
    }
}
