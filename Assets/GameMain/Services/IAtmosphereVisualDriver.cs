namespace Game
{
    /// <summary>
    /// Implemented by Assembly-CSharp bridge (OilAtmosphereDriver) so Game can drive CustomSRP sky without an asmdef cycle.
    /// </summary>
    public interface IAtmosphereVisualDriver
    {
        void SetTimeOfDayHours(float hours);
        void SetWeather(IslandWeatherKind kind, int level01To03);
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
