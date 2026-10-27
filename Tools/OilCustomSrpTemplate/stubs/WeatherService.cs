using UnityEngine;

namespace Game
{
    /// <summary>
    /// Lightweight weather stub so OilCloudLayer / OilWindDriver / OilOceanSurface compile alone.
    /// Replace with your own weather system when integrating into a game.
    /// </summary>
    public sealed class WeatherService : MonoBehaviour
    {
        public IslandWeatherKind Kind { get; private set; } = IslandWeatherKind.Cloud;
        public int Level { get; private set; } = 1;

        /// <summary>0–1 wind weight. Full game lerps this; the stub follows Kind/Level.</summary>
        public float VisualWind01 =>
            Kind == IslandWeatherKind.Wind && Level > 0 ? Mathf.Clamp(Level, 0, 3) / 3f : 0f;

        public static WeatherService Ensure()
        {
            var existing = Object.FindAnyObjectByType<WeatherService>();
            if (existing != null)
                return existing;

            var go = new GameObject("WeatherService");
            Object.DontDestroyOnLoad(go);
            return go.AddComponent<WeatherService>();
        }

        public void SetWeather(IslandWeatherKind kind, int level)
        {
            Kind = kind;
            Level = kind == IslandWeatherKind.Clear ? 0 : Mathf.Clamp(level, 0, 3);
        }
    }
}
