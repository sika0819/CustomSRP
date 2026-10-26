namespace Game
{
    /// <summary>
    /// Implemented by Assembly-CSharp bridge (OilAtmosphereDriver) so Game can drive CustomSRP sky without an asmdef cycle.
    /// </summary>
    public interface IAtmosphereVisualDriver
    {
        void SetTimeOfDayHours(float hours);
        /// <summary>Snap cloud / rain / foggy visuals to <paramref name="kind"/> + <paramref name="level01To03"/>.</summary>
        void SetWeather(IslandWeatherKind kind, int level01To03);
        /// <summary>
        /// Blend cloud / rain / foggy visuals from <paramref name="fromKind"/>/<paramref name="fromLevel"/>
        /// toward <paramref name="toKind"/>/<paramref name="toLevel"/>. <paramref name="t"/> is 0→1
        /// (same SmoothStep clock as <see cref="WeatherService"/> weather lerp).
        /// </summary>
        void SetWeatherBlend(
            IslandWeatherKind fromKind, int fromLevel,
            IslandWeatherKind toKind, int toLevel,
            float t);
        /// <summary>Absolute ExpSquared density for the active preset look.</summary>
        void SetFogDensity(float exponentialSquaredDensity);
        /// <summary>Optional directional-light color override. Pass null to clear and fall back to the time-of-day table.</summary>
        void SetLightColorOverride(UnityEngine.Color? color);
        /// <summary>Boluo AtmosphereProfile-equivalent sky/light/fog table. Snaps and clears any in-flight blend.</summary>
        void SetLookKind(IslandLookKind lookKind);

        /// <summary>
        /// Blend sky / light / ambient / fog from the current look toward <paramref name="to"/>.
        /// <paramref name="t"/> is 0 at the start and 1 when committed. Same clock as the hour lerp.
        /// </summary>
        void SetLookBlend(IslandLookKind to, float t);
    }
}
