using GameFramework;
using GameFramework.Event;

namespace Game.Native
{
    public sealed class NativeTimerEventArgs : GameEventArgs
    {
        public static readonly int EventId = typeof(NativeTimerEventArgs).GetHashCode();
        public override int Id => EventId;

        public int RemainingSeconds { get; private set; }
        public int TotalSeconds { get; private set; }

        public static NativeTimerEventArgs Create(int remaining, int total)
        {
            var e = ReferencePool.Acquire<NativeTimerEventArgs>();
            e.RemainingSeconds = remaining;
            e.TotalSeconds = total;
            return e;
        }

        public override void Clear()
        {
            RemainingSeconds = 0;
            TotalSeconds = 0;
        }
    }
}
