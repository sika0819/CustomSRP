namespace Game
{
    /// <summary>Maps Boluo WeatherType (Wind/Rain/Foggy/Cloud) for UI and atmosphere.</summary>
    public enum IslandWeatherKind
    {
        Clear = -1,
        Wind = 0,
        Rain = 1,
        Foggy = 2,
        Cloud = 3,
    }

    public enum IslandTimePeriod
    {
        Morning,
        Noon,
        Evening,
        Night,
    }

    /// <summary>
    /// Boluo Cozy AtmosphereProfile slots (Camera_1 / 5 / 3 / 7 / 8 / 10).
    /// Values must match CustomSRP.OilSkyLookId.
    /// </summary>
    public enum IslandLookKind
    {
        Canonical = 0,
        CoolFocus = 1,
        WarmAfternoon = 2,
        CyanMorning = 3,
        WarmNoon = 4,
        NightCold = 5,
    }
}
