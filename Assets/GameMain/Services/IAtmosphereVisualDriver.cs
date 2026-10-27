namespace Game
{
    /// <summary>
    /// Implemented by Assembly-CSharp bridge (OilAtmosphereDriver) so Game can drive CustomSRP sky without an asmdef cycle.
    /// </summary>
    public interface IAtmosphereVisualDriver
    {
        void SetTimeOfDayHours(float hours);
        void SetWeather(IslandWeatherKind kind, int level01To03);

        /// <summary>
        /// Blend cloud coverage, rain intensity, and foggy weight toward <paramref name="kind"/>.
        /// A new <paramref name="blendId"/> captures the driver's current look as the start.
        /// <paramref name="t"/> is already smoothed, 0 at the start and 1 when committed.
        /// </summary>
        void SetWeatherBlend(int blendId, IslandWeatherKind kind, int level01To03, float t);
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
