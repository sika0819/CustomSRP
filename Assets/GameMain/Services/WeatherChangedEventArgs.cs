using GameFramework;
using GameFramework.Event;

namespace Game
{
    public sealed class WeatherChangedEventArgs : GameEventArgs
    {
        public static readonly int EventId = typeof(WeatherChangedEventArgs).GetHashCode();

        public override int Id => EventId;

        public IslandWeatherKind Kind { get; private set; }
        public int Level { get; private set; }

        public static WeatherChangedEventArgs Create(IslandWeatherKind kind, int level)
        {
            WeatherChangedEventArgs e = ReferencePool.Acquire<WeatherChangedEventArgs>();
            e.Kind = kind;
            e.Level = level;
            return e;
        }

        public override void Clear()
        {
            Kind = IslandWeatherKind.Clear;
            Level = 0;
        }
    }
}
