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
}
