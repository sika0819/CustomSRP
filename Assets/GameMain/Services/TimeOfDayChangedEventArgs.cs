using GameFramework;
using GameFramework.Event;

namespace Game
{
    public sealed class TimeOfDayChangedEventArgs : GameEventArgs
    {
        public static readonly int EventId = typeof(TimeOfDayChangedEventArgs).GetHashCode();

        public override int Id => EventId;

        public float Hours { get; private set; }
        public IslandTimePeriod Period { get; private set; }

        public static TimeOfDayChangedEventArgs Create(float hours, IslandTimePeriod period)
        {
            TimeOfDayChangedEventArgs e = ReferencePool.Acquire<TimeOfDayChangedEventArgs>();
            e.Hours = hours;
            e.Period = period;
            return e;
        }

        public override void Clear()
        {
            Hours = 0f;
            Period = IslandTimePeriod.Noon;
        }
    }
}
