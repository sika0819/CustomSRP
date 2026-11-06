namespace Game
{
    /// <summary>
    /// 瀑布/河流「没声」相关纯策略：TOD 钉正午、湖↔瀑互斥优先瀑。
    /// 供 <see cref="AmbienceZoneService"/> 与 EditMode 单测共用。
    /// </summary>
    public static class AmbienceWaterSilencePolicy
    {
        /// <summary>
        /// 贴瀑优先于湖的水平距离（米）。
        /// 必须盖住大瀑已播停播距（100）：圈内更近的湖不能把瀑掐掉。
        /// </summary>
        public const float PreferFallWithinMeters = 100f;

        /// <summary>湖/瀑 sticky 滞回（米）。</summary>
        public const float ExclusiveShareHysteresisMeters = 12f;

        /// <summary>
        /// 物体级钉正午 Time of A Day：河/瀑/海/湖 + 篝火。
        /// 篝火若不钉，夜间全局 TOD 会把它掐成「play=1 却听不见」，河瀑仍响（已钉正午）。
        /// </summary>
        public static bool ShouldPinDaytimeTimeOfDay(AmbienceEmitterKind kind)
        {
            switch (kind)
            {
                case AmbienceEmitterKind.Ocean:
                case AmbienceEmitterKind.River:
                case AmbienceEmitterKind.Lake:
                case AmbienceEmitterKind.Waterfall:
                case AmbienceEmitterKind.Campfire:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>贴点燃篝火时压河/瀑/湖的增益（dB，负值）。</summary>
        public const float CampfireWaterDuckDb = -12f;

        /// <summary>火边压水声的水平半径（米），对齐篝火衰减半径。</summary>
        public const float CampfireWaterDuckMeters = 30f;

        /// <summary>
        /// 人贴点燃篝火时，水类增益额外衰减多少。
        /// 火心满压 <see cref="CampfireWaterDuckDb"/>，到半径外线性回到 0。
        /// </summary>
        public static float WaterDuckDbNearCampfire(
            float nearestCampfireHorizontalMeters,
            float duckMeters = CampfireWaterDuckMeters,
            float duckDb = CampfireWaterDuckDb)
        {
            if (duckMeters <= 0f || duckDb >= 0f)
                return 0f;
            if (nearestCampfireHorizontalMeters < 0f
                || nearestCampfireHorizontalMeters >= duckMeters)
                return 0f;
            float t = 1f - nearestCampfireHorizontalMeters / duckMeters;
            return duckDb * t;
        }

        /// <summary>
        /// 湖与瀑同时 want 时是否保留湖（掐瀑）。
        /// 人已贴瀑（≤ <see cref="PreferFallWithinMeters"/>）时永远保留瀑。
        /// </summary>
        public static bool PreferLakeOverWaterfall(
            float bestLakeDist,
            float bestFallDist,
            bool lakePlaying,
            bool fallPlaying,
            float preferFallWithinMeters = PreferFallWithinMeters,
            float hysteresisMeters = ExclusiveShareHysteresisMeters)
        {
            if (bestFallDist <= preferFallWithinMeters)
                return false;

            bool preferLake = bestLakeDist <= bestFallDist;
            if (lakePlaying && !fallPlaying
                && bestLakeDist <= bestFallDist + hysteresisMeters)
                preferLake = true;
            else if (fallPlaying && !lakePlaying
                && bestFallDist <= bestLakeDist + hysteresisMeters)
                preferLake = false;
            return preferLake;
        }
    }
}
