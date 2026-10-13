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
    }
}
