namespace Game
{
    /// <summary>
    /// 瀑布/河流「没声」相关纯策略：TOD 钉正午、湖↔瀑互斥优先瀑。
    /// 供 <see cref="AmbienceZoneService"/> 与 EditMode 单测共用。
    /// </summary>
    public static class AmbienceWaterSilencePolicy
    {
        /// <summary>贴瀑优先于湖的水平距离（米）。</summary>
        public const float PreferFallWithinMeters = 28f;

        /// <summary>湖/瀑 sticky 滞回（米）。</summary>
        public const float ExclusiveShareHysteresisMeters = 12f;

        /// <summary>水域物体级钉正午 Time of A Day，避免夜间全局 RTPC 掐河/瀑/海/湖。</summary>
        public static bool ShouldPinDaytimeTimeOfDay(AmbienceEmitterKind kind)
        {
            switch (kind)
            {
                case AmbienceEmitterKind.Ocean:
                case AmbienceEmitterKind.River:
                case AmbienceEmitterKind.Lake:
                case AmbienceEmitterKind.Waterfall:
                    return true;
                default:
                    return false;
            }
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
